using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Five canonical FLiNG trainer version string formats observed in the wild (progress-saves-and-trainer.md §4.1).
/// </summary>
public enum FlingVersionStringKind
{
    Unknown = 0,

    /// <summary>Bounded version range, e.g. <c>v1.02-v1.16.1</c>.</summary>
    VersionRange = 1,

    /// <summary>Open-ended plus or Early Access range, e.g. <c>v1.0+</c>, <c>Early Access+</c>.</summary>
    OpenEndedPlus = 2,

    /// <summary>Date-stamped build range, e.g. <c>v1.0-v20251216</c>.</summary>
    DateStampedRange = 3,

    /// <summary>Version paired with explicit update date, e.g. <c>v4.1.1 Updated 2025.04.16</c>.</summary>
    VersionWithUpdateDate = 4,

    /// <summary>Version range with trailing undated <c>Updated</c> marker, e.g. <c>v2.0-v2.5 Updated</c>.</summary>
    RangeWithTrailingUpdated = 5,

    /// <summary>Single discrete version, e.g. <c>v1.5.78</c>.</summary>
    SingleVersion = 6,
}

/// <summary>
/// Local game version fingerprint captured at trainer binding time and re-evaluated before launch.
/// </summary>
public sealed record GameVersionFingerprint(
    string? SteamBuildId,
    DateTimeOffset? SteamLastUpdatedUtc,
    long? ExeSize,
    DateTimeOffset? ExeLastWriteUtc,
    string? FilteredPeVersion);

/// <summary>
/// Parsed FLiNG version descriptor with extracted bounds and optional release/update date.
/// </summary>
public sealed record ParsedFlingVersion(
    FlingVersionStringKind Kind,
    Version? MinVersion,
    Version? MaxVersion,
    DateOnly? EffectiveDate);

/// <summary>
/// Captures local game version fingerprints (<c>BuildId</c>, <c>ExeSize</c>, <c>ExeLastWriteUtc</c>),
/// filters Unity (<c>20xx.x.*</c>) / Unreal (<c>4.27.*</c>, <c>5.x.*</c>) PE version noise,
/// and maps the 5 FLiNG version string patterns into 3 non-blocking confidence tiers.
/// </summary>
public sealed partial class GameVersionFingerprintService
{
    public const string GameUpdatedGentleNotice = "游戏文件已更新，建议检查新版修改器";

    private static readonly TimeSpan DateDriftOutdatedThreshold = TimeSpan.FromDays(7);
    private static readonly TimeSpan OpenEndedOutdatedThreshold = TimeSpan.FromDays(14);

