using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>A chat participant with the contact record their identifier matched, and its picture.</summary>
public sealed record PersonView(ChatParticipant P, ContactMatch? Contact, ImageSource? Picture)
{
    public string? ContactNote => Contact is null ? null
        : (Contact.Fields.FirstOrDefault(f => f.Field.Contains("name", StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } n ? $"{n} · " : "") +
          $"in {Contact.Artifact.Name}";
}

/// <summary>One chat in its own tab: participants, the messages as bubbles or as a table, and the selected message's
/// details in the right pane.</summary>
public partial class ChatViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly CaseSession _s;
    readonly bool _truncated;

    public ArtifactInfo Artifact { get; }
    public ConversationItem Chat { get; }
    public IReadOnlyList<ColumnInfo> Columns { get; }
    public IReadOnlyList<GridRow> Rows { get; }
    public string Subtitle { get; }

    [ObservableProperty] private MessageItem? selectedMessage;
    [ObservableProperty] private GridRow? selectedRow;
    [ObservableProperty] private IReadOnlyList<PersonView> people = [];
    [ObservableProperty] private PersonView? selectedPerson;
    /// <summary>Search inside the chat: matching messages are outlined, Prev/Next step through them, and with
    /// OnlyMatches the bubbles and the table show the matches only.</summary>
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool onlyMatches;
    [ObservableProperty] private IReadOnlySet<long> matchIds = new HashSet<long>();
    [ObservableProperty] private string matchText = "";
    [ObservableProperty] private IReadOnlyList<MessageItem> messages;
    [ObservableProperty] private IReadOnlyList<GridRow> shownRows;

    public ChatViewModel(MainViewModel main, CaseSession s, ArtifactInfo a, ConversationItem chat, IReadOnlyList<ColumnInfo> cols, bool truncated)
    {
        _main = main;
        _s = s;
        _truncated = truncated;
        Artifact = a;
        Chat = chat;
        Columns = cols;
        var tags = s.Tags.ForTable(a.TableName);
        Rows = chat.Messages.Select(m => new GridRow(m.RowId, m.Raw.Select((v, i) => ValueFormatter.Format(v, cols[i])).ToArray(), m.Raw,
            tags.GetValueOrDefault(m.RowId)?.Tag)).ToList();
        (messages, shownRows) = (chat.Messages, Rows);
        tagsByRow = Rows.Where(r => r.Tag is not null).ToDictionary(r => r.RowId, r => r.Tag!);
        var sent = chat.Messages.Count(m => m.IsSent);
        Subtitle = $"{a.Category} · {chat.Messages.Count:N0} messages ({sent:N0} sent, {chat.Messages.Count - sent:N0} received) · " +
                   $"{chat.FirstTime} → {chat.LastTime}";
        People = chat.Participants.Select(p => new PersonView(p, null, null)).ToList();
        Translation = new TranslationView(s, a);
        checkedIds = chat.Messages.Select(m => m.RowId).ToHashSet();
        _ = LoadPeopleAsync();
        _ = Translation.LoadAsync();
    }

