using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirage.Core.Agent;

/// <summary>
/// Minimal Model Context Protocol server (JSON-RPC 2.0): initialize, ping, tools/list, tools/call.
/// Used by both the Streamable HTTP endpoint and the stdio proxy.
/// </summary>
public sealed class McpHandler
{
    public const string ServerName = "mirage";
    public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
    static readonly JsonSerializerOptions Out = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    readonly AgentHost _host;
    readonly string _version;

    public McpHandler(AgentHost host, string version = "1.0.0")
    {
        _host = host;
        _version = version;
    }

    /// <summary>Returns the JSON response, or null when nothing should be sent (notifications).</summary>
    public async Task<string?> HandleAsync(string body, string client)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }

        if (node is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
            {
                var r = await HandleOneAsync(item as JsonObject, client);
                if (r != null) responses.Add(r);
            }
            return responses.Count == 0 ? null : responses.ToJsonString(Out);
        }
        var single = await HandleOneAsync(node as JsonObject, client);
        return single?.ToJsonString(Out);
    }

    async Task<JsonObject?> HandleOneAsync(JsonObject? req, string client)
    {
        if (req == null) return ErrorNode(null, -32600, "Invalid request");
        var id = req["id"]?.DeepClone();
        var method = req["method"]?.GetValue<string>();
        bool isNotification = !req.ContainsKey("id");
        if (method == null)
            return isNotification ? null : ErrorNode(id, -32600, "Invalid request");

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var requested = req["params"]?["protocolVersion"]?.GetValue<string>();
                    var version = requested != null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Mirage for Windows", ["version"] = _version },
                        ["instructions"] = "Mirage lets you operate the user's Windows desktop: launch apps, open URLs, switch windows, use the clipboard, run saved workspaces and the user's own commands. Some tools need the user's approval each time; if a call is declined, do not retry it.",
                    });
                }
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                {
                    var tools = new JsonArray();
                    foreach (var t in _host.Tools.OrderBy(t => t.Name))
                    {
                        if (_host.EffectivePermission(t.Name) == Permission.Deny) continue;
                        tools.Add(new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description + (_host.EffectivePermission(t.Name) == Permission.Ask ? " (The user approves each call.)" : ""),
                            ["inputSchema"] = t.InputSchema.DeepClone(),
                            ["annotations"] = new JsonObject { ["readOnlyHint"] = t.ReadOnly },
                        });
                    }
                    return Result(id, new JsonObject { ["tools"] = tools });
                }
                case "tools/call":
                {
                    var name = req["params"]?["name"]?.GetValue<string>() ?? "";
                    var argsNode = req["params"]?["arguments"];
                    using var doc = JsonDocument.Parse(argsNode?.ToJsonString() ?? "{}");
                    var res = await _host.CallAsync(name, doc.RootElement.Clone(), client);
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = res.Text }),
                        ["isError"] = res.IsError,
                    });
                }
                case "resources/list":
                    return Result(id, new JsonObject { ["resources"] = new JsonArray() });
                case "prompts/list":
                    return Result(id, new JsonObject { ["prompts"] = new JsonArray() });
                default:
                    return isNotification ? null : ErrorNode(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            return isNotification ? null : ErrorNode(id, -32603, ex.Message);
        }
    }

    static JsonObject Result(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    static JsonObject ErrorNode(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public static string Error(JsonNode? id, int code, string message) => ErrorNode(id, code, message).ToJsonString(Out);
}
