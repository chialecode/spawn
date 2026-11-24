using System.Runtime.InteropServices;
using Spawn.Contracts;

namespace Spawn.Host.Helpers;

/// <summary>
/// Synchronizes the Win32 DWM immersive dark-mode attribute (<c>DWMWA_USE_IMMERSIVE_DARK_MODE = 20</c>)
/// with SPAWN's active <see cref="SpawnUiThemeKind"/> so native Windows 11 window borders match Light and Dark UI themes.
/// </summary>
public static partial class DwmWindowFrameHelper
{
    public const int DwmwaUseImmersiveDarkMode = 20;

    public static int GetImmersiveDarkModeAttributeValue(SpawnUiThemeKind theme) =>
        theme == SpawnUiThemeKind.Dark ? 1 : 0;

    public static bool TrySyncImmersiveDarkMode(nint hwnd, SpawnUiThemeKind theme)
    {
        if (hwnd == nint.Zero)
        {
            return false;
        }

        int useDark = GetImmersiveDarkModeAttributeValue(theme);
        int hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        return hr == 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
