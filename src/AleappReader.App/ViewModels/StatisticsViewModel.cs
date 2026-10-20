using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>A party in the top or last ten: who, through what, how many exchanges, how many made by the device, and the last.</summary>
public sealed record ContactRow(int Rank, string Contact, string Kind, int Count, string Direction, string Last);
/// <summary>A cell of the activity calendar or of the weekday × hour grid: its shade (0 for none, -1 outside the period), a tooltip and the day it opens.</summary>
public sealed record HeatCell(double Opacity, string Tip, DateOnly? Day);
public sealed record HeatRow(string Label, IReadOnlyList<HeatCell> Cells);
/// <summary>A week of the calendar (Monday on top), with the month that starts in it.</summary>
public sealed record HeatWeek(string Month, IReadOnlyList<HeatCell> Days);
/// <summary>A stretch of the calendar: one column per week.</summary>
public sealed record HeatYear(string Year, IReadOnlyList<HeatWeek> Weeks, string Total);
public sealed record PlaceRow(int Rank, string Where, int Positions, int Visits, string First, string Last, double Lat, double Lon);
public sealed record PeakDay(DateOnly Day, string Text, int Count);

/// <summary>A source the statistics count, on or off: a means of exchange, a type of data, a positions artifact.</summary>
public sealed partial class StatChoice(string name, long count, bool isChecked, Action changed) : ObservableObject
{
    public string Name { get; } = name;
    public long Count { get; } = count;
    public string Text => $"{Name} ({Count:N0})";
    [ObservableProperty] private bool isChecked = isChecked;
    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// Statistics of a device: the parties it exchanged with most and last (one-to-one), its positions and when its user was
/// active, each counted from the sources the examiner leaves checked. Counts of records as stored; a record copied by
/// several artifacts counts once only for calls (one log per means is read).
/// </summary>
public sealed partial class StatisticsViewModel : ObservableObject, IRefreshable
{
    static readonly string[] Weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    /// <summary>Types of data the user does not do, left out to start.</summary>
    static readonly HashSet<string> OffByDefault = ["Cookies"];
    readonly MainViewModel _main;
    readonly CaseSession _s;
    CaseStatistics? _stats;
    IReadOnlyList<ActivitySource> _events = [];

    [ObservableProperty] private string status = "Counting…";
    [ObservableProperty] private IReadOnlyList<StatChoice> means = [];
    [ObservableProperty] private IReadOnlyList<ContactRow> top = [];
    [ObservableProperty] private IReadOnlyList<ContactRow> recent = [];
    [ObservableProperty] private IReadOnlyList<StatChoice> positionSources = [];
    [ObservableProperty] private IReadOnlyList<Field> locations = [];
    [ObservableProperty] private IReadOnlyList<PlaceRow> places = [];
    [ObservableProperty] private IReadOnlyList<StatChoice> activityTypes = [];
    /// <summary>The period of the activity (null: from the first or to the last event).</summary>
    [ObservableProperty] private DateTime? from;
    [ObservableProperty] private DateTime? to;
    [ObservableProperty] private string activityText = "";
    /// <summary>The last twelve months of the period, then the period year by year.</summary>
    [ObservableProperty] private HeatYear? lastYear;
    [ObservableProperty] private IReadOnlyList<HeatYear> years = [];
    [ObservableProperty] private IReadOnlyList<HeatRow> hours = [];
    [ObservableProperty] private IReadOnlyList<PeakDay> peaks = [];
    public IReadOnlyList<string> HourLabels { get; } = [.. Enumerable.Range(0, 24).Select(h => h % 3 == 0 ? h.ToString("00") : "")];

