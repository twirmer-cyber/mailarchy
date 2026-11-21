using System.Windows.Markup;

namespace MailVault.App.Localization;

/// <summary>
/// XAML-Kurzform für Oberflächentexte: <c>Text="{loc:T Common_Save}"</c>
/// (xmlns:loc="clr-namespace:MailVault.App.Localization"). Der Text wird beim Laden gelesen;
/// ein Sprachwechsel wirkt nach dem Neustart.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
