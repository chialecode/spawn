using System.Globalization;
using System.Text;
using Spawn.Contracts;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress.Saves.Rules;

/// <summary>
/// Zero-dependency parser for official Ludusavi <c>manifest.yaml</c> files.
/// Handles top-level game titles, <c>alias</c> redirects, <c>steam.id</c>, <c>gog.id</c>,
/// <c>files</c>, <c>registry</c>, <c>tags</c> (<c>save</c>/<c>config</c>), and <c>when</c> Windows OS filters
/// across both YAML block and flow syntax without external NuGet dependencies.
/// </summary>
public static class LudusaviYamlManifestParser
{
    public static bool LooksLikeJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        foreach (char c in content)
        {
            if (char.IsWhiteSpace(c) || c == '\uFEFF')
            {
                continue;
            }

            return c is '{' or '[';
        }

        return false;
    }

    public static List<LudusaviRuleRecord> Parse(string manifestYaml, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestYaml);

        var lines = TokenizeLines(manifestYaml);
        var entries = new List<RawGameEntry>();
        var aliasesByTarget = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        int idx = 0;
        while (idx < lines.Count)
        {
            YamlLine line = lines[idx];
            if (line.Indent != 0)
            {
                idx++;
                continue;
            }

            if (!TrySplitKeyValue(line.Content, out string rawGameTitle, out string topInlineValue))
            {
                idx++;
                continue;
            }

            string gameTitle = UnquoteScalar(rawGameTitle).Trim();
            idx++;

            if (string.IsNullOrWhiteSpace(gameTitle))
            {
                idx = SkipIndentedBlock(lines, idx, parentIndent: 0);
                continue;
            }

            var entry = new RawGameEntry(gameTitle);
            if (!string.IsNullOrWhiteSpace(topInlineValue) && topInlineValue.StartsWith('{'))
            {
                ParseInlineGameBody(topInlineValue, entry);
            }

            int blockEnd = FindBlockEnd(lines, idx, parentIndent: 0);
            ParseGameBlock(lines, idx, blockEnd, entry);
            idx = blockEnd;

            if (!string.IsNullOrWhiteSpace(entry.AliasTarget)
                && entry.Files.Count == 0
                && entry.Registries.Count == 0)
            {
                string target = entry.AliasTarget.Trim();
                if (!aliasesByTarget.TryGetValue(target, out List<string>? list))
                {
                    list = [];
                    aliasesByTarget[target] = list;
                }

                if (!list.Contains(gameTitle, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(gameTitle);
                }
            }
            else
            {
                entries.Add(entry);
            }
        }

        var results = new List<LudusaviRuleRecord>(entries.Count);
        foreach (RawGameEntry entry in entries)
        {
            var savePaths = new List<string>();
            var configPaths = new List<string>();
            var registryRules = new List<string>();

            foreach (RawTargetItem fileItem in entry.Files)
            {
                if (!MatchesWindowsWhen(fileItem.WhenClauses))
                {
                    continue;
                }

                (bool isSave, bool isConfig) = ClassifyTags(fileItem.Tags);
                if (isConfig && !isSave)
                {
                    if (!configPaths.Contains(fileItem.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        configPaths.Add(fileItem.Path);
                    }
                }
                else
                {
                    if (!savePaths.Contains(fileItem.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        savePaths.Add(fileItem.Path);
                    }
                }
            }

            foreach (RawTargetItem regItem in entry.Registries)
            {
                if (!MatchesWindowsWhen(regItem.WhenClauses))
                {
                    continue;
                }

                (bool isSave, bool isConfig) = ClassifyTags(regItem.Tags);
                if (isConfig && !isSave)
                {
                    continue;
                }

                if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(regItem.Path, out string canonical, out _, out _)
                    && !registryRules.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                {
                    registryRules.Add(canonical);
                }
            }

            if (savePaths.Count == 0 && configPaths.Count == 0 && registryRules.Count == 0)
            {
                continue;
            }

            var aliases = new List<string>(entry.Aliases);
            if (aliasesByTarget.TryGetValue(entry.Title, out List<string>? redirectedAliases))
            {
                foreach (string a in redirectedAliases)
                {
                    if (!aliases.Contains(a, StringComparer.OrdinalIgnoreCase))
                    {
                        aliases.Add(a);
                    }
                }
            }

            results.Add(new LudusaviRuleRecord
            {
                RuleName = entry.Title,
                SteamAppId = entry.SteamAppId,
                GogId = entry.GogId,
                Aliases = aliases,
                SavePaths = FilterRedundantXdgPaths(savePaths),
                ConfigPaths = FilterRedundantXdgPaths(configPaths),
                RegistryRules = registryRules,
                UpdatedAtUtc = nowUtc,
            });
        }

        return results;
    }

    private static void ParseGameBlock(List<YamlLine> lines, int start, int end, RawGameEntry entry)
    {
        if (start >= end)
        {
            return;
        }

        int sectionIndent = lines[start].Indent;
        int i = start;

        while (i < end)
        {
            YamlLine line = lines[i];
            if (line.Indent != sectionIndent)
            {
                i++;
                continue;
            }

            if (!TrySplitKeyValue(line.Content, out string rawKey, out string inlineValue))
            {
                i++;
                continue;
            }

            string key = UnquoteScalar(rawKey).Trim();
            int childStart = i + 1;
            int childEnd = FindBlockEnd(lines, childStart, sectionIndent);

            if (key.Equals("alias", StringComparison.OrdinalIgnoreCase))
            {
                string val = UnquoteScalar(inlineValue).Trim();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    entry.AliasTarget = val;
                }
            }
            else if (key.Equals("aliases", StringComparison.OrdinalIgnoreCase))
            {
                ParseStringList(lines, childStart, childEnd, inlineValue, entry.Aliases);
            }
            else if (key.Equals("steam", StringComparison.OrdinalIgnoreCase))
            {
                string? idVal = ParseNestedIdValue(lines, childStart, childEnd, inlineValue);
                if (int.TryParse(idVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int steamId) && steamId > 0)
                {
                    entry.SteamAppId = steamId;
                }
            }
            else if (key.Equals("gog", StringComparison.OrdinalIgnoreCase))
            {
                string? idVal = ParseNestedIdValue(lines, childStart, childEnd, inlineValue);
                if (!string.IsNullOrWhiteSpace(idVal))
                {
                    entry.GogId = idVal;
                }
            }
            else if (key.Equals("files", StringComparison.OrdinalIgnoreCase))
            {
                ParseTargetMap(lines, childStart, childEnd, inlineValue, entry.Files);
            }
            else if (key.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                ParseTargetMap(lines, childStart, childEnd, inlineValue, entry.Registries);
            }

            i = childEnd;
        }
    }

    private static void ParseInlineGameBody(string flowObject, RawGameEntry entry)
    {
        foreach (var (k, v) in ParseFlowMap(flowObject))
        {
            if (k.Equals("alias", StringComparison.OrdinalIgnoreCase))
            {
                entry.AliasTarget = UnquoteScalar(v).Trim();
            }
            else if (k.Equals("steam", StringComparison.OrdinalIgnoreCase))
            {
                string? idVal = ExtractIdFromFlowMap(v);
                if (int.TryParse(idVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int steamId) && steamId > 0)
                {
                    entry.SteamAppId = steamId;
                }
            }
            else if (k.Equals("gog", StringComparison.OrdinalIgnoreCase))
            {
                entry.GogId = ExtractIdFromFlowMap(v);
            }
        }
    }

    private static void ParseTargetMap(
        List<YamlLine> lines,
        int start,
        int end,
        string inlineValue,
        List<RawTargetItem> targetList)
    {
        if (!string.IsNullOrWhiteSpace(inlineValue) && inlineValue.StartsWith('{'))
        {
            foreach (var (rawPath, rawMeta) in ParseFlowMap(inlineValue))
            {
                string path = UnquoteScalar(rawPath).Trim();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var item = new RawTargetItem(path);
                if (!string.IsNullOrWhiteSpace(rawMeta) && rawMeta.StartsWith('{'))
                {
                    ParseTargetInlineMeta(rawMeta, item);
                }

                targetList.Add(item);
            }
        }

        if (start >= end)
        {
            return;
        }

        int targetIndent = lines[start].Indent;
        int i = start;
        while (i < end)
        {
            YamlLine line = lines[i];
            if (line.Indent != targetIndent)
            {
                i++;
                continue;
            }

            if (!TrySplitKeyValue(line.Content, out string rawPath, out string metaInline))
            {
                i++;
                continue;
            }

            string path = UnquoteScalar(rawPath).Trim();
            int metaStart = i + 1;
            int metaEnd = FindBlockEnd(lines, metaStart, targetIndent);

            if (!string.IsNullOrWhiteSpace(path))
            {
                var item = new RawTargetItem(path);
                if (!string.IsNullOrWhiteSpace(metaInline) && metaInline.StartsWith('{'))
                {
                    ParseTargetInlineMeta(metaInline, item);
                }

                ParseTargetMetaBlock(lines, metaStart, metaEnd, item);
                targetList.Add(item);
            }

            i = metaEnd;
        }
    }

    private static void ParseTargetMetaBlock(List<YamlLine> lines, int start, int end, RawTargetItem item)
    {
        if (start >= end)
        {
            return;
        }

        int metaIndent = lines[start].Indent;
        int i = start;
        while (i < end)
        {
            YamlLine line = lines[i];
            if (line.Indent != metaIndent)
            {
                i++;
                continue;
            }

            if (!TrySplitKeyValue(line.Content, out string rawKey, out string inlineVal))
            {
                i++;
                continue;
            }

            string key = UnquoteScalar(rawKey).Trim();
            int childStart = i + 1;
            int childEnd = FindBlockEnd(lines, childStart, metaIndent);

            if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                ParseStringList(lines, childStart, childEnd, inlineVal, item.Tags);
            }
            else if (key.Equals("when", StringComparison.OrdinalIgnoreCase))
            {
                ParseWhenClauses(lines, childStart, childEnd, inlineVal, item.WhenClauses);
            }

            i = childEnd;
        }
    }

    private static void ParseTargetInlineMeta(string flowObj, RawTargetItem item)
    {
        foreach (var (k, v) in ParseFlowMap(flowObj))
        {
            if (k.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string t in ParseFlowSequence(v))
                {
                    string clean = UnquoteScalar(t).Trim();
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        item.Tags.Add(clean);
                    }
                }
            }
            else if (k.Equals("when", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string clauseText in ParseFlowSequence(v))
                {
                    var clause = new WhenClause();
                    foreach (var (ck, cv) in ParseFlowMap(clauseText))
                    {
                        if (ck.Equals("os", StringComparison.OrdinalIgnoreCase))
                        {
                            clause.Os = UnquoteScalar(cv).Trim();
                        }
                    }

                    item.WhenClauses.Add(clause);
                }
            }
        }
    }

    private static void ParseWhenClauses(
        List<YamlLine> lines,
        int start,
        int end,
        string inlineVal,
        List<WhenClause> sink)
    {
        if (!string.IsNullOrWhiteSpace(inlineVal) && inlineVal.StartsWith('['))
        {
            foreach (string clauseText in ParseFlowSequence(inlineVal))
            {
                var clause = new WhenClause();
                foreach (var (ck, cv) in ParseFlowMap(clauseText))
                {
                    if (ck.Equals("os", StringComparison.OrdinalIgnoreCase))
                    {
                        clause.Os = UnquoteScalar(cv).Trim();
                    }
                }

                sink.Add(clause);
            }
        }

        if (start >= end)
        {
            return;
        }

        int listIndent = lines[start].Indent;
        WhenClause? current = null;

        for (int i = start; i < end; i++)
        {
            YamlLine line = lines[i];
            string content = line.Content;
            if (line.Indent == listIndent && content.StartsWith('-'))
            {
                current = new WhenClause();
                sink.Add(current);

                string rest = content.Length > 1 ? content[1..].Trim() : string.Empty;
                if (rest.StartsWith('{'))
                {
                    foreach (var (ck, cv) in ParseFlowMap(rest))
                    {
                        if (ck.Equals("os", StringComparison.OrdinalIgnoreCase))
                        {
                            current.Os = UnquoteScalar(cv).Trim();
                        }
                    }
                }
                else if (TrySplitKeyValue(rest, out string ck, out string cv)
                    && UnquoteScalar(ck).Trim().Equals("os", StringComparison.OrdinalIgnoreCase))
                {
                    current.Os = UnquoteScalar(cv).Trim();
                }
            }
            else if (current is not null && line.Indent > listIndent)
            {
                if (TrySplitKeyValue(content, out string ck, out string cv)
                    && UnquoteScalar(ck).Trim().Equals("os", StringComparison.OrdinalIgnoreCase))
                {
                    current.Os = UnquoteScalar(cv).Trim();
                }
            }
        }
    }

    private static void ParseStringList(
        List<YamlLine> lines,
        int start,
        int end,
        string inlineVal,
        List<string> sink)
    {
        if (!string.IsNullOrWhiteSpace(inlineVal) && inlineVal.StartsWith('['))
        {
            foreach (string rawItem in ParseFlowSequence(inlineVal))
            {
                string clean = UnquoteScalar(rawItem).Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    sink.Add(clean);
                }
            }

            return;
        }

        for (int i = start; i < end; i++)
        {
            string content = lines[i].Content;
            if (content.StartsWith('-'))
            {
                string val = UnquoteScalar(content.Length > 1 ? content[1..].Trim() : string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    sink.Add(val);
                }
            }
        }
    }

    private static string? ParseNestedIdValue(List<YamlLine> lines, int start, int end, string inlineVal)
    {
        if (!string.IsNullOrWhiteSpace(inlineVal))
        {
            if (inlineVal.StartsWith('{'))
            {
                return ExtractIdFromFlowMap(inlineVal);
            }

            string scalar = UnquoteScalar(inlineVal).Trim();
            if (!string.IsNullOrWhiteSpace(scalar) && !scalar.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return scalar;
            }
        }

        for (int i = start; i < end; i++)
        {
            if (TrySplitKeyValue(lines[i].Content, out string rawKey, out string val)
                && UnquoteScalar(rawKey).Trim().Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                string clean = UnquoteScalar(val).Trim();
                return string.IsNullOrWhiteSpace(clean) || clean.Equals("null", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : clean;
            }
        }

        return null;
    }

    private static string? ExtractIdFromFlowMap(string flowMap)
    {
        foreach (var (k, v) in ParseFlowMap(flowMap))
        {
            if (k.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                string clean = UnquoteScalar(v).Trim();
                return string.IsNullOrWhiteSpace(clean) || clean.Equals("null", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : clean;
            }
        }

        return null;
    }

    private static bool MatchesWindowsWhen(List<WhenClause> clauses)
    {
        if (clauses.Count == 0)
        {
            return true;
        }

        foreach (WhenClause clause in clauses)
        {
            if (string.IsNullOrWhiteSpace(clause.Os)
                || string.Equals(clause.Os, "windows", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (bool IsSave, bool IsConfig) ClassifyTags(List<string> tags)
    {
        if (tags.Count == 0)
        {
            return (true, false);
        }

        bool isSave = false;
        bool isConfig = false;
        foreach (string tag in tags)
        {
            if (tag.Equals("save", StringComparison.OrdinalIgnoreCase))
            {
                isSave = true;
            }
            else if (tag.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                isConfig = true;
            }
        }

        return (!isSave && !isConfig) ? (true, false) : (isSave, isConfig);
    }

    private static List<string> FilterRedundantXdgPaths(List<string> paths)
    {
        bool hasWindowsNative = paths.Any(static p => !LudusaviPlaceholderExpander.UsesXdgPlaceholder(p));
        return hasWindowsNative
            ? paths.Where(static p => !LudusaviPlaceholderExpander.UsesXdgPlaceholder(p)).ToList()
            : paths;
    }

    private static List<YamlLine> TokenizeLines(string yaml)
    {
        var result = new List<YamlLine>();
        using var reader = new StringReader(yaml);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            string line = raw.TrimStart('\uFEFF');
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (indent >= line.Length)
            {
                continue;
            }

            string body = StripInlineComment(line[indent..]).TrimEnd();
            if (string.IsNullOrWhiteSpace(body) || body is "---" or "...")
            {
                continue;
            }

            result.Add(new YamlLine(indent, body));
        }

        return result;
    }

    private static string StripInlineComment(string text)
    {
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && inDouble && i + 1 < text.Length)
            {
                i++;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                if (inSingle && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                return text[..i];
            }
        }

        return text;
    }

    private static bool TrySplitKeyValue(string content, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        bool inSingle = false;
        bool inDouble = false;
        int bracketDepth = 0;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\\' && inDouble && i + 1 < content.Length)
            {
                i++;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                if (inSingle && i + 1 < content.Length && content[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inSingle = !inSingle;
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (!inSingle && !inDouble)
            {
                if (c is '[' or '{')
                {
                    bracketDepth++;
                }
                else if (c is ']' or '}')
                {
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                }
                else if (c == ':' && bracketDepth == 0 && (i == content.Length - 1 || char.IsWhiteSpace(content[i + 1])))
                {
                    key = content[..i].Trim();
                    value = i + 1 < content.Length ? content[(i + 1)..].Trim() : string.Empty;
                    return !string.IsNullOrEmpty(key);
                }
            }
        }

        return false;
    }

    private static List<(string Key, string Value)> ParseFlowMap(string raw)
    {
        var list = new List<(string, string)>();
        string trimmed = raw.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return list;
        }

        foreach (string part in SplitTopLevelFlowItems(trimmed[1..^1]))
        {
            if (TrySplitKeyValue(part, out string k, out string v))
            {
                list.Add((UnquoteScalar(k).Trim(), v.Trim()));
            }
        }

        return list;
    }

    private static List<string> ParseFlowSequence(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return [];
        }

        return SplitTopLevelFlowItems(trimmed[1..^1]);
    }

    private static List<string> SplitTopLevelFlowItems(string inner)
    {
        var items = new List<string>();
        bool inSingle = false;
        bool inDouble = false;
        int depth = 0;
        int start = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\\' && inDouble && i + 1 < inner.Length)
            {
                i++;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                if (inSingle && i + 1 < inner.Length && inner[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (!inSingle && !inDouble)
            {
                if (c is '[' or '{')
                {
                    depth++;
                }
                else if (c is ']' or '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (c == ',' && depth == 0)
                {
                    string piece = inner[start..i].Trim();
                    if (!string.IsNullOrEmpty(piece))
                    {
                        items.Add(piece);
                    }

                    start = i + 1;
                }
            }
        }

        string tail = inner[start..].Trim();
        if (!string.IsNullOrEmpty(tail))
        {
            items.Add(tail);
        }

        return items;
    }

    private static string UnquoteScalar(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            var sb = new StringBuilder(trimmed.Length - 2);
            for (int i = 1; i < trimmed.Length - 1; i++)
            {
                char c = trimmed[i];
                if (c == '\\' && i + 1 < trimmed.Length - 1)
                {
                    char next = trimmed[++i];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        _ => next,
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static int FindBlockEnd(List<YamlLine> lines, int start, int parentIndent)
    {
        int i = start;
        while (i < lines.Count && lines[i].Indent > parentIndent)
        {
            i++;
        }

        return i;
    }

    private static int SkipIndentedBlock(List<YamlLine> lines, int start, int parentIndent) =>
        FindBlockEnd(lines, start, parentIndent);

    private readonly record struct YamlLine(int Indent, string Content);

    private sealed class RawGameEntry(string title)
    {
        public string Title { get; } = title;
        public string? AliasTarget { get; set; }
        public int? SteamAppId { get; set; }
        public string? GogId { get; set; }
        public List<string> Aliases { get; } = [];
        public List<RawTargetItem> Files { get; } = [];
        public List<RawTargetItem> Registries { get; } = [];
    }

    private sealed class RawTargetItem(string path)
    {
        public string Path { get; } = path;
        public List<string> Tags { get; } = [];
        public List<WhenClause> WhenClauses { get; } = [];
    }

    private sealed class WhenClause
    {
        public string? Os { get; set; }
    }
}
