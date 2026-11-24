using System.IO;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Structured provenance metadata describing where an installed/bound trainer originates,
/// clearly distinguishing external third-party websites from user-supplied local imports.
/// </summary>
public sealed record TrainerSourceProvenanceInfo
{
    public required bool IsExternalWebsite { get; init; }
    public required string BadgeText { get; init; }
    public required string SourceHost { get; init; }
    public required string SiteDisplayName { get; init; }
    public required bool HasValidHttpsSourceUrl { get; init; }
    public string? ValidatedHttpsSourceUrl { get; init; }
    public required string SandboxFileName { get; init; }
    public required string CompactOriginSummary { get; init; }
    public required string DetailedDisclosureText { get; init; }
}

/// <summary>
/// Structured provenance metadata for an online trainer discovery candidate before download.
/// </summary>
public sealed record TrainerCandidateProvenanceInfo
{
    public required string BadgeText { get; init; }
    public required string SourceHost { get; init; }
    public required string SiteDisplayName { get; init; }
    public required bool HasValidHttpsPageUrl { get; init; }
    public string? ValidatedHttpsPageUrl { get; init; }
    public required string ProvenanceHintText { get; init; }
}

/// <summary>
/// Pure, schema-free helper formatting trainer source badges, host/URL provenance, and clear Chinese disclosures
/// across all SPAWN trainer views (<c>全部修改器总览</c>, per-game workbench, online discovery, and settings).
/// </summary>
public static class TrainerSourceProvenance
{
    public const string OnlineDiscoveryDisclosureTitle =
        "第三方外部网站检索结果 · 非 SPAWN 内置修改器";

    public const string OnlineDiscoveryDisclosureBody =
        "以下候选条目实时检索自第三方外部网站 FLiNG Trainer（flingtrainer.com / archive.flingtrainer.com），不由 SPAWN 开发、内置或镜像托管。仅当您主动点击下载时才会直连该外部站点拉取，落盘即钉扎本地 SHA-256，启动时在独立 TrainerWorker 沙箱中隔离运行。";

    public const string UnboundProvenanceHintText =
        "来源说明：SPAWN 自身不内置或编写任何修改器。在线匹配仅在您主动触发时直连第三方外部网站 FLiNG Trainer (flingtrainer.com) 获取；手动添加本机文件则标记为「本地导入 · 用户手动添加」。全部修改器均经 SHA-256 钉扎与独立 TrainerWorker 沙箱隔离托管。";

    public const string OverviewProvenanceSummaryText =
        "来源与安全边界：SPAWN 自身不内置任何修改器。在线匹配项来自第三方外部网站（FLiNG Trainer · flingtrainer.com / archive.flingtrainer.com，仅在用户主动触发时直连源站获取），本地导入项由用户从本机手动添加；所有修改器落盘即钉扎 SHA-256，每次启动前复核并在独立 TrainerWorker 沙箱进程中隔离运行。";

    public const string OverviewEmptyStateDescriptionText =
        "在左侧选择任意单机游戏（Tier 0 或已核验离线单机），可按英文标准名从第三方外部网站 FLiNG Trainer (flingtrainer.com) 检索并主动下载修改器，或直接将本机 .exe / .zip / .rar / .7z 修改器拖入工作台导入至隔离沙箱（SPAWN 自身不内置任何修改器）。";

    public const string SettingsProvenanceTitle =
        "修改器来源声明与外部网站边界";

    public const string SettingsProvenanceDescription =
        "SPAWN 自身不内置、不编写、也不在自有服务器镜像任何修改器程序。在线检索与下载仅在您手动点击触发时直连第三方外部网站 FLiNG Trainer（https://flingtrainer.com 及可选归档站 https://archive.flingtrainer.com），严守源站 robots.txt 且零后台爬虫；拖入或浏览添加的本机文件标记为「本地导入 · 用户手动添加」并保留系统 Zone.Identifier。所有修改器落盘即钉扎本地 SHA-256，每次启动前执行 TOCTOU 复核，并在独立 Spawn.TrainerWorker 沙箱进程（KILL_ON_JOB_CLOSE 作业对象 + 零注入静音）中隔离运行。";

