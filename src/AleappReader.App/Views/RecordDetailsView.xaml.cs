using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using AleappReader.App.ViewModels;

namespace AleappReader.App.Views;

public partial class RecordDetailsView : UserControl
{
    /// <summary>The field's structured value decoded in its own window.</summary>
    void OpenParsed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Field { Kind: { } kind } f) ParsedValueWindow.Show(f.Name, f.Raw ?? f.Value, kind);
    }

    static AleappReader.Core.PlistNode? NodeOf(object sender) => (sender as MenuItem)?.DataContext as AleappReader.Core.PlistNode;
    void CopyPath(object sender, RoutedEventArgs e) { if (NodeOf(sender) is { } n) Clipboard.SetText(n.Path); }
    void CopyValue(object sender, RoutedEventArgs e) { if (NodeOf(sender) is { } n) Clipboard.SetText(n.Value); }

    /// <summary>False in the media window, whose viewer shows the file: the fields only.</summary>
    public bool Preview { get; init; } = true;

    GridLength _preview = new(320);
    DetailsViewModel? _vm;

    public RecordDetailsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Media.CollectionChanged -= MediaChanged;
            _vm = DataContext as DetailsViewModel;
            if (_vm is not null) _vm.Media.CollectionChanged += MediaChanged;
            Layout();
        };
    }

    void MediaChanged(object? s, NotifyCollectionChangedEventArgs e) => Layout();

    /// <summary>Shows or hides the email HTML in the preview (the main window fills HtmlView).</summary>
    public void ShowHtml(bool on)
    {
        HtmlBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Layout();
    }

    /// <summary>The preview row opens at its last dragged height when there is media or HTML, and closes when there is none.</summary>
    void Layout()
    {
        bool media = Preview && _vm?.Media.Count > 0, html = HtmlBox.Visibility == Visibility.Visible;
        MediaRow.Height = media ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        HtmlRow.Height = html ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        var open = media || html;
        if (PreviewRow.ActualHeight > 0) _preview = new GridLength(PreviewRow.ActualHeight);
        PreviewRow.Height = open ? _preview : new GridLength(0);
        PreviewRow.MinHeight = open ? 80 : 0;
        PreviewSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
}