    public StatisticsViewModel(MainViewModel main, CaseSession s)
    {
        (_main, _s) = (main, s);
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        try { (_stats, _events) = (await Statistics.Of(_s), await Statistics.UserEvents(_s)); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { Status = "Statistics: " + e.Message; return; }
        Means = [.. _stats.Interactions.GroupBy(i => i.Kind).OrderByDescending(g => g.Sum(i => i.Count)).Select(g => new StatChoice(g.Key, g.Sum(i => i.Count), true, ShowContacts))];
        PositionSources = [.. _stats.Positions.Select(p => new StatChoice(p.Name, p.Points.Count, true, () => _ = ShowPlacesAsync()))];
        ActivityTypes = [.. _events.Select(e => new StatChoice(e.Type, e.Epochs.Length, !OffByDefault.Contains(e.Type), () => _ = LoadActivityAsync()))];
        ShowContacts();
        Status = "";
        await Task.WhenAll(ShowPlacesAsync(), LoadActivityAsync());
    }

    partial void OnFromChanged(DateTime? value) => _ = LoadActivityAsync();
    partial void OnToChanged(DateTime? value) => _ = LoadActivityAsync();
    public void Refresh() { ShowContacts(); _ = ShowPlacesAsync(); _ = LoadActivityAsync(); } // the display time zone changed

    /// <summary>A day with its weekday in English, as the rest of the interface.</summary>
    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture);
    static string Time(long epoch) => epoch == long.MinValue ? "" : ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(epoch));
    static IReadOnlyList<Field> Fields(params (string Name, string Value)[] xs) => [.. xs.Where(x => x.Value != "").Select(x => new Field(x.Name, x.Value))];

    /// <summary>The top ten and last ten parties over the means checked: a party's exchanges are summed over them.</summary>
    void ShowContacts()
    {
        if (_stats is null) return;
        var on = Means.Where(m => m.IsChecked).Select(m => m.Name).ToHashSet();
        var rows = _stats.Interactions.Where(i => on.Contains(i.Kind)).GroupBy(i => i.Key).Select(g =>
        {
            var last = g.MaxBy(i => i.Last)!;
            return new Interaction(string.Join(", ", g.OrderByDescending(i => i.Count).Select(i => i.Kind)), g.Key,
                g.Select(i => i.Contact).OrderByDescending(c => c.StartsWith('+')).First(), // the number with its country code
                g.Select(i => i.Name).FirstOrDefault(n => n != "") ?? "", g.Sum(i => i.Count), g.Sum(i => i.Sent), last.Last, last.LastSent);
        }).ToList();
        ContactRow Row(Interaction i, int n) => new(n, i.Display, i.Kind, i.Count, $"{i.Sent:N0} out · {i.Count - i.Sent:N0} in",
            i.Last == long.MinValue ? "" : $"{Time(i.Last)} ({(i.LastSent ? "out" : "in")})");
        Top = [.. rows.OrderByDescending(i => i.Count).ThenByDescending(i => i.Last).Take(10).Select((i, n) => Row(i, n + 1))];
        Recent = [.. rows.Where(i => i.Last != long.MinValue).OrderByDescending(i => i.Last).Take(10).Select((i, n) => Row(i, n + 1))];
    }

    int _placesVersion;
    /// <summary>The positions of the artifacts checked, their span and the places with most of them.</summary>
    async Task ShowPlacesAsync()
    {
        if (_stats is null) return;
        var version = ++_placesVersion;
        var on = PositionSources.Where(p => p.IsChecked).Select(p => p.Name).ToHashSet();
        var points = _stats.Positions.Where(p => on.Contains(p.Name)).SelectMany(p => p.Points).ToList();
        var places = await Task.Run(() => Statistics.Places(points));
        if (version != _placesVersion) return;
        var dated = points.Where(p => p.Epoch is not null).Select(p => p.Epoch!.Value).ToList();
        Locations = Fields(("Positions", $"{points.Count:N0} from {on.Count:N0} of {PositionSources.Count:N0} artifacts"),
            ("First and last", dated.Count > 0 ? $"{Time(dated.Min())}  →  {Time(dated.Max())}" : ""),
            ("Places (zones of 200 m)", places.Count > 0 ? "the ten with most positions, below" : ""));
        Places = [.. places.Select((p, i) => new PlaceRow(i + 1, string.Create(CultureInfo.InvariantCulture, $"{p.Lat:0.#####}, {p.Lon:0.#####}"), p.Positions, p.Visits, Time(p.First), Time(p.Last), p.Lat, p.Lon))];
    }

    [RelayCommand]
    void WholePeriod() => (From, To) = (null, null);