    public static bool IsExternalWebsiteProvider(TrainerProviderKind provider) =>
        provider is TrainerProviderKind.FlingMain or TrainerProviderKind.FlingArchive;

    /// <summary>
    /// Validates that <paramref name="rawUrl"/> is a well-formed absolute <c>https://</c> URL without user-info
    /// or control characters, extracting its normalized <c>https://</c> URI and lowercased host name.
    /// </summary>
    public static bool TryNormalizeHttpsSourceUrl(string? rawUrl, out string normalizedUrl, out string host)
    {
        normalizedUrl = string.Empty;
        host = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        string trimmed = rawUrl.Trim();
        foreach (char ch in trimmed)
        {
            if (ch < 0x20 || char.IsControl(ch))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return false;
        }

        string cleanHost = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanHost))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        host = cleanHost;
        return true;
    }

    /// <summary>
    /// Resolves the actual external host name from <paramref name="sourceUrl"/> when available,
    /// falling back to the canonical host of <paramref name="provider"/> for external website providers.
    /// </summary>
    public static string ResolveSourceHost(TrainerProviderKind provider, string? sourceUrl)
    {
        if (TryNormalizeHttpsSourceUrl(sourceUrl, out _, out string httpsHost))
        {
            return httpsHost;
        }

        if (TryExtractHttpOrHttpsHostForDisplay(sourceUrl, out string httpHost))
        {
            return httpHost;
        }

        return provider switch
        {
            TrainerProviderKind.FlingMain => FlingUrlPolicy.MainHost,
            TrainerProviderKind.FlingArchive => FlingUrlPolicy.ArchiveHost,
            _ => string.Empty,
        };
    }

    public static string ResolveExternalSiteName(TrainerProviderKind provider, string? host)
    {
        string cleanHost = host?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;

        if (string.Equals(cleanHost, FlingUrlPolicy.ArchiveHost, StringComparison.OrdinalIgnoreCase))
        {
            return "FLiNG Trainer 归档站";
        }

        if (string.Equals(cleanHost, FlingUrlPolicy.MainHost, StringComparison.OrdinalIgnoreCase)
            || cleanHost.EndsWith("." + FlingUrlPolicy.MainHost, StringComparison.OrdinalIgnoreCase))
        {
            return "FLiNG Trainer";
        }

        if (string.IsNullOrEmpty(cleanHost))
        {
            return provider == TrainerProviderKind.FlingArchive
                ? "FLiNG Trainer 归档站"
                : "FLiNG Trainer";
        }

        return "第三方站点";
    }

    /// <summary>
    /// Formats a compact, unmistakable source badge label distinguishing external third-party websites
    /// (e.g. <c>外部网站 · FLiNG Trainer (flingtrainer.com)</c>) from local manual imports (<c>本地导入 · 用户手动添加</c>).
    /// </summary>
    public static string FormatSourceBadgeLabel(TrainerProviderKind provider, string? sourceUrl = null)
    {
        string host = ResolveSourceHost(provider, sourceUrl);

        if (IsExternalWebsiteProvider(provider))
        {
            string siteName = ResolveExternalSiteName(provider, host);
            return $"外部网站 · {siteName} ({host})";
        }

        return !string.IsNullOrWhiteSpace(host)
            ? $"本地导入 · 用户手动添加 ({host})"
            : "本地导入 · 用户手动添加";
    }

    public static TrainerSourceProvenanceInfo DescribeRecord(TrainerCatalogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        bool isExternal = IsExternalWebsiteProvider(record.Provider);
        bool hasHttps = TryNormalizeHttpsSourceUrl(record.SourcePageUrl, out string validHttpsUrl, out _);
        string host = ResolveSourceHost(record.Provider, record.SourcePageUrl);
        string siteName = isExternal
            ? ResolveExternalSiteName(record.Provider, host)
            : "用户手动添加";
        string badge = FormatSourceBadgeLabel(record.Provider, record.SourcePageUrl);
        string sandboxFileName = ExtractSandboxFileName(record.SandboxRelativePath, record.DisplayName);

        string compactSummary;
        string detailedDisclosure;

        if (isExternal)
        {
            compactSummary = hasHttps
                ? $"第三方外部网站（非 SPAWN 内置）· 用户触发获取 · 来源网页：{validHttpsUrl}"
                : $"第三方外部网站 {siteName} ({host}) · 非 SPAWN 内置 · 用户主动触发直连获取";

            detailedDisclosure =
                $"来源说明：该修改器取自第三方外部网站 {siteName} ({host})，非 SPAWN 内置、自研或托管资源；仅在您主动点击下载时直连源站获取，落盘已钉扎本地 SHA-256，启动前复核并在独立 TrainerWorker 沙箱中隔离运行。";
        }
        else
        {
            compactSummary = hasHttps
                ? $"用户从本机手动导入 · 沙箱文件：{sandboxFileName} · 关联来源：{validHttpsUrl}"
                : (!string.IsNullOrWhiteSpace(host)
                    ? $"用户从本机手动导入 · 沙箱文件：{sandboxFileName} · 来源域名：{host}"
                    : $"用户从本机手动导入（非在线检索）· 沙箱文件：{sandboxFileName}");

            detailedDisclosure = hasHttps
                ? $"来源说明：该修改器由用户从本机手动添加（沙箱文件：{sandboxFileName}，关联来源：{validHttpsUrl}），非 SPAWN 内置组件；已保留系统来源标记、钉扎本地 SHA-256，并在独立 TrainerWorker 沙箱中隔离运行。"
                : $"来源说明：该修改器由用户从本机手动添加（沙箱文件：{sandboxFileName}），非外部在线检索且非 SPAWN 内置组件；已保留系统来源标记、钉扎本地 SHA-256，并在独立 TrainerWorker 沙箱中隔离运行。";
        }

        return new TrainerSourceProvenanceInfo
        {
            IsExternalWebsite = isExternal,
            BadgeText = badge,
            SourceHost = host,
            SiteDisplayName = siteName,
            HasValidHttpsSourceUrl = hasHttps,
            ValidatedHttpsSourceUrl = hasHttps ? validHttpsUrl : null,
            SandboxFileName = sandboxFileName,
            CompactOriginSummary = compactSummary,
            DetailedDisclosureText = detailedDisclosure,
        };
    }

    public static TrainerCandidateProvenanceInfo DescribeCandidate(TrainerCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        bool hasHttps = TryNormalizeHttpsSourceUrl(candidate.PageUrl, out string validHttpsUrl, out _);
        string host = ResolveSourceHost(candidate.Provider, candidate.PageUrl);
        string siteName = ResolveExternalSiteName(candidate.Provider, host);
        string badge = FormatSourceBadgeLabel(candidate.Provider, candidate.PageUrl);

        return new TrainerCandidateProvenanceInfo
        {
            BadgeText = badge,
            SourceHost = host,
            SiteDisplayName = siteName,
            HasValidHttpsPageUrl = hasHttps,
            ValidatedHttpsPageUrl = hasHttps ? validHttpsUrl : null,
            ProvenanceHintText = $"第三方外部网站 {siteName} ({host}) · 非 SPAWN 内置，点击下载才直连该站点拉取并钉扎 SHA-256",
        };
    }

    private static string ExtractSandboxFileName(string? sandboxRelativePath, string? fallbackDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(sandboxRelativePath))
        {
            string normalized = sandboxRelativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            string fileName = Path.GetFileName(normalized);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return string.IsNullOrWhiteSpace(fallbackDisplayName) ? "trainer.exe" : fallbackDisplayName.Trim();
    }

    private static bool TryExtractHttpOrHttpsHostForDisplay(string? rawUrl, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        string trimmed = rawUrl.Trim();
        foreach (char ch in trimmed)
        {
            if (ch < 0x20 || char.IsControl(ch))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if ((!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return false;
        }

        string cleanHost = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanHost))
        {
            return false;
        }

        host = cleanHost;
        return true;
    }
}
