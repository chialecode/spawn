using System.Globalization;
using System.Text;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Saves.Rules;

namespace Spawn.Modules.Progress.Saves.Cloud;

/// <summary>
/// Resolves stable cross-machine <c>CloudGameKey</c> identifiers so two PCs with different local random ULID
/// <c>GameId</c> values map the same game to the same remote SPPK cloud slot.
/// Priority:
/// <list type="number">
/// <item><description>Explicit user-configured slot key (<c>save_game_state.cloud_game_key</c>)</description></item>
/// <item><description><c>steam:{SteamAppId}</c></description></item>
/// <item><description><c>gog:{GogGameId}</c></description></item>
/// <item><description><c>slug:{normalized-rule-or-title}</c> (stable across machines)</description></item>
/// </list>
/// </summary>
public static class CloudGameKeyResolver
{
    public static async Task<string> ResolveAsync(
        SpawnDatabase database,
        string gameId,
        SaveGameStateRecord? state = null,
        GameRecord? game = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        state ??= await database.GetSaveGameStateAsync(gameId, ct).ConfigureAwait(false);
        string? explicitKey = NormalizeUserKey(state?.CloudGameKey);
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey;
        }

        game ??= await database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return gameId;
        }

        if (game.SteamAppId is > 0)
        {
            return $"steam:{game.SteamAppId.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (!string.IsNullOrWhiteSpace(game.GogGameId))
        {
            return $"gog:{game.GogGameId.Trim()}";
        }

        // Try matching a canonical Ludusavi rule name so bilingual/localized titles map identically across devices.
        string? matchedRuleName = await TryMatchCanonicalRuleNameAsync(database, game, ct).ConfigureAwait(false);
        string? slugSource = !string.IsNullOrWhiteSpace(matchedRuleName)
            ? matchedRuleName
            : SelectBestTitleForSlug(game);

        string slug = BuildStableSlug(slugSource);
        return !string.IsNullOrWhiteSpace(slug)
            ? $"slug:{slug}"
            : gameId;
    }

    public static string ResolveSync(
        GameRecord? game,
        SaveGameStateRecord? state = null,
        string? matchedRuleName = null,
        string? fallbackGameId = null)
    {
        string? explicitKey = NormalizeUserKey(state?.CloudGameKey);
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey;
        }

        if (game is null)
        {
            return fallbackGameId ?? string.Empty;
        }

        if (game.SteamAppId is > 0)
        {
            return $"steam:{game.SteamAppId.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (!string.IsNullOrWhiteSpace(game.GogGameId))
        {
            return $"gog:{game.GogGameId.Trim()}";
        }

        string? slugSource = !string.IsNullOrWhiteSpace(matchedRuleName)
            ? matchedRuleName
            : SelectBestTitleForSlug(game);

        string slug = BuildStableSlug(slugSource);
        return !string.IsNullOrWhiteSpace(slug)
            ? $"slug:{slug}"
            : (fallbackGameId ?? game.GameId);
    }

    /// <summary>
    /// Validates and trims a user-supplied custom cloud slot key. Returns <see langword="null"/> when empty (clears override).
    /// </summary>
    public static string? NormalizeUserKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        string trimmed = rawKey.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains('/')
            || trimmed.Contains('\\'))
        {
            throw new ArgumentException("云端槽位标识不能包含路径分隔符（/、\\）或上级目录符号（..）。", nameof(rawKey));
        }

        if (trimmed.Length > 120)
        {
            trimmed = trimmed[..120].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Converts a canonical logical <c>CloudGameKey</c> (e.g., <c>steam:1245620</c>) into a cross-filesystem-safe
    /// remote directory segment (e.g., <c>steam_1245620</c>) safe for NTFS, WebDAV, and S3.
    /// </summary>
    public static string ToRemotePathSegment(string cloudGameKey) =>
        CloudSyncTransportHelpers.SanitizePathSegment(cloudGameKey);

    private static async Task<string?> TryMatchCanonicalRuleNameAsync(
        SpawnDatabase database,
        GameRecord game,
        CancellationToken ct)
    {
        foreach (string candidate in EnumerateTitleCandidates(game))
        {
            LudusaviRuleRecord? rule = await database
                .FindLudusaviRuleAsync(title: candidate, cancellationToken: ct)
                .ConfigureAwait(false);
            if (rule is not null && !string.IsNullOrWhiteSpace(rule.RuleName))
            {
                return rule.RuleName;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTitleCandidates(GameRecord game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static IEnumerable<string> Expand(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }

            yield return raw.Trim();
            string cleaned = LudusaviRuleResolver.CleanTitleForRuleLookup(raw);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                yield return cleaned;
            }

            string[] parts = raw.Split(['/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                foreach (string part in parts)
                {
                    yield return part;
                    string cleanedPart = LudusaviRuleResolver.CleanTitleForRuleLookup(part);
                    if (!string.IsNullOrWhiteSpace(cleanedPart))
                    {
                        yield return cleanedPart;
                    }
                }
            }
        }

        foreach (string c in Expand(game.Title).Concat(Expand(game.SortTitle)))
        {
            if (seen.Add(c))
            {
                yield return c;
            }
        }
    }

    private static string SelectBestTitleForSlug(GameRecord game)
    {
        // Prefer an ASCII/English part from bilingual titles like "艾尔登法环 / Elden Ring" so cross-locale libraries align.
        string[] sources = [game.Title, game.SortTitle ?? string.Empty];
        foreach (string src in sources)
        {
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            string[] parts = src.Split(['/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string part in parts)
            {
                string cleaned = LudusaviRuleResolver.CleanTitleForRuleLookup(part);
                if (CountAsciiAlnum(cleaned) >= 3)
                {
                    return cleaned;
                }
            }
        }

        string fallback = LudusaviRuleResolver.CleanTitleForRuleLookup(game.Title);
        return string.IsNullOrWhiteSpace(fallback) ? game.Title : fallback;
    }

    internal static string BuildStableSlug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = LudusaviRuleResolver.CleanTitleForRuleLookup(text);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = text.Trim();
        }

        var ascii = new StringBuilder(cleaned.Length);
        bool prevDash = false;
        foreach (char c in cleaned)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                ascii.Append(char.ToLowerInvariant(c));
                prevDash = false;
            }
            else if (c is ' ' or '-' or '_' or '.' or ':' or '\'')
            {
                if (c != '\'' && ascii.Length > 0 && !prevDash)
                {
                    ascii.Append('-');
                    prevDash = true;
                }
            }
        }

        string asciiSlug = ascii.ToString().Trim('-');
        if (asciiSlug.Length >= 2)
        {
            return asciiSlug.Length > 64 ? asciiSlug[..64].TrimEnd('-') : asciiSlug;
        }

        // Non-Latin-only titles (e.g., pure CJK titles without Steam/GOG IDs): deterministic hash slug.
        string normalizedUnicode = string.Concat(
            cleaned.Normalize(NormalizationForm.FormKC)
                .Where(static c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)))
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedUnicode))
        {
            return string.Empty;
        }

        string hashHex = CasChunkStore.ComputeBlake3Hex(Encoding.UTF8.GetBytes(normalizedUnicode));
        return $"u-{hashHex[..12]}";
    }

    private static int CountAsciiAlnum(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                count++;
            }
        }

        return count;
    }
}
