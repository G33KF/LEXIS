using System.Windows;
using System.Windows.Controls;

namespace AleappReader.App;

/// <summary>
/// A view's header that goes to a new line instead of scrolling: its parts side by side in order, those docked or
/// aligned right last and at the right end of their line, each part that does not fit starting a new line. A part
/// wider than the whole line (a WrapPanel of buttons) is given the line's width, to wrap in it. The last part on the
/// left (the filter box) takes the rest of its line, as in a DockPanel.
/// </summary>
public sealed class HeaderPanel : Panel
{
    readonly List<(UIElement Part, int Line, double X, double Width)> _parts = [];
    readonly List<(double Y, double Height, double End)> _lines = [];

    static bool Right(UIElement e) => DockPanel.GetDock(e) == Dock.Right || e is FrameworkElement { HorizontalAlignment: HorizontalAlignment.Right };

    protected override Size MeasureOverride(Size available)
    {
        _parts.Clear();
        _lines.Clear();
        var children = InternalChildren.Cast<UIElement>().ToList();
        var ordered = children.Where(e => !Right(e)).Concat(children.Where(Right)).ToList();
        double width = available.Width, x = 0, y = 0, height = 0, widest = 0;
        void Close() { _lines.Add((y, height, x)); y += height; x = 0; height = 0; }
        foreach (var e in ordered)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (e.Visibility == Visibility.Collapsed) { _parts.Add((e, _lines.Count, x, 0)); continue; }
            if (x > 0 && x + e.DesiredSize.Width > width) Close();
            if (e.DesiredSize.Width > width) e.Measure(new Size(width, double.PositiveInfinity));
            _parts.Add((e, _lines.Count, x, e.DesiredSize.Width));
            x += e.DesiredSize.Width;
            height = Math.Max(height, e.DesiredSize.Height);
            widest = Math.Max(widest, x);
        }
        Close();
        return new Size(double.IsInfinity(width) ? widest : Math.Min(widest, width), y);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var lastLeft = _parts.LastOrDefault(p => !Right(p.Part) && p.Part.Visibility != Visibility.Collapsed).Part;
        foreach (var (part, line, x, width) in _parts)
        {
            var (y, height, end) = _lines[line];
            var spare = Math.Max(0, final.Width - end);
            // the right parts move to the line's right end; the last left part, the one before them, takes the space between
            var at = Right(part) ? x + spare : x;
            var w = part == lastLeft ? width + spare : width;
            part.Arrange(new Rect(at, y, Math.Max(0, Math.Min(w, final.Width - at)), height));
        }
        return final;
    }
}
