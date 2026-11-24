using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Canonical reasons recorded when SPAWN exits specifically for a local dev reinstall or an in-app release update.
/// </summary>
public static class UpdateRestartReason
{
    public const string DevInstall = "dev-install";
    public const string Update = "update";

    public static string Normalize(string? reason)
    {
        string trimmed = (reason ?? string.Empty).Trim();
        if (string.Equals(trimmed, Update, StringComparison.OrdinalIgnoreCase))
        {
            return Update;
        }

        if (string.Equals(trimmed, DevInstall, StringComparison.OrdinalIgnoreCase))
        {
            return DevInstall;
        }

        return string.IsNullOrEmpty(trimmed) ? DevInstall : trimmed.ToLowerInvariant();
    }
}

/// <summary>
/// Classification of how a version transition occurred across launches.
/// </summary>
public enum UpdateInstallMethod
{
    None = 0,
    DevInstall = 1,
    AutoUpdate = 2,
    ManualInstall = 3,
}

/// <summary>
/// Action a running primary instance should take when receiving forwarded second-instance arguments.
/// </summary>
public enum SecondInstanceAction
{
    Ignore = 0,
    ActivateWindow = 1,
    QuitForUpdate = 2,
}

/// <summary>
/// Persisted across a quit-for-update boundary (<c>config/update-restart-state.json</c>) so the subsequent launch
/// knows why SPAWN exited and whether the main window was open before shutdown.
/// </summary>
public sealed record UpdateRestartMarker(
    string Reason,
    bool WindowOpen,
    string? FromVersion,
    DateTimeOffset AtUtc);

/// <summary>
/// Pure startup decision computed from command-line flags and any consumed <see cref="UpdateRestartMarker"/>.
/// </summary>
public sealed record StartupWindowDecision(
    bool OpenWindow,
    bool ConsumedValidRestartMarker,
    string? ConsumedRestartReason,
    bool? MarkerWindowOpen,
    string? PreviousVersion);

/// <summary>
/// Effective window visibility decision after accounting for tray availability and headless test mode.
/// </summary>
public sealed record EffectiveStartupWindowResult(
    bool OpenWindow,
    bool FellBackBecauseTrayUnavailable);

/// <summary>
/// Deterministic startup, restart-state, tray-fallback, and second-instance policy for <c>Spawn.Host</c>.
/// </summary>
public static class StartupWindowPolicy
{
    /// <summary>
    /// Bounded TTL for <see cref="UpdateRestartMarker"/>: an older or future-skewed marker is ignored and cleared
    /// so a later manual launch always opens the window as usual.
    /// </summary>
    public static readonly TimeSpan RestartMarkerTtl = TimeSpan.FromMinutes(5);

    private static readonly string[] UnversionedSentinels =
    [
        "dev",
        "source",
        "0.1.0-source",
        "unknown",
    ];

    public static bool IsMarkerValid(UpdateRestartMarker? marker, DateTimeOffset nowUtc)
    {
        if (marker is null || marker.AtUtc == default)
        {
            return false;
        }

        TimeSpan age = nowUtc.ToUniversalTime() - marker.AtUtc.ToUniversalTime();
        return age >= TimeSpan.Zero && age <= RestartMarkerTtl;
    }

    /// <summary>
    /// Evaluates whether the main window should open at startup from launch arguments and an optional consumed
    /// <see cref="UpdateRestartMarker"/>:
    /// <list type="bullet">
    /// <item>Default is <c>!options.IsBackgroundTray</c>.</item>
    /// <item>A valid marker (within 5 minutes) restores <see cref="UpdateRestartMarker.WindowOpen"/> unless
    /// <c>reason == "dev-install"</c> AND <c>--background</c> is passed (in which case dev reinstall always stays tray-only).</item>
    /// <item>Expired or future-skewed markers are ignored so normal launches open the window and <c>--background</c> stays tray-only.</item>
    /// </list>
    /// </summary>
    public static StartupWindowDecision Decide(
        UpdateRestartMarker? marker,
        SpawnCommandLineOptions options,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool openWindow = !options.IsBackgroundTray;
        bool validRestart = false;
        string? restartReason = null;
        bool? markerWindowOpen = null;
        string? previousVersion = null;

        if (IsMarkerValid(marker, nowUtc))
        {
            validRestart = true;
            restartReason = UpdateRestartReason.Normalize(marker!.Reason);
            markerWindowOpen = marker.WindowOpen;
            previousVersion = string.IsNullOrWhiteSpace(marker.FromVersion) ? null : marker.FromVersion.Trim();

            bool isDevInstallBackground =
                string.Equals(restartReason, UpdateRestartReason.DevInstall, StringComparison.OrdinalIgnoreCase)
                && options.IsBackgroundTray;

            if (!isDevInstallBackground)
            {
                openWindow = marker.WindowOpen;
            }
        }

        return new StartupWindowDecision(
            OpenWindow: openWindow,
            ConsumedValidRestartMarker: validRestart,
            ConsumedRestartReason: restartReason,
            MarkerWindowOpen: markerWindowOpen,
            PreviousVersion: previousVersion);
    }

    /// <summary>
    /// Resolves the final window action after attempting system tray initialization.
    /// If tray-only startup was planned but tray creation failed in an interactive session, gracefully falls back
    /// to opening the main window so the user is never stranded. Headless test runs never pop a window.
    /// </summary>
    public static EffectiveStartupWindowResult ResolveEffectiveOpenWindow(
        StartupWindowDecision decision,
        bool trayActive,
        bool isHeadlessTest)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (isHeadlessTest)
        {
            return new EffectiveStartupWindowResult(OpenWindow: false, FellBackBecauseTrayUnavailable: false);
        }

