using System.Collections;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailVault.Core;
using MailVault.Core.Ai;
using MailVault.Core.Localization;
using MailVault.Web;
using Xunit;

namespace MailVault.Tests;

/// <summary>Vollständigkeit der Übersetzungen (Kern und Weboberfläche) und Sprachwahl.</summary>
public class LocalizationTests
{
    static readonly string[] Translations = { "en", "fr", "es" };

    static Dictionary<string, string> ResourceSet(CultureInfo culture)
    {
        var set = CoreText.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"Keine Texte für {culture.Name}");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    /// <summary>Indizes der Platzhalter {0}, {1:N0} … (Formatangaben dürfen je Sprache abweichen).</summary>
    static string Placeholders(string s) =>
        string.Join(",", Regex.Matches(s, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));

    [Fact]
    public void Every_core_text_is_translated_with_the_same_placeholders()
    {
        var neutral = ResourceSet(CultureInfo.InvariantCulture);
        Assert.True(neutral.Count > 100);
        foreach (var lang in Translations)
        {
            var culture = CultureInfo.GetCultureInfo(lang);
            var tr = ResourceSet(culture);
            Assert.Empty(neutral.Keys.Except(tr.Keys).Select(k => $"{lang}: fehlt {k}"));
            Assert.Empty(tr.Keys.Except(neutral.Keys).Select(k => $"{lang}: überzählig {k}"));
            foreach (var (key, de) in neutral)
            {
                Assert.False(string.IsNullOrWhiteSpace(tr[key]), $"{lang}: {key} ist leer");
                Assert.True(Placeholders(de) == Placeholders(tr[key]),
                    $"{lang}: Platzhalter in {key} weichen ab ({Placeholders(de)} / {Placeholders(tr[key])})");
                _ = string.Format(culture, tr[key], Enumerable.Repeat<object?>(0, 10).ToArray()); // Formatzeichenfolge gültig?
            }
        }
    }

