using MailVault.Core;
using Xunit;

namespace MailVault.Tests;

/// <summary>Suchfilter gesendet:ja/nein und ordner:… (auch als Teil des Suchtexts).</summary>
public class SentAndFolderSearchTests
{
    static SearchCriteria P(string q) => SearchSyntax.Apply(new SearchCriteria { Query = q });

    [Fact]
    public void Syntax_understands_sent_and_folder_in_all_languages()
    {
        var c = P("gesendet:ja ordner:\"Projekte 2024\" angebot");
        Assert.True(c.Sent);
        Assert.Equal("Projekte 2024", c.FolderName);
        Assert.Equal("angebot", c.Query);
        Assert.False(P("sent:no").Sent);
        Assert.True(P("envoyé:oui").Sent);
        Assert.Equal("x", P("carpeta:x").FolderName);
        Assert.False(P("gesendet:nein").IsEmpty);
        Assert.Throws<ArgumentException>(() => P("gesendet:vielleicht"));
    }

    [Fact]
    public void Sent_means_sent_folder_or_own_address()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var inbox = archive.EnsureFolderPath(new[] { "ich@example.org", "INBOX" });
        var sent = archive.EnsureFolderPath(new[] { "ich@example.org", "Gesendete Elemente" });
        archive.StoreMessage(inbox, ArchiveTests.MakeMessage("Angebot erhalten", "a", from: "Händler <shop@example.com>", to: "ich@example.org"));
        archive.StoreMessage(sent, ArchiveTests.MakeMessage("Angebot verschickt", "b", from: "Ich <ich@example.org>", to: "kunde@example.com"));
        // Gesendet, aber im Posteingang abgelegt (z. B. Kopie an sich selbst)
        archive.StoreMessage(inbox, ArchiveTests.MakeMessage("Notiz an mich", "c", from: "ICH@example.org", to: "ich@example.org"));

        // Ohne bekannte eigene Adressen zählt nur der Gesendet-Ordner
        Assert.Equal("Angebot verschickt", Assert.Single(archive.Search(P("gesendet:ja"))).Subject);

        archive.OwnerAddresses = new[] { "ich@example.org", "kein-adresse" };
        Assert.Equal(2, archive.Search(P("gesendet:ja")).Count);
        Assert.Equal("Angebot erhalten", Assert.Single(archive.Search(P("gesendet:nein"))).Subject);
        Assert.Equal("Angebot verschickt", Assert.Single(archive.Search(P("gesendet:ja angebot"))).Subject);
    }

    [Fact]
    public void Folder_filter_includes_subfolders_and_combines_with_tree_scope()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var a = archive.EnsureFolderPath(new[] { "konto-a", "INBOX", "Projekte" });
        var aSub = archive.EnsureFolderPath(new[] { "konto-a", "INBOX", "Projekte", "Bau" });
        var b = archive.EnsureFolderPath(new[] { "konto-b", "Projekte" });
        var other = archive.EnsureFolderPath(new[] { "konto-a", "INBOX" });
        archive.StoreMessage(a, ArchiveTests.MakeMessage("Plan A", "x"));
        archive.StoreMessage(aSub, ArchiveTests.MakeMessage("Plan Bau", "x"));
        archive.StoreMessage(b, ArchiveTests.MakeMessage("Plan B", "x"));
        archive.StoreMessage(other, ArchiveTests.MakeMessage("Plan sonst", "x"));

        Assert.Equal(3, archive.Search(P("ordner:projekte plan")).Count);
        Assert.Empty(archive.Search(P("ordner:gibtsnicht plan")));
        // Auswahl im Ordnerbaum (FolderId) und ordner: wirken zusammen
        var konto = archive.EnsureFolderPath(new[] { "konto-a" });
        var scoped = SearchSyntax.Apply(new SearchCriteria { Query = "ordner:projekte plan", FolderId = konto, FolderRecurse = true });
        Assert.Equal(new[] { "Plan A", "Plan Bau" }, archive.Search(scoped).Select(m => m.Subject).OrderBy(s => s));
        // Nur Baumauswahl: Suche bleibt im Teilbaum
        Assert.Equal(3, archive.Search(new SearchCriteria { Query = "plan", FolderId = konto }).Count);
    }
}
