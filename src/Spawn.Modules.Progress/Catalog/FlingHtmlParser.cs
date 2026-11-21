using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Spawn.Contracts;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Index entry extracted from <c>/all-trainers/</c> or <c>archive.flingtrainer.com</c>.
/// </summary>
public sealed record FlingIndexEntry(
    string Title,
    string PageUrl,
    TrainerProviderKind Provider,
    string? VersionLabel = null,
    string? UpdatedDateText = null,
    long? ApproximateSizeBytes = null);

/// <summary>
/// Parsed FLiNG trainer detail page result containing filtered attachments and structured hotkeys.
/// </summary>
public sealed record FlingDetailPageParseResult(
    string? PageTitle,
    string? VersionLabel,
    string? UpdatedDateText,
    IReadOnlyList<TrainerAttachmentCandidate> Attachments,
    IReadOnlyList<TrainerHotkeyBindingRecord> Hotkeys,
    TrainerParseMode ParseMode);

/// <summary>
/// 100% clean-room AngleSharp DOM parser for FLiNG index pages, detail attachment tables,
/// <c>toolTips('.tooltip_x', '...')</c> descriptions, and structured hotkey tables.
/// </summary>
public sealed partial class FlingHtmlParser
{
    private static readonly HtmlParser Parser = new();

