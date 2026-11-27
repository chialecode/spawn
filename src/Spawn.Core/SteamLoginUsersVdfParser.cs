using System.Globalization;
using System.Text;

namespace Spawn.Core;

/// <summary>
/// Parsed user record from Steam's <c>config\loginusers.vdf</c>.
/// </summary>
public sealed record SteamLoginUserEntry(
    uint AccountId,
    ulong SteamId64,
    string? AccountName,
    string? PersonaName,
    bool MostRecent,
    long? Timestamp);

/// <summary>
/// Pure managed text KeyValues1 (VDF) parser for Steam's <c>config\loginusers.vdf</c>.
/// Handles quoted strings with escape sequences, unquoted tokens, <c>//</c> comments, BOM, and nested blocks.
/// </summary>
public static class SteamLoginUsersVdfParser
{
    /// <summary>Standard SteamID64 individual account universe/type base offset (<c>76561197960265728</c>).</summary>
    public const ulong SteamId64BaseOffset = 76561197960265728UL;

    /// <summary>
    /// Converts a 64-bit SteamID to its 32-bit account ID (SteamID3 / <c>userdata\&lt;accountId&gt;</c> folder name).
    /// </summary>
    public static bool TryConvertSteamId64ToAccountId(ulong steamId64, out uint accountId)
    {
        if (steamId64 > SteamId64BaseOffset)
        {
            ulong diff = steamId64 - SteamId64BaseOffset;
            if (diff is > 0 and <= uint.MaxValue)
            {
                accountId = (uint)diff;
                return true;
            }
        }

        accountId = 0;
        return false;
    }

    /// <summary>
    /// Converts a 32-bit Steam account ID to its canonical 64-bit individual SteamID.
    /// </summary>
    public static ulong ConvertAccountIdToSteamId64(uint accountId) =>
        accountId == 0 ? 0UL : SteamId64BaseOffset + accountId;

    /// <summary>
    /// Parses <c>config\loginusers.vdf</c> text into accounts ordered by <see cref="SteamLoginUserEntry.MostRecent"/>,
    /// then <see cref="SteamLoginUserEntry.Timestamp"/> descending, then <see cref="SteamLoginUserEntry.AccountId"/> ascending.
    /// </summary>
    public static IReadOnlyList<SteamLoginUserEntry> Parse(string? vdfText)
    {
        if (string.IsNullOrWhiteSpace(vdfText))
        {
            return [];
        }

        var tokens = Tokenize(vdfText);
        if (tokens.Count == 0)
        {
            return [];
        }

        int pos = 0;
        var root = ParseBlock(tokens, ref pos, expectClosingBrace: false, depth: 0);

        Dictionary<string, object>? usersMap = null;
        foreach (var (key, value) in root)
        {
            if (string.Equals(key, "users", StringComparison.OrdinalIgnoreCase)
                && value is Dictionary<string, object> map)
            {
                usersMap = map;
                break;
            }
        }

        if (usersMap is null || usersMap.Count == 0)
        {
            return [];
        }

        var entries = new List<SteamLoginUserEntry>(usersMap.Count);
        var seenAccountIds = new HashSet<uint>();

        foreach (var (rawSteamId, userValue) in usersMap)
        {
            if (userValue is not Dictionary<string, object> fields)
            {
                continue;
            }

            if (!ulong.TryParse(rawSteamId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId64)
                || !TryConvertSteamId64ToAccountId(steamId64, out uint accountId)
                || !seenAccountIds.Add(accountId))
            {
                continue;
            }

            string? accountName = TryGetString(fields, "AccountName");
            string? personaName = TryGetString(fields, "PersonaName");
            string? mostRecentRaw = TryGetString(fields, "MostRecent");
            string? timestampRaw = TryGetString(fields, "Timestamp");

            bool mostRecent = string.Equals(mostRecentRaw, "1", StringComparison.Ordinal)
                              || string.Equals(mostRecentRaw, "true", StringComparison.OrdinalIgnoreCase);

            long? timestamp = long.TryParse(timestampRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts) && ts > 0
                ? ts
                : null;

            entries.Add(new SteamLoginUserEntry(
                AccountId: accountId,
                SteamId64: steamId64,
                AccountName: string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim(),
                PersonaName: string.IsNullOrWhiteSpace(personaName) ? null : personaName.Trim(),
                MostRecent: mostRecent,
                Timestamp: timestamp));
        }

        entries.Sort(static (left, right) =>
        {
            int recentCmp = right.MostRecent.CompareTo(left.MostRecent);
            if (recentCmp != 0)
            {
                return recentCmp;
            }

            int tsCmp = (right.Timestamp ?? 0L).CompareTo(left.Timestamp ?? 0L);
            if (tsCmp != 0)
            {
                return tsCmp;
            }

            return left.AccountId.CompareTo(right.AccountId);
        });

        return entries;
    }

