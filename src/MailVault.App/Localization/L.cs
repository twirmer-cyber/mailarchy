using System.Globalization;
using System.Resources;

namespace MailVault.App.Localization;

/// <summary>
/// Zugriff auf die Oberflächentexte in Resources/Strings*.resx (neutral = Deutsch, dazu en/fr/es).
/// Bewusst ohne von Visual Studio erzeugte Designer-Klasse, damit <c>dotnet build</c> allein genügt.
/// </summary>
public static class L
{
    static readonly ResourceManager Resources = new("MailVault.App.Resources.Strings", typeof(L).Assembly);

    /// <summary>Unterstützte Sprachen (Wert in AppSettings.Language).</summary>
    public static readonly string[] Supported = { "de", "en", "fr", "es" };

    /// <summary>Text zum Schlüssel in der aktuellen Oberflächensprache; fehlt er, wird der Schlüssel selbst gezeigt.</summary>
    public static string T(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Text mit Platzhaltern {0}, {1} … (Zahlen und Datum im Format der aktuellen Sprache).</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, T(key), args);

    /// <summary>
    /// Ermittelt die Oberflächensprache aus der Einstellung: "system" nimmt die Windows-Sprache, sofern sie
    /// unterstützt wird, sonst Englisch.
    /// </summary>
    public static CultureInfo ResolveCulture(string? setting)
    {
        var code = (setting ?? "system").Trim().ToLowerInvariant();
        if (!Supported.Contains(code))
        {
            var windows = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName; // beim Start noch die Windows-Anzeigesprache
            code = Supported.Contains(windows) ? windows : "en";
        }
        // Für Datumsformate die regionale Variante behalten, wenn Windows dieselbe Sprache verwendet (z. B. de-AT).
        var current = CultureInfo.CurrentCulture;
        if (current.TwoLetterISOLanguageName == code && !current.IsNeutralCulture) return current;
        return CultureInfo.CreateSpecificCulture(code);
    }

    /// <summary>Stellt Oberflächensprache und Zahlen-/Datumsformate für alle Threads ein.</summary>
    public static void Apply(string? setting)
    {
        var culture = ResolveCulture(setting);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
