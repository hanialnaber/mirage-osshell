using System.IO;
using System.Threading;
using Mirage.Core;
using Mirage.Core.Agent;

namespace Mirage.App;

internal static class Program
{
    const string MutexName = "Local\\Mirage.Launcher.Instance";
    const string ShowEventName = "Local\\Mirage.Launcher.Show";

    [STAThread]
    static int Main(string[] args)
    {
        // Stdio MCP mode for agents that only speak stdio (Claude Desktop, Cursor, ...).
        // It forwards to the running Mirage over loopback, so the app must be running with the bridge on.
        if (args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
            return RunStdioProxy();

        bool background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask that instance to show itself.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); if (!background) ev.Set(); } catch { }
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        var app = new App();
        app.InitializeComponent();
        app.Startup += (_, _) =>
        {
            app.Boot(background);
            var t = new Thread(() =>
            {
                while (true)
                {
                    show.WaitOne();
                    app.Dispatcher.BeginInvoke(app.ShowLauncherFromSignal);
                }
            }) { IsBackground = true, Name = "Mirage show signal" };
            t.Start();
        };
        return app.Run();
    }

    static int RunStdioProxy()
    {
        Settings s;
        try { s = Settings.Load(Paths.Settings); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Mirage: couldn't read settings: " + ex.Message);
            return 1;
        }
        var stdin = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        return StdioProxy.RunAsync(stdin, stdout, s.Bridge.Port, s.Bridge.Token).GetAwaiter().GetResult();
    }
}