    private enum TokenKind
    {
        String,
        OpenBrace,
        CloseBrace,
    }

    private readonly record struct VdfToken(TokenKind Kind, string Value);

    private static List<VdfToken> Tokenize(string text)
    {
        var tokens = new List<VdfToken>(64);
        int i = 0;
        int len = text.Length;

        if (len > 0 && text[0] == '\uFEFF')
        {
            i = 1;
        }

        var sb = new StringBuilder();
        while (i < len)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Line comment: //...
            if (c == '/' && i + 1 < len && text[i + 1] == '/')
            {
                i += 2;
                while (i < len && text[i] != '\n' && text[i] != '\r')
                {
                    i++;
                }

                continue;
            }

            if (c == '{')
            {
                tokens.Add(new VdfToken(TokenKind.OpenBrace, "{"));
                i++;
                continue;
            }

            if (c == '}')
            {
                tokens.Add(new VdfToken(TokenKind.CloseBrace, "}"));
                i++;
                continue;
            }

            if (c == '"')
            {
                i++;
                sb.Clear();
                while (i < len)
                {
                    char ch = text[i++];
                    if (ch == '\\' && i < len)
                    {
                        char next = text[i++];
                        sb.Append(next switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '"' => '"',
                            '\\' => '\\',
                            _ => next,
                        });
                        continue;
                    }

                    if (ch == '"')
                    {
                        break;
                    }

                    sb.Append(ch);
                }

                tokens.Add(new VdfToken(TokenKind.String, sb.ToString()));
                continue;
            }

            // Unquoted token
            int start = i;
            while (i < len && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
            {
                if (text[i] == '/' && i + 1 < len && text[i + 1] == '/')
                {
                    break;
                }

                i++;
            }

            if (i > start)
            {
                tokens.Add(new VdfToken(TokenKind.String, text[start..i]));
            }
        }

        return tokens;
    }

    private static Dictionary<string, object> ParseBlock(
        List<VdfToken> tokens,
        ref int pos,
        bool expectClosingBrace,
        int depth)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (depth > 32)
        {
            return map;
        }

        while (pos < tokens.Count)
        {
            VdfToken current = tokens[pos];
            if (current.Kind == TokenKind.CloseBrace)
            {
                pos++;
                if (expectClosingBrace)
                {
                    return map;
                }

                continue;
            }

            if (current.Kind == TokenKind.OpenBrace)
            {
                pos++;
                _ = ParseBlock(tokens, ref pos, expectClosingBrace: true, depth + 1);
                continue;
            }

            string key = current.Value;
            pos++;
            if (pos >= tokens.Count)
            {
                break;
            }

            VdfToken next = tokens[pos];
            if (next.Kind == TokenKind.OpenBrace)
            {
                pos++;
                map[key] = ParseBlock(tokens, ref pos, expectClosingBrace: true, depth + 1);
            }
            else if (next.Kind == TokenKind.String)
            {
                pos++;
                map[key] = next.Value;
            }
            else if (next.Kind == TokenKind.CloseBrace)
            {
                pos++;
                if (expectClosingBrace)
                {
                    return map;
                }
            }
        }

        return map;
    }

    private static string? TryGetString(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out object? val) && val is string s ? s : null;
}