    [Fact]
    public void Texts_follow_the_ui_culture()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("manual", new ArchiveProfile().ScheduleDisplay);
            Assert.Contains("from:", SearchSyntax.Examples[4].Example);
            Assert.Equal("Find", AiPromptLibrary.Categories[0].Name);
            var ex = Assert.Throws<ArgumentException>(() => SearchSyntax.Apply(new SearchCriteria { Query = "year:abc" }));
            Assert.Contains("not a valid year", ex.Message);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("manuel", new ArchiveProfile().ScheduleDisplay);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            Assert.Equal("Buscar", AiPromptLibrary.Categories[0].Name);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("manuell", new ArchiveProfile().ScheduleDisplay);
            Assert.Contains("„jahr:abc“ ist kein gültiges Jahr",
                Assert.Throws<ArgumentException>(() => SearchSyntax.Apply(new SearchCriteria { Query = "jahr:abc" })).Message);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Fact]
    public void Prompt_library_has_a_title_and_text_for_every_example()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var lang in new[] { "de", "en", "fr", "es" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(lang);
                foreach (var c in AiPromptLibrary.Categories)
                {
                    Assert.DoesNotContain("AiCat_", c.Name);
                    Assert.All(c.Prompts, p => { Assert.DoesNotContain("AiP_", p.Title); Assert.DoesNotContain("AiP_", p.Prompt); });
                }
            }
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Theory]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("fr-CH, fr;q=0.9, en;q=0.8", "fr")]
    [InlineData("it-IT, es;q=0.5", "es")]
    [InlineData("de", "de")]
    public void Match_picks_the_first_supported_language(string header, string expected)
    {
        var tags = header.Split(',').Select(p => p.Split(';')[0].Trim());
        Assert.Equal(expected, CoreText.Match(tags)!.TwoLetterISOLanguageName);
        Assert.Null(CoreText.Match(new[] { "it", "nl-NL" }));
    }

    // ------------------------------------------------------------------ Weboberfläche

    static string ReadWebResource(string name)
    {
        using var s = typeof(WebArchiveServer).Assembly.GetManifestResourceStream("wwwroot/" + name)
                      ?? throw new InvalidOperationException(name + " ist nicht eingebettet");
        return new StreamReader(s).ReadToEnd();
    }

    /// <summary>Liest die Wörterbücher aus i18n.js (Zeilen der Form <c>"schluessel": "Text",</c> unter <c>de: {</c> usw.).</summary>
    static Dictionary<string, Dictionary<string, string>> WebDictionaries()
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        var langRx = new Regex(@"^\s*(\w\w):\s*\{\s*$");
        var entryRx = new Regex(@"^\s*""([\w.]+)"":\s*""((?:[^""\\]|\\.)*)"",?\s*$");
        foreach (var line in ReadWebResource("i18n.js").Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (langRx.Match(l) is { Success: true } lm) { current = new(); result[lm.Groups[1].Value] = current; continue; }
            if (current is null) continue;
            if (entryRx.Match(l) is { Success: true } em) current.Add(em.Groups[1].Value, em.Groups[2].Value);
            else if (Regex.IsMatch(l, @"^\s*\},?\s*$")) current = null;
            else if (l.Trim().Length > 0) throw new InvalidOperationException("Unerwartete Zeile in i18n.js: " + l);
        }
        return result;
    }

    static string WebPlaceholders(string s) =>
        string.Join(",", Regex.Matches(s, @"\{(\w+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));

    [Fact]
    public void Every_web_text_is_translated_with_the_same_placeholders()
    {
        var dicts = WebDictionaries();
        Assert.Equal(new[] { "de", "en", "es", "fr" }, dicts.Keys.OrderBy(k => k).ToArray());
        var de = dicts["de"];
        Assert.True(de.Count > 50);
        foreach (var lang in Translations)
        {
            var tr = dicts[lang];
            Assert.Empty(de.Keys.Except(tr.Keys).Select(k => $"{lang}: fehlt {k}"));
            Assert.Empty(tr.Keys.Except(de.Keys).Select(k => $"{lang}: überzählig {k}"));
            foreach (var (key, text) in de)
            {
                Assert.False(string.IsNullOrWhiteSpace(tr[key]), $"{lang}: {key} ist leer");
                Assert.True(WebPlaceholders(text) == WebPlaceholders(tr[key]), $"{lang}: Platzhalter in {key} weichen ab");
            }
        }
    }

    [Fact]
    public void Web_page_uses_only_known_text_keys()
    {
        var known = WebDictionaries()["de"].Keys.ToHashSet();
        var html = ReadWebResource("index.html");
        var js = ReadWebResource("app.js");
        var used = Regex.Matches(html, @"data-i18n(?:-[\w-]+)?=""([\w.]+)""").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(js, @"\bt\(""([\w.]+)""").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.True(used.Count > 50);
        Assert.DoesNotContain(used, k => !known.Contains(k));
        Assert.Contains("<script src=\"/i18n.js\"", html);
    }
}

/// <summary>Fehlermeldungen und Suchhilfe des Webzugriffs folgen Accept-Language.</summary>
public class WebLanguageTests : IClassFixture<WebServerFixture>
{
    readonly WebServerFixture _f;
    public WebLanguageTests(WebServerFixture f) => _f = f;

    async Task<string> ErrorFor(string? acceptLanguage)
    {
        using var c = _f.NewClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "api/folders");
        if (acceptLanguage is not null) req.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        var res = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task Api_errors_follow_accept_language()
    {
        Assert.Equal("Not signed in.", await ErrorFor("en-GB,en;q=0.9"));
        Assert.Equal("Non connecté.", await ErrorFor("fr"));
        Assert.Equal("No ha iniciado sesión.", await ErrorFor("es-ES"));
        Assert.Equal("Nicht angemeldet.", await ErrorFor("de-AT"));
        Assert.Equal("Not signed in.", await ErrorFor("it-IT"));    // nicht unterstützt: Englisch
        Assert.Equal("Nicht angemeldet.", await ErrorFor(null));   // ohne Angabe: Sprache der App (in Tests Deutsch)
    }

    [Fact]
    public async Task Search_help_follows_accept_language()
    {
        using var c = await _f.LoginAsync("admin");
        var req = new HttpRequestMessage(HttpMethod.Get, "api/search/help");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en");
        var json = await (await c.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("from:bank", json);
        Assert.DoesNotContain("von:bank", json);
    }
}