    async Task LoadPeopleAsync()
    {
        var ps = Chat.Participants;
        try
        {
            People = await Task.Run(() =>
            {
                using var con = _s.OpenDb();
                var dir = new ContactDirectory(con, _s, Artifact);
                return ps.Select(p => p.LookupId is { } id && dir.Find(id) is { } c
                    ? new PersonView(p, c, c.Picture is { } pic ? GalleryViewModel.Thumb(_s, pic, 96) : null)
                    : new PersonView(p, null, null)).ToList();
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { } // contacts unreadable: participants stay without them
    }

    partial void OnSelectedPersonChanged(PersonView? value)
    {
        if (value is not null) _main.Details.ShowPerson(Artifact, Chat, value.P, value.Contact);
    }

    partial void OnSelectedMessageChanged(MessageItem? value)
    {
        if (value is null) return;
        var key = value.SenderId != "" ? value.SenderId : value.Sender;
        var from = People.FirstOrDefault(p => value.IsSent ? p.P.IsOwner : !p.P.IsOwner && (p.P.Id != "" ? p.P.Id : p.P.Name) == key);
        _main.Details.ShowMessage(_s, Artifact, Columns, Chat, value, from?.Contact);
        SelectedRow = Rows.FirstOrDefault(r => r.RowId == value.RowId);
    }

    partial void OnSelectedRowChanged(GridRow? value)
    {
        if (value is not null) SelectedMessage = Chat.Messages.FirstOrDefault(m => m.RowId == value.RowId);
    }

    partial void OnSearchChanged(string value) => ApplySearch();
    partial void OnOnlyMatchesChanged(bool value) => ApplySearch();

    /// <summary>Filters on the messages' properties: direction, sender, what they hold, a date range.</summary>
    public static string[] Directions { get; } = ["All", "Sent", "Received"];
    public static string[] Kinds { get; } = ["All", "Text only", "Any media", "Images", "Videos", "Audio", "Locations", "Replies", "System messages"];
    public IReadOnlyList<string> Senders => ["All", .. People.Select(p => p.P.Display)];
    [ObservableProperty] private string direction = "All";
    [ObservableProperty] private string kind = "All";
    [ObservableProperty] private string sender = "All";
    [ObservableProperty] private DateTime? from;
    [ObservableProperty] private DateTime? to;
    [ObservableProperty] private string filterText = "";
    partial void OnDirectionChanged(string value) => ApplySearch();
    partial void OnKindChanged(string value) => ApplySearch();
    partial void OnSenderChanged(string value) => ApplySearch();
    partial void OnFromChanged(DateTime? value) => ApplySearch();
    partial void OnToChanged(DateTime? value) => ApplySearch();
    partial void OnPeopleChanged(IReadOnlyList<PersonView> value) => OnPropertyChanged(nameof(Senders));

    [RelayCommand]
    void ClearFilters() { Direction = Kind = Sender = "All"; From = To = null; }

    bool Passes(MessageItem m)
    {
        if (Direction == "Sent" && !m.IsSent || Direction == "Received" && m.IsSent) return false;
        if (Sender != "All" && People.FirstOrDefault(p => p.P.Display == Sender) is { } p &&
            !(p.P.IsOwner ? m.IsSent : !m.IsSent && (p.P.Id != "" ? m.SenderId == p.P.Id : m.Sender == p.P.Name))) return false;
        var ok = Kind switch
        {
            "Text only" => m.Media.Count == 0 && m.Text != "",
            "Any media" => m.Media.Count > 0,
            "Images" => m.Media.Any(x => x.IsImage),
            "Videos" => m.Media.Any(x => x.Kind == "video"),
            "Audio" => m.Media.Any(x => x.Kind == "audio") || m.Text.StartsWith("Voice message", StringComparison.Ordinal),
            "Locations" => m.Location is not null,
            "Replies" => m.QuoteText != "",
            "System messages" => m.IsSystem,
            _ => true,
        };
        if (!ok) return false;
        if ((From ?? To) is null) return true;
        if (!DateTime.TryParse(m.Time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var t)) return false;
        return (From is not { } f || t >= f.Date) && (To is not { } e || t < e.Date.AddDays(1));
    }

    /// <summary>A message matches the search (<see cref="SearchQuery"/> syntax) over its formatted columns.</summary>
    void ApplySearch()
    {
        var q = Search.Trim();
        var query = SearchQuery.Parse(q);
        var hits = q == "" ? [] : Rows.Where(r => query.Matches(r.Cells)).Select(r => r.RowId).ToHashSet();
        MatchIds = hits;
        var only = OnlyMatches && q != "";
        var selected = SelectedMessage; // a new list resets the ListBox selection
        var shown = Chat.Messages.Where(m => (!only || hits.Contains(m.RowId)) && Passes(m)).ToList();
        var ids = shown.Select(m => m.RowId).ToHashSet();
        Messages = shown.Count == Chat.Messages.Count ? Chat.Messages : shown;
        ShownRows = shown.Count == Chat.Messages.Count ? Rows : Rows.Where(r => ids.Contains(r.RowId)).ToList();
        FilterText = shown.Count == Chat.Messages.Count ? "" : $"{shown.Count:N0} of {Chat.Messages.Count:N0} shown";
        if (selected is not null && Messages.Contains(selected)) SelectedMessage = selected;
        MatchText = q == "" ? "" : (hits.Count == 0 ? "No matches" : $"{hits.Count:N0} matches") + (_truncated ? " (loaded messages only)" : "");
    }

    public CaseSession Session => _s;
    public TranslationView Translation { get; }

    /// <summary>Messages checked with their boxes (bubbles and table), all at first: what Translate, Summarize and AI act
    /// on, among those shown.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CheckedText), nameof(HasChecked))] private IReadOnlySet<long> checkedIds = new HashSet<long>();
    public string CheckedText => CheckedIds.Count == Chat.Messages.Count ? $"All {CheckedIds.Count:N0} checked" : $"{CheckedIds.Count:N0} checked";
    public bool HasChecked => CheckedIds.Count > 0;

    /// <summary>The messages' tags, for the tag icon beside the bubbles.</summary>
    [ObservableProperty] private IReadOnlyDictionary<long, string> tagsByRow;

    public void UpdateTag(IReadOnlyCollection<long> ids, string? tag)
    {
        foreach (var r in Rows.Where(r => ids.Contains(r.RowId))) r.Tag = tag;
        TagsByRow = Rows.Where(r => r.Tag is not null).ToDictionary(r => r.RowId, r => r.Tag!);
    }

    public void Check(IEnumerable<long> ids, bool on)
    {
        var set = CheckedIds.ToHashSet();
        foreach (var id in ids) if (on) set.Add(id); else set.Remove(id);
        CheckedIds = set;
    }

    [RelayCommand] void ToggleCheck(long id) => Check([id], !CheckedIds.Contains(id));
    [RelayCommand] void ClearChecks() => CheckedIds = new HashSet<long>();
    [RelayCommand] void CheckAll() => CheckedIds = Chat.Messages.Select(m => m.RowId).ToHashSet();

    /// <summary>How an AI result names this chat in its scope, so the chat's details find its summaries.</summary>
    public static string ScopeTag(ConversationItem c, ArtifactInfo a) => $"of the chat {c.Label} ({a.Name})";

    public AiScope ScopeOf(IReadOnlyList<MessageItem> ms, string what) => new(
        $"{ms.Count:N0} {what}messages {ScopeTag(Chat, Artifact)}" +
        (ms.Count == Chat.Messages.Count ? " · the whole chat" : ms.Count > 0 ? $" · {ms[0].Time} – {ms[^1].Time}" : ""),
        [new AiSelection(Artifact, ms.Select(m => m.RowId).ToList())], ms.SelectMany(m => m.Media).DistinctBy(m => m.RefId).ToList());

    /// <summary>The checked messages among those shown (filters, search), with their attachments; null, said in the
    /// status bar, when none is.</summary>
    AiScope? Scope()
    {
        var ms = Messages.Where(m => CheckedIds.Contains(m.RowId)).ToList();
        if (ms.Count == 0) { _main.Status = "Nothing checked: check the messages first"; return null; }
        return ScopeOf(ms, ms.Count == Chat.Messages.Count ? "" : "checked ");
    }

    [RelayCommand] void Ai() { if (Scope() is { } sc) _main.OpenAi(_s, sc); }
    [RelayCommand] Task Translate() => Scope() is { } sc ? _main.TranslateQuietly(_s, sc, Translation) : Task.CompletedTask;
    [RelayCommand] void Summarize() { if (Scope() is { } sc) _ = _main.OpenAi(_s, sc)?.SummarizeCommand.ExecuteAsync(null); }

    [RelayCommand] void NextMatch() => StepMatch(+1);
    [RelayCommand] void PrevMatch() => StepMatch(-1);

    /// <summary>Selects the next (or previous) match after the selected message, wrapping around.</summary>
    void StepMatch(int dir)
    {
        var hits = Chat.Messages.Select((m, i) => (m, i)).Where(x => MatchIds.Contains(x.m.RowId)).ToList();
        if (hits.Count == 0) return;
        var cur = SelectedMessage is { } sel ? Chat.Messages.IndexOf(sel) : -1;
        var k = dir > 0 ? hits.FindIndex(h => h.i > cur) : hits.FindLastIndex(h => h.i < cur);
        if (k < 0) k = dir > 0 ? 0 : hits.Count - 1;
        SelectedMessage = hits[k].m;
        MatchText = $"{k + 1:N0} of {hits.Count:N0}";
    }

    /// <summary>The map of the artifact the position was read from (the chat, or e.g. WeChat's Shared Locations),
    /// centred on it.</summary>
    [RelayCommand]
    void OpenOnMap(MessageItem? m)
    {
        if (m?.Location is { } p && _s.Manifest.Artifacts.FirstOrDefault(x => x.TableName == p.TableName) is { } src)
            _main.NavigateToMap(src, p);
    }

    /// <summary>A photo, video or audio of a bubble in its own window: enlarged or playable, with its details and the message.</summary>
    [RelayCommand]
    void OpenMediaWindow(MediaRef? m)
    {
        if (m is null) return;
        var msg = Chat.Messages.FirstOrDefault(x => x.Media.Contains(m));
        Views.RecordWindow.ShowMedia(_main, m, msg is null ? null : new MediaUse(Artifact, msg.RowId, Chat.Label, msg.Sender, msg.IsSent, null));
    }

    [RelayCommand]
    async Task Export()
    {
        var chats = await ConversationViewModel.Complete(_main, _s, Artifact, _truncated, [Chat]);
        _main.Status = await ConversationViewModel.ExportChats(_s, Artifact, chats, null) ?? "Export cancelled";
    }
}
