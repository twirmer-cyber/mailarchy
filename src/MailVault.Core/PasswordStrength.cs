namespace MailVault.Core;

/// <summary>
/// Grobe Einschätzung der Kennwortstärke (geschätzte Entropie in Bit), offline und ohne Wörterbuchdienst.
/// Bewusst vorsichtig: Wiederholungen, Folgen und bekannte Allerweltskennwörter senken die Wertung deutlich.
/// Sie ersetzt keine echte Prüfung, hilft aber, offensichtlich schwache Kennwörter zu erkennen.
/// </summary>
public static class PasswordStrength
{
    public enum Level { VeryWeak, Weak, Fair, Strong, VeryStrong }

    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwort", "kennwort", "12345678", "123456789", "1234567890", "qwertz", "qwerty", "qwertzuiop",
        "qwertyuiop", "asdfghjk", "iloveyou", "letmein", "welcome", "willkommen", "hallo123", "admin123", "geheim",
        "sommer", "winter", "frühling", "fruehling", "herbst", "mailvault", "archiv", "fussball", "schatz", "dragon",
        "monkey", "sunshine", "princess", "azerty", "motdepasse", "contraseña", "contrasena", "abc12345", "11111111",
    };

    /// <summary>Geschätzte Entropie in Bit.</summary>
    public static double Bits(string pw)
    {
        if (string.IsNullOrEmpty(pw)) return 0;
        int pool = 0;
        if (pw.Any(char.IsLower)) pool += 26;
        if (pw.Any(char.IsUpper)) pool += 26;
        if (pw.Any(char.IsDigit)) pool += 10;
        if (pw.Any(c => !char.IsLetterOrDigit(c) && c < 128)) pool += 33;
        if (pw.Any(c => c >= 128)) pool += 64;

        // Effektive Länge: direkte Wiederholungen und auf-/absteigende Folgen (abc, 123, cba) zählen kaum.
        double effective = 1;
        for (var i = 1; i < pw.Length; i++)
        {
            int d = pw[i] - pw[i - 1];
            effective += d == 0 ? 0.1 : Math.Abs(d) == 1 ? 0.3 : 1;
        }
        var bits = effective * Math.Log2(Math.Max(pool, 2));

        var core = new string(pw.Where(char.IsLetter).ToArray());
        if (Common.Contains(pw) || Common.Any(c => pw.Contains(c, StringComparison.OrdinalIgnoreCase) && c.Length >= pw.Length - 4)
            || (core.Length >= 4 && Common.Contains(core)))
            bits = Math.Min(bits, 20);
        return bits;
    }

    public static Level Rate(string pw) => Bits(pw) switch
    {
        < 35 => Level.VeryWeak,
        < 50 => Level.Weak,
        < 65 => Level.Fair,
        < 85 => Level.Strong,
        _ => Level.VeryStrong,
    };
}
