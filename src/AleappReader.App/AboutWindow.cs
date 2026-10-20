using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AleappReader.App;

/// <summary>A text whose web addresses are links that open in the browser (the About of LEXIS, its sources).</summary>
public static partial class AboutWindow
{
    [GeneratedRegex(@"https?://[^\s)]+")]
    private static partial Regex Url();

    public static void Show(string title, string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), LineHeight = 20 };
        var at = 0;
        foreach (Match m in Url().Matches(text))
        {
            block.Inlines.Add(new Run(text[at..m.Index]));
            var link = new Hyperlink(new Run(m.Value)) { NavigateUri = new Uri(m.Value), ToolTip = "Open in the browser" };
            link.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            block.Inlines.Add(link);
            at = m.Index + m.Length;
        }
        block.Inlines.Add(new Run(text[at..]));
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Padding = new Thickness(18, 3, 18, 3), Margin = new Thickness(0, 0, 16, 12), HorizontalAlignment = HorizontalAlignment.Right };
        var dock = new DockPanel();
        DockPanel.SetDock(ok, Dock.Bottom);
        dock.Children.Add(ok);
        dock.Children.Add(new ScrollViewer { Content = block, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var w = new Window { Title = title, Content = dock, Width = 760, Height = 600, Owner = Application.Current.MainWindow,
                             WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        ok.Click += (_, _) => w.Close();
        w.ShowDialog();
    }
}
