using System.Text.RegularExpressions;
using MailVault.Core;
using MailVault.Core.Sources;
using Xunit;

namespace MailVault.Tests;

/// <summary>Zusage: MailVault löscht oder verändert nie etwas im Postfach. Diese Tests verhindern, dass sich das ändert.</summary>
public class ReadOnlySourceTests
{
    [Fact]
    public void Sources_offer_no_way_to_modify_the_mailbox()
    {
        var members = typeof(IMailSource).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(members, n => Regex.IsMatch(n, "Delete|Remove|Move|Expunge|Flag|Store", RegexOptions.IgnoreCase));
        Assert.DoesNotContain(typeof(ArchiveProfile).GetProperties(), p => p.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_never_calls_modifying_mail_protocol_commands()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MailVault.Core", "Sources"));
        Assert.True(Directory.Exists(dir), dir);
        string[] forbidden =
        {
            "FolderAccess.ReadWrite", "MessageFlags.Deleted", "ExpungeAsync", "Expunge(", "DeleteMessage", "AddFlags", "SetFlags",
            "RemoveFlags", "MoveToAsync", "AppendAsync", "ReplaceAsync", "SetLabels", "AddLabels", "StoreAsync",
        };
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            foreach (var f in forbidden)
                Assert.False(code.Contains(f, StringComparison.Ordinal), $"{Path.GetFileName(file)} enthält '{f}'");
        }
    }
}
