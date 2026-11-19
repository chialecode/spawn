using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Captures achievements for GOG Offline Installer and GOG Galaxy titles across three zero-intrusion modes:
/// 1) Local <c>127.0.0.1:9977</c> Comet Protobuf daemon (<see cref="GogCometLocalDaemon"/>),
/// 2) Read-only SQLite WAL monitoring of official GOG Galaxy <c>gameplay.db</c> / <c>galaxy-2.0.db</c>,
/// 3) <c>NemirtingasGalaxyEmu</c> offline JSON saves.
/// </summary>
public sealed class GogAchievementProvider(GogCometLocalDaemon cometDaemon) : IAchievementSourceProvider
{
    public const string Id = "gog.galaxy_and_comet";

    public string ProviderId => Id;
    public int Priority => 20;

    public IReadOnlyList<AchievementWatchTarget> DiscoverTargets(AchievementSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var targets = new List<AchievementWatchTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string installDir = context.Game.InstallDir;
        var (detectedGameId, detectedClientId) = TryParseGogGameInfo(installDir);
        string? gogGameId = !string.IsNullOrWhiteSpace(context.EffectiveGogGameId) ? context.EffectiveGogGameId : detectedGameId;
        string? gogClientId = !string.IsNullOrWhiteSpace(context.EffectiveGogClientId) ? context.EffectiveGogClientId : detectedClientId;

        bool hasGalaxyDll = HasGalaxySdkDll(installDir);

        // 1. NemirtingasGalaxyEmu JSON saves (%APPDATA%/NemirtingasGalaxyEmu)
        string appData = context.GetAppDataRoaming();
        if (!string.IsNullOrWhiteSpace(appData))
        {
            string nemRoot = Path.Combine(appData, "NemirtingasGalaxyEmu");
            if (Directory.Exists(nemRoot))
            {
                foreach (string file in SteamEmulatorFormatParsers.EnumerateFilesSafe(nemRoot, "*achiev*.json", maxDepth: 4))
                {
                    if ((gogGameId is not null && file.Contains(gogGameId, StringComparison.OrdinalIgnoreCase))
                        || (gogClientId is not null && file.Contains(gogClientId, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (seen.Add(file))
                        {
                            targets.Add(new AchievementWatchTarget
                            {
                                ProviderId = ProviderId,
                                Flavor = EmulatorFlavor.NemirtingasGalaxyEmu,
                                CaptureMode = AchievementCaptureMode.SteamEmulator,
                                ChannelLabel = "Nemirtingas GalaxyEmu · 就绪",
                                FilePath = Path.GetFullPath(file),
                                IsPrimary = targets.Count == 0,
                            });
                        }
                    }
                }
            }
        }

        // 2. Local 127.0.0.1:9977 Comet daemon for GOG Offline games shipping Galaxy.dll / Galaxy64.dll
        if (hasGalaxyDll && (!string.IsNullOrWhiteSpace(gogGameId) || !string.IsNullOrWhiteSpace(gogClientId)))
        {
            cometDaemon.RegisterGameSession(gogGameId, gogClientId);
            string bucket = gogGameId ?? gogClientId!;
            string stateFile = cometDaemon.GetPersistedStateFilePath(bucket);

            if (!cometDaemon.IsYieldedToOfficialGalaxy && seen.Add(stateFile))
            {
                targets.Add(new AchievementWatchTarget
                {
                    ProviderId = ProviderId,
                    Flavor = EmulatorFlavor.GogCometLocal,
                    CaptureMode = AchievementCaptureMode.GogCometLocal,
                    ChannelLabel = "GOG Comet 9977 · 就绪",
                    FilePath = stateFile,
                    IsPrimary = targets.Count == 0,
                });
            }
        }

        // 3. Official GOG Galaxy SQLite WAL read-only databases (primary when Galaxy is installed or port 9977 yielded)
        string programData = context.GetProgramData();
        if (!string.IsNullOrWhiteSpace(programData) && (!string.IsNullOrWhiteSpace(gogGameId) || !string.IsNullOrWhiteSpace(gogClientId)))
        {
            string[] pdCandidates =
            [
                Path.Combine(programData, "GOG.com", "Galaxy", "storage", "gameplay.db"),
                Path.Combine(programData, "GOG.com", "Galaxy", "storage", "galaxy-2.0.db"),
            ];

            foreach (string dbPath in pdCandidates)
            {
                if (File.Exists(dbPath) && seen.Add(dbPath))
                {
                    targets.Add(new AchievementWatchTarget
                    {
                        ProviderId = ProviderId,
                        Flavor = EmulatorFlavor.GogGalaxyDb,
                        CaptureMode = AchievementCaptureMode.GogCometLocal,
                        ChannelLabel = "GOG Galaxy 本地库 (只读) · 就绪",
                        FilePath = Path.GetFullPath(dbPath),
                        IsPrimary = targets.Count == 0,
                    });
                }
            }
        }

        string localAppData = context.GetLocalAppData();
        if (!string.IsNullOrWhiteSpace(localAppData) && !string.IsNullOrWhiteSpace(gogClientId))
        {
            string clientGameplayRoot = Path.Combine(localAppData, "GOG.com", "Galaxy", "Applications", gogClientId, "Storage", "Shared", "Files", "gameplay");
            if (Directory.Exists(clientGameplayRoot))
            {
                foreach (string dbFile in SteamEmulatorFormatParsers.EnumerateFilesSafe(clientGameplayRoot, "gameplay.db", maxDepth: 3))
                {
                    if (seen.Add(dbFile))
                    {
                        targets.Add(new AchievementWatchTarget
                        {
                            ProviderId = ProviderId,
                            Flavor = EmulatorFlavor.GogGalaxyDb,
                            CaptureMode = AchievementCaptureMode.GogCometLocal,
                            ChannelLabel = "GOG Galaxy gameplay.db · 就绪",
                            FilePath = Path.GetFullPath(dbFile),
                            IsPrimary = targets.Count == 0,
                        });
                    }
                }
            }
        }

        return targets;
    }

    public async ValueTask<IReadOnlyList<ParsedAchievementEntry>> ReadUnlocksAsync(
        AchievementWatchTarget target,
        AchievementSourceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(target.FilePath))
        {
            return [];
        }

        if (target.Flavor == EmulatorFlavor.GogGalaxyDb)
        {
            return await ReadGogGalaxySqliteAsync(
                target.FilePath,
                context.EffectiveGogGameId,
                context.EffectiveGogClientId,
                cancellationToken).ConfigureAwait(false);
        }

        byte[]? raw = await ResilientFileReader.ReadSharedBytesWithRetryAsync(target.FilePath, cancellationToken).ConfigureAwait(false);
        if (raw is null || raw.Length == 0)
        {
            return [];
        }

        return target.Flavor == EmulatorFlavor.NemirtingasGalaxyEmu
            ? SteamEmulatorFormatParsers.ParseNemirtingasGalaxyJson(raw)
            : SteamEmulatorFormatParsers.ParseGoldbergOrGbeForkJson(raw);
    }

    public static bool HasGalaxySdkDll(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return false;
        }

        return SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "Galaxy64.dll", maxDepth: 4).Any()
               || SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "Galaxy.dll", maxDepth: 4).Any()
               || SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "GalaxyCSharpGlue.dll", maxDepth: 4).Any();
    }

    public static (string? GameId, string? ClientId) TryParseGogGameInfo(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return (null, null);
        }

        foreach (string infoFile in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "goggame-*.info", maxDepth: 2))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(infoFile));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? gid = doc.RootElement.TryGetProperty("gameId", out var gp) ? gp.ToString() : null;
                string? cid = doc.RootElement.TryGetProperty("clientId", out var cp) ? cp.ToString() : null;
                if (!string.IsNullOrWhiteSpace(gid) || !string.IsNullOrWhiteSpace(cid))
                {
                    return (gid, cid);
                }
            }
            catch
            {
                // Ignore malformed info file.
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Reads unlocked achievements from GOG Galaxy <c>gameplay.db</c> or <c>galaxy-2.0.db</c> strictly in read-only mode.
    /// </summary>
    public static async Task<IReadOnlyList<ParsedAchievementEntry>> ReadGogGalaxySqliteAsync(
        string dbPath,
        string? gogGameId,
        string? gogClientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            return [];
        }

        var results = new List<ParsedAchievementEntry>();
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
                DefaultTimeout = 2,
            };

            await using var conn = new SqliteConnection(csb.ToString());
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var tblCmd = conn.CreateCommand())
            {
                tblCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                await using var tr = await tblCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await tr.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    tables.Add(tr.GetString(0));
                }
            }

            // Schema 1: Per-application gameplay.db (`achievements` table with `key` / `achievement_key` & `unlock_time`)
            if (tables.Contains("achievements"))
            {
                var cols = await GetTableColumnsAsync(conn, "achievements", cancellationToken).ConfigureAwait(false);
                string? keyCol = PickFirstColumn(cols, "key", "achievement_key", "api_key", "id");
                string? timeCol = PickFirstColumn(cols, "unlock_time", "date_unlocked", "unlocked_at");
                string? unlockedCol = PickFirstColumn(cols, "unlocked", "is_unlocked");

                if (keyCol is not null)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM achievements;";
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    int keyOrd = reader.GetOrdinal(keyCol);
                    int timeOrd = timeCol is not null ? reader.GetOrdinal(timeCol) : -1;
                    int unlOrd = unlockedCol is not null ? reader.GetOrdinal(unlockedCol) : -1;

                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(keyOrd))
                        {
                            continue;
                        }

                        string apiKey = reader.GetString(keyOrd).Trim();
                        DateTimeOffset? unlockDto = null;
                        if (timeOrd >= 0 && !reader.IsDBNull(timeOrd))
                        {
                            unlockDto = ParseGogSqliteTimestamp(reader.GetValue(timeOrd));
                        }

                        bool isUnlocked = unlockDto.HasValue
                                          || (unlOrd >= 0 && !reader.IsDBNull(unlOrd) && Convert.ToInt64(reader.GetValue(unlOrd), CultureInfo.InvariantCulture) != 0);

                        results.Add(new ParsedAchievementEntry
                        {
                            ApiName = apiKey,
                            IsUnlocked = isUnlocked,
                            UnlockedUtc = unlockDto,
                        });
                    }
                }
            }

            // Schema 2: Global galaxy-2.0.db (`UserReleaseAchievements` / `GamePiece` fallback)
            if (results.Count == 0 && tables.Contains("UserReleaseAchievements") && !string.IsNullOrWhiteSpace(gogGameId))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT achievementKey, dateUnlocked
                    FROM UserReleaseAchievements
                    WHERE releaseKey LIKE $relPrefix;
                    """;
                cmd.Parameters.AddWithValue("$relPrefix", $"gog_{gogGameId}%");
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (reader.IsDBNull(0))
                    {
                        continue;
                    }

                    string key = reader.GetString(0);
                    DateTimeOffset? dt = !reader.IsDBNull(1) ? ParseGogSqliteTimestamp(reader.GetValue(1)) : null;
                    results.Add(new ParsedAchievementEntry
                    {
                        ApiName = key,
                        IsUnlocked = dt.HasValue,
                        UnlockedUtc = dt,
                    });
                }
            }
        }
        catch
        {
            // Database may be temporarily locked or follow an unrecognized layout; never fail caller.
        }

        return results;
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        SqliteConnection conn,
        string tableName,
        CancellationToken ct)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            cols.Add(reader.GetString(1));
        }

        return cols;
    }

    private static string? PickFirstColumn(HashSet<string> cols, params ReadOnlySpan<string> candidates)
    {
        foreach (string c in candidates)
        {
            if (cols.Contains(c))
            {
                return c;
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseGogSqliteTimestamp(object rawVal)
    {
        switch (rawVal)
        {
            case long l when l > 0:
                return SteamEmulatorFormatParsers.UnixTimestampToUtc(l);
            case int i when i > 0:
                return SteamEmulatorFormatParsers.UnixTimestampToUtc(i);
            case string s when !string.IsNullOrWhiteSpace(s):
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix) && unix > 0)
                {
                    return SteamEmulatorFormatParsers.UnixTimestampToUtc(unix);
                }

                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                {
                    return dto.ToUniversalTime();
                }

                return null;
            default:
                return null;
        }
    }
}
