using System.Windows;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>Asks how a chat report looks and whether the examiner's tags and notes go in: null when cancelled.</summary>
public partial class ChatExportWindow : Window
{
    ChatExportWindow(ArtifactInfo a, int chats)
    {
        InitializeComponent();
        Heading.Text = chats == 1 ? "Chat report" : $"{chats:N0} chat reports";
        Theme.SelectedIndex = (int)ChatExportOptions.ThemeFor(a);
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static ChatExportOptions? Ask(ArtifactInfo a, int chats)
    {
        var w = new ChatExportWindow(a, chats) { Owner = Application.Current.MainWindow };
        return w.ShowDialog() == true ? new ChatExportOptions((ChatTheme)w.Theme.SelectedIndex, w.Tags.IsChecked == true) : null;
    }
}