    private static readonly HashSet<string> BlockTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "P", "LI", "DIV", "H1", "H2", "H3", "H4", "H5", "H6", "TR", "BLOCKQUOTE", "PRE",
    };

    private static readonly HashSet<string> SkippedSectionHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "options",
        "notes",
        "updates",
        "update",
        "notice",
        "download",
        "downloads",
        "instructions",
        "game info",
        "related trainers",
    };

    private static readonly string[] TriggerLabelPrefixes =
    [
        "add ",
        "edit ",
        "set ",
        "teleport",
        "save location",
        "undo teleport",
        "restore ",
        "refill ",
        "unlock ",
        "complete ",
        "clear ",
        "reset ",
        "increase ",
        "decrease ",
        "gain ",
        "give ",
        "spawn ",
        "fill ",
        "kill all",
        "skip ",
    ];

    // Extracts toolTips('.tooltip_0', 'Description text') calls from <script> blocks prior to stripping.
    [GeneratedRegex(
        @"toolTips\s*\(\s*['""]\.?([A-Za-z0-9_\-]+)['""]\s*,\s*(['""])((?:\\.|(?!\2)[\s\S])*)\2\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolTipsCallRegex();

    // Clean-room structured hotkey chord grammar at the start of an option line.
    [GeneratedRegex(
        @"^(?<mods>(?:(?:Ctrl|Control|Alt|Shift)\s*\+\s*)*)(?<key>Num(?:pad|\s*Pad)?\s*(?:Enter|Return|[0-9]|\.|Plus|Minus|Multiply|Divide|\+|\-|\*|/)|F(?:1[0-2]|[1-9])(?![0-9])|Page\s*Up|PgUp|Page\s*Down|PgDn|Home|End|Insert|Ins|Delete|Del|[A-Za-z0-9](?=\s*(?:–|—|:|\s-\s)))\s*(?:–|—|:|(?<=\s)-(?=\s))\s*(?<label>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotkeyLineRegex();

    // Version pattern inside FLiNG titles or attachment names (covers all 5 FLiNG version styles).
    [GeneratedRegex(
        @"(v?\d+(?:\.\d+){1,3}\s*[-–—~]\s*v?20\d{6}|v?\d+(?:\.\d+){1,3}(?:\s*[-–—~]\s*v?\d+(?:\.\d+){1,3})?\+?\s+Updated\s+20\d{2}[.\-/]\d{1,2}[.\-/]\d{1,2}|v?\d+(?:\.\d+){1,3}\s*[-–—~]\s*v?\d+(?:\.\d+){1,3}\s+Updated\b|v?\d+(?:\.\d+){1,3}\s*[-–—~]\s*v?\d+(?:\.\d+){1,3}|v?\d+(?:\.\d+){1,3}\+|Early\s*Access\+?|v\d+(?:\.\d+){1,3})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedVersionRegex();

    [GeneratedRegex(@"\b(20\d{2}[.\-/](?:0?[1-9]|1[0-2])[.\-/](?:0?[1-9]|[12]\d|3[01]))\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedDateRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(KB|KiB|MB|MiB|GB|GiB|B)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileSizeRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^tooltip_[0-9a-zA-Z_\-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooltipClassNameRegex();

    /// <summary>
    /// Parses the FLiNG <c>/all-trainers/</c> or <c>archive.flingtrainer.com</c> HTML index into whitelisted,
    /// promo-filtered trainer links.
    /// </summary>
    public static IReadOnlyList<FlingIndexEntry> ParseTrainerIndex(
        string html,
        Uri baseUri,
        TrainerProviderKind provider)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        using IHtmlDocument document = Parser.ParseDocument(html);
        StripExecutableAndStyleNodes(document);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<FlingIndexEntry>();

        foreach (IElement anchor in document.QuerySelectorAll("a[href]"))
        {
            string? href = anchor.GetAttribute("href");
            string text = NormalizeWhitespace(anchor.TextContent);
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (FlingUrlPolicy.ContainsPromotionalToken(href) || FlingUrlPolicy.ContainsPromotionalToken(text))
            {
                continue;
            }

            if (!FlingUrlPolicy.TryParseAllowedFlingUri(href, baseUri, out Uri? resolved, out _) || resolved is null)
            {
                continue;
            }

            if (provider == TrainerProviderKind.FlingMain
                && !resolved.AbsolutePath.Contains("/trainer/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (provider == TrainerProviderKind.FlingArchive
                && (resolved.AbsolutePath == "/" || resolved.AbsolutePath.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string normalizedUrl = resolved.GetLeftPart(UriPartial.Path);
            if (!seen.Add(normalizedUrl))
            {
                continue;
            }

            string? version = ExtractVersionLabel(text);
            string? date = ExtractDateText(text);
            long? approxSize = null;
            if (provider == TrainerProviderKind.FlingArchive)
            {
                IElement? row = anchor.Closest("tr");
                if (row is not null)
                {
                    approxSize = ExtractFileSizeBytes(NormalizeWhitespace(row.TextContent));
                }
            }

            entries.Add(new FlingIndexEntry(text, resolved.AbsoluteUri, provider, version, date, approxSize));
        }

        return entries;
    }

    /// <summary>
    /// Parses a FLiNG detail page HTML to extract filtered attachment downloads, tooltip metadata,
    /// and structured hotkeys. Gracefully falls back to <see cref="TrainerParseMode.FreeHosted"/> when no
    /// structured hotkeys can be extracted.
    /// </summary>
    public static FlingDetailPageParseResult ParseDetailPage(
        string html,
        Uri pageUri,
        string trainerIdPlaceholder = "",
        GameVersionFingerprint? localFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(pageUri);

        if (string.IsNullOrWhiteSpace(html))
        {
            return new FlingDetailPageParseResult(null, null, null, [], [], TrainerParseMode.FreeHosted);
        }

        using IHtmlDocument document = Parser.ParseDocument(html);

        // 1. Extract toolTips('.tooltip_x', 'description') from <script> tags BEFORE stripping scripts.
        Dictionary<string, string> tooltipMap = ExtractTooltipMap(document);

        // 2. Strip all <script>, <style>, <noscript> nodes from the DOM.
        StripExecutableAndStyleNodes(document);

        string? pageTitle = NormalizeWhitespace(
            document.QuerySelector("h1.entry-title, article h1, h1")?.TextContent
            ?? document.Title
            ?? string.Empty);
        if (string.IsNullOrWhiteSpace(pageTitle))
        {
            pageTitle = null;
        }

        string? pageVersion = ExtractVersionLabel(pageTitle);
        string? pageUpdated = ExtractDateText(pageTitle)
            ?? ExtractDateText(document.QuerySelector("time[datetime]")?.GetAttribute("datetime"))
            ?? ExtractDateText(document.QuerySelector("time")?.TextContent);

        // 3. Extract whitelisted, promo-filtered attachment downloads.
        List<TrainerAttachmentCandidate> attachments = ExtractAttachments(
            document,
            pageUri,
            pageVersion,
            pageUpdated,
            localFingerprint);

        // 4. Extract structured hotkeys with tooltip enrichment.
        List<TrainerHotkeyBindingRecord> hotkeys = ExtractHotkeysFromDocument(
            document,
            tooltipMap,
            trainerIdPlaceholder);

        TrainerParseMode parseMode = hotkeys.Count > 0
            ? TrainerParseMode.Structured
            : TrainerParseMode.FreeHosted;

        string? effectiveVersion = attachments.Count > 0 ? (attachments[0].VersionLabel ?? pageVersion) : pageVersion;
        string? effectiveDate = attachments.Count > 0 ? (attachments[0].UpdatedDateText ?? pageUpdated) : pageUpdated;

        return new FlingDetailPageParseResult(
            PageTitle: pageTitle,
            VersionLabel: effectiveVersion,
            UpdatedDateText: effectiveDate,
            Attachments: attachments,
            Hotkeys: hotkeys,
            ParseMode: parseMode);
    }

    /// <summary>
    /// Parses structured hotkeys from an HTML snippet (such as <c>wp-json</c> <c>content.rendered</c> or detail HTML).
    /// Returns <see cref="TrainerParseMode.FreeHosted"/> with an empty list when no hotkeys are present.
    /// </summary>
    public static (IReadOnlyList<TrainerHotkeyBindingRecord> Hotkeys, TrainerParseMode ParseMode) ParseHotkeysFromHtml(
        string html,
        string trainerIdPlaceholder = "")
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return ([], TrainerParseMode.FreeHosted);
        }

        using IHtmlDocument document = Parser.ParseDocument(html);
        Dictionary<string, string> tooltipMap = ExtractTooltipMap(document);
        StripExecutableAndStyleNodes(document);

        var hotkeys = ExtractHotkeysFromDocument(document, tooltipMap, trainerIdPlaceholder);
        return (hotkeys, hotkeys.Count > 0 ? TrainerParseMode.Structured : TrainerParseMode.FreeHosted);
    }

    private static Dictionary<string, string> ExtractTooltipMap(IHtmlDocument document)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (IElement script in document.QuerySelectorAll("script"))
        {
            string body = script.TextContent;
            if (string.IsNullOrWhiteSpace(body) || !body.Contains("toolTips", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in ToolTipsCallRegex().Matches(body))
            {
                string className = match.Groups[1].Value.Trim();
                string rawJsValue = match.Groups[3].Value;
                string decoded = DecodeTooltipText(rawJsValue);
                if (!string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(decoded))
                {
                    map[className] = decoded;
                }
            }
        }

        return map;
    }

    private static string DecodeTooltipText(string rawJsLiteral)
    {
        if (string.IsNullOrWhiteSpace(rawJsLiteral))
        {
            return string.Empty;
        }

        string unescaped = rawJsLiteral
            .Replace("\\'", "'", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", string.Empty, StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);

        using IHtmlDocument fragDoc = Parser.ParseDocument($"<div>{unescaped}</div>");
        string text = fragDoc.Body?.TextContent ?? unescaped;
        return NormalizeMultilineText(text);
    }

    private static void StripExecutableAndStyleNodes(IHtmlDocument document)
    {
        foreach (IElement node in document.QuerySelectorAll("script, style, noscript").ToArray())
        {
            node.Remove();
        }
    }

    private static List<TrainerAttachmentCandidate> ExtractAttachments(
        IHtmlDocument document,
        Uri pageUri,
        string? pageVersion,
        string? pageUpdated,
        GameVersionFingerprint? localFingerprint)
    {
        var collected = new List<(TrainerAttachmentCandidate Candidate, DateOnly? SortDate, Version? SortVersion, int Order)>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int order = 0;

        // Priority 1: Structured download attachment table rows (table.da-attachments-table tr, .da-attachments-list li).
        var attachmentContainers = document.QuerySelectorAll(
            "table.da-attachments-table tbody tr, table.da-attachments-table tr, ul.da-attachments-list li, .da-attachments li, .trainer-downloads tr, .trainer-downloads li");

        foreach (IElement row in attachmentContainers)
        {
            IElement? anchor = row.QuerySelector("a[href]");
            if (anchor is null)
            {
                continue;
            }

            TryAddAttachmentFromAnchor(anchor, row, pageUri, pageVersion, pageUpdated, localFingerprint, seenUrls, collected, ref order);
        }

        // Priority 2: Standalone download anchors inside entry content (e.g. archive direct links or attachment-link anchors).
        if (collected.Count == 0)
        {
            foreach (IElement anchor in document.QuerySelectorAll(".entry-content a[href], article a[href], a.attachment-link[href]"))
            {
                string? href = anchor.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                bool looksLikeDownload = anchor.ClassList.Contains("attachment-link")
                    || href.Contains("/downloads/", StringComparison.OrdinalIgnoreCase)
                    || href.Contains("attachment-id", StringComparison.OrdinalIgnoreCase)
                    || href.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || href.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                    || href.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                    || href.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                if (!looksLikeDownload)
                {
                    continue;
                }

                TryAddAttachmentFromAnchor(anchor, anchor.ParentElement, pageUri, pageVersion, pageUpdated, localFingerprint, seenUrls, collected, ref order);
            }
        }

        // Sort latest / newest version first while preserving all historical attachments.
        return collected
            .OrderByDescending(x => x.SortDate ?? DateOnly.MinValue)
            .ThenByDescending(x => x.SortVersion ?? new Version(0, 0, 0, 0))
            .ThenBy(x => x.Order)
            .Select(x => x.Candidate)
            .ToList();
    }

    private static void TryAddAttachmentFromAnchor(
        IElement anchor,
        IElement? rowContainer,
        Uri pageUri,
        string? fallbackVersion,
        string? fallbackDate,
        GameVersionFingerprint? localFingerprint,
        HashSet<string> seenUrls,
        List<(TrainerAttachmentCandidate Candidate, DateOnly? SortDate, Version? SortVersion, int Order)> sink,
        ref int order)
    {
        string? href = anchor.GetAttribute("href");
        string anchorText = NormalizeWhitespace(anchor.TextContent);
        string titleAttr = NormalizeWhitespace(anchor.GetAttribute("title") ?? string.Empty);
        string rowText = rowContainer is not null ? NormalizeWhitespace(rowContainer.TextContent) : anchorText;

        if (string.IsNullOrWhiteSpace(href))
        {
            return;
        }

        // Explicit promo / ad / installer link filtering on href, anchor text, title attribute, and row text.
        if (FlingUrlPolicy.ContainsPromotionalToken(href)
            || FlingUrlPolicy.ContainsPromotionalToken(anchorText)
            || FlingUrlPolicy.ContainsPromotionalToken(titleAttr)
            || FlingUrlPolicy.ContainsPromotionalToken(rowText))
        {
            return;
        }

        if (!FlingUrlPolicy.TryParseAllowedFlingUri(href, pageUri, out Uri? resolvedUri, out _) || resolvedUri is null)
        {
            return;
        }

        if (!seenUrls.Add(resolvedUri.AbsoluteUri))
        {
            return;
        }

        string fileName = !string.IsNullOrWhiteSpace(anchorText)
            ? anchorText
            : (!string.IsNullOrWhiteSpace(titleAttr) ? titleAttr : Path.GetFileName(resolvedUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "trainer-download";
        }

        string? versionLabel = ExtractVersionLabel(fileName)
            ?? ExtractVersionLabel(rowText)
            ?? fallbackVersion;

        string? updatedDateText = ExtractDateFromRow(rowContainer)
            ?? ExtractDateText(fileName)
            ?? ExtractDateText(rowText)
            ?? fallbackDate;

        long? approxSize = ExtractFileSizeBytes(rowText);

        var (confidence, reason) = GameVersionFingerprintService.EvaluateConfidence(
            versionLabel,
            updatedDateText,
            localFingerprint);

        ParsedFlingVersion parsedVer = GameVersionFingerprintService.ParseFlingVersionString(versionLabel, updatedDateText);
        DateOnly? sortDate = parsedVer.EffectiveDate ?? GameVersionFingerprintService.TryParseDateOnly(updatedDateText);
        Version? sortVer = parsedVer.MaxVersion ?? parsedVer.MinVersion;

        string attachmentId = anchor.GetAttribute("data-attachment-id")?.Trim()
            ?? ComputeStableShortId(resolvedUri.AbsoluteUri);

        sink.Add((
            new TrainerAttachmentCandidate
            {
                AttachmentId = attachmentId,
                FileName = fileName,
                DownloadUrl = resolvedUri.AbsoluteUri,
                VersionLabel = versionLabel,
                UpdatedDateText = updatedDateText,
                ApproximateSizeBytes = approxSize,
                VersionConfidence = confidence,
                ConfidenceReason = reason,
            },
            sortDate,
            sortVer,
            order++));
    }

    private static List<TrainerHotkeyBindingRecord> ExtractHotkeysFromDocument(
        IHtmlDocument document,
        Dictionary<string, string> tooltipMap,
        string trainerIdPlaceholder)
    {
        IElement? root = document.QuerySelector(".entry-content")
            ?? document.QuerySelector("article")
            ?? document.Body;

        if (root is null)
        {
            return [];
        }

        var lines = new List<(string Text, string? Tooltip)>();
        CollectVisualLines(root, tooltipMap, lines);

        var bindings = new List<TrainerHotkeyBindingRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawLine, tooltip) in lines)
        {
            string cleaned = NormalizeWhitespace(rawLine);
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 4 || SkippedSectionHeadings.Contains(cleaned))
            {
                continue;
            }

            if (!TryParseHotkeyLine(cleaned, out string canonicalChord, out string label))
            {
                continue;
            }

            string dedupKey = $"{canonicalChord}|{label}";
            if (!seen.Add(dedupKey))
            {
                continue;
            }

            int sortOrder = bindings.Count;
            bindings.Add(new TrainerHotkeyBindingRecord
            {
                TrainerId = trainerIdPlaceholder,
                EntryId = $"hk_{(sortOrder + 1).ToString("D3", CultureInfo.InvariantCulture)}",
                Label = label,
                Tooltip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip,
                OriginalChord = canonicalChord,
                MappedChord = null,
                EntryKind = InferEntryKind(label),
                ProxyState = false,
                SortOrder = sortOrder,
            });
        }

        return bindings;
    }

    private static void CollectVisualLines(
        IElement container,
        Dictionary<string, string> tooltipMap,
        List<(string Text, string? Tooltip)> sink)
    {
        // Skip attachment download tables / nav / footer areas so download filenames are not misread as hotkeys.
        if (ShouldSkipElementForHotkeys(container))
        {
            return;
        }

        bool hasNestedBlocks = container.Children.Any(c => BlockTagNames.Contains(c.TagName));
        if (hasNestedBlocks)
        {
            var inlineBuffer = new StringBuilder();
            var inlineTooltips = new List<string>();

            void FlushInlineBuffer()
            {
                FlushLineText(inlineBuffer.ToString(), inlineTooltips, sink);
                inlineBuffer.Clear();
                inlineTooltips.Clear();
            }

            foreach (INode child in container.ChildNodes)
            {
                if (child is IElement childEl && BlockTagNames.Contains(childEl.TagName))
                {
                    FlushInlineBuffer();
                    CollectVisualLines(childEl, tooltipMap, sink);
                }
                else
                {
                    WalkInlineNode(child, tooltipMap, inlineBuffer, inlineTooltips, sink);
                }
            }

            FlushInlineBuffer();
            return;
        }

        var lineBuffer = new StringBuilder();
        var lineTooltips = new List<string>();

        CollectTooltipsFromElement(container, tooltipMap, lineTooltips);
        foreach (INode child in container.ChildNodes)
        {
            WalkInlineNode(child, tooltipMap, lineBuffer, lineTooltips, sink);
        }

        // Critical: flush trailing item ending directly at </p> / </li> / </div> without a trailing <br>.
        FlushLineText(lineBuffer.ToString(), lineTooltips, sink);
    }

    private static void WalkInlineNode(
        INode node,
        Dictionary<string, string> tooltipMap,
        StringBuilder lineBuffer,
        List<string> lineTooltips,
        List<(string Text, string? Tooltip)> sink)
    {
        if (node is IText textNode)
        {
            string value = textNode.Data;
            if (value.Contains('\n') || value.Contains('\r'))
            {
                string[] segments = value.Split('\n');
                for (int i = 0; i < segments.Length; i++)
                {
                    lineBuffer.Append(segments[i].TrimEnd('\r'));
                    if (i < segments.Length - 1)
                    {
                        FlushLineText(lineBuffer.ToString(), lineTooltips, sink);
                        lineBuffer.Clear();
                        lineTooltips.Clear();
                    }
                }
            }
            else
            {
                lineBuffer.Append(value);
            }

            return;
        }

        if (node is not IElement el)
        {
            return;
        }

        if (ShouldSkipElementForHotkeys(el))
        {
            return;
        }

        if (string.Equals(el.TagName, "BR", StringComparison.OrdinalIgnoreCase))
        {
            FlushLineText(lineBuffer.ToString(), lineTooltips, sink);
            lineBuffer.Clear();
            lineTooltips.Clear();
            return;
        }

        CollectTooltipsFromElement(el, tooltipMap, lineTooltips);
        foreach (INode child in el.ChildNodes)
        {
            WalkInlineNode(child, tooltipMap, lineBuffer, lineTooltips, sink);
        }
    }

    private static void CollectTooltipsFromElement(
        IElement el,
        Dictionary<string, string> tooltipMap,
        List<string> lineTooltips)
    {
        foreach (string cls in el.ClassList)
        {
            if (tooltipMap.TryGetValue(cls, out string? tip) && !string.IsNullOrWhiteSpace(tip))
            {
                if (!lineTooltips.Contains(tip, StringComparer.Ordinal))
                {
                    lineTooltips.Add(tip);
                }
            }
            else if (TooltipClassNameRegex().IsMatch(cls) && tooltipMap.TryGetValue("." + cls, out string? dottedTip) && !string.IsNullOrWhiteSpace(dottedTip))
            {
                if (!lineTooltips.Contains(dottedTip, StringComparer.Ordinal))
                {
                    lineTooltips.Add(dottedTip);
                }
            }
        }

        string? dataTooltip = el.GetAttribute("data-tooltip") ?? el.GetAttribute("data-original-title");
        if (!string.IsNullOrWhiteSpace(dataTooltip))
        {
            string cleaned = NormalizeMultilineText(dataTooltip);
            if (!string.IsNullOrWhiteSpace(cleaned) && !lineTooltips.Contains(cleaned, StringComparer.Ordinal))
            {
                lineTooltips.Add(cleaned);
            }
        }
    }

    private static bool ShouldSkipElementForHotkeys(IElement el)
    {
        string tag = el.TagName;
        if (tag.Equals("TABLE", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("NAV", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("FOOTER", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("HEADER", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("ASIDE", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("FORM", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string cls in el.ClassList)
        {
            if (cls.Contains("da-attachments", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("attachment", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("comments", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("related", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void FlushLineText(
        string text,
        List<string> tooltips,
        List<(string Text, string? Tooltip)> sink)
    {
        string normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        string? combinedTooltip = tooltips.Count > 0 ? string.Join("\n", tooltips) : null;
        sink.Add((normalized, combinedTooltip));
    }

    /// <summary>
    /// Parses a single text line into a canonical hotkey chord and option label.
    /// </summary>
    public static bool TryParseHotkeyLine(string line, out string canonicalChord, out string label)
    {
        canonicalChord = string.Empty;
        label = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string normalized = NormalizeWhitespace(line);
        Match m = HotkeyLineRegex().Match(normalized);
        if (!m.Success)
        {
            return false;
        }

        string rawMods = m.Groups["mods"].Value;
        string rawKey = m.Groups["key"].Value.Trim();
        string rawLabel = m.Groups["label"].Value.Trim(' ', '-', '–', '—', ':');

        if (string.IsNullOrWhiteSpace(rawLabel) || rawLabel.Length < 2)
        {
            return false;
        }

        // Single alphanumeric key without any modifier (e.g. "1 - Something" or "A - Note") is rejected to prevent prose false positives.
        if (string.IsNullOrWhiteSpace(rawMods) && rawKey.Length == 1 && char.IsLetterOrDigit(rawKey[0]))
        {
            return false;
        }

        string? normalizedKey = CanonicalizeMainKey(rawKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return false;
        }

        string normalizedMods = CanonicalizeModifiers(rawMods);
        canonicalChord = string.IsNullOrEmpty(normalizedMods)
            ? normalizedKey
            : $"{normalizedMods}+{normalizedKey}";
        label = rawLabel;
        return true;
    }

    private static string CanonicalizeModifiers(string rawMods)
    {
        if (string.IsNullOrWhiteSpace(rawMods))
        {
            return string.Empty;
        }

        bool ctrl = false;
        bool alt = false;
        bool shift = false;

        foreach (string part in rawMods.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
        }

        var ordered = new List<string>(3);
        if (ctrl) ordered.Add("Ctrl");
        if (alt) ordered.Add("Alt");
        if (shift) ordered.Add("Shift");
        return string.Join('+', ordered);
    }

    private static string? CanonicalizeMainKey(string rawKey)
    {
        string trimmed = NormalizeWhitespace(rawKey);
        if (trimmed.StartsWith("Num", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed;
            if (rest.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[6..].Trim();
            }
            else if (rest.StartsWith("Num Pad", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[7..].Trim();
            }
            else
            {
                rest = rest[3..].Trim();
            }

            if (rest.Length == 1 && char.IsAsciiDigit(rest[0]))
            {
                return $"Num {rest}";
            }

            return rest.ToLowerInvariant() switch
            {
                "." => "Num .",
                "+" or "plus" => "Num +",
                "-" or "minus" => "Num -",
                "*" or "multiply" => "Num *",
                "/" or "divide" => "Num /",
                "enter" or "return" => "Num Enter",
                _ => null,
            };
        }

        if (trimmed.Length >= 2 && (trimmed[0] == 'F' || trimmed[0] == 'f')
            && int.TryParse(trimmed[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int fNum)
            && fNum is >= 1 and <= 12)
        {
            return $"F{fNum.ToString(CultureInfo.InvariantCulture)}";
        }

        string lowerNoSpace = trimmed.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return lowerNoSpace switch
        {
            "home" => "Home",
            "end" => "End",
            "insert" or "ins" => "Insert",
            "delete" or "del" => "Delete",
            "pageup" or "pgup" => "Page Up",
            "pagedown" or "pgdn" => "Page Down",
            _ when trimmed.Length == 1 && char.IsAsciiLetterOrDigit(trimmed[0]) => trimmed.ToUpperInvariant(),
            _ => null,
        };
    }

    private static TrainerHotkeyEntryKind InferEntryKind(string label)
    {
        foreach (string prefix in TriggerLabelPrefixes)
        {
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return TrainerHotkeyEntryKind.Trigger;
            }
        }

        return TrainerHotkeyEntryKind.Toggle;
    }

    private static string? ExtractVersionLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match m = EmbeddedVersionRegex().Match(text);
        return m.Success ? NormalizeWhitespace(m.Value) : null;
    }

    private static string? ExtractDateFromRow(IElement? row)
    {
        if (row is null)
        {
            return null;
        }

        IElement? dateCell = row.QuerySelector(".attachment-date, td.date, time");
        if (dateCell is not null)
        {
            string? dtAttr = dateCell.GetAttribute("datetime");
            string? fromAttr = ExtractDateText(dtAttr);
            if (!string.IsNullOrWhiteSpace(fromAttr))
            {
                return fromAttr;
            }

            string cellText = NormalizeWhitespace(dateCell.TextContent);
            if (!string.IsNullOrWhiteSpace(cellText))
            {
                return ExtractDateText(cellText) ?? cellText;
            }
        }

        return null;
    }

    private static string? ExtractDateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match m = EmbeddedDateRegex().Match(text);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        DateOnly? parsed = GameVersionFingerprintService.TryParseDateOnly(text);
        return parsed?.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
    }

    private static long? ExtractFileSizeBytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match m = FileSizeRegex().Match(text);
        if (!m.Success
            || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || value < 0)
        {
            return null;
        }

        double multiplier = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" or "KIB" => 1024.0,
            "MB" or "MIB" => 1024.0 * 1024.0,
            "GB" or "GIB" => 1024.0 * 1024.0 * 1024.0,
            _ => 1.0,
        };

        return (long)Math.Round(value * multiplier);
    }

    private static string ComputeStableShortId(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string replaced = text
            .Replace('\u00A0', ' ')
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return MultiSpaceRegex().Replace(replaced, " ").Trim();
    }

    private static string NormalizeMultilineText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var cleaned = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            string norm = NormalizeWhitespace(line);
            if (!string.IsNullOrWhiteSpace(norm))
            {
                cleaned.Add(norm);
            }
        }

        return string.Join("\n", cleaned);
    }
}
