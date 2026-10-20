using System.Windows;
using System.Windows.Controls;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>A JSON, XML, plist, HTML or BLOB value decoded into a tree and readable text, beside the value as stored.</summary>
public partial class ParsedValueWindow : Window
{
    public sealed record Model(string Title, string About, IReadOnlyList<PlistNode> Roots, string Pretty, string Stored);

    ParsedValueWindow() => InitializeComponent();

    /// <summary>Opens the decoded value; says so when it does not decode as its kind.</summary>
    public static void Show(string name, object? raw, string kind)
    {
        var tree = StructuredValue.Parse(raw, kind);
        var pretty = StructuredValue.Pretty(raw, kind);
        if (tree is null && pretty is null)
        {
            MessageBox.Show($"\"{name}\" looks like {kind} but does not decode as it (damaged or cut short). The value stays as stored.", kind,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var stored = raw is byte[] b ? Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 64 * 1024))) + (b.Length > 64 * 1024 ? "…" : "") : raw?.ToString() ?? "";
        var w = new ParsedValueWindow
        {
            Title = $"{name} · {kind}", Owner = Application.Current.MainWindow,
            DataContext = new Model(name, $"{kind} decoded from the record's value (read-only). Right-click a node: copy its key path or value.",
                tree is null ? [] : [tree], pretty ?? "", stored),
        };
        w.Show();
    }

    static PlistNode? NodeOf(object sender) => (sender as MenuItem)?.DataContext as PlistNode;
    void CopyPath(object sender, RoutedEventArgs e) { if (NodeOf(sender) is { } n) Clipboard.SetText(n.Path); }
    void CopyValue(object sender, RoutedEventArgs e) { if (NodeOf(sender) is { } n) Clipboard.SetText(n.Value); }
}
