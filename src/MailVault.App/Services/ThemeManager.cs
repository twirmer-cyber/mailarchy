using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MailVault.App.Services;

/// <summary>
/// Hell, dunkel oder Windows-Kontrastdesign. Tauscht das Farbwörterbuch (Themes/Light.xaml, Dark.xaml) in den
/// Anwendungsressourcen aus; alle Oberflächen beziehen ihre Pinsel als DynamicResource und folgen sofort.
/// Im Kontrastdesign werden die Systemfarben verwendet, die der Benutzer in Windows gewählt hat.
/// </summary>
public static class ThemeManager
{
    public const string System = "system", Light = "light", Dark = "dark";

    static string _mode = System;
    static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }
    public static bool IsHighContrast => SystemParameters.HighContrast;
    public static event Action? Changed;

    public static void Initialize(string? mode)
    {
        _mode = Normalize(mode);
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
        // Titelleiste jedes neuen Fensters passend einfärben
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));
    }

    public static void SetMode(string? mode)
    {
        _mode = Normalize(mode);
        Apply();
    }

    static string Normalize(string? mode) => mode is Light or Dark ? mode : System;

    static void Apply()
    {
        var app = Application.Current;
        if (app is null) return;
        IsDark = !IsHighContrast && (_mode == Dark || (_mode == System && WindowsUsesDarkApps()));

        var palette = IsHighContrast ? BuildHighContrast()
            : new ResourceDictionary { Source = new Uri($"pack://application:,,,/MailVault;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute) };
        var merged = app.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, palette);
        _current = palette;

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    /// <summary>Hintergrundfarbe für WebView2-Inhalte, die MailVault selbst erzeugt (KI-Antworten, Netzwerk).</summary>
    public static global::System.Drawing.Color WebBackground
    {
        get
        {
            var c = Application.Current?.TryFindResource("WebBackgroundColor") is Color col ? col
                  : IsHighContrast ? SystemColors.WindowColor : Color.FromRgb(0xFA, 0xF9, 0xF7);
            return global::System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
    }

    static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Gleiche Schlüssel wie Light.xaml, aber mit den Systemfarben des Kontrastdesigns.</summary>
    static ResourceDictionary BuildHighContrast()
    {
        var window = SystemColors.WindowBrush;
        var text = SystemColors.WindowTextBrush;
        var hi = SystemColors.HighlightBrush;
        var hiText = SystemColors.HighlightTextBrush;
        var hot = SystemColors.HotTrackBrush;
        var gray = SystemColors.GrayTextBrush;
        var d = new ResourceDictionary
        {
            ["AccentColor"] = SystemColors.HighlightColor,
            ["ShadowColor"] = Colors.Transparent,
            ["WebBackgroundColor"] = SystemColors.WindowColor,
        };
        foreach (var k in new[] { "PageBrush", "SidebarBrush", "SurfaceBrush", "HoverBrush", "PressedBrush", "ShelfHoverBrush",
                                  "WarnBackBrush", "DangerBackBrush", "NoteBackBrush" })
            d[k] = window;
        foreach (var k in new[] { "TextBrush", "MutedTextBrush", "BorderBrush", "LineStrongBrush", "InputEdgeBrush", "SelectionTextBrush",
                                  "OkBrush", "WarnBrush", "WarnTextBrush", "DangerBrush", "ScrollThumbBrush" })
            d[k] = text;
        d["AccentBrush"] = hi;
        d["AccentHoverBrush"] = hot;
        d["AccentPressedBrush"] = hot;
        d["AccentLightBrush"] = hi;
        d["OnAccentBrush"] = hiText;
        d["SelectionTextBrush"] = hiText;
        d["TextSelectionBrush"] = hi;
        d["FocusBrush"] = text;
        d["ScrollThumbHoverBrush"] = hi;
        d["SelectedGlyphBrush"] = hiText;
        return d;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dunkle Titelleiste (Windows 10 20H1+ / 11), sonst bleibt sie hell.</summary>
    static readonly DependencyProperty DarkTitleProperty =
        DependencyProperty.RegisterAttached("DarkTitle", typeof(bool), typeof(ThemeManager), new PropertyMetadata(false));

    static void ApplyTitleBar(Window w)
    {
        // Nur bei einer Änderung aufrufen: Ein unnötiger Aufruf beim ersten Laden kann das erste Zeichnen verzögern.
        if ((bool)w.GetValue(DarkTitleProperty) == IsDark) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); } catch { /* ältere Windows-Version */ }
        w.SetValue(DarkTitleProperty, IsDark);
    }
}
