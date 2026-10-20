using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>Asks for a condition on one column. Dates are typed in the case time zone and stored as epoch seconds.</summary>
public static class ColumnFilterWindow
{
    public sealed record Result(ColumnFilter? Filter);

    static readonly (string Op, string Text)[] TextOps =
        [("contains", "contains"), ("notcontains", "does not contain"), ("equals", "is exactly"), ("empty", "is empty"),
         ("notempty", "is not empty"), ("between", "number between")];
    static readonly (string Op, string Text)[] DateOps = [("between", "between"), ("empty", "is empty"), ("notempty", "is not empty")];

    /// <summary>The new condition (Filter null = none), or null when cancelled.</summary>
    public static Result? Ask(Window? owner, ColumnInfo col, ColumnFilter? current)
    {
        var ops = col.IsDateTime ? DateOps : TextOps;
        var op = new ComboBox { ItemsSource = ops.Select(o => o.Text).ToList(), Margin = new Thickness(0, 0, 0, 8) };
        op.SelectedIndex = Math.Max(0, Array.FindIndex(ops, o => o.Op == current?.Op));
        var v1 = new TextBox { Text = Show(col, current?.Value), Margin = new Thickness(0, 0, 0, 6) };
        var v2 = new TextBox { Text = Show(col, current?.Value2) };
        var and = new TextBlock { Text = col.IsDateTime ? $"and (yyyy-MM-dd [HH:mm], {ValueFormatter.Zone.Id}; leave one empty for open-ended)" : "and", Margin = new Thickness(0, 0, 0, 4) };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        void Sync()
        {
            var o = ops[Math.Max(0, op.SelectedIndex)].Op;
            v1.Visibility = o is "empty" or "notempty" ? Visibility.Collapsed : Visibility.Visible;
            v2.Visibility = and.Visibility = o == "between" ? Visibility.Visible : Visibility.Collapsed;
        }
        op.SelectionChanged += (_, _) => Sync();
        Sync();

        var ok = new Button { Content = "Apply", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = col.Display, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var c in new UIElement[] { op, v1, and, v2, error, buttons }) panel.Children.Add(c);
        var w = new Window
        {
            Title = "Filter column", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ColumnFilter? result = null;
        ok.Click += (_, _) =>
        {
            var o = ops[Math.Max(0, op.SelectedIndex)].Op;
            string? a = v1.Text.Trim(), b = v2.Text.Trim();
            if (o is "empty" or "notempty") a = b = null;
            else if (o != "between") b = null;
            if (o == "between")
            {
                if (Parse(col, a, false) is not { } pa || Parse(col, b, true) is not { } pb) { error.Text = col.IsDateTime ? "Use a date like 2024-05-01 or 2024-05-01 14:30." : "Use numbers, e.g. 10 and 250."; return; }
                if (pa == "" && pb == "") { error.Text = "Give at least one bound."; return; }
                (a, b) = (pa == "" ? null : pa, pb == "" ? null : pb);
            }
            else if (o != "empty" && o != "notempty" && string.IsNullOrEmpty(a)) { error.Text = "Type a value."; return; }
            result = new ColumnFilter(col.Sql, o, a, b);
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return null;
        return new Result(result);
    }

    /// <summary>"" for an empty bound, the number or epoch as invariant text, or null when it does not parse.</summary>
    static string? Parse(ColumnInfo col, string? s, bool upper)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (!col.IsDateTime)
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString(CultureInfo.InvariantCulture) : null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            && !DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out t)) return null;
        if (upper && t.TimeOfDay == TimeSpan.Zero && !s.Contains(':')) t = t.AddDays(1).AddSeconds(-1); // a whole day
        return ValueFormatter.ToUnixSeconds(t, upper).ToString(CultureInfo.InvariantCulture);
    }

    static string Show(ColumnInfo col, string? v) =>
        col.IsDateTime && long.TryParse(v, out var e)
            ? TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(e), ValueFormatter.Zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : v ?? "";
}
