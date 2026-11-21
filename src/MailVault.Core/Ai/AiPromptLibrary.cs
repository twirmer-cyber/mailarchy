using MailVault.Core.Localization;

namespace MailVault.Core.Ai;

public sealed record AiPrompt(string Title, string Prompt);
public sealed record AiPromptCategory(string Name, string Icon, IReadOnlyList<AiPrompt> Prompts);

/// <summary>
/// Beispiel-Fragen für den KI-Assistenten. Bewährte Muster aus der Dokumentenanalyse sind eingearbeitet:
/// Ergebnisse als Tabelle mit "nicht gefunden" statt Raten, Fristen mit Quelle und Verantwortlichem, Chronologien,
/// Hervorheben riskanter Vertragsklauseln. Platzhalter in [eckigen Klammern] ersetzt der Benutzer vor dem Absenden.
/// Die Texte stehen in Resources/CoreStrings*.resx (AiCat_*, AiP_*) und folgen der Oberflächensprache.
/// </summary>
public static class AiPromptLibrary
{
    /// <summary>Kategorie-Schlüssel und Anzahl der Beispiele je Kategorie (Reihenfolge = Anzeige).</summary>
    static readonly (string Key, int Count)[] Layout =
    {
        ("Find", 5),
        ("Money", 7),
        ("Deadlines", 4),
        ("Connections", 5),
        ("Analysis", 6),
        ("Security", 4),
    };

    public static IReadOnlyList<AiPromptCategory> Categories =>
        Layout.Select(c => new AiPromptCategory(
                CoreText.T("AiCat_" + c.Key), "",
                Enumerable.Range(1, c.Count)
                    .Select(i => new AiPrompt(CoreText.T($"AiP_{c.Key}_{i}_Title"), CoreText.T($"AiP_{c.Key}_{i}_Text")))
                    .ToList()))
            .ToList();
}
