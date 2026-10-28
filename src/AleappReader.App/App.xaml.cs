using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AleappReader.Core;

namespace AleappReader.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--mcp", var root, "--ai-db", var db, "--run", var run]) { Shutdown(RunMcp(root, db, run)); return; }
        if (e.Args is ["--mcp-bridge"]) { Shutdown(RunBridge()); return; }
        DispatcherUnhandledException += OnUnhandled;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(CopyText));
        GridExport.Install();
        // frames, audio and prompts an earlier run could not delete (copies of evidence); older than any job's time limit
        _ = Task.Run(() => { try { TempWork.Sweep(TimeSpan.FromHours(6)); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } });
        try { ValueFormatter.Zone = TimeZoneInfo.FindSystemTimeZoneById(Settings.Current.TimeZoneId); }
        catch (TimeZoneNotFoundException) { }

        var window = new MainWindow();
        window.Show();

        // Path arguments open in turn (several investigation cases side by side); a reader shipped beside a portable case
        // zip opens that zip by itself.
        string[] start = e.Args.Length > 0 ? e.Args : PortableCase.FindBeside(AppContext.BaseDirectory) is { } zip ? [zip] : [];
        OpenAll(window.ViewModel, start);
    }

    static async void OpenAll(ViewModels.MainViewModel vm, IEnumerable<string> paths)
    {
        foreach (var p in paths) await vm.OpenCaseAsync(p);
    }

    /// <summary>LEXIS as a read-only MCP server on stdio for the Claude CLI: no window, the case opened read-only, every
    /// tool answer logged in the case's AI base (its _reader_ai.db, or one of LEXIS's own in %LOCALAPPDATA%). Refused
    /// unless the AI settings allow the Claude CLI; other programs go through the server inside the open LEXIS.</summary>
    static int RunMcp(string manifest, string db, string run)
    {
        // the client shows stderr in its log: say why the server ends
        if (run.StartsWith(McpServer.ExternalRun, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("LEXIS: this configuration is from an older LEXIS. Add LEXIS again from AI settings › MCP for other programs.");
            return 3;
        }
        if (!Settings.Current.Ai.ClaudeAllowed) { Console.Error.WriteLine("LEXIS: the Claude CLI is not allowed in LEXIS's AI settings."); return 3; }
        try
        {
            using var s = CaseSession.Open(manifest);
            if (!AiStore.Allowed(s, db)) { Console.Error.WriteLine("LEXIS: --ai-db is not this case's AI base: " + db); return 2; }
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            Task.Run(() => new McpServer(s, new AiStore(db), run).Run(input, output)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e) // no window to show it in: the client sees the server end, and why on stderr
        {
            Console.Error.WriteLine($"LEXIS: cannot serve {manifest}: {e.Message}");
            return 1;
        }
    }

    /// <summary>For an MCP client that only starts servers on stdio: no window, no case opened here, every message passed
    /// to the MCP server of the LEXIS that is open.</summary>
    static int RunBridge()
    {
        var ai = Settings.Current.Ai;
        if (ai.McpToken == "") { Console.Error.WriteLine("LEXIS: the MCP server has never been turned on. Turn it on in LEXIS › AI settings › MCP for other programs."); return 3; }
        using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
        Task.Run(() => McpHttpServer.Bridge(input, output, ai.McpUrl)).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Right-click on any text that has no menu of its own (labels, headers, values): "Copy" puts it on the clipboard.
    /// Text boxes keep their own menu, record grids and lists theirs.</summary>
    static void CopyText(object sender, MouseButtonEventArgs e)
    {
        var o = e.OriginalSource as DependencyObject;
        while (o is TextElement t) o = t.Parent; // a run or a link inside the text
        if (o is not TextBlock tb || string.IsNullOrWhiteSpace(tb.Text)) return;
        for (var p = (DependencyObject?)tb; p is not null; p = p is Visual ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is FrameworkElement { ContextMenu: not null } or ButtonBase or TextBoxBase) return;
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => Clipboard.SetText(tb.Text);
        _ = new ContextMenu { Items = { copy }, PlacementTarget = tb, IsOpen = true };
        e.Handled = true;
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // the box shows only the message: the stack goes to a log beside the settings, to find where it came from
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        MessageBox.Show(e.Exception.Message, "LEXIS", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
