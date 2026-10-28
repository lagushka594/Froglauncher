using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RibbixLauncher.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RibbixLauncher.Services;

/// <summary>Что показывать в Discord.</summary>
public sealed record PresenceState(
    string Details,
    string? State = null,
    DateTimeOffset? StartedAt = null,
    string SmallImage = "",
    string SmallText = "",
    string? JoinSecret = null);

/// <summary>
/// Discord Rich Presence через локальный IPC (\\.\pipe\discord-ipc-N) без сторонних пакетов.
/// Протокол: кадр = [op:int32 LE][len:int32 LE][json]. op 0 — handshake, op 1 — команда.
/// Ключи картинок (frog_logo, playing, download, java, crown) загружаются в
/// Discord Developer Portal → Rich Presence → Art Assets.
/// Discord не запущен — тихо ничего не делаем и переподключаемся при следующем обновлении.
/// </summary>
public sealed class DiscordPresenceService(IOptions<LauncherOptions> options, ILogger<DiscordPresenceService> log) : IAsyncDisposable
{
    private readonly string _clientId = options.Value.DiscordApplicationId;
    private readonly string _gitHub = options.Value.GitHubUrl;
    private readonly string _discord = options.Value.DiscordUrl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly DateTimeOffset _launcherStarted = DateTimeOffset.UtcNow;
    private NamedPipeClientStream? _pipe;
    private PresenceState? _last;
    private DateTimeOffset _lastSent;

    public bool Enabled { get; set; } = true;

    /// <summary>Друг нажал «Присоединиться» в Discord: секрет — короткое приглашение (версия, загрузчик, сервер).</summary>
    public event Action<string>? JoinRequested;
    private bool _subscribed;
    private Task? _pump;

    public Task SetIdleAsync() => SetAsync(new PresenceState(L.T("В лаунчере"), L.T("Выбирает, во что поиграть 🐸"), _launcherStarted));

    public async Task SetAsync(PresenceState state)
    {
        if (!Enabled || string.IsNullOrEmpty(_clientId)) return;

        // Discord ограничивает частоту обновлений (~5 за 20 с) — прогресс шлём не чаще раза в 4 с.
        if (state == _last || (_last?.Details == state.Details && DateTimeOffset.UtcNow - _lastSent < TimeSpan.FromSeconds(4)))
            return;

        await _lock.WaitAsync();
        try
        {
            if (!await EnsureConnectedAsync()) return;
            await WriteFrameAsync(1, BuildActivity(state));
            await ReadReplyAsync(); // ответ Discord на команду (события по дороге — обрабатываем)
            _last = state;
            _lastSent = DateTimeOffset.UtcNow;
        }
        // OperationCanceledException — Discord не ответил вовремя (завис или перезапускается): тоже не повод ронять лаунчер.
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or JsonException or OperationCanceledException)
        {
            log.LogDebug(ex, "Discord RPC: соединение потеряно");
            Disconnect();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync()
    {
        if (_pipe is null) return;
        await _lock.WaitAsync();
        try
        {
            var payload = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject { ["pid"] = Environment.ProcessId },
                ["nonce"] = Guid.NewGuid().ToString(),
            };
            await WriteFrameAsync(1, payload);
            _last = null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Disconnect();
        }
        finally
        {
            _lock.Release();
        }
    }

    private JsonObject BuildActivity(PresenceState s)
    {
        var assets = new JsonObject
        {
            ["large_image"] = "frog_logo",
            ["large_text"] = L.T("Ribbix — неофициальный лаунчер Minecraft"),
        };
        if (!string.IsNullOrEmpty(s.SmallImage))
        {
            assets["small_image"] = s.SmallImage;
            assets["small_text"] = s.SmallText;
        }

        var activity = new JsonObject
        {
            ["details"] = Trim(s.Details),
            ["assets"] = assets,
        };
        // Discord: либо кнопки-ссылки, либо «Присоединиться» (секрет + группа) — вместе нельзя.
        if (s.JoinSecret is { Length: > 0 } secret)
        {
            activity["party"] = new JsonObject { ["id"] = "ribbix-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(secret)))[..12], ["size"] = new JsonArray(1, 10) };
            activity["secrets"] = new JsonObject { ["join"] = secret };
            activity["instance"] = true;
        }
        else
        {
            activity["buttons"] = new JsonArray(
                new JsonObject { ["label"] = L.T("Скачать Ribbix"), ["url"] = _gitHub + "/releases/latest" },
                new JsonObject { ["label"] = "Discord", ["url"] = _discord });
        }
        if (s.State is not null) activity["state"] = Trim(s.State);
        if (s.StartedAt is { } t) activity["timestamps"] = new JsonObject { ["start"] = t.ToUnixTimeSeconds() };

        return new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString(),
        };
    }

