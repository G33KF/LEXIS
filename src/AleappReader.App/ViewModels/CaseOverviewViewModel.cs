using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>One device on the case overview: what it is, how much it holds, who uses it.</summary>
public sealed partial class DeviceCard(Device device) : ObservableObject
{
    public Device Device { get; } = device;
    public long Records { get; } = device.Session.Manifest.Artifacts.Sum(a => a.RecordCount);
    public int Artifacts { get; } = device.Session.Manifest.Artifacts.Count(a => a.RecordCount > 0);
    public int Tagged { get; } = device.Session.Tags.All().Count;
    public string Report => Device.Path != "" ? Device.Path : Device.Session.RootDir;
    [ObservableProperty] private IReadOnlyList<Field> identity = [];
    [ObservableProperty] private string names = "…";
    [ObservableProperty] private string phones = "…";
    internal IReadOnlyList<string> NameList { get; set; } = [];
}

/// <summary>An investigation case at a glance: the case, its devices side by side, the names found on more than one.</summary>
public sealed partial class CaseOverviewViewModel : ObservableObject
{
    readonly MainViewModel _main;
    public OpenCase Case { get; }
    public IReadOnlyList<Field> CaseInfo { get; }
    public IReadOnlyList<DeviceCard> Devices { get; }
    [ObservableProperty] private string sharedNames = "…";

    public CaseOverviewViewModel(MainViewModel main, OpenCase c)
    {
        _main = main;
        Case = c;
        Devices = [.. c.Devices.Select(d => new DeviceCard(d))];
        CaseInfo =
        [
            .. new[] { new Field("Name", c.Name), new Field("Number", c.Number) }.Where(f => f.Value != ""),
            new("Case file", c.IsDefault ? "None: the reports opened on their own" : c.File),
            new("Devices", string.Join("\n", c.Devices.Select(d => $"{d.Label} ({d.Platform})"))),
            new("Artifacts with data", Devices.Sum(d => d.Artifacts).ToString("N0")),
            new("Records", Devices.Sum(d => d.Records).ToString("N0")),
            new("Tagged items", Devices.Sum(d => d.Tagged).ToString("N0")),
        ];
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        await Task.WhenAll(Devices.Select(LoadAsync));
        var shared = Devices.SelectMany(d => d.NameList.Distinct(StringComparer.OrdinalIgnoreCase)).GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        SharedNames = Devices.Count < 2 ? "" : shared.Count == 0 ? "No name recorded as a user's on more than one device." : List(shared, 30);
    }

    static async Task LoadAsync(DeviceCard card)
    {
        var s = card.Device.Session;
        try
        {
            var (identity, groups) = await Task.Run(() =>
            {
                var facts = LeappDeviceInfo.Read(s.RootDir);
                var ufd = UfdInfo.ForExtraction(EvidenceOrigin.InputPath(s))?.Summary() ?? [];
                using var con = s.OpenDb();
                return (SummaryViewModel.GlanceOf(ufd, facts), AccountRoster.Summarize(AccountRoster.Collect(con, s.Manifest.Artifacts.Where(a => a.RecordCount > 0))));
            });
            card.Identity = identity;
            IReadOnlyList<string> Rows(string title) => [.. groups.FirstOrDefault(g => g.Title == title)?.Rows.Select(r => r.Value) ?? []];
            card.NameList = Rows("Names and nicknames");
            card.Names = card.NameList.Count == 0 ? "None recorded" : List(card.NameList, 8);
            var phones = Rows("Phone numbers");
            card.Phones = phones.Count == 0 ? "None recorded" : List(phones, 6);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
        {
            card.Names = card.Phones = "Not read: " + e.Message;
        }
    }

    static string List(IReadOnlyList<string> xs, int max) => string.Join(", ", xs.Take(max)) + (xs.Count > max ? $" and {xs.Count - max} more" : "");

    [RelayCommand]
    void OpenDevice(DeviceCard? card) { if (card is not null) _main.SelectDevice(card.Device); }

    [RelayCommand]
    void Rename(DeviceCard? card) { if (card is not null) _main.RenameDevice(card.Device); }

    public string EditText => !Case.IsDefault ? "Edit case…" : Case.Devices.Count > 0 ? "Make a case of these devices…" : "New case…";

    [RelayCommand]
    void Edit() => _main.InvestigationCase(Case);
}
