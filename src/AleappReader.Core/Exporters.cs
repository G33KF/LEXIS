using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AleappReader.Core;

/// <summary>A block of rows to export: one artifact, its columns and the rows chosen.</summary>
public sealed record ExportSection(string Title, IReadOnlyList<ColumnInfo> Columns, IEnumerable<RecordRow> Rows,
    IReadOnlyDictionary<long, TagEntry>? Tags = null, ArtifactInfo? Artifact = null, string? Filter = null);

public static class CsvExporter
{
    public static void Write(string path, ExportSection section)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true)); // BOM: Excel reads UTF-8 correctly
        var tagged = section.Tags is { Count: > 0 };
        var header = section.Columns.Select(c => c.Display).ToList();
        if (tagged) header.AddRange(["Tag", "Note"]);
        w.WriteLine(string.Join(",", header.Select(Cell)));
        foreach (var row in section.Rows)
        {
            var cells = row.Values.Select((v, i) => ValueFormatter.Format(v, section.Columns[i])).ToList();
            if (tagged)
            {
                var t = section.Tags!.GetValueOrDefault(row.RowId);
                cells.Add(t?.Tag ?? "");
                cells.Add(t?.Note ?? "");
            }
            w.WriteLine(string.Join(",", cells.Select(Cell)));
        }
    }

    /// <summary>RFC 4180 quoting, plus a leading apostrophe on cells a spreadsheet would run as a formula.</summary>
    public static string Cell(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>Sections as JSON for other tools: one object per section, each row an object keyed by column name with
/// the values as displayed, plus the row's tag and note.</summary>
public static class JsonExporter
{
    public static void Write(string path, IEnumerable<ExportSection> sections)
    {
        using var f = File.Create(path);
        using var w = new System.Text.Json.Utf8JsonWriter(f, new() { Indented = true });
        w.WriteStartArray();
        foreach (var s in sections)
        {
            w.WriteStartObject();
            w.WriteString("artifact", s.Title);
            if (s.Artifact is { } a) { w.WriteString("category", a.Category); w.WriteString("table", a.TableName); }
            if (s.Filter is not null) w.WriteString("filter", s.Filter);
            w.WriteStartArray("rows");
            foreach (var row in s.Rows)
            {
                w.WriteStartObject();
                w.WriteNumber("rowid", row.RowId);
                for (var i = 0; i < s.Columns.Count; i++) w.WriteString(s.Columns[i].Display, ValueFormatter.Format(row.Values[i], s.Columns[i]));
                if (s.Tags?.GetValueOrDefault(row.RowId) is { } t) { w.WriteString("_tag", t.Tag); w.WriteString("_note", t.Note); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

/// <summary>
/// Shared plumbing for the HTML reports: page head, case information, the per-artifact origin block,
/// and media copied into a "&lt;report&gt;_files" folder beside the page so the report stays portable.
/// </summary>
sealed class HtmlReport : IDisposable
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    readonly StreamWriter _w;
    readonly CaseSession _s;
    readonly string _path, _tmp, _filesDir, _filesName;
    readonly Dictionary<string, (string? Url, string? Error)> _copied = [];
    bool _completed;

    // Written to a .tmp file and renamed by Complete(), so a failed export never leaves a page that looks finished.
    public HtmlReport(string path, CaseSession s, string title, string extraCss = "", bool caseInfo = true)
    {
        _s = s;
        _path = Path.GetFullPath(path);
        _tmp = _path + ".tmp";
        _filesName = Path.GetFileNameWithoutExtension(path) + "_files";
        _filesDir = Path.Combine(Path.GetDirectoryName(_path)!, _filesName);
        _w = new StreamWriter(_tmp, false, Encoding.UTF8);
        _w.WriteLine($$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(title)}}</title><style>
            body{font:13px Segoe UI,Arial,sans-serif;margin:24px;color:#1b1b1b}
            h1{font-size:20px;border-bottom:3px solid #0b4f8a;padding-bottom:6px}
            h2{font-size:15px;background:#0b4f8a;color:#fff;padding:6px 10px;margin-top:28px}
            table{border-collapse:collapse;width:100%;margin-bottom:8px}
            th,td{border:1px solid #c9d3de;padding:4px 6px;text-align:left;vertical-align:top;word-break:break-word}
            th{background:#e8eef5}
            .meta td:first-child{font-weight:600;width:220px;background:#f5f7fa}
            .origin{background:#f5f7fa;border-left:4px solid #0b4f8a;padding:6px 10px;margin:6px 0 10px;font-size:12px}
            .origin b{display:inline-block;min-width:130px}
            .media img{max-width:220px;max-height:220px;display:block;margin:2px 0}
            .media video{max-width:320px;display:block}
            .src{color:#5b6672;font-size:11px}
            @media print{h2,.origin{-webkit-print-color-adjust:exact;print-color-adjust:exact} }
            {{extraCss}}
            </style></head><body>
            <h1>{{E(title)}}</h1>
            """);
        if (!caseInfo) return;
        _w.WriteLine("<table class=\"meta\">");
        WriteProvenance();
        _w.WriteLine($"<tr><td>Report generated</td><td>{E(ValueFormatter.FormatTime(DateTimeOffset.UtcNow))}</td></tr></table>");
    }

    /// <summary>Device, data source and case rows (into an open "meta" table): the evidence, not the examiner's machine.</summary>
    public void WriteProvenance()
    {
        foreach (var g in EvidenceOrigin.Provenance(_s).GroupBy(x => x.Group))
        {
            _w.WriteLine($"<tr><th colspan=\"2\">{E(g.Key)}</th></tr>");
            foreach (var x in g) _w.WriteLine($"<tr><td>{E(x.Name)}</td><td>{E(x.Value)}</td></tr>");
        }
        if (_s.Package is not { } p) return;
        _w.WriteLine("<tr><th colspan=\"2\">Portable case</th></tr>");
        _w.WriteLine($"<tr><td>Integrity</td><td>{(p.Intact ? "intact: every file matches SHA256SUMS.txt" : $"ALTERED: {p.Problems.Count} problem(s)")}</td></tr>");
        _w.WriteLine($"<tr><td>Signed by key</td><td>{E(p.SignatureValid switch { true => p.Fingerprint, false => "signature not valid", null => "not signed" })}</td></tr>");
        _w.WriteLine($"<tr><td>Content root</td><td>{E(p.Root)}</td></tr>");
    }

    static readonly HashSet<string> Reserved = new(["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>A file name from device data: no path or control characters, no surrogates (they do not survive URL
    /// escaping, so a split emoji would break links), and never a Windows device name.</summary>
    public static string SafeName(string name)
    {
        var s = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsSurrogate(c) || char.IsControl(c) ? '_' : c));
        return Reserved.Contains(s.Split('.')[0].Trim()) ? "_" + s : s;
    }

    public void Write(string html) => _w.Write(html);
    public void WriteLine(string html) => _w.WriteLine(html);

    /// <summary>Where the rows came from: the LEAPP module, the artifact table and the source files it parsed.</summary>
    public void Origin(ArtifactInfo? a, string? filter)
    {
        if (a is null) return;
        var sb = new StringBuilder("<div class=\"origin\">");
        void Line(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) sb.Append($"<div><b>{E(k)}</b> {E(v)}</div>"); }
        var (group, type) = MacroCategories.Classify(a);
        Line("Category", $"{group.Name} › {type} › {a.Category}");
        Line("LEAPP module", a.Module);
        Line("Artifact table", a.TableName);
        Line("Source file(s) on the device", EvidenceOrigin.Join(EvidenceOrigin.Sources(a)));
        Line("Filter", filter);
        _w.WriteLine(sb.Append("</div>").ToString());
    }

    /// <summary>Copies one media file beside the report (once): its relative URL, or why it is not there.</summary>
    (string? Url, string? Error) Copy(MediaRef m)
    {
        if (!m.Exists) return (null, "file not in case");
        if (_copied.TryGetValue(m.FullPath!, out var done)) return done;
        var name = $"{_copied.Count:D5}_{SafeName(Path.GetFileName(m.FullPath!))}";
        try
        {
            Directory.CreateDirectory(_filesDir);
            var dest = Path.Combine(_filesDir, name);
            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal); // a re-export over a read-only copy
            using (var src = m.OpenRead()) using (var d = File.Create(dest)) src.CopyTo(d);
            File.SetAttributes(dest, FileAttributes.Normal);
            done = (Uri.EscapeDataString(_filesName) + "/" + Uri.EscapeDataString(name), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { done = (null, "copy failed: " + e.Message); }
        _copied[m.FullPath!] = done;
        return done;
    }

    // Opened from file:// these would run script; they are offered as downloads, never rendered or opened.
    static readonly string[] Active = [".svg", ".svgz", ".htm", ".html", ".xhtml", ".xml", ".js", ".hta", ".mht", ".mhtml", ".pdf"];

    /// <summary>The copied file's relative URL, null when it is not in the case.</summary>
    public string? Url(MediaRef m) => Copy(m).Url;

    /// <summary>Inline image, player or link for each media item, with its original device path as origin.</summary>
    public string Media(IEnumerable<MediaRef> media)
    {
        var sb = new StringBuilder();
        foreach (var m in media)
        {
            var (url, error) = Copy(m);
            var u = E(url);
            var active = Active.Contains(Path.GetExtension(m.FullPath ?? m.Name).ToLowerInvariant()) || (m.MimeType ?? "").Contains("svg");
            sb.Append("<div class=\"media\">");
            sb.Append(url is null ? $"<span>&#128206; {E(m.Name)} ({E(error)})</span>" : (active ? "file" : m.Kind) switch
            {
                "image" => $"<a href=\"{u}\" target=\"_blank\"><img src=\"{u}\" alt=\"{E(m.Name)}\" loading=\"lazy\"></a>",
                "video" => $"<video src=\"{u}\" controls preload=\"metadata\"></video>",
                "audio" => $"<audio src=\"{u}\" controls preload=\"none\"></audio>",
                _ => $"<a href=\"{u}\" download>&#128206; {E(m.Name)}</a>",
            });
            if (!string.IsNullOrEmpty(m.SourcePath)) sb.Append($"<div class=\"src\">Source on the device: {E(EvidenceOrigin.DevicePath(m.SourcePath))}</div>");
            sb.Append("</div>");
        }
        return sb.ToString();
    }

    public void Complete()
    {
        _w.WriteLine("</body></html>");
        _w.Dispose();
        File.Move(_tmp, _path, true);
        _completed = true;
    }

    public void Dispose()
    {
        if (_completed) return;
        _w.Dispose();
        try { File.Delete(_tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class HtmlReportExporter
{
    /// <summary>A file name from device data, safe on Windows.</summary>
    public static string SafeName(string name) => HtmlReport.SafeName(name);

    public static void Write(string path, CaseSession session, IEnumerable<ExportSection> sections, string title)
    {
        static string E(string? s) => HtmlReport.E(s);
        using var report = new HtmlReport(path, session, title);
        using var con = session.OpenDb();
        var resolver = new MediaResolver(session);

        foreach (var sec in sections)
        {
            var tagged = sec.Tags is { Count: > 0 };
            long n = 0; // rows stream from the database: a 20M-row artifact never sits in memory
            report.WriteLine($"<h2>{E(sec.Title)}</h2>");
            report.Origin(sec.Artifact, sec.Filter);
            report.Write("<table><tr>");
            foreach (var c in sec.Columns) report.Write($"<th>{E(c.Display)}</th>");
            if (tagged) report.Write("<th>Tag</th><th>Note</th>");
            report.WriteLine("</tr>");
            foreach (var row in sec.Rows)
            {
                n++;
                report.Write("<tr>");
                for (int i = 0; i < sec.Columns.Count; i++)
                    report.Write(sec.Columns[i].IsMedia
                        ? $"<td>{report.Media(resolver.Resolve(con, row.Values[i]))}</td>"
                        : $"<td>{E(ValueFormatter.Format(row.Values[i], sec.Columns[i]))}</td>");
                if (tagged)
                {
                    var t = sec.Tags!.GetValueOrDefault(row.RowId);
                    report.Write($"<td>{E(t?.Tag)}</td><td>{E(t?.Note)}</td>");
                }
                report.WriteLine("</tr>");
            }
            report.WriteLine("</table>");
            report.WriteLine($"<div class=\"src\">{n:N0} records exported" +
                             (sec.Artifact is { } a ? $" of {a.RecordCount:N0}" : "") + "</div>");
        }
        report.Complete();
    }
}

