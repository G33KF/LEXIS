using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

public partial class DeviceRow : ObservableObject
{
    [ObservableProperty] private string path = "";
    [ObservableProperty] private string label = "";
    [ObservableProperty] private string color = LexisCase.Palette[0];
}

/// <summary>Creates or edits an investigation case (.lexiscase): a name, a proceeding number and the reports of its
/// devices, each with a label and a colour. Saving writes the file; the reports themselves are not touched.</summary>
public partial class InvestigationCaseViewModel : ObservableObject
{
    public IReadOnlyList<string> Palette => LexisCase.Palette;
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    [ObservableProperty] private string name;
    [ObservableProperty] private string number;
    [ObservableProperty] private DeviceRow? selected;
    [ObservableProperty] private string message = "";
    /// <summary>The file written, once saved.</summary>
    public string? Saved { get; private set; }
    readonly string? _file;

    public event Action? Done;

    public InvestigationCaseViewModel(LexisCase c, string? file)
    {
        (name, number, _file) = (c.Name, c.Number, file);
        foreach (var d in c.Devices) Devices.Add(new DeviceRow { Path = d.Path, Label = d.Label, Color = d.Color });
    }

    void Add(string path)
    {
        var n = Devices.Count + 1;
        while (Devices.Any(d => d.Label.Equals($"Device {n}", StringComparison.OrdinalIgnoreCase))) n++;
        Devices.Add(new DeviceRow { Path = path, Label = $"Device {n}", Color = LexisCase.Palette[Devices.Count % LexisCase.Palette.Length] });
    }

    [RelayCommand]
    void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = "ALEAPP or iLEAPP report folder of the device" };
        if (dlg.ShowDialog() == true) Add(dlg.FolderName);
    }

    [RelayCommand]
    void AddFile()
    {
        var dlg = new OpenFileDialog { Filter = "Report or portable case (*.lava;*.json;*.zip)|*.lava;*_lava_data.json;*.zip|All files|*.*" };
        if (dlg.ShowDialog() == true) Add(dlg.FileName);
    }

    [RelayCommand]
    void Remove(DeviceRow? row)
    {
        if ((row ?? Selected) is { } r) Devices.Remove(r);
    }

    [RelayCommand]
    void Save()
    {
        var labels = Devices.Select(d => d.Label.Trim()).ToList();
        if (labels.Any(l => l == "")) { Message = "Every device needs a label."; return; }
        if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count) { Message = "Two devices have the same label."; return; }
        if (Devices.FirstOrDefault(d => !File.Exists(d.Path) && !Directory.Exists(d.Path)) is { } missing) { Message = $"Not found: {missing.Path}"; return; }
        var file = _file;
        if (file is null)
        {
            var dlg = new SaveFileDialog { Filter = "Investigation case (*.lexiscase)|*.lexiscase", DefaultExt = LexisCase.Extension,
                                           FileName = string.Concat((Name.Trim() is { Length: > 0 } n ? n : "case").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) };
            if (dlg.ShowDialog() != true) return;
            file = dlg.FileName;
        }
        try
        {
            new LexisCase(Name.Trim(), Number.Trim(), Devices.Select(d => new LexisDevice(d.Path, d.Label.Trim(), d.Color)).ToList()).Save(file);
            Saved = file;
            Done?.Invoke();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = "Cannot save: " + e.Message; }
    }
}
