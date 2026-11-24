using System.Windows;
using Spawn.Contracts;

namespace Spawn.Host.Services;

/// <summary>
/// Manages instant runtime switching between SPAWN's default Light palette (<c>#F7F7F7</c> / <c>#FFFFFF</c>)
/// and Dark Geek palette (<c>#1B1E24</c> / <c>#14161A</c>).
/// </summary>
public sealed class ThemeManagerService
{
    private static readonly string HostAssemblyName =
        typeof(ThemeManagerService).Assembly.GetName().Name ?? "Spawn.Host";

    private static readonly Uri LightThemeUri =
        new($"pack://application:,,,/{HostAssemblyName};component/Themes/LightTheme.xaml", UriKind.Absolute);

    private static readonly Uri DarkThemeUri =
        new($"pack://application:,,,/{HostAssemblyName};component/Themes/DarkTheme.xaml", UriKind.Absolute);

    private ResourceDictionary? _activePaletteDict;

    public SpawnUiThemeKind CurrentTheme { get; private set; } = SpawnUiThemeKind.Light;

    public event Action<SpawnUiThemeKind>? ThemeChanged;

    public void ApplyTheme(SpawnUiThemeKind theme)
    {
        CurrentTheme = theme;
        if (Application.Current is null)
        {
            ThemeChanged?.Invoke(theme);
            return;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        Uri targetUri = theme == SpawnUiThemeKind.Dark ? DarkThemeUri : LightThemeUri;
        var nextDict = new ResourceDictionary { Source = targetUri };

        if (_activePaletteDict is not null)
        {
            int idx = merged.IndexOf(_activePaletteDict);
            if (idx >= 0)
            {
                merged[idx] = nextDict;
            }
            else
            {
                merged.Insert(0, nextDict);
            }
        }
        else
        {
            var existing = merged.FirstOrDefault(d =>
                d.Source is not null
                && (d.Source.OriginalString.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || d.Source.OriginalString.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)));

            if (existing is not null)
            {
                int idx = merged.IndexOf(existing);
                merged[idx] = nextDict;
            }
            else
            {
                merged.Insert(0, nextDict);
            }
        }

        _activePaletteDict = nextDict;
        ThemeChanged?.Invoke(theme);
    }
}
