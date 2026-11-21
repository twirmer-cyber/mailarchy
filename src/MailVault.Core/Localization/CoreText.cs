using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("de")]

namespace MailVault.Core.Localization;

/// <summary>
/// Zugriff auf die übersetzten Texte der Kernbibliothek (Resources/CoreStrings*.resx).
/// Deutsch ist die neutrale Sprache; Englisch, Französisch und Spanisch liegen als Satellitenassemblys bei.
/// Maßgeblich ist <see cref="CultureInfo.CurrentUICulture"/>; Zahlen und Daten in <see cref="F(string, object?[])"/>
/// werden mit <see cref="CultureInfo.CurrentCulture"/> formatiert.
/// </summary>
public static class CoreText
{
    /// <summary>Unterstützte Oberflächensprachen (zweistellige ISO-Codes), Deutsch zuerst.</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = new[] { "de", "en", "fr", "es" };

    /// <summary>Der ResourceManager hinter den Texten (z. B. für Tests oder eigene Kulturen).</summary>
    public static ResourceManager ResourceManager { get; } =
        new("MailVault.Core.Resources.CoreStrings", typeof(CoreText).Assembly);

    /// <summary>Text zum Schlüssel in der aktuellen Oberflächensprache; fehlt er, wird der Schlüssel selbst geliefert.</summary>
    public static string T(string key) => T(CultureInfo.CurrentUICulture, key);

    /// <summary>Text mit Platzhaltern {0}, {1} … in der aktuellen Oberflächensprache.</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Text in einer bestimmten Sprache (z. B. Protokollzeilen in der Sprache der Desktop-App).</summary>
    public static string T(CultureInfo culture, string key) => ResourceManager.GetString(key, culture) ?? key;

    /// <summary>Text mit Platzhaltern in einer bestimmten Sprache; formatiert auch Zahlen/Daten mit dieser Kultur.</summary>
    public static string F(CultureInfo culture, string key, params object?[] args) =>
        string.Format(culture, T(culture, key), args);

    /// <summary>
    /// Wählt aus einer Liste von Sprachwünschen (z. B. HTTP Accept-Language, bereits nach Priorität sortiert)
    /// die erste unterstützte Sprache; null, wenn keine passt.
    /// </summary>
    public static CultureInfo? Match(IEnumerable<string> languageTags)
    {
        foreach (var tag in languageTags)
        {
            var t = (tag ?? "").Trim();
            if (t.Length < 2) continue;
            var two = t[..2].ToLowerInvariant();
            if (!SupportedLanguages.Contains(two)) continue;
            try { return CultureInfo.GetCultureInfo(t); }
            catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo(two); }
        }
        return null;
    }
}
