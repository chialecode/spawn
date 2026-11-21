using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Asynchronous SQLite WAL repository (<c>spawn.db</c>) managing schema migrations, games, play sessions,
/// achievement schemas/definitions/unlocks, and application settings.
/// </summary>
public sealed partial class SpawnDatabase : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly SpawnPaths _paths;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _initialized;
    private int _disposed;

    public string DatabaseFilePath => _paths.DatabaseFilePath;

    public SpawnDatabase(SpawnPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_paths.DbDirectory);
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ApplyMigrationsCoreAsync(conn, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Games CRUD
    // -------------------------------------------------------------------------

    public async Task UpsertGameAsync(GameRecord game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO games (
                    game_id, title, sort_title, install_dir, executable_rel_path,
                    platform_source, anti_cheat_tier, anti_cheat_signature,
                    steam_app_id, gog_game_id, gog_client_id,
                    achievement_capture_mode, achievement_channel_label,
                    cover_image_path, icon_path, hero_image_path, launch_arguments,
                    total_active_sec, total_background_sec, total_afk_sec,
                    last_played_utc, created_utc,
                    steam_build_id, steam_last_updated,
                    verified_offline_bypass, verified_offline_bypass_at_utc
                ) VALUES (
                    $game_id, $title, $sort_title, $install_dir, $executable_rel_path,
                    $platform_source, $anti_cheat_tier, $anti_cheat_signature,
                    $steam_app_id, $gog_game_id, $gog_client_id,
                    $achievement_capture_mode, $achievement_channel_label,
                    $cover_image_path, $icon_path, $hero_image_path, $launch_arguments,
                    $total_active_sec, $total_background_sec, $total_afk_sec,
                    $last_played_utc, $created_utc,
                    $steam_build_id, $steam_last_updated,
                    $verified_offline_bypass, $verified_offline_bypass_at_utc
                )
                ON CONFLICT(game_id) DO UPDATE SET
                    title = excluded.title,
                    sort_title = excluded.sort_title,
                    install_dir = excluded.install_dir,
                    executable_rel_path = excluded.executable_rel_path,
                    platform_source = excluded.platform_source,
                    anti_cheat_tier = excluded.anti_cheat_tier,
                    anti_cheat_signature = excluded.anti_cheat_signature,
                    steam_app_id = excluded.steam_app_id,
                    gog_game_id = excluded.gog_game_id,
                    gog_client_id = excluded.gog_client_id,
                    achievement_capture_mode = excluded.achievement_capture_mode,
                    achievement_channel_label = excluded.achievement_channel_label,
                    cover_image_path = excluded.cover_image_path,
                    icon_path = excluded.icon_path,
                    hero_image_path = excluded.hero_image_path,
                    launch_arguments = excluded.launch_arguments,
                    total_active_sec = excluded.total_active_sec,
                    total_background_sec = excluded.total_background_sec,
                    total_afk_sec = excluded.total_afk_sec,
                    last_played_utc = excluded.last_played_utc,
                    steam_build_id = COALESCE(excluded.steam_build_id, games.steam_build_id),
                    steam_last_updated = COALESCE(excluded.steam_last_updated, games.steam_last_updated);
                """;

            string normalizedInstallDir = NormalizeDirectoryPath(game.InstallDir);
            string sortTitle = string.IsNullOrWhiteSpace(game.SortTitle) ? game.Title : game.SortTitle;

            cmd.Parameters.AddWithValue("$game_id", game.GameId);
            cmd.Parameters.AddWithValue("$title", game.Title);
            cmd.Parameters.AddWithValue("$sort_title", sortTitle);
            cmd.Parameters.AddWithValue("$install_dir", normalizedInstallDir);
            cmd.Parameters.AddWithValue("$executable_rel_path", game.ExecutableRelPath);
            cmd.Parameters.AddWithValue("$platform_source", game.PlatformSource.ToString());
            cmd.Parameters.AddWithValue("$anti_cheat_tier", (int)game.AntiCheatTier);
            cmd.Parameters.AddWithValue("$anti_cheat_signature", (object?)game.AntiCheatSignature ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$steam_app_id", game.SteamAppId.HasValue ? game.SteamAppId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$gog_game_id", (object?)game.GogGameId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gog_client_id", (object?)game.GogClientId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$achievement_capture_mode", game.AchievementCaptureMode.ToString());
            cmd.Parameters.AddWithValue("$achievement_channel_label", (object?)game.AchievementChannelLabel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cover_image_path", (object?)game.CoverImagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$icon_path", (object?)game.IconPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hero_image_path", (object?)game.HeroImagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$launch_arguments", (object?)game.LaunchArguments ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$total_active_sec", game.TotalActiveSec);
            cmd.Parameters.AddWithValue("$total_background_sec", game.TotalBackgroundSec);
            cmd.Parameters.AddWithValue("$total_afk_sec", game.TotalAfkSec);
            cmd.Parameters.AddWithValue("$last_played_utc", FormatNullableUtc(game.LastPlayedUtc));
            cmd.Parameters.AddWithValue("$created_utc", FormatUtc(game.CreatedUtc));
            cmd.Parameters.AddWithValue("$steam_build_id", (object?)game.SteamBuildId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$steam_last_updated", FormatNullableUtc(game.SteamLastUpdatedUtc));
            cmd.Parameters.AddWithValue("$verified_offline_bypass", game.VerifiedOfflineBypass ? 1 : 0);
            cmd.Parameters.AddWithValue("$verified_offline_bypass_at_utc", FormatNullableUtc(game.VerifiedOfflineBypassAtUtc));

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM games WHERE game_id = $game_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$game_id", gameId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapGameRecord(reader)
            : null;
    }

    public async Task<GameRecord?> GetGameByInstallDirAsync(string installDir, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalized = NormalizeDirectoryPath(installDir);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM games WHERE install_dir = $install_dir COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$install_dir", normalized);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapGameRecord(reader)
            : null;
    }

    public async Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM games ORDER BY sort_title COLLATE NOCASE ASC, title COLLATE NOCASE ASC;";

        var results = new List<GameRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapGameRecord(reader));
        }

        return results;
    }

    public async Task UpdateGamePlaytimeAsync(
        string gameId,
        long addActiveSec,
        long addBackgroundSec,
        long addAfkSec,
        DateTimeOffset lastPlayedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE games
                SET total_active_sec = total_active_sec + $active,
                    total_background_sec = total_background_sec + $bg,
                    total_afk_sec = total_afk_sec + $afk,
                    last_played_utc = $last_played
                WHERE game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$active", Math.Max(0, addActiveSec));
            cmd.Parameters.AddWithValue("$bg", Math.Max(0, addBackgroundSec));
            cmd.Parameters.AddWithValue("$afk", Math.Max(0, addAfkSec));
            cmd.Parameters.AddWithValue("$last_played", FormatUtc(lastPlayedUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteGameAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM games WHERE game_id = $game_id;";
            cmd.Parameters.AddWithValue("$game_id", gameId);
            int affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // PlaySessions CRUD
    // -------------------------------------------------------------------------

    public async Task InsertPlaySessionAsync(PlaySessionRecord session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO play_sessions (
                    session_id, game_id, started_utc, ended_utc,
                    active_sec, background_sec, afk_sec, exit_code
                ) VALUES (
                    $session_id, $game_id, $started_utc, $ended_utc,
                    $active_sec, $background_sec, $afk_sec, $exit_code
                )
                ON CONFLICT(session_id) DO UPDATE SET
                    ended_utc = excluded.ended_utc,
                    active_sec = excluded.active_sec,
                    background_sec = excluded.background_sec,
                    afk_sec = excluded.afk_sec,
                    exit_code = excluded.exit_code;
                """;
            BindPlaySessionParameters(cmd, session);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task UpdatePlaySessionAsync(PlaySessionRecord session, CancellationToken cancellationToken = default) =>
        InsertPlaySessionAsync(session, cancellationToken);

    public async Task CompletePlaySessionAndAccumulateAsync(
        PlaySessionRecord completedSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedSession);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var sessionCmd = conn.CreateCommand())
            {
                sessionCmd.Transaction = tx;
                sessionCmd.CommandText = """
                    INSERT INTO play_sessions (
                        session_id, game_id, started_utc, ended_utc,
                        active_sec, background_sec, afk_sec, exit_code
                    ) VALUES (
                        $session_id, $game_id, $started_utc, $ended_utc,
                        $active_sec, $background_sec, $afk_sec, $exit_code
                    )
                    ON CONFLICT(session_id) DO UPDATE SET
                        ended_utc = excluded.ended_utc,
                        active_sec = excluded.active_sec,
                        background_sec = excluded.background_sec,
                        afk_sec = excluded.afk_sec,
                        exit_code = excluded.exit_code;
                    """;
                BindPlaySessionParameters(sessionCmd, completedSession);
                await sessionCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var gameCmd = conn.CreateCommand())
            {
                gameCmd.Transaction = tx;
                gameCmd.CommandText = """
                    UPDATE games
                    SET total_active_sec = total_active_sec + $active,
                        total_background_sec = total_background_sec + $bg,
                        total_afk_sec = total_afk_sec + $afk,
                        last_played_utc = $last_played
                    WHERE game_id = $game_id;
                    """;
                gameCmd.Parameters.AddWithValue("$game_id", completedSession.GameId);
                gameCmd.Parameters.AddWithValue("$active", Math.Max(0, completedSession.ActiveSec));
                gameCmd.Parameters.AddWithValue("$bg", Math.Max(0, completedSession.BackgroundSec));
                gameCmd.Parameters.AddWithValue("$afk", Math.Max(0, completedSession.AfkSec));
                gameCmd.Parameters.AddWithValue("$last_played", FormatUtc(completedSession.EndedUtc ?? DateTimeOffset.UtcNow));
                await gameCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<PlaySessionRecord>> GetPlaySessionsByGameAsync(
        string gameId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM play_sessions
            WHERE game_id = $game_id
            ORDER BY started_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var list = new List<PlaySessionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new PlaySessionRecord
            {
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                GameId = reader.GetString(reader.GetOrdinal("game_id")),
                StartedUtc = ParseUtc(reader.GetString(reader.GetOrdinal("started_utc"))),
                EndedUtc = ParseNullableUtc(reader, "ended_utc"),
                ActiveSec = reader.GetInt64(reader.GetOrdinal("active_sec")),
                BackgroundSec = reader.GetInt64(reader.GetOrdinal("background_sec")),
                AfkSec = reader.GetInt64(reader.GetOrdinal("afk_sec")),
                ExitCode = GetNullableInt32(reader, "exit_code"),
            });
        }

        return list;
    }

    // -------------------------------------------------------------------------
    // Achievement Schemas, Definitions & Unlocks CRUD
    // -------------------------------------------------------------------------

    public async Task UpsertAchievementSchemaAsync(
        string gameId,
        string schemaKey,
        AchievementSchemaSource source,
        string language,
        IReadOnlyList<AchievementDefinitionDto> definitions,
        bool? hasEnablerDeployed = null,
        string? originalApiDllSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKey);
        ArgumentNullException.ThrowIfNull(definitions);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var schemaCmd = conn.CreateCommand())
            {
                schemaCmd.Transaction = tx;
                schemaCmd.CommandText = """
                    INSERT INTO achievement_game_schemas (
                        game_id, schema_key, schema_source, language,
                        total_achievements, has_enabler_deployed, original_api_dll_sha256, last_synced_at_utc
                    ) VALUES (
                        $game_id, $schema_key, $schema_source, $language,
                        $total_achievements, COALESCE($has_enabler, 0), $orig_sha256, $last_synced
                    )
                    ON CONFLICT(game_id) DO UPDATE SET
                        schema_key = excluded.schema_key,
                        schema_source = excluded.schema_source,
                        language = excluded.language,
                        total_achievements = excluded.total_achievements,
                        has_enabler_deployed = COALESCE($has_enabler, achievement_game_schemas.has_enabler_deployed),
                        original_api_dll_sha256 = COALESCE($orig_sha256, achievement_game_schemas.original_api_dll_sha256),
                        last_synced_at_utc = excluded.last_synced_at_utc;
                    """;
                schemaCmd.Parameters.AddWithValue("$game_id", gameId);
                schemaCmd.Parameters.AddWithValue("$schema_key", schemaKey);
                schemaCmd.Parameters.AddWithValue("$schema_source", source.ToString());
                schemaCmd.Parameters.AddWithValue("$language", string.IsNullOrWhiteSpace(language) ? "schinese" : language);
                schemaCmd.Parameters.AddWithValue("$total_achievements", definitions.Count);
                schemaCmd.Parameters.AddWithValue("$has_enabler", hasEnablerDeployed.HasValue ? (hasEnablerDeployed.Value ? 1 : 0) : DBNull.Value);
                schemaCmd.Parameters.AddWithValue("$orig_sha256", (object?)originalApiDllSha256 ?? DBNull.Value);
                schemaCmd.Parameters.AddWithValue("$last_synced", FormatUtc(DateTimeOffset.UtcNow));
                await schemaCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var def in definitions)
            {
                await using var defCmd = conn.CreateCommand();
                defCmd.Transaction = tx;
                defCmd.CommandText = """
                    INSERT INTO achievement_definitions (
                        game_id, schema_key, api_name, display_name, description,
                        hidden_spoiler_description, is_hidden, global_unlock_percent,
                        unlocked_icon_path, locked_gray_icon_path, sort_order
                    ) VALUES (
                        $game_id, $schema_key, $api_name, $display_name, $description,
                        $hidden_spoiler, $is_hidden, $global_percent,
                        $icon_path, $icon_gray_path, $sort_order
                    )
                    ON CONFLICT(game_id, api_name) DO UPDATE SET
                        schema_key = excluded.schema_key,
                        display_name = excluded.display_name,
                        description = CASE
                            WHEN excluded.description <> '' THEN excluded.description
                            ELSE achievement_definitions.description
                        END,
                        hidden_spoiler_description = COALESCE(excluded.hidden_spoiler_description, achievement_definitions.hidden_spoiler_description),
                        is_hidden = excluded.is_hidden,
                        global_unlock_percent = COALESCE(excluded.global_unlock_percent, achievement_definitions.global_unlock_percent),
                        unlocked_icon_path = COALESCE(excluded.unlocked_icon_path, achievement_definitions.unlocked_icon_path),
                        locked_gray_icon_path = COALESCE(excluded.locked_gray_icon_path, achievement_definitions.locked_gray_icon_path),
                        sort_order = excluded.sort_order;
                    """;
                defCmd.Parameters.AddWithValue("$game_id", gameId);
                defCmd.Parameters.AddWithValue("$schema_key", schemaKey);
                defCmd.Parameters.AddWithValue("$api_name", def.ApiName);
                defCmd.Parameters.AddWithValue("$display_name", def.DisplayName);
                defCmd.Parameters.AddWithValue("$description", def.Description ?? string.Empty);
                defCmd.Parameters.AddWithValue("$hidden_spoiler", (object?)def.HiddenSpoilerDescription ?? DBNull.Value);
                defCmd.Parameters.AddWithValue("$is_hidden", def.IsHidden ? 1 : 0);
                defCmd.Parameters.AddWithValue("$global_percent", def.GlobalPercent.HasValue ? def.GlobalPercent.Value : DBNull.Value);
                defCmd.Parameters.AddWithValue("$icon_path", (object?)def.IconPath ?? DBNull.Value);
                defCmd.Parameters.AddWithValue("$icon_gray_path", (object?)def.IconGrayPath ?? DBNull.Value);
                defCmd.Parameters.AddWithValue("$sort_order", def.SortOrder);
                await defCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetEnablerStateAsync(
        string gameId,
        bool hasEnablerDeployed,
        string? originalApiDllSha256,
        string? backupDllPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE achievement_game_schemas
                    SET has_enabler_deployed = $deployed,
                        original_api_dll_sha256 = $sha256
                    WHERE game_id = $game_id;
                    """;
                cmd.Parameters.AddWithValue("$game_id", gameId);
                cmd.Parameters.AddWithValue("$deployed", hasEnablerDeployed ? 1 : 0);
                cmd.Parameters.AddWithValue("$sha256", (object?)originalApiDllSha256 ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (hasEnablerDeployed && !string.IsNullOrWhiteSpace(originalApiDllSha256))
            {
                await using var bkpCmd = conn.CreateCommand();
                bkpCmd.Transaction = tx;
                bkpCmd.CommandText = """
                    INSERT INTO achievement_enabler_backups (
                        game_id, backup_dll_path, original_sha256, applied_at_utc
                    ) VALUES (
                        $game_id, $backup_path, $sha256, $applied_at
                    )
                    ON CONFLICT(game_id) DO UPDATE SET
                        backup_dll_path = excluded.backup_dll_path,
                        original_sha256 = excluded.original_sha256,
                        applied_at_utc = excluded.applied_at_utc;
                    """;
                bkpCmd.Parameters.AddWithValue("$game_id", gameId);
                bkpCmd.Parameters.AddWithValue("$backup_path", backupDllPath ?? string.Empty);
                bkpCmd.Parameters.AddWithValue("$sha256", originalApiDllSha256);
                bkpCmd.Parameters.AddWithValue("$applied_at", FormatUtc(DateTimeOffset.UtcNow));
                await bkpCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!hasEnablerDeployed)
            {
                await using var delCmd = conn.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM achievement_enabler_backups WHERE game_id = $game_id;";
                delCmd.Parameters.AddWithValue("$game_id", gameId);
                await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(string BackupDllPath, string OriginalSha256)?> GetEnablerBackupAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT backup_dll_path, original_sha256 FROM achievement_enabler_backups WHERE game_id = $game_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$game_id", gameId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    public async Task<IReadOnlyList<AchievementDefinitionDto>> GetAchievementDefinitionsAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM achievement_definitions
            WHERE game_id = $game_id
            ORDER BY sort_order ASC, api_name COLLATE NOCASE ASC;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);

        var list = new List<AchievementDefinitionDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapAchievementDefinition(reader));
        }

        return list;
    }

    /// <summary>
    /// Upserts a batch of unlock states for a game and returns the subset of items that newly transitioned from locked to unlocked.
    /// </summary>
    public async Task<IReadOnlyList<AchievementUnlockDto>> UpsertAchievementUnlocksBatchAsync(
        string gameId,
        IReadOnlyList<AchievementUnlockDto> unlocks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(unlocks);
        if (unlocks.Count == 0)
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var newlyUnlocked = new List<AchievementUnlockDto>();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var existingMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            await using (var existingCmd = conn.CreateCommand())
            {
                existingCmd.Transaction = tx;
                existingCmd.CommandText = "SELECT api_name, is_unlocked FROM achievement_unlocks WHERE game_id = $game_id;";
                existingCmd.Parameters.AddWithValue("$game_id", gameId);
                await using var reader = await existingCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingMap[reader.GetString(0)] = reader.GetInt32(1) != 0;
                }
            }

            foreach (var unlock in unlocks)
            {
                bool wasUnlocked = existingMap.TryGetValue(unlock.ApiName, out bool prev) && prev;

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO achievement_unlocks (
                        game_id, api_name, is_unlocked, unlocked_at_utc,
                        current_progress, max_progress, adapter_source, raw_source_path, notified_popup
                    ) VALUES (
                        $game_id, $api_name, $is_unlocked, $unlocked_at_utc,
                        $current_progress, $max_progress, $adapter_source, $raw_source_path, $notified_popup
                    )
                    ON CONFLICT(game_id, api_name) DO UPDATE SET
                        is_unlocked = excluded.is_unlocked,
                        unlocked_at_utc = CASE
                            WHEN excluded.is_unlocked = 0 THEN NULL
                            ELSE COALESCE(achievement_unlocks.unlocked_at_utc, excluded.unlocked_at_utc)
                        END,
                        current_progress = COALESCE(excluded.current_progress, achievement_unlocks.current_progress),
                        max_progress = COALESCE(excluded.max_progress, achievement_unlocks.max_progress),
                        adapter_source = excluded.adapter_source,
                        raw_source_path = COALESCE(excluded.raw_source_path, achievement_unlocks.raw_source_path),
                        notified_popup = CASE
                            WHEN excluded.is_unlocked = 0 THEN 0
                            WHEN achievement_unlocks.notified_popup = 1 THEN 1
                            ELSE excluded.notified_popup
                        END;
                    """;

                DateTimeOffset? effectiveTime = unlock.IsUnlocked
                    ? (unlock.UnlockedUtc ?? DateTimeOffset.UtcNow)
                    : null;

                cmd.Parameters.AddWithValue("$game_id", gameId);
                cmd.Parameters.AddWithValue("$api_name", unlock.ApiName);
                cmd.Parameters.AddWithValue("$is_unlocked", unlock.IsUnlocked ? 1 : 0);
                cmd.Parameters.AddWithValue("$unlocked_at_utc", FormatNullableUtc(effectiveTime));
                cmd.Parameters.AddWithValue("$current_progress", unlock.CurrentProgress.HasValue ? unlock.CurrentProgress.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$max_progress", unlock.MaxProgress.HasValue ? unlock.MaxProgress.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$adapter_source", unlock.SourceProvider ?? string.Empty);
                cmd.Parameters.AddWithValue("$raw_source_path", (object?)unlock.RawSourcePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$notified_popup", unlock.NotifiedPopup ? 1 : 0);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                existingMap[unlock.ApiName] = unlock.IsUnlocked;
                if (!wasUnlocked && unlock.IsUnlocked)
                {
                    newlyUnlocked.Add(unlock with
                    {
                        GameId = gameId,
                        UnlockedUtc = effectiveTime,
                    });
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return newlyUnlocked;
    }

    public async Task<bool> UpsertAchievementUnlockAsync(
        AchievementUnlockDto unlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unlock);
        var newly = await UpsertAchievementUnlocksBatchAsync(unlock.GameId, [unlock], cancellationToken).ConfigureAwait(false);
        return newly.Count > 0;
    }

    public async Task MarkAchievementPopupNotifiedAsync(
        string gameId,
        string apiName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE achievement_unlocks
                SET notified_popup = 1
                WHERE game_id = $game_id AND api_name = $api_name COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$api_name", apiName);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AchievementUnlockDto>> GetAchievementUnlocksAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM achievement_unlocks WHERE game_id = $game_id ORDER BY api_name COLLATE NOCASE ASC;";
        cmd.Parameters.AddWithValue("$game_id", gameId);

        var list = new List<AchievementUnlockDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapAchievementUnlock(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<AchievementUnlockDto>> GetRecentUnlocksAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM achievement_unlocks
            WHERE is_unlocked = 1
            ORDER BY unlocked_at_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var list = new List<AchievementUnlockDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapAchievementUnlock(reader));
        }

        return list;
    }

    public async Task<GameAchievementSummaryDto> GetGameAchievementSummaryAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var game = await GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        var definitions = await GetAchievementDefinitionsAsync(gameId, cancellationToken).ConfigureAwait(false);
        var unlocks = await GetAchievementUnlocksAsync(gameId, cancellationToken).ConfigureAwait(false);

        string schemaKey = string.Empty;
        var schemaSource = AchievementSchemaSource.None;
        bool hasEnabler = false;
        DateTimeOffset? lastSyncedUtc = null;

        await using (var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM achievement_game_schemas WHERE game_id = $game_id LIMIT 1;";
            cmd.Parameters.AddWithValue("$game_id", gameId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schemaKey = reader.GetString(reader.GetOrdinal("schema_key"));
                schemaSource = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("schema_source")), AchievementSchemaSource.None);
                hasEnabler = reader.GetInt32(reader.GetOrdinal("has_enabler_deployed")) != 0;
                lastSyncedUtc = ParseNullableUtc(reader, "last_synced_at_utc");
            }
        }

        var unlockByApi = unlocks.ToDictionary(u => u.ApiName, StringComparer.OrdinalIgnoreCase);
        var items = new List<GameAchievementItemDto>(Math.Max(definitions.Count, unlockByApi.Count));

        foreach (var def in definitions)
        {
            unlockByApi.TryGetValue(def.ApiName, out var unlock);
            items.Add(new GameAchievementItemDto
            {
                Definition = def,
                Unlock = unlock,
            });
        }

        int nextSort = items.Count;
        foreach (var orphan in unlocks)
        {
            if (definitions.Any(d => string.Equals(d.ApiName, orphan.ApiName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            items.Add(new GameAchievementItemDto
            {
                Definition = new AchievementDefinitionDto
                {
                    ApiName = orphan.ApiName,
                    DisplayName = orphan.ApiName,
                    Description = string.Empty,
                    SortOrder = nextSort++,
                },
                Unlock = orphan,
            });
        }

        int unlockedCount = items.Count(i => i.IsUnlocked);
        var captureMode = game?.AchievementCaptureMode ?? AchievementCaptureMode.None;
        string channelText = !string.IsNullOrWhiteSpace(game?.AchievementChannelLabel)
            ? game!.AchievementChannelLabel!
            : FormatDefaultChannelLabel(captureMode);

        return new GameAchievementSummaryDto
        {
            GameId = gameId,
            SchemaKey = schemaKey,
            SchemaSource = schemaSource,
            CaptureMode = captureMode,
            ChannelStatusText = channelText,
            HasGbeForkActivated = hasEnabler || captureMode == AchievementCaptureMode.GbeForkActivated,
            CanActivateGbeFork = game is { AntiCheatTier: AntiCheatTier.Tier0 }
                                 && captureMode is AchievementCaptureMode.PendingGbeForkActivation or AchievementCaptureMode.SteamEmulator or AchievementCaptureMode.None,
            UnlockedCount = unlockedCount,
            TotalCount = items.Count,
            LastSyncedUtc = lastSyncedUtc,
            Items = items,
        };
    }

    // -------------------------------------------------------------------------
    // AppSettings CRUD
    // -------------------------------------------------------------------------

    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM app_settings WHERE setting_key = $key;";
                cmd.Parameters.AddWithValue("$key", key);
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO app_settings (setting_key, setting_value, updated_at_utc)
                    VALUES ($key, $val, $updated)
                    ON CONFLICT(setting_key) DO UPDATE SET
                        setting_value = excluded.setting_value,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$val", value);
                cmd.Parameters.AddWithValue("$updated", FormatUtc(DateTimeOffset.UtcNow));
            }

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setting_value FROM app_settings WHERE setting_key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is string s ? s : null;
    }

    public async Task<T> GetSettingJsonAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default)
    {
        string? json = await GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return defaultValue;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public Task SetSettingJsonAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        return SetSettingAsync(key, json, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setting_key, setting_value FROM app_settings;";

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            dict[reader.GetString(0)] = reader.GetString(1);
        }

        return dict;
    }

    public async Task<OverlaySettingsDto> GetOverlaySettingsAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllSettingsAsync(cancellationToken).ConfigureAwait(false);

        var theme = all.TryGetValue(AppSettingKeys.OverlayTheme, out string? rawTheme)
            ? ParseEnumOrDefault(rawTheme, OverlayThemeKind.Steam)
            : OverlayThemeKind.Steam;

        var pos = all.TryGetValue(AppSettingKeys.OverlayPosition, out string? rawPos)
            ? ParseEnumOrDefault(rawPos, OverlayPositionKind.BottomRight)
            : OverlayPositionKind.BottomRight;

        var soundPreset = all.TryGetValue(AppSettingKeys.OverlaySoundPreset, out string? rawPreset)
            ? ParseEnumOrDefault(rawPreset, OverlaySoundPreset.SteamAuthentic)
            : OverlaySoundPreset.SteamAuthentic;

        string? customSoundPath = all.TryGetValue(AppSettingKeys.OverlayCustomSoundPath, out string? rawCustomPath)
                                  && !string.IsNullOrWhiteSpace(rawCustomPath)
            ? rawCustomPath.Trim()
            : null;

        string? audioOutputDeviceId = all.TryGetValue(AppSettingKeys.OverlayAudioDeviceId, out string? rawAudioDev)
                                      && !string.IsNullOrWhiteSpace(rawAudioDev)
            ? rawAudioDev.Trim()
            : null;

        double vol = all.TryGetValue(AppSettingKeys.OverlaySoundVolume, out string? rawVol)
                     && double.TryParse(rawVol, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVol)
            ? Math.Clamp(parsedVol, 0.0, 1.0)
            : 1.0;

        bool mute = all.TryGetValue(AppSettingKeys.OverlayMuteSound, out string? rawMute)
                    && bool.TryParse(rawMute, out bool parsedMute) && parsedMute;

        return new OverlaySettingsDto
        {
            Theme = theme,
            Position = pos,
            SoundPreset = soundPreset,
            CustomSoundPath = customSoundPath,
            AudioOutputDeviceId = audioOutputDeviceId,
            SoundVolume = vol,
            MuteSound = mute,
        };
    }

    public async Task SaveOverlaySettingsAsync(OverlaySettingsDto settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await SetSettingAsync(AppSettingKeys.OverlayTheme, settings.Theme.ToString(), cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(AppSettingKeys.OverlayPosition, settings.Position.ToString(), cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(AppSettingKeys.OverlaySoundPreset, settings.SoundPreset.ToString(), cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(
            AppSettingKeys.OverlayCustomSoundPath,
            string.IsNullOrWhiteSpace(settings.CustomSoundPath) ? null : settings.CustomSoundPath.Trim(),
            cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(
            AppSettingKeys.OverlayAudioDeviceId,
            string.IsNullOrWhiteSpace(settings.AudioOutputDeviceId) ? null : settings.AudioOutputDeviceId.Trim(),
            cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(AppSettingKeys.OverlaySoundVolume, Math.Clamp(settings.SoundVolume, 0.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(AppSettingKeys.OverlayMuteSound, settings.MuteSound ? "true" : "false", cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Game Data Rules Cache & Game State Reset / Undo Restore
    // -------------------------------------------------------------------------

    public async Task UpsertGameDataRuleCacheAsync(
        GameDataRuleCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.CacheKey);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO game_data_rules (
                    cache_key, game_id, steam_app_id, gog_game_id, provider, rules_json, fetched_at_utc
                ) VALUES (
                    $cache_key, $game_id, $steam_app_id, $gog_game_id, $provider, $rules_json, $fetched_at_utc
                )
                ON CONFLICT(cache_key) DO UPDATE SET
                    game_id = COALESCE(excluded.game_id, game_data_rules.game_id),
                    steam_app_id = COALESCE(excluded.steam_app_id, game_data_rules.steam_app_id),
                    gog_game_id = COALESCE(excluded.gog_game_id, game_data_rules.gog_game_id),
                    provider = excluded.provider,
                    rules_json = excluded.rules_json,
                    fetched_at_utc = excluded.fetched_at_utc;
                """;
            cmd.Parameters.AddWithValue("$cache_key", record.CacheKey);
            cmd.Parameters.AddWithValue("$game_id", (object?)record.GameId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$steam_app_id", record.SteamAppId.HasValue ? record.SteamAppId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$gog_game_id", (object?)record.GogGameId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$provider", string.IsNullOrWhiteSpace(record.Provider) ? "PCGamingWiki" : record.Provider);
            cmd.Parameters.AddWithValue("$rules_json", record.RulesJson ?? "[]");
            cmd.Parameters.AddWithValue("$fetched_at_utc", FormatUtc(record.FetchedUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<GameDataRuleCacheRecord?> GetGameDataRuleCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data_rules WHERE cache_key = $cache_key COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$cache_key", cacheKey);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapGameDataRuleRecord(reader)
            : null;
    }

    public async Task<GameDataRuleCacheRecord?> GetGameDataRuleCacheByGameAsync(
        string gameId,
        int? steamAppId = null,
        string? gogGameId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM game_data_rules
            WHERE ($game_id IS NOT NULL AND game_id = $game_id)
               OR ($steam_app_id IS NOT NULL AND steam_app_id = $steam_app_id)
               OR ($gog_game_id IS NOT NULL AND gog_game_id = $gog_game_id)
            ORDER BY fetched_at_utc DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$game_id", string.IsNullOrWhiteSpace(gameId) ? DBNull.Value : gameId);
        cmd.Parameters.AddWithValue("$steam_app_id", steamAppId.HasValue ? steamAppId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$gog_game_id", string.IsNullOrWhiteSpace(gogGameId) ? DBNull.Value : gogGameId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapGameDataRuleRecord(reader)
            : null;
    }

    /// <summary>
    /// Clears all persisted achievement unlock rows for <paramref name="gameId"/> while keeping schema definitions intact.
    /// </summary>
    public async Task ResetGameAchievementsOnlyAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM achievement_unlocks WHERE game_id = $game_id;";
            cmd.Parameters.AddWithValue("$game_id", gameId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically resets a game's SPAWN achievement unlocks, play session ledger, and cumulative tri-state playtime counters to zero.
    /// </summary>
    public async Task ResetGameAchievementsAndPlaytimeAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var delUnlocks = conn.CreateCommand())
            {
                delUnlocks.Transaction = tx;
                delUnlocks.CommandText = "DELETE FROM achievement_unlocks WHERE game_id = $game_id;";
                delUnlocks.Parameters.AddWithValue("$game_id", gameId);
                await delUnlocks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var delSessions = conn.CreateCommand())
            {
                delSessions.Transaction = tx;
                delSessions.CommandText = "DELETE FROM play_sessions WHERE game_id = $game_id;";
                delSessions.Parameters.AddWithValue("$game_id", gameId);
                await delSessions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var resetGame = conn.CreateCommand())
            {
                resetGame.Transaction = tx;
                resetGame.CommandText = """
                    UPDATE games
                    SET total_active_sec = 0,
                        total_background_sec = 0,
                        total_afk_sec = 0,
                        last_played_utc = NULL
                    WHERE game_id = $game_id;
                    """;
                resetGame.Parameters.AddWithValue("$game_id", gameId);
                await resetGame.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically restores a previously backed-up snapshot of game playtime counters, play sessions, and achievement unlocks.
    /// </summary>
    public async Task RestoreGamePlaytimeAndUnlocksSnapshotAsync(
        string gameId,
        long totalActiveSec,
        long totalBackgroundSec,
        long totalAfkSec,
        DateTimeOffset? lastPlayedUtc,
        IReadOnlyList<PlaySessionRecord>? playSessions,
        IReadOnlyList<AchievementUnlockDto>? unlocks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var gameCmd = conn.CreateCommand())
            {
                gameCmd.Transaction = tx;
                gameCmd.CommandText = """
                    UPDATE games
                    SET total_active_sec = $active,
                        total_background_sec = $bg,
                        total_afk_sec = $afk,
                        last_played_utc = $last_played
                    WHERE game_id = $game_id;
                    """;
                gameCmd.Parameters.AddWithValue("$game_id", gameId);
                gameCmd.Parameters.AddWithValue("$active", Math.Max(0, totalActiveSec));
                gameCmd.Parameters.AddWithValue("$bg", Math.Max(0, totalBackgroundSec));
                gameCmd.Parameters.AddWithValue("$afk", Math.Max(0, totalAfkSec));
                gameCmd.Parameters.AddWithValue("$last_played", FormatNullableUtc(lastPlayedUtc));
                await gameCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (playSessions is { Count: > 0 })
            {
                foreach (var session in playSessions)
                {
                    await using var sessionCmd = conn.CreateCommand();
                    sessionCmd.Transaction = tx;
                    sessionCmd.CommandText = """
                        INSERT INTO play_sessions (
                            session_id, game_id, started_utc, ended_utc,
                            active_sec, background_sec, afk_sec, exit_code
                        ) VALUES (
                            $session_id, $game_id, $started_utc, $ended_utc,
                            $active_sec, $background_sec, $afk_sec, $exit_code
                        )
                        ON CONFLICT(session_id) DO UPDATE SET
                            ended_utc = excluded.ended_utc,
                            active_sec = excluded.active_sec,
                            background_sec = excluded.background_sec,
                            afk_sec = excluded.afk_sec,
                            exit_code = excluded.exit_code;
                        """;
                    BindPlaySessionParameters(sessionCmd, session);
                    await sessionCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (unlocks is { Count: > 0 })
            {
                foreach (var unlock in unlocks)
                {
                    await using var unlockCmd = conn.CreateCommand();
                    unlockCmd.Transaction = tx;
                    unlockCmd.CommandText = """
                        INSERT INTO achievement_unlocks (
                            game_id, api_name, is_unlocked, unlocked_at_utc,
                            current_progress, max_progress, adapter_source, raw_source_path, notified_popup
                        ) VALUES (
                            $game_id, $api_name, $is_unlocked, $unlocked_at_utc,
                            $current_progress, $max_progress, $adapter_source, $raw_source_path, $notified_popup
                        )
                        ON CONFLICT(game_id, api_name) DO UPDATE SET
                            is_unlocked = excluded.is_unlocked,
                            unlocked_at_utc = excluded.unlocked_at_utc,
                            current_progress = excluded.current_progress,
                            max_progress = excluded.max_progress,
                            adapter_source = excluded.adapter_source,
                            raw_source_path = excluded.raw_source_path,
                            notified_popup = excluded.notified_popup;
                        """;
                    unlockCmd.Parameters.AddWithValue("$game_id", gameId);
                    unlockCmd.Parameters.AddWithValue("$api_name", unlock.ApiName);
                    unlockCmd.Parameters.AddWithValue("$is_unlocked", unlock.IsUnlocked ? 1 : 0);
                    unlockCmd.Parameters.AddWithValue("$unlocked_at_utc", FormatNullableUtc(unlock.UnlockedUtc));
                    unlockCmd.Parameters.AddWithValue("$current_progress", unlock.CurrentProgress.HasValue ? unlock.CurrentProgress.Value : DBNull.Value);
                    unlockCmd.Parameters.AddWithValue("$max_progress", unlock.MaxProgress.HasValue ? unlock.MaxProgress.Value : DBNull.Value);
                    unlockCmd.Parameters.AddWithValue("$adapter_source", unlock.SourceProvider ?? string.Empty);
                    unlockCmd.Parameters.AddWithValue("$raw_source_path", (object?)unlock.RawSourcePath ?? DBNull.Value);
                    unlockCmd.Parameters.AddWithValue("$notified_popup", unlock.NotifiedPopup ? 1 : 0);
                    await unlockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Schema Migrations & Internal Helpers
    // -------------------------------------------------------------------------

    private Task EnsureInitializedAsync(CancellationToken cancellationToken) =>
        _initialized ? Task.CompletedTask : InitializeAsync(cancellationToken);

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store = MEMORY;
            """;
        await pragmaCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    private static async Task ApplyMigrationsCoreAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version         INTEGER PRIMARY KEY,
                applied_at_utc  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS games (
                game_id                     TEXT PRIMARY KEY,
                title                       TEXT NOT NULL,
                sort_title                  TEXT NOT NULL,
                install_dir                 TEXT NOT NULL,
                executable_rel_path         TEXT NOT NULL,
                platform_source             TEXT NOT NULL DEFAULT 'Standalone',
                anti_cheat_tier             INTEGER NOT NULL DEFAULT 0,
                anti_cheat_signature        TEXT,
                steam_app_id                INTEGER,
                gog_game_id                 TEXT,
                gog_client_id               TEXT,
                achievement_capture_mode    TEXT NOT NULL DEFAULT 'None',
                achievement_channel_label   TEXT,
                cover_image_path            TEXT,
                icon_path                   TEXT,
                hero_image_path             TEXT,
                launch_arguments            TEXT,
                total_active_sec            INTEGER NOT NULL DEFAULT 0,
                total_background_sec        INTEGER NOT NULL DEFAULT 0,
                total_afk_sec               INTEGER NOT NULL DEFAULT 0,
                last_played_utc             TEXT,
                created_utc                 TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_games_install_dir ON games(install_dir COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_games_steam_app_id ON games(steam_app_id) WHERE steam_app_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_games_gog_game_id ON games(gog_game_id) WHERE gog_game_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS play_sessions (
                session_id      TEXT PRIMARY KEY,
                game_id         TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                started_utc     TEXT NOT NULL,
                ended_utc       TEXT,
                active_sec      INTEGER NOT NULL DEFAULT 0,
                background_sec  INTEGER NOT NULL DEFAULT 0,
                afk_sec         INTEGER NOT NULL DEFAULT 0,
                exit_code       INTEGER
            );

            CREATE INDEX IF NOT EXISTS ix_play_sessions_game_started ON play_sessions(game_id, started_utc DESC);

            CREATE TABLE IF NOT EXISTS achievement_game_schemas (
                game_id                 TEXT PRIMARY KEY REFERENCES games(game_id) ON DELETE CASCADE,
                schema_key              TEXT NOT NULL,
                schema_source           TEXT NOT NULL DEFAULT 'None',
                language                TEXT NOT NULL DEFAULT 'schinese',
                total_achievements      INTEGER NOT NULL DEFAULT 0,
                has_enabler_deployed    INTEGER NOT NULL DEFAULT 0,
                original_api_dll_sha256 TEXT,
                last_synced_at_utc      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS achievement_definitions (
                game_id                     TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                schema_key                  TEXT NOT NULL,
                api_name                    TEXT NOT NULL COLLATE NOCASE,
                display_name                TEXT NOT NULL,
                description                 TEXT NOT NULL DEFAULT '',
                hidden_spoiler_description  TEXT,
                is_hidden                   INTEGER NOT NULL DEFAULT 0,
                global_unlock_percent       REAL,
                unlocked_icon_path          TEXT,
                locked_gray_icon_path       TEXT,
                sort_order                  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (game_id, api_name)
            );

            CREATE TABLE IF NOT EXISTS achievement_unlocks (
                game_id             TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                api_name            TEXT NOT NULL COLLATE NOCASE,
                is_unlocked         INTEGER NOT NULL DEFAULT 0,
                unlocked_at_utc     TEXT,
                current_progress    REAL,
                max_progress        REAL,
                adapter_source      TEXT NOT NULL DEFAULT '',
                raw_source_path     TEXT,
                notified_popup      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (game_id, api_name)
            );

            CREATE INDEX IF NOT EXISTS ix_achievement_unlocks_recent
                ON achievement_unlocks(is_unlocked, unlocked_at_utc DESC);

            CREATE TABLE IF NOT EXISTS achievement_enabler_backups (
                game_id             TEXT PRIMARY KEY REFERENCES games(game_id) ON DELETE CASCADE,
                backup_dll_path     TEXT NOT NULL,
                original_sha256     TEXT NOT NULL,
                applied_at_utc      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                setting_key     TEXT PRIMARY KEY COLLATE NOCASE,
                setting_value   TEXT NOT NULL,
                updated_at_utc  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS game_data_rules (
                cache_key       TEXT PRIMARY KEY COLLATE NOCASE,
                game_id         TEXT,
                steam_app_id    INTEGER,
                gog_game_id     TEXT,
                provider        TEXT NOT NULL DEFAULT 'PCGamingWiki',
                rules_json      TEXT NOT NULL,
                fetched_at_utc  TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_game_data_rules_game_id ON game_data_rules(game_id) WHERE game_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_game_data_rules_steam_id ON game_data_rules(steam_app_id) WHERE steam_app_id IS NOT NULL;

            INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
            VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

            INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

            -- v3 (one-shot): the overlay volume default moved from 75% to 100% (Steam original playback level).
            -- Only the untouched legacy default is migrated; any later user choice (including 75%) is kept.
            UPDATE app_settings
               SET setting_value = '1', updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
             WHERE setting_key = 'overlay.sound_volume'
               AND setting_value = '0.75'
               AND NOT EXISTS (SELECT 1 FROM schema_migrations WHERE version = 3);

            INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
            VALUES (3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ApplyV4TrainerMigrationsAsync(conn, cancellationToken).ConfigureAwait(false);
    }

    private static GameDataRuleCacheRecord MapGameDataRuleRecord(SqliteDataReader reader) =>
        new()
        {
            CacheKey = reader.GetString(reader.GetOrdinal("cache_key")),
            GameId = GetNullableString(reader, "game_id"),
            SteamAppId = GetNullableInt32(reader, "steam_app_id"),
            GogGameId = GetNullableString(reader, "gog_game_id"),
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            RulesJson = reader.GetString(reader.GetOrdinal("rules_json")),
            FetchedUtc = ParseUtc(reader.GetString(reader.GetOrdinal("fetched_at_utc"))),
        };

    private static void BindPlaySessionParameters(SqliteCommand cmd, PlaySessionRecord session)
    {
        cmd.Parameters.AddWithValue("$session_id", session.SessionId);
        cmd.Parameters.AddWithValue("$game_id", session.GameId);
        cmd.Parameters.AddWithValue("$started_utc", FormatUtc(session.StartedUtc));
        cmd.Parameters.AddWithValue("$ended_utc", FormatNullableUtc(session.EndedUtc));
        cmd.Parameters.AddWithValue("$active_sec", session.ActiveSec);
        cmd.Parameters.AddWithValue("$background_sec", session.BackgroundSec);
        cmd.Parameters.AddWithValue("$afk_sec", session.AfkSec);
        cmd.Parameters.AddWithValue("$exit_code", session.ExitCode.HasValue ? session.ExitCode.Value : DBNull.Value);
    }

    private static GameRecord MapGameRecord(SqliteDataReader reader) =>
        new()
        {
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            SortTitle = reader.GetString(reader.GetOrdinal("sort_title")),
            InstallDir = reader.GetString(reader.GetOrdinal("install_dir")),
            ExecutableRelPath = reader.GetString(reader.GetOrdinal("executable_rel_path")),
            PlatformSource = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("platform_source")), PlatformSource.Standalone),
            AntiCheatTier = (AntiCheatTier)Math.Clamp(reader.GetInt32(reader.GetOrdinal("anti_cheat_tier")), 0, 3),
            AntiCheatSignature = GetNullableString(reader, "anti_cheat_signature"),
            SteamAppId = GetNullableInt32(reader, "steam_app_id"),
            GogGameId = GetNullableString(reader, "gog_game_id"),
            GogClientId = GetNullableString(reader, "gog_client_id"),
            AchievementCaptureMode = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("achievement_capture_mode")), AchievementCaptureMode.None),
            AchievementChannelLabel = GetNullableString(reader, "achievement_channel_label"),
            CoverImagePath = GetNullableString(reader, "cover_image_path"),
            IconPath = GetNullableString(reader, "icon_path"),
            HeroImagePath = GetNullableString(reader, "hero_image_path"),
            LaunchArguments = GetNullableString(reader, "launch_arguments"),
            TotalActiveSec = reader.GetInt64(reader.GetOrdinal("total_active_sec")),
            TotalBackgroundSec = reader.GetInt64(reader.GetOrdinal("total_background_sec")),
            TotalAfkSec = reader.GetInt64(reader.GetOrdinal("total_afk_sec")),
            LastPlayedUtc = ParseNullableUtc(reader, "last_played_utc"),
            CreatedUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_utc"))),
            SteamBuildId = GetNullableString(reader, "steam_build_id"),
            SteamLastUpdatedUtc = ParseNullableUtc(reader, "steam_last_updated"),
            VerifiedOfflineBypass = GetNullableInt32(reader, "verified_offline_bypass") is > 0,
            VerifiedOfflineBypassAtUtc = ParseNullableUtc(reader, "verified_offline_bypass_at_utc"),
        };

    private static AchievementDefinitionDto MapAchievementDefinition(SqliteDataReader reader) =>
        new()
        {
            ApiName = reader.GetString(reader.GetOrdinal("api_name")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            Description = reader.GetString(reader.GetOrdinal("description")),
            HiddenSpoilerDescription = GetNullableString(reader, "hidden_spoiler_description"),
            IsHidden = reader.GetInt32(reader.GetOrdinal("is_hidden")) != 0,
            GlobalPercent = GetNullableDouble(reader, "global_unlock_percent"),
            IconPath = GetNullableString(reader, "unlocked_icon_path"),
            IconGrayPath = GetNullableString(reader, "locked_gray_icon_path"),
            SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
        };

    private static AchievementUnlockDto MapAchievementUnlock(SqliteDataReader reader) =>
        new()
        {
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            ApiName = reader.GetString(reader.GetOrdinal("api_name")),
            IsUnlocked = reader.GetInt32(reader.GetOrdinal("is_unlocked")) != 0,
            UnlockedUtc = ParseNullableUtc(reader, "unlocked_at_utc"),
            CurrentProgress = GetNullableDouble(reader, "current_progress"),
            MaxProgress = GetNullableDouble(reader, "max_progress"),
            SourceProvider = reader.GetString(reader.GetOrdinal("adapter_source")),
            RawSourcePath = GetNullableString(reader, "raw_source_path"),
            NotifiedPopup = reader.GetInt32(reader.GetOrdinal("notified_popup")) != 0,
        };

    private static string FormatDefaultChannelLabel(AchievementCaptureMode mode) => mode switch
    {
        AchievementCaptureMode.GogNativeSave => "GOG 原生存档监听 · 就绪",
        AchievementCaptureMode.GogCometLocal => "GOG Comet 9977 · 就绪",
        AchievementCaptureMode.SteamEmulator => "离线 Steam 模拟器监听 · 就绪",
        AchievementCaptureMode.SteamLocalCache => "Steam 本地缓存监听 · 就绪",
        AchievementCaptureMode.GbeForkActivated => "Goldberg GSE (已无损激活) · 就绪",
        AchievementCaptureMode.PendingGbeForkActivation => "待激活离线成就补丁",
        _ => "未检测到成就通道",
    };

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string FormatUtc(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullableUtc(DateTimeOffset? dto) =>
        dto.HasValue ? FormatUtc(dto.Value) : DBNull.Value;

    private static DateTimeOffset ParseUtc(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUniversalTime()
            : DateTimeOffset.UtcNow;

    private static DateTimeOffset? ParseNullableUtc(SqliteDataReader reader, string columnName)
    {
        int ord = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ord) ? null : ParseUtc(reader.GetString(ord));
    }

    private static string? GetNullableString(SqliteDataReader reader, string columnName)
    {
        int ord = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    private static int? GetNullableInt32(SqliteDataReader reader, string columnName)
    {
        int ord = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord);
    }

    private static double? GetNullableDouble(SqliteDataReader reader, string columnName)
    {
        int ord = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ord) ? null : reader.GetDouble(ord);
    }

    private static TEnum ParseEnumOrDefault<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            using var sample = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(sample);
        }
        catch
        {
            // Ignore pool cleanup race on shutdown.
        }

        _writeLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
