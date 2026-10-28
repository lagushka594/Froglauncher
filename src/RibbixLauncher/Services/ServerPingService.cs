using RibbixLauncher.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DnsClient;

namespace RibbixLauncher.Services;

/// <summary>Живой статус сервера (ответ Server List Ping).</summary>
public sealed record ServerStatus(
    bool Online, int Players, int MaxPlayers, string Version, int Protocol, string Motd, byte[]? Icon, int PingMs, string? Error)
{
    public static ServerStatus Offline(string error) => new(false, 0, 0, "", 0, "", null, -1, error);
}

/// <summary>
/// Server List Ping (протокол Minecraft 1.7+): handshake → status request → JSON с онлайном, версией, MOTD и иконкой,
/// затем ping/pong для задержки. Адрес «mc.example.com» сначала ищем в SRV-записи _minecraft._tcp, как делает сама игра.
/// </summary>
public static class ServerPing
{
    private static readonly LookupClient Dns = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(3), Retries = 1, UseCache = true });

    public static async Task<(string Host, int Port)> ResolveAsync(string host, int port, CancellationToken ct)
    {
        if (port != 25565 || System.Net.IPAddress.TryParse(host, out _)) return (host, port);
        try
        {
            var r = await Dns.QueryAsync($"_minecraft._tcp.{host}", QueryType.SRV, cancellationToken: ct);
            if (r.Answers.SrvRecords().OrderBy(s => s.Priority).FirstOrDefault() is { } srv)
                return (srv.Target.Value.TrimEnd('.'), srv.Port);
        }
        catch (Exception ex) when (ex is DnsResponseException or SocketException or TimeoutException) { }
        return (host, port);
    }

    public static async Task<ServerStatus> PingAsync(string address, CancellationToken ct, int timeoutMs = 5000)
    {
        var (host, port) = ParseAddress(address);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            var (h, p) = await ResolveAsync(host, port, cts.Token);
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(h, p, cts.Token);
            await using var s = tcp.GetStream();

            // Handshake: id 0, protocol -1 (любой), адрес, порт, next state 1 (status). Адрес — исходный, как ввёл игрок.
            var hs = new MemoryStream();
            WriteVarInt(hs, 0x00);
            WriteVarInt(hs, -1);
            WriteString(hs, host);
            hs.Write(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((ushort)p)));
            WriteVarInt(hs, 1);
            await SendPacketAsync(s, hs.ToArray(), cts.Token);
            await SendPacketAsync(s, new byte[] { 0x00 }, cts.Token);

            var packet = await ReadPacketAsync(s, cts.Token);
            using var pr = new MemoryStream(packet);
            if (ReadVarInt(pr) != 0x00) return ServerStatus.Offline(L.T("Неожиданный ответ сервера"));
            var json = JsonNode.Parse(ReadString(pr))!;

            // Ping/pong — задержка.
            var sw = Stopwatch.StartNew();
            var ping = new MemoryStream();
            WriteVarInt(ping, 0x01);
            ping.Write(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(DateTime.UtcNow.Ticks)));
            await SendPacketAsync(s, ping.ToArray(), cts.Token);
            int ms;
            try { await ReadPacketAsync(s, cts.Token); ms = (int)sw.ElapsedMilliseconds; }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { ms = (int)sw.ElapsedMilliseconds; } // часть серверов не отвечает на ping

            byte[]? icon = null;
            if (json["favicon"]?.GetValue<string>() is { } fav && fav.IndexOf("base64,", StringComparison.Ordinal) is var i and >= 0)
            {
                try { icon = Convert.FromBase64String(fav[(i + 7)..].Replace("\n", "")); } catch (FormatException) { }
            }

            return new ServerStatus(true,
                json["players"]?["online"]?.GetValue<int>() ?? 0,
                json["players"]?["max"]?.GetValue<int>() ?? 0,
                StripColors(json["version"]?["name"]?.GetValue<string>() ?? ""),
                json["version"]?["protocol"]?.GetValue<int>() ?? 0,
                MotdText(json["description"]), icon, ms, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ServerStatus.Offline(L.T("Сервер не ответил за 5 секунд"));
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or System.Text.Json.JsonException or InvalidDataException)
        {
            return ServerStatus.Offline(L.T("Сервер недоступен"));
        }
    }

    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        var colon = address.LastIndexOf(':');
        if (colon > 0 && !address.Contains(']') && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and < 65536)
            return (address[..colon], port);
        return (address, 25565);
    }

    /// <summary>MOTD: строка или чат-компонент {text, extra[]}; §-коды цвета убираем.</summary>
    internal static string MotdText(JsonNode? d)
    {
        var sb = new StringBuilder();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonValue v when v.TryGetValue(out string? s): sb.Append(s); break;
                case JsonObject o:
                    if (o["text"]?.GetValue<string>() is { } t) sb.Append(t);
                    foreach (var e in o["extra"]?.AsArray() ?? new JsonArray()) Walk(e);
                    break;
                case JsonArray a: foreach (var e in a) Walk(e); break;
            }
        }
        Walk(d);
        var text = sb.ToString();
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length) { i++; continue; }
            clean.Append(text[i]);
        }
        return string.Join("\n", clean.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2));
    }

    internal static string StripColors(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length) { i++; continue; }
            sb.Append(text[i]);
        }
        return sb.ToString().Trim();
    }

    private static async Task SendPacketAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        var buf = new MemoryStream();
        WriteVarInt(buf, payload.Length);
        buf.Write(payload);
        await s.WriteAsync(buf.ToArray(), ct);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream s, CancellationToken ct)
    {
        var len = await ReadVarIntAsync(s, ct);
        if (len is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException(L.T("Некорректная длина пакета"));
        var data = new byte[len];
        await s.ReadExactlyAsync(data, ct);
        return data;
    }

    internal static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        while (true)
        {
            if ((v & ~0x7Fu) == 0) { s.WriteByte((byte)v); return; }
            s.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
    }

    internal static int ReadVarInt(Stream s)
    {
        int result = 0, shift = 0;
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) throw new InvalidDataException(L.T("VarInt слишком длинный"));
        }
    }

    private static async Task<int> ReadVarIntAsync(Stream s, CancellationToken ct)
    {
        int result = 0, shift = 0;
        var one = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(one, ct);
            result |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) throw new InvalidDataException(L.T("VarInt слишком длинный"));
        }
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes);
    }

    private static string ReadString(Stream s)
    {
        var len = ReadVarInt(s);
        var bytes = new byte[len];
        s.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
