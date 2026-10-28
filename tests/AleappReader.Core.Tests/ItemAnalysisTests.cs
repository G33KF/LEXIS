using AleappReader.Core;

namespace AleappReader.Core.Tests;

public class ItemAnalysisTests
{
    static readonly AiProvider P = new("Local", AiKind.OpenAi, "http://localhost:1235/v1");
    const string K = "[media#x-1]";
    static AiStore Store() => new(Path.Combine(Path.GetTempPath(), $"ai-{Guid.NewGuid():N}.db"));

    [Fact]
    public void The_grid_and_chats_read_translations_as_the_examiner_left_them()
    {
        var s = Store();
        const string R1 = "[sms#1]", R2 = "[sms#2]";
        var a = s.Save(P, "m", "Translation", "p", "scope", $"{R1} hello", [R1], [], 0);
        var b = s.Save(P, "m", "Translation", "p", "scope", $"{R2} goodbye", [R2], [], 0);
        s.Revise(a.Id, AiStore.Edit, "hello there,\nfriend");
        s.Revise(b.Id, AiStore.Exclude);
        var t = AiText.Translations(AiText.AsRevised(s, [a, b]));
        Assert.Equal("hello there, friend", t[R1]);
        Assert.False(t.ContainsKey(R2));
        Assert.Equal("hello there, friend", Assert.Single(AiText.Notes(AiText.AsRevised(s, [a]), R1)).Text);
    }

    [Fact]
    public void Edit_exclude_and_restore_keep_the_model_output()
    {
        var s = Store();
        var r = s.Save(P, "m", "Explanation", "p", "scope", "model text", [K], [], 0);
        s.Revise(r.Id, AiStore.Edit, "examiner text");
        s.Revise(r.Id, AiStore.Exclude);
        var v = s.Revisions([r.Id])[r.Id];
        Assert.Equal("examiner text", v.Text);
        Assert.True(v.Excluded);
        s.Revise(r.Id, AiStore.Restore);
        Assert.False(s.Revisions([r.Id])[r.Id].Excluded);
        Assert.Equal("model text", Assert.Single(s.Citing(K)).Output);
        Assert.Equal(3, s.Revisions([r.Id])[r.Id].History.Count);
    }

    [Fact]
    public void Every_analysis_is_listed_and_the_summary_goes_stale_after_a_change()
    {
        var s = Store();
        var a = s.Save(P, "m", "Explanation", "p", "x", "first", [K], [], 0);
        s.Save(P, "m", "Explanation", "p", "x", "second", [K], [], 0);
        var all = AiText.Analyses(s, [K]);
        Assert.Equal(["second", "first"], all.Select(x => x.Shown));
        var sum = s.Save(P, "m", AiText.ItemSummary, "p", AiText.SummaryScope(all), "summary", [K], [], 0);
        Assert.DoesNotContain(AiText.Analyses(s, [K]), x => x.Result.Id == sum.Id);
        Assert.False(AiText.IsStale(sum, AiText.Analyses(s, [K])));

        s.Revise(a.Id, AiStore.Edit, "first, corrected");
        Assert.True(AiText.IsStale(sum, AiText.Analyses(s, [K])));
        Assert.Contains(AiText.Analyses(s, [K]), x => x.Shown == "first, corrected" && x.Revised);

        s.Revise(a.Id, AiStore.Exclude);
        Assert.Contains(AiText.Analyses(s, [K]), x => x.Result.Id == a.Id && x.Excluded);
    }

    [Fact]
    public void A_text_translation_is_stale_once_its_part_reads_differently()
    {
        var s = Store();
        s.Save(P, "m", AiText.TextTranslation, "p", AiText.PartScope("page 1", "مرحبا"), "Hello", [K], [], 0);
        Assert.False(AiText.PartTranslations(s, K, [("page 1", "مرحبا")])["page 1"].Stale);
        Assert.True(AiText.PartTranslations(s, K, [("page 1", "مرحبا بكم")])["page 1"].Stale);
        Assert.Equal("Translation", Assert.Single(AiText.Analyses(s, [K])).Kind);
    }

    [Fact]
    public void An_explanation_of_one_record_keeps_every_value_line_without_the_key()
    {
        var s = Store();
        const string R = "[get_chat#12]";
        s.Save(P, "m", "Explanation", "p", "x",
               $"{R} What it is: a received message\n{R} timestamp = 1700000000000 → Unix ms\n{R} status = 4 → delivered", [R], [], 0);
        Assert.Equal("What it is: a received message\ntimestamp = 1700000000000 → Unix ms\nstatus = 4 → delivered",
                     Assert.Single(AiText.Analyses(s, [R])).Text);
    }

    static ArtifactInfo A(string category, string name) => new() { Category = category, Name = name, TableName = name, Columns = [] };

    [Fact]
    public void Translation_and_summary_are_offered_by_what_the_artifact_holds()
    {
        var cookies = A("Chrome", "Chrome - Cookies");
        var sms = A("SMS", "SMS messages");
        Assert.False(MacroCategories.Written(cookies));
        Assert.True(MacroCategories.Written(sms));
        Assert.True(MacroCategories.Written(A("Notes", "Notes")));
        Assert.False(MacroCategories.Summarizable([cookies]));
        Assert.True(MacroCategories.Summarizable([sms]));
        Assert.True(MacroCategories.Summarizable([A("Call Log", "Call Log")]));
        Assert.True(MacroCategories.Summarizable([cookies, A("Chrome", "Chrome - Autofill")])); // several together: a timeline
    }
}
