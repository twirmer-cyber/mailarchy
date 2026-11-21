using System.Globalization;
using System.Runtime.CompilerServices;

namespace MailVault.Tests;

/// <summary>
/// Die Tests prüfen viele deutsche Meldungen. Damit sie auch auf einem englischen Build-Rechner (CI: en-US) gelten,
/// laufen alle Tests mit deutscher Oberflächen- und Formatkultur – unabhängig von den Einstellungen des Rechners.
/// </summary>
static class TestCulture
{
    [ModuleInitializer]
    internal static void UseGerman()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = de;
        CultureInfo.DefaultThreadCurrentUICulture = de;
        CultureInfo.CurrentCulture = de;
        CultureInfo.CurrentUICulture = de;
    }
}