    [GeneratedRegex(@"^v?(?:20\d{2}\.\d+(?:\.\d+)*(?:[abfp]\d+)?|6000\.\d+(?:\.\d+)*(?:[abfp]\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnityEngineVersionRegex();

    [GeneratedRegex(@"^v?(?:4\.27\.\d+(?:\.\d+)*|5\.\d+\.\d+(?:\.\d+)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnrealEngineVersionRegex();

    [GeneratedRegex(@"^v?0+(?:\.0+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllZeroVersionRegex();

    // Category 3: v1.0-v20251216 or v20250101-v20251216
    [GeneratedRegex(@"(?:v?(\d+(?:\.\d+){1,3})\s*[-–—~]\s*)?v?(20\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateStampedRangeRegex();

    // Category 4: v4.1.1 Updated 2025.04.16 / v1.02-v1.16 Updated 2025-04-16
    [GeneratedRegex(@"v?(\d+(?:\.\d+){1,3})(?:\s*[-–—~]\s*v?(\d+(?:\.\d+){1,3}))?\+?\s+Updated\s+(20\d{2})[.\-/](0?[1-9]|1[0-2])[.\-/](0?[1-9]|[12]\d|3[01])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionWithUpdateDateRegex();

    // Category 5: v2.0-v2.5 Updated (without trailing date)
    [GeneratedRegex(@"v?(\d+(?:\.\d+){1,3})\s*[-–—~]\s*v?(\d+(?:\.\d+){1,3})\s+Updated\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RangeWithTrailingUpdatedRegex();

    // Category 1: v1.02-v1.16.1
    [GeneratedRegex(@"v?(\d+(?:\.\d+){1,3})\s*[-–—~]\s*v?(\d+(?:\.\d+){1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundedVersionRangeRegex();

    // Category 2: v1.0+ / Early Access+ / Early Access
    [GeneratedRegex(@"(?:v?(\d+(?:\.\d+){1,3})\+|Early\s*Access\+?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenEndedPlusRegex();

    // Fallback single version: v1.5.78
    [GeneratedRegex(@"\bv?(\d+(?:\.\d+){1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleVersionRegex();

    // Generic date string (2025.04.16, 2025-04-16, 2025/04/16, April 16, 2025, 16/04/2025)
    [GeneratedRegex(@"\b(20\d{2})[.\-/](0?[1-9]|1[0-2])[.\-/](0?[1-9]|[12]\d|3[01])\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoLikeDateRegex();

    [GeneratedRegex(@"""buildid""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfBuildIdRegex();

    [GeneratedRegex(@"""LastUpdated""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfLastUpdatedRegex();

    [GeneratedRegex(@"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfInstallDirRegex();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="versionText"/> represents Unity (<c>20xx.x.*</c> / <c>6000.x.*</c>)
    /// or Unreal (<c>4.27.*</c> / <c>5.x.*</c>) engine runtime version noise, or is empty/zero.
    /// </summary>
    public static bool IsEngineVersionNoise(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return true;
        }

        string normalized = NormalizeVersionToken(versionText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        return AllZeroVersionRegex().IsMatch(normalized)
            || UnityEngineVersionRegex().IsMatch(normalized)
            || UnrealEngineVersionRegex().IsMatch(normalized);
    }

    /// <summary>
    /// Captures the local game's current version fingerprint: Steam ACF (<c>buildid</c>, <c>LastUpdated</c>),
    /// main executable (<c>ExeSize</c>, <c>ExeLastWriteUtc</c>), and engine-noise-filtered PE version.
    /// </summary>
    public static GameVersionFingerprint CaptureFingerprint(GameRecord game)
    {
        ArgumentNullException.ThrowIfNull(game);

        string? buildId = game.SteamBuildId;
        DateTimeOffset? steamLastUpdated = game.SteamLastUpdatedUtc;

        if (string.IsNullOrWhiteSpace(buildId) || !steamLastUpdated.HasValue)
        {
            var (acfBuildId, acfLastUpdated) = TryReadAcfFingerprintFromDisk(game.InstallDir, game.SteamAppId);
            buildId ??= acfBuildId;
            steamLastUpdated ??= acfLastUpdated;
        }

        long? exeSize = null;
        DateTimeOffset? exeLastWriteUtc = null;
        string? filteredPeVersion = null;

        try
        {
            string exeFullPath = game.ExecutableFullPath;
            if (File.Exists(exeFullPath))
            {
                var fi = new FileInfo(exeFullPath);
                exeSize = fi.Length;
                exeLastWriteUtc = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
                filteredPeVersion = TryExtractUsefulPeVersion(exeFullPath);
            }
        }
        catch
        {
            // Ignore transient filesystem stat failures.
        }

        return new GameVersionFingerprint(
            SteamBuildId: buildId,
            SteamLastUpdatedUtc: steamLastUpdated,
            ExeSize: exeSize,
            ExeLastWriteUtc: exeLastWriteUtc,
            FilteredPeVersion: filteredPeVersion);
    }

    /// <summary>
    /// Compares a bound trainer's recorded game fingerprint (<c>BoundBuildId</c>, <c>BoundExeSize</c>, <c>BoundExeLastWriteUtc</c>)
    /// against the current local game state to produce a non-blocking update drift signal.
    /// </summary>
    public static (bool Changed, string? Notice) DetectGameUpdateSinceBinding(
        TrainerCatalogRecord trainer,
        GameVersionFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(trainer);
        ArgumentNullException.ThrowIfNull(current);

        bool buildChanged = !string.IsNullOrWhiteSpace(trainer.BoundBuildId)
            && !string.IsNullOrWhiteSpace(current.SteamBuildId)
            && !string.Equals(trainer.BoundBuildId.Trim(), current.SteamBuildId.Trim(), StringComparison.Ordinal);

        bool sizeChanged = trainer.BoundExeSize.HasValue
            && current.ExeSize.HasValue
            && trainer.BoundExeSize.Value != current.ExeSize.Value;

        bool timestampChanged = trainer.BoundExeLastWriteUtc.HasValue
            && current.ExeLastWriteUtc.HasValue
            && Math.Abs((trainer.BoundExeLastWriteUtc.Value - current.ExeLastWriteUtc.Value).TotalSeconds) > 2.0;

        if (buildChanged || sizeChanged || timestampChanged)
        {
            return (true, GameUpdatedGentleNotice);
        }

        return (false, null);
    }

    /// <summary>
    /// Classifies a FLiNG version label into one of the 5 recognized version string categories
    /// (plus single version / unknown fallback) and extracts numeric bounds and embedded dates.
    /// </summary>
    public static ParsedFlingVersion ParseFlingVersionString(string? versionLabel, string? updatedDateText = null)
    {
        DateOnly? externalDate = TryParseDateOnly(updatedDateText);
        if (string.IsNullOrWhiteSpace(versionLabel))
        {
            return new ParsedFlingVersion(FlingVersionStringKind.Unknown, null, null, externalDate);
        }

        string text = versionLabel.Trim();

        // Category 4: v4.1.1 Updated 2025.04.16
        Match m4 = VersionWithUpdateDateRegex().Match(text);
        if (m4.Success)
        {
            Version? minV = TryParseNumericVersion(m4.Groups[1].Value);
            Version? maxV = m4.Groups[2].Success ? TryParseNumericVersion(m4.Groups[2].Value) : minV;
            DateOnly? inlineDate = TryCreateDate(m4.Groups[3].Value, m4.Groups[4].Value, m4.Groups[5].Value);
            return new ParsedFlingVersion(FlingVersionStringKind.VersionWithUpdateDate, minV, maxV, inlineDate ?? externalDate);
        }

        // Category 3: v1.0-v20251216
        Match m3 = DateStampedRangeRegex().Match(text);
        if (m3.Success)
        {
            Version? minV = m3.Groups[1].Success ? TryParseNumericVersion(m3.Groups[1].Value) : null;
            DateOnly? buildDate = TryCreateDate(m3.Groups[2].Value, m3.Groups[3].Value, m3.Groups[4].Value);
            return new ParsedFlingVersion(FlingVersionStringKind.DateStampedRange, minV, null, buildDate ?? externalDate);
        }

        // Category 5: v2.0-v2.5 Updated
        Match m5 = RangeWithTrailingUpdatedRegex().Match(text);
        if (m5.Success)
        {
            Version? minV = TryParseNumericVersion(m5.Groups[1].Value);
            Version? maxV = TryParseNumericVersion(m5.Groups[2].Value);
            return new ParsedFlingVersion(FlingVersionStringKind.RangeWithTrailingUpdated, minV, maxV, externalDate);
        }

        // Category 1: v1.02-v1.16.1
        Match m1 = BoundedVersionRangeRegex().Match(text);
        if (m1.Success)
        {
            Version? minV = TryParseNumericVersion(m1.Groups[1].Value);
            Version? maxV = TryParseNumericVersion(m1.Groups[2].Value);
            return new ParsedFlingVersion(FlingVersionStringKind.VersionRange, minV, maxV, externalDate);
        }

        // Category 2: v1.0+ / Early Access+
        Match m2 = OpenEndedPlusRegex().Match(text);
        if (m2.Success)
        {
            Version? minV = m2.Groups[1].Success ? TryParseNumericVersion(m2.Groups[1].Value) : null;
            return new ParsedFlingVersion(FlingVersionStringKind.OpenEndedPlus, minV, null, externalDate);
        }

        // Single discrete version: v1.5.78
        Match mSingle = SingleVersionRegex().Match(text);
        if (mSingle.Success)
        {
            Version? v = TryParseNumericVersion(mSingle.Groups[1].Value);
            return new ParsedFlingVersion(FlingVersionStringKind.SingleVersion, v, v, externalDate);
        }

        return new ParsedFlingVersion(FlingVersionStringKind.Unknown, null, null, externalDate);
    }

    /// <summary>
    /// Evaluates a FLiNG candidate or attachment version string against a local game's fingerprint,
    /// mapping all 5 FLiNG version string patterns into the 3 non-blocking confidence tiers:
    /// <see cref="TrainerVersionConfidence.RangeMatched"/>,
    /// <see cref="TrainerVersionConfidence.CheckByDate"/>, or
    /// <see cref="TrainerVersionConfidence.PossiblyOutdated"/>.
    /// </summary>
    public static (TrainerVersionConfidence Confidence, string Reason) EvaluateConfidence(
        string? versionLabel,
        string? updatedDateText,
        GameVersionFingerprint? localFingerprint)
    {
        ParsedFlingVersion parsed = ParseFlingVersionString(versionLabel, updatedDateText);

        Version? localVer = localFingerprint is not null && !IsEngineVersionNoise(localFingerprint.FilteredPeVersion)
            ? TryParseNumericVersion(localFingerprint.FilteredPeVersion)
            : null;

        DateTimeOffset? localGameDate = localFingerprint?.SteamLastUpdatedUtc ?? localFingerprint?.ExeLastWriteUtc;
        DateTimeOffset? trainerDate = parsed.EffectiveDate.HasValue
            ? new DateTimeOffset(parsed.EffectiveDate.Value.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc), TimeSpan.Zero)
            : null;

        switch (parsed.Kind)
        {
            case FlingVersionStringKind.VersionRange:
            case FlingVersionStringKind.SingleVersion:
            {
                if (localVer is not null && parsed.MinVersion is not null && parsed.MaxVersion is not null
                    && AreSameMagnitude(localVer, parsed.MaxVersion))
                {
                    if (CompareNormalized(localVer, parsed.MaxVersion) > 0)
                    {
                        return (TrainerVersionConfidence.PossiblyOutdated,
                            $"本地游戏版本 ({localFingerprint!.FilteredPeVersion}) 高于修改器声明版本 ({versionLabel})，可能已过期。");
                    }

                    if (CompareNormalized(localVer, parsed.MinVersion) >= 0)
                    {
                        return (TrainerVersionConfidence.RangeMatched,
                            $"本地游戏版本 ({localFingerprint!.FilteredPeVersion}) 命中修改器支持范围 ({versionLabel})。");
                    }

                    return (TrainerVersionConfidence.CheckByDate,
                        $"本地游戏版本 ({localFingerprint!.FilteredPeVersion}) 低于声明范围下限 ({versionLabel})，建议按更新日期核对。");
                }

                if (localGameDate.HasValue && trainerDate.HasValue
                    && localGameDate.Value - trainerDate.Value > DateDriftOutdatedThreshold)
                {
                    return (TrainerVersionConfidence.PossiblyOutdated,
                        $"本地游戏更新于 {localGameDate.Value:yyyy-MM-dd}，晚于修改器发布日期 {parsed.EffectiveDate:yyyy-MM-dd}，可能已过期。");
                }

                if (parsed.Kind == FlingVersionStringKind.VersionRange && trainerDate.HasValue && localGameDate.HasValue)
                {
                    return (TrainerVersionConfidence.RangeMatched,
                        $"版本区间 ({versionLabel}) 与本地游戏更新日期吻合。");
                }

                return (TrainerVersionConfidence.CheckByDate,
                    $"未读取到同量级本地版本号，建议按更新日期核对 ({versionLabel})。");
            }

            case FlingVersionStringKind.VersionWithUpdateDate:
            {
                if (localGameDate.HasValue && trainerDate.HasValue
                    && localGameDate.Value - trainerDate.Value > DateDriftOutdatedThreshold)
                {
                    return (TrainerVersionConfidence.PossiblyOutdated,
                        $"本地游戏更新于 {localGameDate.Value:yyyy-MM-dd}，晚于修改器更新日期 {parsed.EffectiveDate:yyyy-MM-dd}，可能已过期。");
                }

                if (localVer is not null && parsed.MinVersion is not null && parsed.MaxVersion is not null
                    && AreSameMagnitude(localVer, parsed.MaxVersion))
                {
                    if (CompareNormalized(localVer, parsed.MaxVersion) > 0)
                    {
                        return (TrainerVersionConfidence.PossiblyOutdated,
                            $"本地游戏版本 ({localFingerprint!.FilteredPeVersion}) 高于修改器版本 ({versionLabel})，可能已过期。");
                    }

                    if (CompareNormalized(localVer, parsed.MinVersion) >= 0)
                    {
                        return (TrainerVersionConfidence.RangeMatched,
                            $"本地游戏版本 ({localFingerprint!.FilteredPeVersion}) 与修改器版本及更新日期 ({parsed.EffectiveDate:yyyy-MM-dd}) 匹配。");
                    }
                }

                return (TrainerVersionConfidence.CheckByDate,
                    $"请结合修改器更新日期 ({parsed.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未知"}) 与本地构建核对。");
            }

            case FlingVersionStringKind.RangeWithTrailingUpdated:
            {
                if (localVer is not null && parsed.MinVersion is not null && parsed.MaxVersion is not null
                    && AreSameMagnitude(localVer, parsed.MaxVersion))
                {
                    if (CompareNormalized(localVer, parsed.MaxVersion) > 0)
                    {
                        return (TrainerVersionConfidence.PossiblyOutdated,
                            $"本地游戏版本 ({localFingerprint!.FilteredPeVersion}) 高于修改器上限 ({versionLabel})，可能已过期。");
                    }

                    if (CompareNormalized(localVer, parsed.MinVersion) >= 0)
                    {
                        return (TrainerVersionConfidence.RangeMatched,
                            $"本地游戏版本 ({localFingerprint!.FilteredPeVersion}) 在修改器区间 ({versionLabel}) 内。");
                    }
                }

                if (localGameDate.HasValue && trainerDate.HasValue
                    && localGameDate.Value - trainerDate.Value > DateDriftOutdatedThreshold)
                {
                    return (TrainerVersionConfidence.PossiblyOutdated,
                        $"本地游戏更新日期 ({localGameDate.Value:yyyy-MM-dd}) 晚于该版本附件日期 ({parsed.EffectiveDate:yyyy-MM-dd})，可能已过期。");
                }

                return (TrainerVersionConfidence.CheckByDate,
                    $"版本串含无日期 Updated 标记 ({versionLabel})，建议按附件更新日期核对。");
            }

            case FlingVersionStringKind.DateStampedRange:
            {
                if (localGameDate.HasValue && trainerDate.HasValue
                    && localGameDate.Value - trainerDate.Value > DateDriftOutdatedThreshold)
                {
                    return (TrainerVersionConfidence.PossiblyOutdated,
                        $"本地游戏更新于 {localGameDate.Value:yyyy-MM-dd}，晚于日期型修改器 ({parsed.EffectiveDate:yyyy-MM-dd})，可能已过期。");
                }

                return (TrainerVersionConfidence.CheckByDate,
                    $"日期型版本串 ({versionLabel})，建议按更新日期 ({parsed.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未知"}) 核对。");
            }

            case FlingVersionStringKind.OpenEndedPlus:
            {
                if (localGameDate.HasValue && trainerDate.HasValue
                    && localGameDate.Value - trainerDate.Value > OpenEndedOutdatedThreshold)
                {
                    return (TrainerVersionConfidence.PossiblyOutdated,
                        $"开放版本串 ({versionLabel}) 更新于 {parsed.EffectiveDate:yyyy-MM-dd}，早于本地游戏更新时间 {localGameDate.Value:yyyy-MM-dd}，可能已过期。");
                }

                return (TrainerVersionConfidence.CheckByDate,
                    $"开放式版本串 ({versionLabel}) 无法自动封顶校验，建议按更新日期核对。");
            }

            default:
            {
                if (localGameDate.HasValue && trainerDate.HasValue
                    && localGameDate.Value - trainerDate.Value > OpenEndedOutdatedThreshold)
                {
                    return (TrainerVersionConfidence.PossiblyOutdated,
                        $"本地游戏更新日期 ({localGameDate.Value:yyyy-MM-dd}) 明显晚于修改器日期 ({parsed.EffectiveDate:yyyy-MM-dd})，可能已过期。");
                }

                return (TrainerVersionConfidence.CheckByDate,
                    "修改器版本格式非标准，建议按更新日期与列表顶部最新版核对。");
            }
        }
    }

    public static DateOnly? TryParseDateOnly(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate))
        {
            return null;
        }

        string trimmed = rawDate.Trim();
        Match iso = IsoLikeDateRegex().Match(trimmed);
        if (iso.Success)
        {
            return TryCreateDate(iso.Groups[1].Value, iso.Groups[2].Value, iso.Groups[3].Value);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            return DateOnly.FromDateTime(dto.UtcDateTime);
        }

        return null;
    }

    private static string? TryExtractUsefulPeVersion(string exeFullPath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exeFullPath);
            foreach (string? candidate in new[] { info.ProductVersion, info.FileVersion })
            {
                string normalized = NormalizeVersionToken(candidate);
                if (!string.IsNullOrWhiteSpace(normalized) && !IsEngineVersionNoise(normalized))
                {
                    return normalized;
                }
            }
        }
        catch
        {
            // Ignore malformed PE version resource blocks.
        }

        return null;
    }

    private static (string? BuildId, DateTimeOffset? LastUpdatedUtc) TryReadAcfFingerprintFromDisk(string? installDir, int? steamAppId)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return (null, null);
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
            var parentDir = Directory.GetParent(normalized);
            if (parentDir is null || !string.Equals(parentDir.Name, "common", StringComparison.OrdinalIgnoreCase))
            {
                return (null, null);
            }

            var steamAppsDir = parentDir.Parent;
            if (steamAppsDir is null || !string.Equals(steamAppsDir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return (null, null);
            }

            string folderName = Path.GetFileName(normalized);
            if (steamAppId.HasValue && steamAppId.Value > 0)
            {
                string directPath = Path.Combine(steamAppsDir.FullName, $"appmanifest_{steamAppId.Value.ToString(CultureInfo.InvariantCulture)}.acf");
                if (File.Exists(directPath))
                {
                    return ParseAcfBuildAndUpdated(File.ReadAllText(directPath));
                }
            }

            foreach (string acfPath in Directory.EnumerateFiles(steamAppsDir.FullName, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(acfPath);
                Match installMatch = AcfInstallDirRegex().Match(text);
                if (installMatch.Success
                    && string.Equals(installMatch.Groups[1].Value, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    return ParseAcfBuildAndUpdated(text);
                }
            }
        }
        catch
        {
            // Ignore inaccessible Steam manifest directories.
        }

        return (null, null);
    }

    private static (string? BuildId, DateTimeOffset? LastUpdatedUtc) ParseAcfBuildAndUpdated(string acfText)
    {
        Match buildMatch = AcfBuildIdRegex().Match(acfText);
        string? rawBuild = buildMatch.Success ? buildMatch.Groups[1].Value.Trim() : null;
        string? buildId = !string.IsNullOrWhiteSpace(rawBuild) && !string.Equals(rawBuild, "0", StringComparison.Ordinal)
            ? rawBuild
            : null;

        DateTimeOffset? lastUpdated = null;
        Match updatedMatch = AcfLastUpdatedRegex().Match(acfText);
        if (updatedMatch.Success
            && long.TryParse(updatedMatch.Groups[1].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSec)
            && unixSec > 0)
        {
            try
            {
                lastUpdated = DateTimeOffset.FromUnixTimeSeconds(unixSec);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore out-of-range Unix epoch values.
            }
        }

        return (buildId, lastUpdated);
    }

    private static string NormalizeVersionToken(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return string.Empty;
        }

        string trimmed = rawVersion.Trim();
        int spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx > 0 && !trimmed.Contains(','))
        {
            trimmed = trimmed[..spaceIdx];
        }

        if (trimmed.Contains(','))
        {
            string[] parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            trimmed = string.Join('.', parts);
        }

        return trimmed.Trim();
    }

    private static Version? TryParseNumericVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string cleaned = NormalizeVersionToken(raw).TrimStart('v', 'V');
        string[] segs = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length < 2 || segs.Length > 4)
        {
            return null;
        }

        int[] nums = new int[4];
        for (int i = 0; i < segs.Length; i++)
        {
            string digits = segs[i];
            int nonDigit = 0;
            while (nonDigit < digits.Length && char.IsAsciiDigit(digits[nonDigit]))
            {
                nonDigit++;
            }

            if (nonDigit == 0 || !int.TryParse(digits[..nonDigit], NumberStyles.None, CultureInfo.InvariantCulture, out int val))
            {
                return null;
            }

            nums[i] = val;
        }

        return segs.Length switch
        {
            2 => new Version(nums[0], nums[1], 0, 0),
            3 => new Version(nums[0], nums[1], nums[2], 0),
            _ => new Version(nums[0], nums[1], nums[2], nums[3]),
        };
    }

    private static bool AreSameMagnitude(Version localVer, Version trainerVer)
    {
        // Reject comparing completely different numbering scales (e.g. build number 105402.0 vs v1.16).
        if (localVer.Major >= 100 && trainerVer.Major < 20)
        {
            return false;
        }

        if (trainerVer.Major >= 100 && localVer.Major < 20)
        {
            return false;
        }

        return true;
    }

    private static int CompareNormalized(Version left, Version right)
    {
        int c = left.Major.CompareTo(right.Major);
        if (c != 0)
        {
            return c;
        }

        c = Math.Max(0, left.Minor).CompareTo(Math.Max(0, right.Minor));
        if (c != 0)
        {
            return c;
        }

        c = Math.Max(0, left.Build).CompareTo(Math.Max(0, right.Build));
        if (c != 0)
        {
            return c;
        }

        return Math.Max(0, left.Revision).CompareTo(Math.Max(0, right.Revision));
    }

    private static DateOnly? TryCreateDate(string y, string m, string d)
    {
        if (int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            && int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out int month)
            && int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out int day))
        {
            try
            {
                return new DateOnly(year, month, day);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }
}