    int _activityVersion;
    async Task LoadActivityAsync()
    {
        var version = ++_activityVersion;
        ActivityText = "Counting…";
        var on = ActivityTypes.Where(t => t.IsChecked).Select(t => t.Name).ToHashSet();
        var (from, to) = (From is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null, To is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null);
        var map = await Task.Run(() => Statistics.Activity(_events.Where(e => on.Contains(e.Type)).SelectMany(e => e.Epochs), from, to));
        if (version != _activityVersion) return;
        var max = map.Days.Count == 0 ? 1 : map.Days.Values.Max();
        // a square root shade, so a few busy days do not leave every other day blank
        double Shade(int n, int top) => n == 0 ? 0 : 0.2 + 0.8 * Math.Sqrt((double)n / Math.Max(1, top));
        HeatYear Grid(string title, DateOnly first, DateOnly last)
        {
            var weeks = new List<HeatWeek>();
            for (var w = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); w <= last; w = w.AddDays(7))
            {
                var days = Enumerable.Range(0, 7).Select(i => w.AddDays(i)).ToList();
                var month = days.FirstOrDefault(d => d.Day == 1 && d >= first && d <= last);
                weeks.Add(new(month == default ? "" : month.ToString("MMM", CultureInfo.InvariantCulture), [.. days.Select(d => d < first || d > last ? new HeatCell(-1, "", null)
                    : map.Days.GetValueOrDefault(d) is var n && n > 0 ? new HeatCell(Shade(n, max), $"{Day(d)}: {n:N0} events — click to open the timeline", d)
                    : new HeatCell(0, $"{Day(d)}: none", null))]));
            }
            return new HeatYear(title, weeks, $"{map.Days.Where(x => x.Key >= first && x.Key <= last).Sum(x => x.Value):N0} events");
        }

        if (map.Days.Count == 0) (LastYear, Years) = (null, []);
        else
        {
            var (start, end) = (from ?? map.Days.Keys.Min(), to ?? map.Days.Keys.Max());
            var yearAgo = end.AddYears(-1).AddDays(1);
            LastYear = Grid($"{Day(yearAgo > start ? yearAgo : start)} → {Day(end)}", yearAgo > start ? yearAgo : start, end);
            Years = [.. Enumerable.Range(start.Year, end.Year - start.Year + 1).Reverse().Select(y =>
                Grid(y.ToString(), new DateOnly(y, 1, 1) is var a && a > start ? a : start, new DateOnly(y, 12, 31) is var b && b < end ? b : end))];
        }
        var hmax = map.WeekHours.DefaultIfEmpty(0).Max();
        HeatCell Hour(string name, int d, int h) => map.WeekHours[d * 24 + h] is var n ? new(Shade(n, hmax), $"{name} {h:00}:00–{h:00}:59: {n:N0} events", null) : null!;
        Hours = [.. Weekdays.Select((name, d) => new HeatRow(name, [.. Enumerable.Range(0, 24).Select(h => Hour(name, d, h))]))];
        Peaks = [.. map.Days.OrderByDescending(x => x.Value).Take(10).Select(x => new PeakDay(x.Key, Day(x.Key), x.Value))];
        var busiest = Enumerable.Range(0, map.WeekHours.Length).MaxBy(i => map.WeekHours[i]);
        ActivityText = map.Total == 0 ? "No dated events of the types checked in this period." :
            $"{map.Total:N0} events on {map.Days.Count:N0} days, {ValueFormatter.Zone.DisplayName}. Busiest hour: {Weekdays[busiest / 24]} {busiest % 24:00}:00. " +
            "One event per record of the types checked, at its first date.";
    }

    [RelayCommand]
    void OpenDay(DateOnly? day) { if (day is { } d) _main.ShowTimelineDay(_s, d); }

    [RelayCommand]
    void ShowPlace(PlaceRow? p)
    {
        if (p is not null && _main.Devices.FirstOrDefault(d => d.Session == _s) is { } dev)
            _main.ShowZoneOnMap([dev], new GeoPoint(p.Lat, p.Lon, "", "", 0, "", $"Place {p.Rank}"));
    }
}
