using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AleappReader.App.Views;

/// <summary>The tags offered in menus and their keyboard shortcuts.</summary>
public static class SettingsWindow
{
    /// <summary>True when the settings were saved.</summary>
    public static bool Show(Window owner)
    {
        var rows = new StackPanel();
        void AddRow(string tag, string shortcut)
        {
            var name = new TextBox { Text = tag, Width = 200, Margin = new Thickness(0, 0, 8, 0) };
            var key = new TextBox { Text = shortcut, Width = 140, IsReadOnly = true, Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Press the keys (e.g. Ctrl+1). Backspace clears." };
            key.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k is Key.Back or Key.Delete) { key.Text = ""; return; }
                if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab) return;
                try { key.Text = new KeyGesture(k, Keyboard.Modifiers).GetDisplayStringForCulture(System.Globalization.CultureInfo.InvariantCulture); }
                catch (NotSupportedException) { key.Text = ""; } // a letter without Ctrl/Alt is typing, not a shortcut
            };
            var remove = new Button { Content = "Remove", Padding = new Thickness(8, 2, 8, 2) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            remove.Click += (_, _) => rows.Children.Remove(row);
            row.Children.Add(name);
            row.Children.Add(key);
            row.Children.Add(remove);
            rows.Children.Add(row);
        }
        var st = Settings.Current;
        var byApp = new CheckBox { Content = "Show \"Data by application\"", IsChecked = st.GroupByApp, Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Each app's artifacts (e.g. WeChat messages, contacts and moments) together under Analyzed Data › Data by application, in addition to the categories" };
        var single = new CheckBox { Content = "Use single navigation tab", IsChecked = st.SingleNavigationTab, Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Whatever is opened from the tree shows in one tab, which takes the place of the view before" };
        var grouped = new CheckBox { Content = "Use navigation group tabs", IsChecked = st.NavigationGroupTabs, Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "One tab for each group of the tree (e.g. Analyzed Data › Calls): what is opened inside a group shows in that group's tab" };
        single.Checked += (_, _) => grouped.IsChecked = false; // one or the other; neither: a tab for each view
        grouped.Checked += (_, _) => single.IsChecked = false;
        foreach (var t in st.Tags) AddRow(t, st.TagShortcuts.GetValueOrDefault(t) ?? "");

        var add = new Button { Content = "Add tag", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2) };
        add.Click += (_, _) => AddRow("", "");
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Case tree", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(byApp);
        panel.Children.Add(new TextBlock { Text = "An app is recognised by the data folder on the device its artifacts were read from.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Tabs", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(single);
        panel.Children.Add(grouped);
        panel.Children.Add(new TextBlock { Text = "With neither, each view opens in a tab of its own.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Tags and shortcuts", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "A shortcut tags the selected records of the focused table, or the record in the details pane.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(rows);
        panel.Children.Add(add);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = "Settings", Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Width = 520, SizeToContent = SizeToContent.Height, MaxHeight = 700, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
        };
        save.Click += (_, _) =>
        {
            var pairs = rows.Children.OfType<StackPanel>()
                .Select(r => (Tag: ((TextBox)r.Children[0]).Text.Trim(), Key: ((TextBox)r.Children[1]).Text.Trim()))
                .Where(p => p.Tag != "").DistinctBy(p => p.Tag, StringComparer.OrdinalIgnoreCase).ToList();
            if (pairs.Count == 0) { MessageBox.Show(w, "Keep at least one tag.", "Settings"); return; }
            var clash = pairs.Where(p => p.Key != "").GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (clash is not null) { MessageBox.Show(w, $"{clash.Key} is used by more than one tag.", "Settings"); return; }
            st.Tags = [.. pairs.Select(p => p.Tag)];
            st.TagShortcuts = pairs.Where(p => p.Key != "").ToDictionary(p => p.Tag, p => p.Key);
            st.GroupByApp = byApp.IsChecked == true;
            (st.SingleNavigationTab, st.NavigationGroupTabs) = (single.IsChecked == true, grouped.IsChecked == true);
            st.Save();
            w.DialogResult = true;
        };
        return w.ShowDialog() == true;
    }
}
