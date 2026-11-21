using System.Windows;
using System.Windows.Automation;

namespace MailVault.App.Views;

/// <summary>
/// Angefügte Eigenschaften für Eingabefelder.
/// <c>views:Field.Placeholder</c> zeigt einen Hinweistext im leeren Feld (gezeichnet von der TextBox-Vorlage in
/// Themes/Styles.xaml) und gibt ihn der Sprachausgabe als Hilfetext mit, sofern keiner gesetzt ist.
/// </summary>
public static class Field
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Field), new FrameworkPropertyMetadata(null, OnPlaceholderChanged));

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

    static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(AutomationProperties.GetHelpText(d)) && e.NewValue is string text)
            AutomationProperties.SetHelpText(d, text);
    }
}
