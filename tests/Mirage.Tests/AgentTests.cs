using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mirage.Core;
using Mirage.Core.Agent;
using Xunit;

namespace Mirage.Tests;

sealed class FakeDesktop : IDesktop
{
    public Settings Settings { get; } = Settings.CreateDefault();
    public List<string> Calls { get; } = new();
    public string? Clipboard = "secret";

    public Task<IReadOnlyList<AppInfo>> ListAppsAsync(string? query, int limit) =>
        Task.FromResult<IReadOnlyList<AppInfo>>(new[] { new AppInfo("Notepad", "notepad", "desktop") });
    public Task<string> LaunchAppAsync(string nameOrId, string? args) { Calls.Add("launch:" + nameOrId); return Task.FromResult("launched " + nameOrId); }
    public Task OpenUrlAsync(string url) { Calls.Add("url:" + url); return Task.CompletedTask; }
    public Task OpenPathAsync(string path) { Calls.Add("path:" + path); return Task.CompletedTask; }
    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() => Task.FromResult<IReadOnlyList<WindowInfo>>(new[] { new WindowInfo(42, "Doc", "WINWORD", true) });
    public Task<WindowInfo?> GetActiveWindowAsync() => Task.FromResult<WindowInfo?>(new WindowInfo(42, "Doc", "WINWORD", true));
    public Task<bool> FocusWindowAsync(long handle) { Calls.Add("focus:" + handle); return Task.FromResult(handle == 42); }
    public Task<bool> WindowActionAsync(long handle, string action) => Task.FromResult(true);
    public Task<string?> GetClipboardAsync() => Task.FromResult(Clipboard);
    public Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public Task PasteTextAsync(string text) => Task.CompletedTask;
    public Task SendKeysAsync(string combo) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> SearchFilesAsync(string query, int limit) => Task.FromResult<IReadOnlyList<string>>(new[] { "C:\\a.txt" });
    public Task<string> RunWorkspaceAsync(string name) => Task.FromResult("opened 3 items");
    public Task<string> RunCommandAsync(string keyword, string? query) => Task.FromResult("ran");
    public Task AddNoteAsync(string text) { Calls.Add("note:" + text); return Task.CompletedTask; }
    public Task<string> ReadNotesAsync(int maxChars) => Task.FromResult("notes");
    public Task NotifyAsync(string title, string message) => Task.CompletedTask;
    public Task LockAsync() => Task.CompletedTask;
    public Task ShowLauncherAsync(string? query) => Task.CompletedTask;
}

sealed class FakeConfirmer : IConfirmer
{
    public ConfirmResult Answer = ConfirmResult.Deny;
    public int Asked;
    public Task<ConfirmResult> ConfirmAsync(string client, string tool, string summary) { Asked++; return Task.FromResult(Answer); }
}

public class AgentHostTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Allowed_tool_runs_without_asking()
    {
        var d = new FakeDesktop(); var c = new FakeConfirmer();
        var host = new AgentHost(d, c);
        var r = await host.CallAsync("launch_app", J("{\"app\":\"notepad\"}"), "test");
        Assert.False(r.IsError);
        Assert.Equal(0, c.Asked);
        Assert.Contains("launch:notepad", d.Calls);
    }

    [Fact]
    public async Task Ask_tool_is_blocked_when_user_declines()
    {
        var d = new FakeDesktop(); var c = new FakeConfirmer { Answer = ConfirmResult.Deny };
        var host = new AgentHost(d, c);
        var r = await host.CallAsync("clipboard_read", J("{}"), "test");
        Assert.True(r.IsError);
        Assert.Equal(1, c.Asked);
        Assert.DoesNotContain("secret", r.Text);
    }

    [Fact]
    public async Task Always_allow_persists_permission()
    {
        var d = new FakeDesktop(); var c = new FakeConfirmer { Answer = ConfirmResult.AlwaysAllow };
        var host = new AgentHost(d, c);
        bool changed = false; host.SettingsChanged += () => changed = true;
        var r = await host.CallAsync("clipboard_read", J("{}"), "test");
        Assert.Equal("secret", r.Text);
        Assert.True(changed);
        await host.CallAsync("clipboard_read", J("{}"), "test");
        Assert.Equal(1, c.Asked);
    }

    [Fact]
    public async Task Denied_tool_never_runs_and_is_hidden()
    {
        var d = new FakeDesktop(); var c = new FakeConfirmer();
        d.Settings.Bridge.Permissions["add_note"] = Permission.Deny;
        var host = new AgentHost(d, c);
        var r = await host.CallAsync("add_note", J("{\"text\":\"x\"}"), "test");
        Assert.True(r.IsError);
        Assert.Empty(d.Calls);
        var mcp = new McpHandler(host);
        var list = await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", "t");
        Assert.DoesNotContain("add_note", list);
    }

    [Fact]
    public async Task Open_url_rejects_non_http_schemes()
    {
        var d = new FakeDesktop(); var host = new AgentHost(d, new FakeConfirmer());
        var r = await host.CallAsync("open_url", J("{\"url\":\"file:///C:/Windows/System32/calc.exe\"}"), "t");
        Assert.True(r.IsError);
        Assert.Empty(d.Calls);
    }

    [Fact]
    public async Task Missing_args_are_reported()
    {
        var host = new AgentHost(new FakeDesktop(), new FakeConfirmer());
        var r = await host.CallAsync("launch_app", J("{}"), "t");
        Assert.True(r.IsError);
        Assert.Contains("app", r.Text);
    }
}

