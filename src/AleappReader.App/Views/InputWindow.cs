using System.Windows;
using System.Windows.Controls;

namespace AleappReader.App.Views;

/// <summary>Asks for one line of text.</summary>
public static class InputWindow
{
    /// <summary>The text typed, or null when cancelled.</summary>
    public static string? Ask(string title, string prompt, string value)
    {
        var owner = Application.Current.MainWindow;
        var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Content = panel, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
