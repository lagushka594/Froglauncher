using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace FrogLauncher.Core;

/// <summary>
/// Discord Rich Presence по локальному IPC (named pipe discord-ipc-N), без сторонних библиотек.
/// Показывает у пользователя в Discord «Играет в FrogLauncher» + подробности. Если Discord не запущен — тихо ждёт.
/// </summary>
public sealed class DiscordRpc : IDisposable
{
    private readonly string _appId;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private volatile bool _connected;
    private volatile bool _dirty = true;
    private string _details = "", _state = "";
    private DateTimeOffset _start = DateTimeOffset.UtcNow;
    /// <summary>Свой текст статуса (привилегия Frog Legacy). Пусто — стандартный.</summary>
    public string? StateOverride { get; set; }
    private readonly object _lock = new();

    public DiscordRpc(string appId)
    {
        _appId = appId?.Trim() ?? "";
        if (_appId != "") Task.Run(LoopAsync);
    }

    /// <summary>Обновить статус (timer = время «в игре» с этого момента).</summary>
    public void Set(string details, string state, bool resetTimer = false)
    {
        lock (_lock)
        {
            _details = details; _state = state;
            if (resetTimer) _start = DateTimeOffset.UtcNow;
            _dirty = true;
        }
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_connected) await ConnectAsync(ct);
                if (_connected && _dirty) { _dirty = false; await SendActivityAsync(ct); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn("Discord RPC: " + ex.Message);
                _connected = false; _dirty = true;
                try { _pipe?.Dispose(); } catch { }
            }
            try { await Task.Delay(_connected ? 1000 : 15000, ct); } catch { break; }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                to.CancelAfter(600);
                await pipe.ConnectAsync(to.Token);
            }
            catch { pipe.Dispose(); continue; }

            _pipe = pipe;
            await WriteFrameAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = _appId }.ToJsonString(), ct);
            var (_, json) = await ReadFrameAsync(ct);          // ответ READY
            if (json.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)) { Log.Warn("Discord RPC: " + json); pipe.Dispose(); return; }
            _connected = true; _dirty = true;
            return;
        }
    }

    private async Task SendActivityAsync(CancellationToken ct)
    {
        string d, s; DateTimeOffset start;
        lock (_lock) { d = _details; s = string.IsNullOrWhiteSpace(StateOverride) ? _state : StateOverride!; start = _start; }
        var activity = new JsonObject
        {
            ["details"] = d,
            ["state"] = s,
            ["timestamps"] = new JsonObject { ["start"] = start.ToUnixTimeSeconds() },
            // ключ «frog» — картинка, загруженная в Developer Portal → Rich Presence → Art Assets (необязательно)
            ["assets"] = new JsonObject { ["large_image"] = "frog", ["large_text"] = "FrogLauncher" }
        };
        var dc = BuiltIn.DiscordUrl;
        if (dc.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            activity["buttons"] = new JsonArray(new JsonObject { ["label"] = "Discord", ["url"] = dc });

        var payload = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString()
        };
        await WriteFrameAsync(1, payload.ToJsonString(), ct);
        await ReadFrameAsync(ct);
    }

    private async Task WriteFrameAsync(int op, string json, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + data.Length];
        BitConverter.GetBytes(op).CopyTo(buf, 0);
        BitConverter.GetBytes(data.Length).CopyTo(buf, 4);
        data.CopyTo(buf, 8);
        await _pipe!.WriteAsync(buf, ct);
        await _pipe.FlushAsync(ct);
    }

    private async Task<(int op, string json)> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await ReadExactAsync(header, ct);
        int op = BitConverter.ToInt32(header, 0), len = BitConverter.ToInt32(header, 4);
        if (len < 0 || len > 1_000_000) throw new InvalidDataException("Неверный кадр Discord");
        var body = new byte[len];
        await ReadExactAsync(body, ct);
        return (op, Encoding.UTF8.GetString(body));
    }

    private async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await _pipe!.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("Discord закрыл соединение");
            read += n;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pipe?.Dispose(); } catch { }
    }
}