public class McpTests
{
    [Fact]
    public async Task Initialize_list_and_call()
    {
        var d = new FakeDesktop();
        var mcp = new McpHandler(new AgentHost(d, new FakeConfirmer()), "1.2.3");
        var init = JsonDocument.Parse((await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"version\":\"1\"}}}", "t"))!);
        Assert.Equal("2025-03-26", init.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("mirage", init.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        Assert.Null(await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", "t"));

        var list = JsonDocument.Parse((await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"tools/list\"}", "t"))!);
        var names = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("launch_app", names);
        Assert.Contains("run_workspace", names);
        Assert.Equal("a", list.RootElement.GetProperty("id").GetString());

        var call = JsonDocument.Parse((await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"focus_window\",\"arguments\":{\"handle\":42}}}", "t"))!);
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("focused", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Unknown_method_and_parse_errors()
    {
        var mcp = new McpHandler(new AgentHost(new FakeDesktop(), new FakeConfirmer()));
        Assert.Contains("-32601", await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"nope\"}", "t"));
        Assert.Contains("-32700", await mcp.HandleAsync("{not json", "t"));
    }
}

public class BridgeServerTests
{
    static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    static async Task<(int status, string body)> Send(int port, string raw)
    {
        using var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.UTF8.GetBytes(raw));
        using var reader = new StreamReader(s);
        var all = await reader.ReadToEndAsync();
        var status = int.Parse(all.Split(' ')[1]);
        var body = all[(all.IndexOf("\r\n\r\n") + 4)..];
        return (status, body);
    }

    static string Post(int port, string path, string body, string? token, string? origin = null) =>
        $"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\n" +
        (token != null ? $"Authorization: Bearer {token}\r\n" : "") +
        (origin != null ? $"Origin: {origin}\r\n" : "") +
        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    [Fact]
    public async Task End_to_end_over_tcp()
    {
        var d = new FakeDesktop();
        var host = new AgentHost(d, new FakeConfirmer());
        using var server = new BridgeServer(host, new McpHandler(host), () => d.Settings.Bridge.Token);
        var port = FreePort();
        server.Start(port);
        var token = d.Settings.Bridge.Token;

        var health = await Send(port, $"GET /health HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");
        Assert.Equal(200, health.status);

        var noAuth = await Send(port, Post(port, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", null));
        Assert.Equal(401, noAuth.status);

        var wrong = await Send(port, Post(port, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", "mrg_wrong"));
        Assert.Equal(401, wrong.status);

        var browser = await Send(port, Post(port, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", token, "https://evil.example"));
        Assert.Equal(403, browser.status);

        var rebind = await Send(port, $"GET /health HTTP/1.1\r\nHost: evil.example:{port}\r\n\r\n");
        Assert.Equal(403, rebind.status);

        var ping = await Send(port, Post(port, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", token));
        Assert.Equal(200, ping.status);
        Assert.Contains("\"result\"", ping.body);

        var note = await Send(port, Post(port, "/mcp", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", token));
        Assert.Equal(202, note.status);

        var rest = await Send(port, Post(port, "/v1/tools/add_note", "{\"text\":\"from agent\"}", token));
        Assert.Equal(200, rest.status);
        Assert.Contains("note:from agent", d.Calls);

        var restList = await Send(port, $"GET /v1/tools HTTP/1.1\r\nHost: localhost:{port}\r\nX-Mirage-Token: {token}\r\n\r\n");
        Assert.Equal(200, restList.status);
        Assert.Contains("needsApproval", restList.body);

        var apps = await Send(port, Post(port, "/v1/tools/list_apps", "{}", token));
        using var appsDoc = JsonDocument.Parse(apps.body);
        Assert.True(appsDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Array, appsDoc.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Stdio_proxy_round_trip()
    {
        var d = new FakeDesktop();
        var host = new AgentHost(d, new FakeConfirmer());
        using var server = new BridgeServer(host, new McpHandler(host), () => d.Settings.Bridge.Token);
        var port = FreePort();
        server.Start(port);
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"launch_app\",\"arguments\":{\"app\":\"calc\"}}}\n");
        var output = new StringWriter();
        await StdioProxy.RunAsync(input, output, port, d.Settings.Bridge.Token);
        var lines = output.ToString().Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("serverInfo", lines[0]);
        Assert.Contains("launched calc", lines[1]);
    }

    [Fact]
    public async Task Stdio_proxy_reports_when_mirage_is_off()
    {
        var output = new StringWriter();
        await StdioProxy.RunAsync(new StringReader("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}\n"), output, FreePort(), "mrg_x");
        Assert.Contains("isn't reachable", output.ToString());
        Assert.Contains("\"id\":7", output.ToString());
    }
}
