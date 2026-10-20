using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

public sealed record TaggedItem(TagEntry Entry, ArtifactInfo? Artifact)
{
    /// <summary>The path of a tagged file (gallery), else null.</summary>
    public string? FilePath => Entry.TableName.StartsWith(TagStore.FilePrefix, StringComparison.Ordinal) ? Entry.TableName[TagStore.FilePrefix.Length..] : null;
    public string ArtifactName => Artifact?.Name ?? FilePath ?? Entry.TableName;
    public string Record => FilePath is null ? Entry.RowId.ToString() : "file";
    public string Created => ValueFormatter.FormatTime(Entry.Created);
}

/// <summary>Every tagged record across the case, exportable as one HTML report.</summary>
public partial class TaggedViewModel : ObservableObject, IRefreshable
{
    readonly MainViewModel _main;
    readonly CaseSession _s;

    public ObservableCollection<TaggedItem> Items { get; } = [];
    [ObservableProperty] private TaggedItem? selected;

    public TaggedViewModel(MainViewModel main, CaseSession s)
    {
        _main = main;
        _s = s;
        Refresh();
    }

    public void Refresh()
    {
        var byTable = _s.Manifest.Artifacts.ToDictionary(a => a.TableName);
        Items.Clear();
        foreach (var t in _s.Tags.All()) Items.Add(new TaggedItem(t, byTable.GetValueOrDefault(t.TableName)));
    }

    partial void OnSelectedChanged(TaggedItem? value)
    {
        if (value?.Artifact is { } a) _main.Details.ShowRecord(_s, a, value.Entry.RowId);
    }

    [RelayCommand]
    void GoTo(TaggedItem? item)
    {
        if ((item ?? Selected)?.Artifact is { } a) _main.NavigateTo(a, null, (item ?? Selected)!.Entry.RowId);
    }

    [RelayCommand]
    async Task ExportHtml()
    {
        var dlg = new SaveFileDialog { Filter = "HTML report (*.html)|*.html|JSON (*.json)|*.json", FileName = "Tagged items.html" };
        if (dlg.ShowDialog() != true) return;
        await Task.Run(() =>
        {
            using var con = _s.OpenDb();
            var sections = Items.Where(i => i.Artifact is not null).GroupBy(i => i.Artifact!).Select(g =>
            {
                var page = ArtifactQuery.Query(con, g.Key, new PageRequest(0, int.MaxValue, RowIds: g.Select(i => i.Entry.RowId).ToList()));
                return new ExportSection(g.Key.Name, page.Columns, page.Rows, _s.Tags.ForTable(g.Key.TableName), g.Key, "Tagged rows");
            }).ToList();
            var files = Items.Where(i => i.FilePath is not null).Select((i, n) => new RecordRow(n, [i.FilePath, i.Entry.Tag, i.Entry.Note, i.Created])).ToList();
            if (files.Count > 0)
                sections.Add(new ExportSection("Tagged files", [new("path", "File", null), new("tag", "Tag", null), new("note", "Note", null), new("created", "Tagged at", null)], files));
            if (System.IO.Path.GetExtension(dlg.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)) JsonExporter.Write(dlg.FileName, sections);
            else HtmlReportExporter.Write(dlg.FileName, _s, sections, "Tagged items");
        });
        _main.Status = "Exported " + dlg.FileName;
        ExportLog.Add("Tagged items", dlg.FileName, $"{Items.Count:N0} items", _s);
    }
}
