using System.Windows;
using System.Windows.Controls;
using MailVault.App.ViewModels;

namespace MailVault.App.Views;

public partial class SettingsView : UserControl
{
    bool _filling;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => FillPasswords();
        IsVisibleChanged += (_, _) => { if (IsVisible) FillPasswords(); };
    }

    SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>PasswordBox lässt sich nicht binden – Werte beim Anzeigen eintragen.</summary>
    void FillPasswords()
    {
        if (Vm is not { } vm) return;
        _filling = true;
        SmtpPasswordBox.Password = vm.Automation.SmtpPassword;
        ApiKeyBox.Password = vm.Ai.ApiKey;
        _filling = false;
        vm.Automation.PasswordReloadRequested -= FillPasswords;
        vm.Automation.PasswordReloadRequested += FillPasswords;
    }

    void SmtpPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filling && Vm is { } vm) vm.Automation.SmtpPassword = SmtpPasswordBox.Password;
    }

    void ApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filling && Vm is { } vm) vm.Ai.ApiKey = ApiKeyBox.Password;
    }

    void Users_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.Web.EditUserCommand.Execute(null);
    }
}
