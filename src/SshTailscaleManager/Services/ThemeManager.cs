using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SshTailscaleManager.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Swaps the Light/Dark palette at runtime, persists the choice and themes the window title bar.</summary>
public static class ThemeManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshTailscaleManager", "settings.json");

    private static ResourceDictionary? _palette;

    public static AppTheme Preference { get; private set; } = AppTheme.System;

    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        Preference = LoadPreference();
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Preference == AppTheme.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.InvokeAsync(Apply);
        };
    }

    public static void SetPreference(AppTheme theme)
    {
        if (theme == Preference) return;
        Preference = theme;
        SavePreference(theme);
        Apply();
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on Windows 10 20H1+ / 11, and 19 on older Windows 10 builds.
        if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
    }

    private static void Apply()
    {
        var app = Application.Current;
        if (app is null) return;

        IsDark = Preference == AppTheme.Dark || (Preference == AppTheme.System && SystemUsesDarkTheme());
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SshTailscaleManager;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };

        var dicts = app.Resources.MergedDictionaries;
        var index = _palette is null ? -1 : dicts.IndexOf(_palette);
        if (index >= 0) dicts[index] = palette;
        else dicts.Insert(0, palette);
        _palette = palette;

        foreach (Window w in app.Windows) ApplyTitleBar(w);
    }

    private static AppTheme LoadPreference()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return AppTheme.System;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            return settings is not null && Enum.TryParse<AppTheme>(settings.Theme, out var t) ? t : AppTheme.System;
        }
        catch
        {
            return AppTheme.System;
        }
    }

    private static void SavePreference(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { Theme = theme.ToString() }));
        }
        catch
        {
            // Not critical: the choice just won't persist.
        }
    }

    private sealed class Settings
    {
        public string? Theme { get; set; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
