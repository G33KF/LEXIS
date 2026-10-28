using System.Windows;
using System.Windows.Controls;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>What the complete analysis is asked to do.</summary>
public sealed record CompleteAnalysisRequest(IReadOnlyList<ArtifactInfo> Artifacts, string Objective,
    bool Translate, bool Describe, bool Transcribe, bool Web, bool Claude);

/// <summary>The data categories to analyse (ticked by group or one by one), the objective, and what to do first.</summary>
public sealed class CompleteAnalysisWindow : Window
{
    readonly List<(CheckBox Box, ArtifactInfo A)> _artifacts = [];
    readonly TextBox _objective = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly CheckBox _translate, _describe, _transcribe, _web, _claude;
    CompleteAnalysisRequest? _result;

    CompleteAnalysisWindow(CaseSession s, string objective, AiSettings ai)
    {
        Title = "Complete analysis";
        Width = 620;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        _objective.Text = objective;

        var tree = new StackPanel();
        foreach (var g in s.Manifest.Artifacts.Where(a => a.RecordCount > 0).GroupBy(MacroCategories.Of).OrderBy(g => g.Key.Name))
        {
            var group = new CheckBox { Content = $"{g.Key.Name} ({g.Sum(a => a.RecordCount):N0} records)", FontWeight = FontWeights.SemiBold, IsChecked = false, Margin = new(0, 6, 0, 2) };
            var items = new StackPanel { Margin = new(22, 0, 0, 0) };
            var boxes = new List<CheckBox>();
            foreach (var a in g.OrderBy(a => a.Name))
            {
                var b = new CheckBox { Content = $"{a.Name} · {a.RecordCount:N0}", ToolTip = $"{a.Category} · {a.TableName}", Margin = new(0, 1, 0, 1) };
                b.Click += (_, _) => group.IsChecked = boxes.All(x => x.IsChecked == true) ? true : boxes.Any(x => x.IsChecked == true) ? null : false;
                boxes.Add(b);
                items.Children.Add(b);
                _artifacts.Add((b, a));
            }
            group.Click += (_, _) => { var on = group.IsChecked == true; boxes.ForEach(b => b.IsChecked = on); };
            tree.Children.Add(group);
            tree.Children.Add(items);
        }

        _translate = new CheckBox { Content = $"Translate the records into {ai.TranslateTo} first (shown beside each record)", Margin = new(0, 2, 0, 2) };
        _describe = new CheckBox { Content = "Describe the pictures and read their text (OCR) first", Margin = new(0, 2, 0, 2) };
        _transcribe = new CheckBox { Content = "Transcribe the audio first", Margin = new(0, 2, 0, 2) };
        // a job without a model in AI settings cannot run: its step is shown, off
        foreach (var (box, ready) in new[] { (_translate, AiState.Instance.Text), (_describe, AiState.Instance.Vision), (_transcribe, AiState.Instance.Audio) })
            if (!ready) { box.IsEnabled = false; box.ToolTip = "No model for this job: choose one in AI settings"; ToolTipService.SetShowOnDisabled(box, true); }
        _web = new CheckBox { Content = "Web search for the objective's words (shown and confirmed)", Margin = new(0, 2, 0, 2) };
        _claude = new CheckBox
        {
            Content = "Claude CLI explores the whole case through LEXIS's read-only tools (leaves this machine)", Margin = new(0, 2, 0, 2),
            Visibility = ai.ClaudeExplores ? Visibility.Visible : Visibility.Collapsed,
            ToolTip = "Instead of the text model: Claude reads any record of the case through the MCP tools and writes the report, citing what it read",
        };

        var start = new Button { Content = "Start the analysis", IsDefault = true, Padding = new(12, 4, 12, 4), Margin = new(0, 0, 6, 0) };
        start.Click += (_, _) => Start();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new(12, 4, 12, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        buttons.Children.Add(start);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new(14) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "💀 Complete analysis", FontSize = 18, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8),
            Text = "The AI reads every record of the ticked categories, keeps those that bear on the objective, and writes a report that cites " +
                   "each record it rests on. What it does to single records (translations, descriptions, transcriptions) shows in their details. " +
                   "A review aid, not a finding. It can take long; stopped, it resumes.",
        });
        head.Children.Add(new TextBlock { Text = "Objective", FontWeight = FontWeights.SemiBold });
        head.Children.Add(_objective);
        head.Children.Add(new TextBlock { Text = "Data categories", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 0) });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var options = new StackPanel { Margin = new(0, 8, 0, 0) };
        options.Children.Add(new TextBlock { Text = "First", FontWeight = FontWeights.SemiBold });
        foreach (var c in new[] { _translate, _describe, _transcribe, _web, _claude }) options.Children.Add(c);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(options, Dock.Bottom);
        root.Children.Add(options);
        root.Children.Add(new Border
        {
            BorderThickness = new(1), Padding = new(6), Child = new ScrollViewer { Content = tree, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
        ((Border)root.Children[^1]).SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Content = root;
    }

    void Start()
    {
        var chosen = _artifacts.Where(x => x.Box.IsChecked == true).Select(x => x.A).ToList();
        if (_objective.Text.Trim() == "") { MessageBox.Show(this, "Write the objective first.", Title); return; }
        if (chosen.Count == 0) { MessageBox.Show(this, "Tick at least one data category.", Title); return; }
        _result = new(chosen, _objective.Text.Trim(), _translate.IsChecked == true, _describe.IsChecked == true, _transcribe.IsChecked == true,
                      _web.IsChecked == true, _claude.IsChecked == true);
        DialogResult = true;
    }

    /// <summary>The request, or null when cancelled.</summary>
    public static CompleteAnalysisRequest? Ask(CaseSession s, string objective)
    {
        var w = new CompleteAnalysisWindow(s, objective, Settings.Current.Ai)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current.MainWindow,
        };
        return w.ShowDialog() == true ? w._result : null;
    }
}
