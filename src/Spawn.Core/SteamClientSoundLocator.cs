using Microsoft.Win32;

namespace Spawn.Core;

/// <summary>
/// Result of probing the local machine for an installed official Steam client and its built-in notification sound files.
/// </summary>
public sealed record SteamClientSoundProbeResult(
    bool IsSteamInstalled,
    string? SteamInstallDirectory,
    string? AchievementToastWavPath,
    string? GamepadUiAchievementToastWavPath,
    string StatusMessage)
{
    public bool HasAuthenticAchievementSound => !string.IsNullOrWhiteSpace(AchievementToastWavPath);

    public string? EffectiveSoundPath => AchievementToastWavPath ?? GamepadUiAchievementToastWavPath;
}

/// <summary>
/// Clean-Host compliant, zero-hardcoded-path locator that discovers the local Steam client installation
/// via standard Windows registry keys and special folders to resolve the achievement toast sound.
/// <para>
/// The Steam client maps its <c>ToastAchievement</c> sound to <c>steamui\sounds\desktop_toast_default.wav</c> in the regular
/// desktop UI and only to <c>deck_ui_achievement_toast.wav</c> in Big Picture / Steam Deck gamepad UI. SPAWN therefore prefers
/// the desktop file (what a desktop player actually hears in the in-game overlay) and falls back to the gamepad UI file.
/// </para>
/// </summary>
public static class SteamClientSoundLocator
{
    /// <summary>Desktop Steam client achievement toast sound (<c>ToastAchievement</c> outside gamepad UI).</summary>
    public static readonly string PrimaryAchievementToastWavRelativePath =
        Path.Combine("steamui", "sounds", "desktop_toast_default.wav");

    /// <summary>Big Picture / Steam Deck gamepad UI achievement toast sound, used only when the desktop file is missing.</summary>
    public static readonly string GamepadUiAchievementToastWavRelativePath =
        Path.Combine("steamui", "sounds", "deck_ui_achievement_toast.wav");

    public static SteamClientSoundProbeResult Probe(string? explicitSteamRootOverride = null)
    {
        var candidates = EnumerateCandidateSteamDirectories(explicitSteamRootOverride);
        for (int i = 0; i < candidates.Count; i++)
        {
            string candidateRoot = candidates[i];
            string primaryWav = Path.Combine(candidateRoot, PrimaryAchievementToastWavRelativePath);
            string fallbackWav = Path.Combine(candidateRoot, GamepadUiAchievementToastWavRelativePath);

            bool hasPrimary = File.Exists(primaryWav);
            bool hasFallback = File.Exists(fallbackWav);

            if (hasPrimary || hasFallback)
            {
                return new SteamClientSoundProbeResult(
                    IsSteamInstalled: true,
                    SteamInstallDirectory: candidateRoot,
                    AchievementToastWavPath: hasPrimary ? Path.GetFullPath(primaryWav) : null,
                    GamepadUiAchievementToastWavPath: hasFallback ? Path.GetFullPath(fallbackWav) : null,
                    StatusMessage: hasPrimary
                        ? "已自动关联本机 Steam 官方原版音效"
                        : "已关联本机 Steam 大屏幕模式成就音效");
            }
        }

        string? installOnlyRoot = candidates.Count > 0 ? candidates[0] : null;
        if (!string.IsNullOrWhiteSpace(installOnlyRoot))
        {
            return new SteamClientSoundProbeResult(
                IsSteamInstalled: true,
                SteamInstallDirectory: installOnlyRoot,
                AchievementToastWavPath: null,
                GamepadUiAchievementToastWavPath: null,
                StatusMessage: "已检测到 Steam 目录，但未找到内置音效文件（将自动回退主题合成音效）");
        }

        return new SteamClientSoundProbeResult(
            IsSteamInstalled: false,
            SteamInstallDirectory: null,
            AchievementToastWavPath: null,
            GamepadUiAchievementToastWavPath: null,
            StatusMessage: "本机未检测到 Steam 客户端（将自动使用内置和弦合成音效）");
    }

    public static string? TryFindSteamInstallDirectory(string? explicitSteamRootOverride = null) =>
        Probe(explicitSteamRootOverride).SteamInstallDirectory;

    public static string? TryFindAchievementToastWavPath(string? explicitSteamRootOverride = null) =>
        Probe(explicitSteamRootOverride).AchievementToastWavPath;

    public static string? TryFindGamepadUiAchievementToastWavPath(string? explicitSteamRootOverride = null) =>
        Probe(explicitSteamRootOverride).GamepadUiAchievementToastWavPath;

    public static IReadOnlyList<string> EnumerateCandidateSteamDirectories(string? explicitSteamRootOverride = null)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddCandidate(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return;
            }

            try
            {
                string normalized = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(rawPath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar)));
                if (Directory.Exists(normalized) && seen.Add(normalized))
                {
                    candidates.Add(normalized);
                }
            }
            catch
            {
                // Ignore malformed registry path strings.
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitSteamRootOverride))
        {
            TryAddCandidate(explicitSteamRootOverride);
            return candidates;
        }

        // 1. HKCU\Software\Valve\Steam ("SteamPath" and "SteamExe")
        TryAddCandidate(TryReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"));
        string? hkcuExe = TryReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamExe");
        if (!string.IsNullOrWhiteSpace(hkcuExe))
        {
            try
            {
                TryAddCandidate(Path.GetDirectoryName(hkcuExe.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                // Ignore malformed SteamExe value.
            }
        }

        // 2. HKLM\SOFTWARE\WOW6432Node\Valve\Steam & HKLM\SOFTWARE\Valve\Steam ("InstallPath")
        TryAddCandidate(TryReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"));
        TryAddCandidate(TryReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        TryAddCandidate(TryReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"));

        // 3. Standard ProgramFiles(x86)\Steam & ProgramFiles\Steam fallbacks
        string progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
        {
            TryAddCandidate(Path.Combine(progX86, "Steam"));
        }

        string progX64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(progX64))
        {
            TryAddCandidate(Path.Combine(progX64, "Steam"));
        }

        return candidates;
    }

    private static string? TryReadRegistryString(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var subKey = baseKey.OpenSubKey(subKeyPath, writable: false);
            return subKey?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
