using MailVault.Core;
using Xunit;

namespace MailVault.Tests;

public class SearchSyntaxTests
{
    static SearchCriteria P(string q) => SearchSyntax.Apply(new SearchCriteria { Query = q });

    [Fact]
    public void Filters_are_extracted_and_free_text_remains()
    {
        var c = P("von:telekom rechnung jahr:2024 anhang:ja");
        Assert.Equal("telekom", c.From);
        Assert.Equal("rechnung", c.Query);
        Assert.True(c.HasAttachments);
        Assert.Equal(DateRangeType.Custom, c.DateRange);
        var (from, to) = MailArchive.ResolveDateRange(c, DateTime.Now);
        Assert.Equal(new DateTime(2024, 1, 1), from);
        Assert.Equal(new DateTime(2025, 1, 1), to);
    }

    [Fact]
    public void Quoted_values_and_phrases()
    {
        var c = P("an:\"Anna Berg\" \"Vertrag gekündigt\" betreff:urlaub");
        Assert.Equal("Anna Berg", c.To);
        Assert.Equal("urlaub", c.Subject);
        Assert.Equal("\"Vertrag gekündigt\"", c.Query);
    }

    [Fact]
    public void Date_range_with_months_and_days()
    {
        var c = P("nach:2023-05 vor:2023-09-15");
        var (from, to) = MailArchive.ResolveDateRange(c, DateTime.Now);
        Assert.Equal(new DateTime(2023, 5, 1), from);
        Assert.Equal(new DateTime(2023, 9, 15), to);   // exklusiv
        Assert.Null(c.Query);
    }

    [Theory]
    [InlineData("termin 10:30")]
    [InlineData("http://example.org")]
    [InlineData("rechnung")]
    public void Unknown_keys_stay_free_text(string q) => Assert.Equal(q, P(q).Query);

    [Theory]
    [InlineData("jahr:abc")]
    [InlineData("anhang:vielleicht")]
    [InlineData("nach:gestern")]
    public void Invalid_values_explain_the_problem(string q) => Assert.Throws<ArgumentException>(() => P(q));

    [Fact]
    public void Archive_search_applies_subject_filter_and_relevance()
    {
        using var tmp = new TempDir();
        using var a = MailArchive.Open(tmp.Path);
        var f = a.EnsureFolderPath(new[] { "T" });
        var once = a.StoreMessage(f, ArchiveTests.MakeMessage("Urlaub", "Kurz erwähnt: Rechnung", date: new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.Zero)))!.Value;
        var often = a.StoreMessage(f, ArchiveTests.MakeMessage("Rechnung Rechnung", "Rechnung Rechnung Rechnung", date: new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero)))!.Value;

        Assert.Equal(new[] { once, often }, a.Search(new SearchCriteria { Query = "rechnung" }).Select(m => m.Id));          // neueste zuerst
        Assert.Equal(new[] { often, once }, a.Search(new SearchCriteria { Query = "rechnung", SortByRelevance = true }).Select(m => m.Id));
        Assert.Equal(new[] { once }, a.Search(SearchSyntax.Apply(new SearchCriteria { Query = "betreff:urlaub" })).Select(m => m.Id));
        Assert.Equal(new[] { often }, a.Search(SearchSyntax.Apply(new SearchCriteria { Query = "rechnung jahr:2023" })).Select(m => m.Id));
    }
}
