using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

/// <summary>The chat list of an artifact that declares data_views.conversation, optionally one local account's only.
/// Double-click opens a chat in its own tab.</summary>
public partial class ConversationViewModel : ObservableObject, IColumnFilters
{
    /// <summary>The chat list's columns that take a condition from their header (Sql = the column's SortMemberPath).</summary>
    public IReadOnlyList<ColumnInfo> ListColumns { get; } =
    [
        new("Label", "Chat", null), new("Type", "Type", null), new("Key", "Chat ID", null), new("ParticipantCount", "Participants", "number"),
        new("Messages.Count", "Messages", "number"), new("FirstTime", "First message", "datetime"),
        new("LastTime", "Last message", "datetime"), new("Preview", "Last text", null), new("Owner", "Account", null),
    ];
    const string AllAccounts = "All accounts";
    public static IReadOnlyList<string> TypeChoices { get; } = ["All chats", "Private", "Group"];
    readonly List<ColumnFilter> _columnFilters = [];

    const int MaxMessages = 50000; // ponytail: whole-artifact load; page by conversation if a case exceeds this
    readonly MainViewModel _main;
    readonly CaseSession _s;
    readonly ArtifactInfo _a;
    readonly string? _account;
    List<ConversationItem> _all = [];
    IReadOnlyList<ColumnInfo> _cols = [];
    bool _truncated;

