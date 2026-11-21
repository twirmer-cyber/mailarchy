using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using MailVault.App.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailVault.App.Views;

public partial class AiView : UserControl
{
    AiViewModel? _vm;
    // Jede Ansicht getrennt starten: Eine WebView2 in einem noch nie angezeigten Tab wird erst fertig,
    // wenn der Tab sichtbar wird – sie darf die andere nicht blockieren.
    readonly Dictionary<WebView2, Task> _ready = new();
    static Task<CoreWebView2Environment>? _env;

    public AiView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => { Attach(); _vm?.Refresh(); };
    }

    void Attach()
    {
        if (DataContext is not AiViewModel vm || vm == _vm) return;
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        vm.PropertyChanged += OnVmChanged;
        if (_ready.Count == 0)
        {
            _ready[ChatView] = InitAsync(ChatView);
            _ready[NetworkView] = InitAsync(NetworkView);
        }
        _ = ShowAsync(ChatView, vm.ChatHtml);
        _ = ShowAsync(NetworkView, vm.NetworkHtml);
    }

    async Task InitAsync(WebView2 view)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailVault", "WebView2");
        var env = await (_env ??= CoreWebView2Environment.CreateAsync(null, dataDir));
        {
            view.DefaultBackgroundColor = Services.ThemeManager.WebBackground;
            await view.EnsureCoreWebView2Async(env);
            ApplyColorScheme(view);
            Services.ThemeManager.Changed += () => ApplyColorScheme(view);
            var s = view.CoreWebView2.Settings;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            // Nur die selbst erzeugten Seiten anzeigen; Links öffnen Belege bzw. den Standardbrowser.
            view.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("data:", StringComparison.Ordinal) || e.Uri == "about:blank") return;
                e.Cancel = true;
                if (e.Uri.StartsWith("mailvault://msg/", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(e.Uri["mailvault://msg/".Length..].TrimEnd('/'), out var id))
                    Dispatcher.BeginInvoke(() => _vm?.OpenMessage(id));
            };
            view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        }
        if (view != NetworkView) return;
        NetworkView.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (doc.RootElement.GetProperty("type").GetString() == "contact" && doc.RootElement.GetProperty("address").GetString() is { } a)
                    _vm?.ShowContact(a);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { }
        };
    }

    /// <summary>Die selbst erzeugten Seiten folgen hell/dunkel über prefers-color-scheme.</summary>
    static void ApplyColorScheme(WebView2 view)
    {
        if (view.CoreWebView2 is null) return;
        view.DefaultBackgroundColor = Services.ThemeManager.WebBackground;
        view.CoreWebView2.Profile.PreferredColorScheme = Services.ThemeManager.IsDark
            ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(AiViewModel.ChatHtml)) _ = ShowAsync(ChatView, _vm.ChatHtml);
        else if (e.PropertyName == nameof(AiViewModel.NetworkHtml)) _ = ShowAsync(NetworkView, _vm.NetworkHtml);
    }

    async Task ShowAsync(WebView2 view, string html)
    {
        if (string.IsNullOrEmpty(html)) return;
        try
        {
            if (_ready.TryGetValue(view, out var ready)) await ready;
            view.CoreWebView2?.NavigateToString(html);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException) { }
    }

    void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _vm is not null)
        {
            e.Handled = true;
            if (_vm.AskCommand.CanExecute(null)) _vm.AskCommand.Execute(null);
        }
    }
}
