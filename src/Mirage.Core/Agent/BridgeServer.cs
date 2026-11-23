using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirage.Core.Agent;

public sealed class HttpRequestLite
{
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; set; } = "";
}

public sealed record HttpResponseLite(int Status, string Body, string ContentType = "application/json");

/// <summary>
/// Loopback-only HTTP endpoint for AI agents. Serves MCP (Streamable HTTP, JSON responses)
/// at /mcp and a plain REST mirror at /v1/tools. Every call except /health needs the token.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    public const int MaxBody = 1 << 20;
    readonly AgentHost _host;
    readonly McpHandler _mcp;
    readonly Func<string> _token;
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    public int Port { get; private set; }
    public bool Running => _listener != null;
    public event Action<string>? Error;

    public BridgeServer(AgentHost host, McpHandler mcp, Func<string> token)
    {
        _host = host;
        _mcp = mcp;
        _token = token;
    }

    public void Start(int port)
    {
        Stop();
        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptLoop(_listener, _cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var req = await ReadRequestAsync(stream, timeout.Token);
            // Approval prompts can take a while; don't time out while the user decides.
            var res = req == null ? new HttpResponseLite(400, "{\"error\":\"bad request\"}") : await HandleAsync(req);
            await WriteResponseAsync(stream, res, ct);
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
    }

    public static async Task<HttpRequestLite?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return null;
            buffer.Write(chunk, 0, n);
            if (buffer.Length > 64 * 1024) return null;
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }
        var all = buffer.GetBuffer();
        var head = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var req = new HttpRequestLite { Method = first[0].ToUpperInvariant(), Path = first[1] };
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) req.Headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        int len = 0;
        if (req.Headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, out len) || len < 0 || len > MaxBody))
            return null;
        var bodyStart = headerEnd + 4;
        var have = (int)buffer.Length - bodyStart;
        var body = new byte[len];
        Array.Copy(all, bodyStart, body, 0, Math.Min(have, len));
        int got = Math.Min(have, len);
        while (got < len)
        {
            int n = await stream.ReadAsync(body.AsMemory(got, len - got), ct);
            if (n == 0) return null;
            got += n;
        }
        req.Body = Encoding.UTF8.GetString(body);
        return req;
    }

    static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle)
    {
        var idx = haystack.AsSpan(0, length).IndexOf(needle);
        return idx;
    }

    public static async Task WriteResponseAsync(Stream stream, HttpResponseLite res, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(res.Body);
        var reason = res.Status switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {res.Status} {reason}\r\n");
        if (body.Length > 0) sb.Append($"Content-Type: {res.ContentType}; charset=utf-8\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    bool HostAllowed(HttpRequestLite req)
    {
        if (!req.Headers.TryGetValue("Host", out var host)) return true; // HTTP/1.0 local tools
        var h = host.ToLowerInvariant();
        var p = ":" + Port;
        return h == "127.0.0.1" + p || h == "localhost" + p || h == "[::1]" + p || h == "127.0.0.1" || h == "localhost";
    }

    static bool OriginAllowed(HttpRequestLite req)
    {
        if (!req.Headers.TryGetValue("Origin", out var origin) || string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        return u.Host is "127.0.0.1" or "localhost" or "[::1]";
    }

    bool Authorized(HttpRequestLite req)
    {
        string? presented = null;
        if (req.Headers.TryGetValue("Authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = auth[7..].Trim();
        else if (req.Headers.TryGetValue("X-Mirage-Token", out var t))
            presented = t.Trim();
        return presented != null && Tokens.FixedTimeEquals(presented, _token());
    }

    static string ClientName(HttpRequestLite req) =>
        req.Headers.TryGetValue("X-Mirage-Client", out var c) && c.Length > 0 ? c[..Math.Min(40, c.Length)]
        : req.Headers.TryGetValue("User-Agent", out var ua) && ua.Length > 0 ? ua[..Math.Min(40, ua.Length)]
        : "local agent";

    public async Task<HttpResponseLite> HandleAsync(HttpRequestLite req)
    {
        var path = req.Path.Split('?')[0].TrimEnd('/');
        if (path.Length == 0) path = "/";

        if (!HostAllowed(req) || !OriginAllowed(req))
            return new HttpResponseLite(403, "{\"error\":\"forbidden origin\"}");

        if (path == "/health" && req.Method == "GET")
            return new HttpResponseLite(200, "{\"ok\":true,\"app\":\"mirage\"}");

        if (!Authorized(req))
            return new HttpResponseLite(401, "{\"error\":\"missing or wrong token. Copy it from Mirage > Settings > AI agents.\"}");

        var client = ClientName(req);

        if (path == "/mcp")
        {
            if (req.Method != "POST") return new HttpResponseLite(405, "{\"error\":\"use POST\"}");
            var reply = await _mcp.HandleAsync(req.Body, client);
            return reply == null ? new HttpResponseLite(202, "") : new HttpResponseLite(200, reply);
        }

        if (path == "/v1/tools" && req.Method == "GET")
        {
            var arr = new JsonArray();
            foreach (var t in _host.Tools.OrderBy(t => t.Name))
            {
                var perm = _host.EffectivePermission(t.Name);
                if (perm == Permission.Deny) continue;
                arr.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["needsApproval"] = perm == Permission.Ask,
                    ["inputSchema"] = t.InputSchema.DeepClone(),
                });
            }
            return new HttpResponseLite(200, new JsonObject { ["tools"] = arr }.ToJsonString());
        }

        if (path.StartsWith("/v1/tools/") && req.Method == "POST")
        {
            var name = Uri.UnescapeDataString(path["/v1/tools/".Length..]);
            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body);
                args = doc.RootElement.Clone();
            }
            catch (JsonException) { return new HttpResponseLite(400, "{\"error\":\"body must be JSON\"}"); }
            var res = await _host.CallAsync(name, args, client);
            JsonNode? result;
            try { result = JsonNode.Parse(res.Text); } catch { result = JsonValue.Create(res.Text); }
            if (result is JsonValue && res.Text.Length > 0 && res.Text[0] != '"' && !char.IsDigit(res.Text[0]) && res.Text[0] != '-' && res.Text is not ("true" or "false" or "null"))
                result = JsonValue.Create(res.Text);
            var body = new JsonObject { ["ok"] = !res.IsError, [res.IsError ? "error" : "result"] = result };
            return new HttpResponseLite(res.IsError ? 400 : 200, body.ToJsonString());
        }

        return new HttpResponseLite(404, "{\"error\":\"not found\"}");
    }
}
