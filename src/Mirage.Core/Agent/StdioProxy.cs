using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Mirage.Core.Agent;

/// <summary>
/// "Mirage.exe --mcp" mode: speaks MCP over stdio (one JSON message per line) and forwards
/// each message to the running Mirage instance's loopback endpoint. Lets apps that only
/// support stdio MCP servers (e.g. Claude Desktop, Cursor) use Mirage.
/// </summary>
public static class StdioProxy
{
    public static async Task<int> RunAsync(TextReader input, TextWriter output, int port, string token, CancellationToken ct = default)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("X-Mirage-Client", "stdio agent");

        string? line;
        while ((line = await input.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? reply;
            try
            {
                using var content = new StringContent(line, Encoding.UTF8, "application/json");
                using var res = await http.PostAsync("mcp", content, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if ((int)res.StatusCode == 202 || body.Length == 0) continue;
                reply = res.IsSuccessStatusCode ? body : ErrorFor(line, $"Mirage refused the request ({(int)res.StatusCode}): {body}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                reply = ErrorFor(line, "Mirage timed out (the approval prompt may have been left open).");
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException)
            {
                reply = ErrorFor(line, "Mirage isn't reachable. Start Mirage and turn on Settings > AI agents.");
            }
            if (reply != null)
            {
                await output.WriteLineAsync(reply.AsMemory(), ct);
                await output.FlushAsync(ct);
            }
        }
        return 0;
    }

    static string? ErrorFor(string requestLine, string message)
    {
        JsonNode? id = null;
        try
        {
            var n = JsonNode.Parse(requestLine) as JsonObject;
            if (n == null || !n.ContainsKey("id")) return null; // notification: no reply
            id = n["id"]?.DeepClone();
        }
        catch { }
        return McpHandler.Error(id, -32000, message);
    }
}
