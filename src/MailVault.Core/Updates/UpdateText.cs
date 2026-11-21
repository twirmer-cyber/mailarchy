using MailVault.Core.Localization;

namespace MailVault.Core.Updates;

/// <summary>Meldungen der Update-Funktion (Texte in Resources/CoreStrings*.resx).</summary>
public static class UpdateText
{
    public static string InsecureServer => CoreText.T("Update_InsecureServer");
    public static string NoReleaseYet => CoreText.T("Update_NoReleaseYet");
    public static string ServerError(int code) => CoreText.F("Update_ServerError", code);
    public static string Unreachable => CoreText.T("Update_Unreachable");
    public static string BadSignature => CoreText.T("Update_BadSignature");
    public static string NothingSelected => CoreText.T("Update_NothingSelected");
    public static string Downloading(string version) => CoreText.F("Update_Downloading", version);
    public static string DownloadingProgress(string version, double doneMb, double totalMb) =>
        CoreText.F("Update_DownloadingProgress", version, doneMb, totalMb);
    public static string PackageMissing(int code) => CoreText.F("Update_PackageMissing", code);
    public static string Verifying => CoreText.T("Update_Verifying");
    public static string ChecksumMismatch => CoreText.T("Update_ChecksumMismatch");
    public static string Extracting => CoreText.T("Update_Extracting");
    public static string NoProgramInPackage => CoreText.T("Update_NoProgramInPackage");
    public static string WrongVersion(string found, string expected) => CoreText.F("Update_WrongVersion", found, expected);
}