    public ObservableCollection<ConversationItem> Conversations { get; } = [];
    public ArtifactInfo Artifact => _a;
    public CaseSession Session => _s;
    public IReadOnlyList<ColumnInfo> Columns => _cols;
    public string Title => _a.Name;
    public string Subtitle { get; }
    [ObservableProperty] private ConversationItem? selected;
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private string info = "Loading…";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasColumnFilters))] private int columnFilterCount;
    public bool HasColumnFilters => ColumnFilterCount > 0;
    public string CountText => Conversations.Count == _all.Count ? "" : $"{Conversations.Count:N0} of {_all.Count:N0} chats shown";
    /// <summary>The local accounts (SIM, number, backup…) the chats belong to, when the list is not already one account's:
    /// the list splits by them as the Chats tree does.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAccounts))] private IReadOnlyList<string> accountChoices = [];
    [ObservableProperty] private string accountChoice = AllAccounts;
    public bool HasAccounts => AccountChoices.Count > 0;
    [ObservableProperty] private string typeChoice = "All chats";
    /// <summary>Some chat names the user's side (the local account or the sender of the sent messages).</summary>
    [ObservableProperty] private bool hasOwner;

    public ConversationViewModel(MainViewModel main, CaseSession s, ArtifactInfo a, string? account = null, string? accountLabel = null)
    {
        _main = main;
        _s = s;
        _a = a;
        _account = account;
        Subtitle = account is null ? $"{a.Category} · all accounts" : $"{a.Category} · account {accountLabel ?? account}";
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var set = await Task.Run(() =>
        {
            using var con = _s.OpenDb();
            return ConversationBuilder.Build(con, _s, _a, MaxMessages, account: _account);
        });
        (_all, _cols, _truncated) = (set.Conversations, set.Columns, set.Truncated);
        if (_account is null && _all.Any(c => c.Account != ""))
            AccountChoices = [AllAccounts, .. _all.Select(c => c.AccountDisplay).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
        HasOwner = _all.Any(c => c.Owner != "");
        Info = $"{_all.Count:N0} chats · {_all.Sum(c => c.Messages.Count):N0} messages" + (set.Truncated ? $" (first {MaxMessages:N0})" : "") +
               " — double-click a chat to open it";
        ApplyFilter();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnAccountChoiceChanged(string value) => ApplyFilter();
    partial void OnTypeChoiceChanged(string value) => ApplyFilter();

    static bool Matches(ConversationItem c, string filter) => filter == "" || c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                              c.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                              c.Messages.Any(m => m.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));

    static string ValueOf(ConversationItem c, string sql) => sql switch
    {
        "Label" => c.Label, "Key" => c.Key, "ParticipantCount" => c.ParticipantCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "Messages.Count" => c.Messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "FirstTime" => c.FirstTime, "LastTime" => c.LastTime, "Preview" => c.Preview, "Owner" => c.Owner, "Type" => c.Type, _ => "",
    };

    void ApplyFilter()
    {
        Conversations.Clear();
        foreach (var c in _all.Where(c => Matches(c, Filter) && (AccountChoice is null or AllAccounts || c.AccountDisplay == AccountChoice) && (TypeChoice == TypeChoices[0] || c.Type == TypeChoice) && _columnFilters.All(f => f.Matches(ValueOf(c, f.Sql), f.Sql.EndsWith("Time")))))
            Conversations.Add(c);
        OnPropertyChanged(nameof(CountText));
    }

    public ColumnFilter? FilterOf(string sqlColumn) => _columnFilters.FirstOrDefault(f => f.Sql == sqlColumn);

    public void SetColumnFilter(string sqlColumn, ColumnFilter? f)
    {
        _columnFilters.RemoveAll(x => x.Sql == sqlColumn);
        if (f is not null) _columnFilters.Add(f);
        ColumnFilterCount = _columnFilters.Count;
        ApplyFilter();
    }

    /// <summary>"Participants between 3 and 10", in words for the export's selection line.</summary>
    string Describe(ColumnFilter f)
    {
        var col = ListColumns.First(c => c.Sql == f.Sql);
        string V(string? v) => col.IsDateTime && long.TryParse(v, out var e) ? ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(e)) : v ?? "";
        return f.Op switch
        {
            "between" => $"{col.Display} between {(f.Value is null ? "…" : V(f.Value))} and {(f.Value2 is null ? "…" : V(f.Value2))}",
            "empty" or "notempty" => $"{col.Display} {(f.Op == "empty" ? "is empty" : "is not empty")}",
            _ => $"{col.Display} {f.Op switch { "notcontains" => "does not contain", "equals" => "is", _ => "contains" }} {f.Value}",
        };
    }

    [RelayCommand]
    void ClearColumnFilters()
    {
        _columnFilters.Clear();
        ColumnFilterCount = 0;
        ApplyFilter();
    }

    partial void OnSelectedChanged(ConversationItem? value)
    {
        if (value is null) return;
        var sent = value.Messages.Count(m => m.IsSent);
        _main.Details.ShowFields(value.Label, $"{_a.Category} chat", [
            new("Conversation ID", value.Key),
            new("Type", value.Type),
            new("Local account", value.Participants.FirstOrDefault(p => p.IsOwner)?.Display ?? ""),
            new("Participants", string.Join("\n", value.Participants.Select(p => $"{p.Display} — {p.Messages:N0} messages{(p.IsOwner ? " (local account)" : "")}"))),
            new("Messages", $"{value.Messages.Count:N0} ({sent:N0} sent, {value.Messages.Count - sent:N0} received) · {value.Messages.Sum(m => m.Media.Count):N0} media"),
            new("First message", value.FirstTime),
            new("Last message", value.LastTime),
            new("Source file(s) on the device", EvidenceOrigin.Join(EvidenceOrigin.Sources(_a))),
        ], Summaries(value));
        _ = ShowPicturesAsync(value, _main.Details.Shown);
    }

    /// <summary>The profile pictures of the chat's participants (and of a group's own record) that their identifiers
    /// match in the contact records, added to the details once found.</summary>
    async Task ShowPicturesAsync(ConversationItem c, int shown)
    {
        List<(string Who, MediaRef Pic)> found;
        try
        {
            found = await Task.Run(() =>
            {
                using var con = _s.OpenDb();
                var dir = new ContactDirectory(con, _s, _a); // ponytail: rebuilt per chat clicked; keep one if contact tables get big
                var ids = c.Participants.Where(p => p.LookupId is not null).Select(p => (Who: p.Display, Id: p.LookupId!)).ToList();
                if (!ids.Any(p => p.Id == c.Key)) ids.Insert(0, ("The chat itself", c.Key));
                return ids.Select(p => (p.Who, dir.Find(p.Id)?.Picture)).Where(x => x.Picture is not null).Select(x => (x.Who, x.Picture!)).ToList();
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return; } // contacts unreadable: no pictures
        if (found.Count == 0 || _main.Details.Shown != shown) return; // the details moved on
        var at = _main.Details.Fields.TakeWhile(f => !(f.Header && f.Name == "AI")).Count();
        _main.Details.Fields.Insert(at, new Field("Profile pictures", string.Join("\n", found.Select(f => $"{f.Who} — {f.Pic.Name}"))));
        foreach (var f in found) _main.Details.Media.Add(f.Pic);
    }

    /// <summary>The summaries made of this chat, newest first, each with the messages it covers.</summary>
    List<AiNote> Summaries(ConversationItem c)
    {
        var tag = ChatViewModel.ScopeTag(c, _a);
        try
        {
            return [.. AiStore.For(_s).Results("Summary").Select(r => (r, i: r.Scope.IndexOf(tag, StringComparison.Ordinal))).Where(x => x.i >= 0)
                .Select(x => (x.r, Span: string.Join(" · ", new[] { x.r.Scope[..x.i].Trim(), x.r.Scope[(x.i + tag.Length)..].Trim(' ', '·') }.Where(p => p != "")))) // "120 messages · 1/3 – 5/3"
                .Select(x => new AiNote($"Summary · {x.Span}", x.r.Output, x.r.Label))];
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return []; } // AI base unreadable: no summaries shown
    }

    [RelayCommand]
    void OpenChat(ConversationItem? c)
    {
        c ??= Selected;
        if (c is not null) _main.OpenChat(new ChatViewModel(_main, _s, _a, c, _cols, _truncated));
    }

    /// <summary>The view holds at most MaxMessages; an export must carry whole chats, so a truncated view is re-read in full.</summary>
    public static async Task<IReadOnlyList<ConversationItem>> Complete(MainViewModel main, CaseSession s, ArtifactInfo a, bool truncated,
        IReadOnlyList<ConversationItem> shown)
    {
        if (!truncated) return shown;
        main.Status = "Reading the complete conversations for export…";
        var keys = shown.Select(c => (c.Account, c.Key)).ToHashSet();
        return await Task.Run(() =>
        {
            using var con = s.OpenDb();
            return ConversationBuilder.Build(con, s, a, int.MaxValue).Conversations.Where(c => keys.Contains((c.Account, c.Key))).ToList();
        });
    }

    [RelayCommand]
    async Task ExportChat()
    {
        if (Selected is not { } sel) return;
        _main.Status = await ExportChats(_s, _a, await Complete(_main, _s, _a, _truncated, [sel]), null) ?? "Export cancelled";
    }

    [RelayCommand]
    async Task ExportShown()
    {
        var filters = string.Join("; ", new[] { Filter == "" ? "" : $"text {Filter}", TypeChoice == TypeChoices[0] ? "" : $"{TypeChoice.ToLowerInvariant()} chats" }.Concat(_columnFilters.Select(Describe)).Where(x => x != ""));
        var chats = await Complete(_main, _s, _a, _truncated, Conversations.ToList());
        _main.Status = await ExportChats(_s, _a, chats, filters == "" ? null : $"Chat list filter: {filters} (all messages of each listed chat)")
                       ?? "Export cancelled";
    }

    /// <summary>One chat: a Save dialog. Several: a new subfolder of the chosen folder, one HTML page per chat.
    /// Returns the status line, or null if cancelled.</summary>
    public static async Task<string?> ExportChats(CaseSession s, ArtifactInfo a, IReadOnlyList<ConversationItem> chats, string? selection)
    {
        if (chats.Count == 0 || Views.ChatExportWindow.Ask(a, chats.Count) is not { } o) return null;
        if (chats.Count == 1)
        {
            var save = new SaveFileDialog { Filter = "HTML chat report (*.html)|*.html", FileName = ChatHtmlExporter.FileName(chats[0].Label) + ".html" };
            if (save.ShowDialog() != true) return null;
            await Task.Run(() => ChatHtmlExporter.Write(save.FileName, s, a, chats[0], selection, o));
            ExportLog.Add($"{a.Name}: chat {chats[0].Label} (HTML)", save.FileName, selection ?? "the whole chat", s);
            return $"Exported chat to {save.FileName}";
        }
        var folder = new OpenFolderDialog { Title = $"Folder for {chats.Count:N0} chat reports (a new subfolder is created)" };
        if (folder.ShowDialog() != true) return null;
        var dir = System.IO.Path.Combine(folder.FolderName, HtmlReportExporter.SafeName(a.Name) + $" chats {DateTime.Now:yyyy-MM-dd HHmmss}");
        System.IO.Directory.CreateDirectory(dir);
        var (written, failed) = await Task.Run(() => ChatHtmlExporter.WriteEach(dir, s, a, chats, selection, o));
        ExportLog.Add($"{a.Name}: chats (HTML)", dir, $"{written.Count:N0} chats" + (failed.Count > 0 ? $", {failed.Count} failed" : ""), s);
        return $"Exported {written.Count:N0} chats to {dir}" + (failed.Count > 0 ? $" — {failed.Count} failed: {string.Join("; ", failed)}" : "");
    }
}
