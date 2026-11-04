using System;
using System.Linq;
using System.Windows;

namespace VLAP;

/// <summary>
/// Swaps the application-wide palette. Every window, including the two editor dialogs,
/// resolves its brushes from here, so one dictionary swap repaints all of them.
/// </summary>
public static class ThemeManager
{
    private const string DarkSource = "pack://application:,,,/VLAP;component/Themes/Dark.xaml";
    private const string LightSource = "pack://application:,,,/VLAP;component/Themes/Light.xaml";

    public static bool IsLight { get; private set; }

    public static void Apply(bool light)
    {
        IsLight = light;

        try
        {
            var app = Application.Current;
            if (app == null) return;

            var merged = app.Resources.MergedDictionaries;
            var source = new Uri(light ? LightSource : DarkSource, UriKind.Absolute);

            var theme = merged.FirstOrDefault(d =>
                d.Source != null &&
                d.Source.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase));

            if (theme != null) theme.Source = source;
            else merged.Insert(0, new ResourceDictionary { Source = source });

            SettingsStore.Log("theme: " + (light ? "light" : "dark"));
        }
        catch (Exception ex)
        {
            SettingsStore.Log("theme: " + ex.Message);
        }
    }
}
