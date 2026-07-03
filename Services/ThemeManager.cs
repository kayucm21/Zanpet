using System.Windows;

namespace ZapretUI.Services;

/// <summary>
/// Manages runtime theme switching between dark and light themes.
/// Loads theme resources from Themes/ folder and applies them to the application.
/// </summary>
public static class ThemeManager
{
    private const string DarkThemePath = "Themes/Theme.xaml";
    private const string LightThemePath = "Themes/LightTheme.xaml";

    private static ResourceDictionary? _currentTheme;

    /// <summary>
    /// Apply the theme from settings ("dark" or "light").
    /// Call on startup and whenever the setting changes.
    /// </summary>
    public static void ApplyTheme(string themeName)
    {
        var app = Application.Current;
        if (app is null) return;

        string path = themeName.Equals("light", StringComparison.OrdinalIgnoreCase)
            ? LightThemePath
            : DarkThemePath;

        try
        {
            var newTheme = new ResourceDictionary { Source = new Uri(path, UriKind.Relative) };

            // Remove old theme dictionary (the one at index 0 in MergedDictionaries)
            if (app.Resources.MergedDictionaries.Count > 0)
                app.Resources.MergedDictionaries[0] = newTheme;
            else
                app.Resources.MergedDictionaries.Add(newTheme);

            _currentTheme = newTheme;
        }
        catch
        {
            // Fallback to dark theme if light theme fails to load
            if (!path.Contains("Theme.xaml"))
                ApplyTheme("dark");
        }
    }

    /// <summary>Get the current theme name from the loaded resource.</summary>
    public static string GetCurrentTheme()
    {
        if (_currentTheme?.Source?.ToString().Contains("Light", StringComparison.OrdinalIgnoreCase) == true)
            return "light";
        return "dark";
    }
}