        if (decision.OpenWindow)
        {
            return new EffectiveStartupWindowResult(OpenWindow: true, FellBackBecauseTrayUnavailable: false);
        }

        if (!trayActive)
        {
            return new EffectiveStartupWindowResult(OpenWindow: true, FellBackBecauseTrayUnavailable: true);
        }

        return new EffectiveStartupWindowResult(OpenWindow: false, FellBackBecauseTrayUnavailable: false);
    }

    /// <summary>
    /// Determines how a running primary instance should react to forwarded second-instance arguments.
    /// </summary>
    public static SecondInstanceAction DecideSecondInstanceAction(
        SpawnCommandLineOptions incomingOptions,
        bool isQuitting)
    {
        ArgumentNullException.ThrowIfNull(incomingOptions);

        if (incomingOptions.IsQuitForUpdate)
        {
            return SecondInstanceAction.QuitForUpdate;
        }

        if (isQuitting || (incomingOptions.IsBackgroundTray && !incomingOptions.IsActivate))
        {
            return SecondInstanceAction.Ignore;
        }

        return SecondInstanceAction.ActivateWindow;
    }

    /// <summary>
    /// Classifies an effective version change across launches for update diagnostics and future release auto-update tracking.
    /// </summary>
    public static UpdateInstallMethod ClassifyVersionChange(
        string? channel,
        string? previousVersion,
        string? currentVersion,
        StartupWindowDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        string prev = (previousVersion ?? string.Empty).Trim();
        string cur = (currentVersion ?? string.Empty).Trim();
        if (IsUnversioned(prev) || IsUnversioned(cur) || string.Equals(prev, cur, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateInstallMethod.None;
        }

        bool isRelease = string.Equals(channel?.Trim(), "release", StringComparison.OrdinalIgnoreCase)
            || string.Equals(channel?.Trim(), "stable", StringComparison.OrdinalIgnoreCase);

        if (!isRelease)
        {
            return UpdateInstallMethod.DevInstall;
        }

        if (decision.ConsumedValidRestartMarker
            && !string.Equals(decision.ConsumedRestartReason, UpdateRestartReason.DevInstall, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateInstallMethod.AutoUpdate;
        }

        return UpdateInstallMethod.ManualInstall;
    }

    /// <summary>
    /// Checks whether automated headless test environment flags are active.
    /// Release builds always ignore headless test environment variables so stray env state cannot hide a user's release window or tray.
    /// </summary>
    public static bool IsHeadlessTestEnvironment(bool isReleaseChannel = false)
    {
        if (isReleaseChannel)
        {
            return false;
        }

        return IsTruthyEnv("SPAWN_HEADLESS_TEST") || IsTruthyEnv("SPAWN_TEST_HEADLESS");
    }

    private static bool IsTruthyEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnversioned(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return true;
        }

        foreach (string sentinel in UnversionedSentinels)
        {
            if (string.Equals(version, sentinel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return version.EndsWith("-source", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Atomic JSON persistence for <see cref="UpdateRestartMarker"/> inside <c>&lt;DataRoot&gt;/config/update-restart-state.json</c>.
/// </summary>
public static class UpdateRestartStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record PersistedRestartMarkerDto
    {
        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("windowOpen")]
        public bool WindowOpen { get; init; }

        [JsonPropertyName("fromVersion")]
        public string? FromVersion { get; init; }

        [JsonPropertyName("at")]
        public string? At { get; init; }
    }

    public static void SaveMarker(string filePath, UpdateRestartMarker marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(marker);

        string? parentDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var dto = new PersistedRestartMarkerDto
        {
            Reason = UpdateRestartReason.Normalize(marker.Reason),
            WindowOpen = marker.WindowOpen,
            FromVersion = string.IsNullOrWhiteSpace(marker.FromVersion) ? null : marker.FromVersion.Trim(),
            At = marker.AtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };

        string json = JsonSerializer.Serialize(dto, SerializerOptions) + "\n";
        string tmpPath = $"{filePath}.tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    /// Reads and unconditionally deletes the restart marker file (whether valid, expired, or corrupt)
    /// so no stale state can ever affect a subsequent manual launch.
    /// </summary>
    public static UpdateRestartMarker? TryReadAndDeleteMarker(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        string? raw = null;
        try
        {
            raw = File.ReadAllText(filePath);
        }
        catch
        {
            // Fall through to best-effort delete.
        }
        finally
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Ignore concurrent delete races.
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            PersistedRestartMarkerDto? dto = JsonSerializer.Deserialize<PersistedRestartMarkerDto>(raw, SerializerOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.At))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(
                    dto.At.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset atUtc))
            {
                return null;
            }

            return new UpdateRestartMarker(
                Reason: UpdateRestartReason.Normalize(dto.Reason),
                WindowOpen: dto.WindowOpen,
                FromVersion: string.IsNullOrWhiteSpace(dto.FromVersion) ? null : dto.FromVersion.Trim(),
                AtUtc: atUtc);
        }
        catch
        {
            return null;
        }
    }

    public static StartupWindowDecision ConsumeAndDecide(
        string filePath,
        SpawnCommandLineOptions options,
        DateTimeOffset nowUtc)
    {
        UpdateRestartMarker? marker = TryReadAndDeleteMarker(filePath);
        return StartupWindowPolicy.Decide(marker, options, nowUtc);
    }
}
