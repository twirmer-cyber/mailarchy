using System.Security.Cryptography;
using System.Text;

namespace MailVault.Core;

/// <summary>Verschlüsselt Kennwörter und API-Schlüssel mit Windows-DPAPI (nur dieses Benutzerkonto kann sie lesen).</summary>
public static class Secret
{
    public static string? Protect(string? value, string purpose)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy(purpose), DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string? stored, string purpose)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy(purpose), DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; } // anderes Benutzerkonto/Rechner
    }

    static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("MailVault." + purpose + ".v1");
}