    /// <summary>Discord требует 2–128 символов в details/state.</summary>
    private static string Trim(string s) => s.Length > 128 ? s[..127] + "…" : s.PadRight(2);

    private async Task<bool> EnsureConnectedAsync()
    {
        if (_pipe is { IsConnected: true }) return true;
        Disconnect();

        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150);
                _pipe = pipe;
                await WriteFrameAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = _clientId });
                await ReadFrameAsync(); // READY
                log.LogInformation("Discord RPC подключён (discord-ipc-{Index})", i);
                // Кнопка «Присоединиться»: подписка на ACTIVITY_JOIN и фоновая проверка входящих событий.
                await WriteFrameAsync(1, new JsonObject { ["cmd"] = "SUBSCRIBE", ["evt"] = "ACTIVITY_JOIN", ["nonce"] = Guid.NewGuid().ToString() });
                await ReadReplyAsync();
                _subscribed = true;
                _pump ??= Task.Run(PumpAsync);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
            {
                await pipe.DisposeAsync();
                _pipe = null;
            }
        }

        return false;
    }

    private async Task WriteFrameAsync(int op, JsonNode payload)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0), op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame);
        await _pipe.FlushAsync();
    }

    /// <summary>Читать кадры до ответа на команду; события (ACTIVITY_JOIN) — отдать подписчикам.</summary>
    private async Task ReadReplyAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            var frame = await ReadFrameAsync();
            if (!Dispatch(frame)) return;
        }
    }

    /// <summary>true — это было событие (DISPATCH), а не ответ на команду.</summary>
    private bool Dispatch(JsonNode? frame)
    {
        if (frame?["cmd"]?.GetValue<string>() != "DISPATCH") return false;
        if (frame["evt"]?.GetValue<string>() == "ACTIVITY_JOIN" && frame["data"]?["secret"]?.GetValue<string>() is { Length: > 0 } secret)
        {
            log.LogInformation("Discord: друг присоединяется");
            JoinRequested?.Invoke(secret);
        }
        return true;
    }

    /// <summary>Пока лаунчер просто открыт, события копятся в канале — проверяем раз в 1,5 с, есть ли что прочитать.</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            await Task.Delay(1500);
            if (!_subscribed || _pipe is not { IsConnected: true } pipe) continue;
            if (!PeekNamedPipe(pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero) || available == 0) continue;
            await _lock.WaitAsync();
            try
            {
                while (_pipe is { IsConnected: true } p && PeekNamedPipe(p.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out var n, IntPtr.Zero) && n > 0)
                    Dispatch(await ReadFrameAsync());
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or OperationCanceledException)
            {
                Disconnect();
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, IntPtr buffer, int size, IntPtr read, out int available, IntPtr left);

    private async Task<JsonNode?> ReadFrameAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var header = new byte[8];
        await _pipe!.ReadExactlyAsync(header, cts.Token);
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (len is < 0 or > 64 * 1024) throw new IOException(L.T("Discord RPC: некорректный кадр"));
        var body = new byte[len];
        await _pipe.ReadExactlyAsync(body, cts.Token);
        return JsonNode.Parse(body);
    }

    private void Disconnect()
    {
        _subscribed = false;
        try { _pipe?.Dispose(); } catch (IOException) { }
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ClearAsync();
        Disconnect();
    }
}
