using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>
/// Sets up whisper.cpp: its program from the GitHub releases (the variant chosen) or the one the examiner already has,
/// and its models from Hugging Face, each downloaded only after a confirmation and kept only once its published SHA-256
/// matches. Also adds and deletes models. When a step fails it says which and why, and points to installing by hand.
/// </summary>
public sealed class WhisperSetupWindow : Window
{
    string? _current; // the whisper-cli.exe the examiner already has
    readonly TextBox _folder = new() { Text = WhisperSetup.DefaultFolder, VerticalContentAlignment = VerticalAlignment.Center };
    readonly StackPanel _programs = new(), _models = new();
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 4) };
    readonly ProgressBar _bar = new() { Height = 6, Maximum = 100, Visibility = Visibility.Collapsed };
    readonly Button _install = new() { Content = "Install", IsDefault = true, Padding = new(12, 4, 12, 4), Margin = new(0, 0, 6, 0), IsEnabled = false };
    readonly Button _close = new() { Content = "Close", Padding = new(12, 4, 12, 4) };
    readonly List<(RadioButton Box, WhisperSetup.Build? Build)> _programChoices = [];
    readonly List<(CheckBox Box, WhisperSetup.Download M)> _modelChoices = [];
    List<WhisperSetup.Download> _available = [];
    CancellationTokenSource? _run;
    (string Exe, string? Model)? _result;

    WhisperSetupWindow(string current)
    {
        Title = "whisper.cpp setup";
        Width = 640;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        _current = File.Exists(current) ? current : null;

        var browseFolder = new Button { Content = "Choose…", Padding = new(8, 2, 8, 2), Margin = new(6, 0, 0, 0) };
        browseFolder.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for whisper.cpp", InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : "" };
            if (d.ShowDialog(this) == true) { _folder.Text = d.FolderName; ShowModels(); }
        };
        _folder.LostFocus += (_, _) => ShowModels();
        var have = new Button { Content = "I already have whisper.cpp (browse)…", Padding = new(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 0) };
        have.Click += (_, _) => BrowseProgram();
        _install.Click += async (_, _) => await Install();
        _close.Click += (_, _) => { if (_run is { } r) r.Cancel(); else Close(); };

        var folderRow = new DockPanel { Margin = new(0, 4, 0, 0) };
        DockPanel.SetDock(browseFolder, Dock.Right);
        folderRow.Children.Add(browseFolder);
        folderRow.Children.Add(_folder);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "🎙 whisper.cpp setup", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(Muted("whisper.cpp transcribes audio on this computer; nothing leaves it. LEXIS does not include it: the program comes from " +
                                "the whisper.cpp releases on GitHub (ggml-org/whisper.cpp), the models from Hugging Face (ggerganov/whisper.cpp). " +
                                "Each file is downloaded only after you confirm, and kept only if its SHA-256 is the one published there; " +
                                "install.log in the folder lists what was installed and from where."));
        body.Children.Add(Header("Program"));
        body.Children.Add(_programs);
        body.Children.Add(have);
        body.Children.Add(Header("Folder (for a program downloaded here)"));
        body.Children.Add(folderRow);
        body.Children.Add(Header("Models"));
        body.Children.Add(Muted("ggml-large-v3-turbo-q5_0.bin is a good balance of accuracy and speed; small and base are faster and less accurate. " +
                                "Models with .en understand English only."));
        body.Children.Add(_models);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        buttons.Children.Add(_install);
        buttons.Children.Add(_close);
        var foot = new StackPanel();
        foot.Children.Add(_status);
        foot.Children.Add(_bar);
        foot.Children.Add(buttons);

        var root = new DockPanel { Margin = new(14) };
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await Load();
        Closing += (_, e) => e.Cancel = _run is not null;
    }

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 2) };

    static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    static string Size(long b) => b >= 1L << 30 ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576.0:N0} MB";

    async Task Load()
    {
        _status.Text = "Reading the whisper.cpp releases (GitHub) and models (Hugging Face)…";
        try
        {
            var builds = WhisperSetup.Builds(CancellationToken.None);
            var models = WhisperSetup.Models(CancellationToken.None);
            ShowPrograms(await builds);
            _available = await models;
            _status.Text = "";
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ShowPrograms([]);
            _status.Text = "The lists could not be read: " + e.Message;
            Manual("reading the list of releases and models", e.Message);
        }
        ShowModels();
        _install.IsEnabled = true;
    }

    void ShowPrograms(List<WhisperSetup.Build> builds)
    {
        _programs.Children.Clear();
        _programChoices.Clear();
        var nvidia = WhisperSetup.HasNvidia;
        if (_current is not null) Add(null, $"Keep the one I have: {_current}");
        foreach (var b in builds.Where(b => nvidia || !b.Variant.StartsWith("NVIDIA")))
            Add(b, $"Download {b.Variant} · {b.File.Name} · {b.Tag} · {Size(b.File.Size)}");
        if (builds.Count > 0 && !nvidia) _programs.Children.Add(Muted("No NVIDIA graphics card found: the CUDA builds are not offered."));
        var pick = _programChoices.FirstOrDefault(c => c.Build is null).Box
                   ?? _programChoices.FirstOrDefault(c => c.Build?.Variant == (nvidia ? "NVIDIA CUDA 12" : "CPU")).Box
                   ?? _programChoices.FirstOrDefault().Box;
        if (pick is not null) pick.IsChecked = true;

        void Add(WhisperSetup.Build? b, string text)
        {
            var r = new RadioButton { Content = new TextBlock { Text = text }, GroupName = "program", Margin = new(0, 2, 0, 2) };
            r.Checked += (_, _) => ShowModels();
            _programChoices.Add((r, b));
            _programs.Children.Add(r);
        }
    }

    void BrowseProgram()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "whisper.cpp program", Filter = "whisper-cli.exe|whisper-cli.exe|Programs (*.exe)|*.exe" };
        if (d.ShowDialog(this) != true) return;
        _current = d.FileName;
        ShowPrograms(_programChoices.Select(c => c.Build).OfType<WhisperSetup.Build>().ToList());
    }

    WhisperSetup.Build? ChosenBuild => _programChoices.FirstOrDefault(c => c.Box.IsChecked == true).Build;

    /// <summary>The program the models go with: the one kept, or where the chosen download will put it.</summary>
    string? Exe() => _programChoices.Any(c => c.Box.IsChecked == true) && ChosenBuild is null ? _current
                   : Path.IsPathRooted(_folder.Text.Trim()) ? Path.Combine(_folder.Text.Trim(), "bin", "whisper-cli.exe") : null;

    /// <summary>The models found for <paramref name="exe"/>, by name: where whisper.cpp looks for them and where they are downloaded.</summary>
    static Dictionary<string, string> Installed(string? exe) =>
        exe is null ? [] : WhisperCpp.ModelFolders(exe).Append(WhisperSetup.ModelFolder(exe)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "ggml-*.bin")).GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);

    void ShowModels()
    {
        var ticked = _modelChoices.Where(c => c.Box.IsChecked == true).Select(c => c.M.Name).ToHashSet();
        _models.Children.Clear();
        _modelChoices.Clear();
        var installed = Installed(Exe());
        var list = _available.Concat(installed.Keys.Where(n => _available.All(a => a.Name != n)).Select(n => new WhisperSetup.Download(n, new FileInfo(installed[n]).Length, "", "")));
        foreach (var m in list)
        {
            var row = new DockPanel { Margin = new(0, 1, 0, 1) };
            if (installed.TryGetValue(m.Name, out var path))
            {
                var del = new Button { Content = "Delete", Padding = new(8, 0, 8, 0), Margin = new(6, 0, 0, 0), ToolTip = path };
                del.Click += (_, _) => Delete(path);
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                row.Children.Add(new TextBlock { Text = $"✓ {m.Name} · {Size(m.Size)} · installed", Margin = new(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                var box = new CheckBox { Content = new TextBlock { Text = $"{m.Name} · {Size(m.Size)}" }, IsChecked = ticked.Contains(m.Name) };
                _modelChoices.Add((box, m));
                row.Children.Add(box);
            }
            _models.Children.Add(row);
        }
        if (installed.Count == 0 && ticked.Count == 0 && _modelChoices.FirstOrDefault(c => c.M.Name == "ggml-large-v3-turbo-q5_0.bin").Box is { } best)
            best.IsChecked = true;
    }

    void Delete(string path)
    {
        if (MessageBox.Show(this, $"Delete the model {Path.GetFileName(path)} ({Size(new FileInfo(path).Length)})?\n\n{path}", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Not deleted: " + e.Message, Title); }
        ShowModels();
    }

    async Task Install()
    {
        var build = ChosenBuild;
        var wanted = _modelChoices.Where(c => c.Box.IsChecked == true).Select(c => c.M).ToList();
        var folder = _folder.Text.Trim();
        if (build is null && _current is null) { MessageBox.Show(this, "Choose a program to download, or give the whisper-cli.exe you have.", Title); return; }
        if (build is not null && !Path.IsPathRooted(folder)) { MessageBox.Show(this, "Give the full path of the folder.", Title); return; }
        if (build is null && wanted.Count == 0)
        {
            _result = (_current!, Installed(_current).Keys.FirstOrDefault());
            DialogResult = true;
            return;
        }
        var files = (build is null ? [] : new[] { build.File }).Concat(wanted).ToList();
        var exeTarget = Exe()!;
        var sources = (build is null ? [] : new[] { "GitHub (ggml-org/whisper.cpp)" }).Concat(wanted.Count == 0 ? [] : new[] { "Hugging Face (ggerganov/whisper.cpp)" });
        if (MessageBox.Show(this,
                "Download, check against the published SHA-256 and install:\n\n" +
                string.Join("\n", files.Select(f => $"  • {f.Name} · {Size(f.Size)}")) +
                $"\n\nTotal {Size(files.Sum(f => f.Size))}, from {string.Join(" and ", sources)}.\n" +
                (build is null ? "" : $"Program: {Path.Combine(folder, "bin")}\n") +
                (wanted.Count == 0 ? "" : $"Models: {WhisperSetup.ModelFolder(exeTarget)}\n") + "\nContinue?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;

        _run = new CancellationTokenSource();
        _install.IsEnabled = false;
        _close.Content = "Stop";
        _bar.Visibility = Visibility.Visible;
        var step = "";
        try
        {
            var ct = _run.Token;
            var exe = _current;
            if (build is not null)
            {
                step = $"downloading {build.File.Name}";
                var zip = await Fetch(build.File, folder, step, ct);
                step = $"unpacking {build.File.Name}";
                _status.Text = step + "…";
                exe = await Task.Run(() => WhisperSetup.Unpack(zip, folder), ct);
                WhisperSetup.Log(folder, build.File, exe);
                File.Delete(zip);
                step = "the test run of whisper-cli";
                _status.Text = "Running whisper-cli…";
                await WhisperSetup.Check(exe, null, ct);
            }
            var models = WhisperSetup.ModelFolder(exe!);
            foreach (var m in wanted)
            {
                step = $"downloading {m.Name}";
                var path = await Fetch(m, models, step, ct);
                WhisperSetup.Log(Path.GetDirectoryName(models)!, m, path);
            }
            var model = wanted.FirstOrDefault()?.Name ?? Installed(exe).Keys.FirstOrDefault();
            if (model is not null)
            {
                step = $"the test run of whisper-cli with {model}";
                _status.Text = $"Testing {model} on one second of silence…";
                await WhisperSetup.Check(exe!, model, ct);
            }
            _result = (exe!, model);
            _run = null;
            MessageBox.Show(this, $"whisper.cpp is set up: {exe}" + (model is null ? "\n\nNo model yet: download one here." : $"\n\nTranscription uses it with {model}."), Title);
            DialogResult = true;
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception e)
        {
            _status.Text = $"Stopped at {step}: {e.Message}";
            _run = null;
            Manual(step, e.Message);
        }
        finally
        {
            _run = null;
            _install.IsEnabled = true;
            _close.Content = "Close";
            _bar.Visibility = Visibility.Collapsed;
            if (IsVisible) ShowModels();
        }
    }

    Task<string> Fetch(WhisperSetup.Download d, string folder, string step, CancellationToken ct)
    {
        _bar.Value = 0;
        var progress = new Progress<long>(n =>
        {
            _bar.Value = n * 100.0 / Math.Max(1, d.Size);
            _status.Text = $"{char.ToUpper(step[0])}{step[1..]}: {Size(n)} of {Size(d.Size)}";
        });
        return WhisperSetup.Fetch(d, folder, progress, ct);
    }

    /// <summary>Says which step failed and why, and how to install by hand; Yes opens the two download pages.</summary>
    void Manual(string step, string why)
    {
        if (MessageBox.Show(this,
                $"The automatic setup stopped at {step}:\n{why}\n\n" +
                "Install it by hand: download a Windows zip of whisper.cpp (whisper-bin-x64.zip) and unpack it, put a ggml model " +
                "(e.g. ggml-large-v3-turbo-q5_0.bin) in a models folder beside whisper-cli.exe, then choose whisper-cli.exe with " +
                "\"I already have whisper.cpp\".\n\nOpen the two download pages?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        foreach (var url in new[] { WhisperCpp.Downloads, WhisperCpp.ModelDownloads })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>The whisper-cli.exe set up and the model to transcribe with, or null when closed without.</summary>
    public static (string Exe, string? Model)? Run(string current)
    {
        var w = new WhisperSetupWindow(current)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current.MainWindow,
        };
        return w.ShowDialog() == true ? w._result : null;
    }
}
