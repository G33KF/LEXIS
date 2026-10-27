using System.Windows.Media;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>An account as a row of the accounts table: the card (<see cref="AccountsOverview"/>), its picture when the
/// extraction has it, and its main values one per column.</summary>
public sealed record AccountCardView(AccountCard Card, ImageSource? Picture)
{
    const long MaxPicture = 5_000_000;

    string Of(params string[] labels) =>
        string.Join(", ", Card.Values.Where(v => labels.Contains(v.Label)).Select(v => v.Value).Distinct(StringComparer.OrdinalIgnoreCase));

    public string Name => Of("Full name") is { Length: > 0 } n ? n : Of("Nickname");
    public string Username => Of("Username");
    public string Phone => Of("Phone");
    public string Email => Of("E-mail");
    public string Ids => Of("UID", "DSID", "Account ID", "Account");
    public string Services => Of("Service");

    /// <summary>Shows every value of the account in the details, each with where it was read from, then its artifacts.</summary>
    public void Show(DetailsViewModel details) =>
        details.ShowFields(Card.Title, $"Account · {Card.Values.Count} values from {Card.Sources.Split(", ").Length} artifacts",
            [.. Card.Values.Select(v => KeyValuePair.Create(v.Caption, $"{v.Value}\n{v.Hint}")), KeyValuePair.Create("Artifacts", Card.Sources.Replace(", ", "\n"))]);

    /// <summary>The accounts of a device, read off the UI thread; each picture only from a file of the extraction,
    /// never fetched online.</summary>
    public static Task<IReadOnlyList<AccountCardView>> LoadAsync(CaseSession s) => Task.Run<IReadOnlyList<AccountCardView>>(() =>
    {
        using var con = s.OpenDb();
        var files = s.Extraction;
        return AccountsOverview.Build(con, s.Manifest.Artifacts.Where(a => a.RecordCount > 0))
            .Select(c => new AccountCardView(c, files is null ? null : c.Pictures.Where(files.Contains)
                .Select(p => files.Read(p, MaxPicture) is { } b ? SqliteViewModel.Image(b) : null).FirstOrDefault(i => i is not null)))
            .ToList();
    });
}
