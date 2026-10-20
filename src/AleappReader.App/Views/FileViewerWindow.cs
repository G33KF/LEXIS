using System.Windows;
using System.Windows.Controls;
using AleappReader.App.ViewModels;

namespace AleappReader.App.Views;

/// <summary>The Extraction Files viewer (hex, text, tree) in a window of its own, following the file selected.</summary>
public sealed class FileViewerWindow : Window
{
    public FileViewerWindow(FilesViewModel vm, Window owner)
    {
        Title = "File viewer";
        (Owner, Width, Height, ShowInTaskbar, DataContext) = (owner, 760, 620, false, vm);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ContentControl { Content = vm, ContentTemplate = (DataTemplate)FindResource("FileViewer"), Focusable = false };
    }
}
