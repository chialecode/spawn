using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;

namespace Spawn.Core;

public sealed partial class SpawnDatabase
{
    private const string CacheKeyPrefix = "cache:";

    private static readonly (string ColumnName, string ColumnDefinition)[] GamesV4Columns =
    [
        ("steam_build_id", "TEXT"),
        ("steam_last_updated", "TEXT"),
        ("verified_offline_bypass", "INTEGER NOT NULL DEFAULT 0"),
        ("verified_offline_bypass_at_utc", "TEXT"),
    ];

    // -------------------------------------------------------------------------
    // Schema v3 -> v4 Idempotent Migration
    // -------------------------------------------------------------------------

    private static async Task ApplyV4TrainerMigrationsAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existingGameColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.Transaction = tx;
            pragmaCmd.CommandText = "PRAGMA table_info(games);";
            await using var reader = await pragmaCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            int nameOrd = reader.GetOrdinal("name");
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingGameColumns.Add(reader.GetString(nameOrd));
            }
        }

        foreach (var (colName, colDef) in GamesV4Columns)
        {
            if (existingGameColumns.Contains(colName))
            {
                continue;
            }

            await using var alterCmd = conn.CreateCommand();
            alterCmd.Transaction = tx;
            alterCmd.CommandText = $"ALTER TABLE games ADD COLUMN {colName} {colDef};";
            await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var ddlCmd = conn.CreateCommand())
        {
            ddlCmd.Transaction = tx;
            ddlCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS trainer_catalog (
                    trainer_id                  TEXT PRIMARY KEY,
                    game_id                     TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                    provider                    TEXT NOT NULL DEFAULT 'LocalImport',
                    display_name                TEXT,
                    version_label               TEXT,
                    source_page_url             TEXT,
                    source_updated_at           TEXT,
                    sandbox_relative_path       TEXT NOT NULL,
                    file_sha256                 TEXT NOT NULL,
                    file_size                   INTEGER,
                    pe_machine                  TEXT,
                    requires_admin              INTEGER NOT NULL DEFAULT 0,
                    authenticode_status         TEXT,
                    parse_mode                  TEXT NOT NULL DEFAULT 'FreeHosted',
                    bound_build_id              TEXT,
                    bound_exe_size              INTEGER,
                    bound_exe_last_write_utc    TEXT,
                    imported_at_utc             TEXT NOT NULL,
                    last_launched_at_utc        TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_trainer_catalog_game_id
                    ON trainer_catalog(game_id);

                CREATE TABLE IF NOT EXISTS trainer_title_map (
                    game_id                 TEXT PRIMARY KEY REFERENCES games(game_id) ON DELETE CASCADE,
                    english_title           TEXT NOT NULL,
                    title_source            TEXT NOT NULL DEFAULT 'Cleaner',
                    user_search_override    TEXT,
                    fling_page_url          TEXT,
                    updated_at_utc          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS trainer_hotkey_bindings (
                    trainer_id      TEXT NOT NULL REFERENCES trainer_catalog(trainer_id) ON DELETE CASCADE,
                    entry_id        TEXT NOT NULL,
                    label           TEXT NOT NULL,
                    tooltip         TEXT,
                    original_chord  TEXT NOT NULL,
                    mapped_chord    TEXT,
                    entry_kind      TEXT NOT NULL DEFAULT 'Toggle',
                    proxy_state     INTEGER NOT NULL DEFAULT 0,
                    sort_order      INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (trainer_id, entry_id)
                );

                CREATE INDEX IF NOT EXISTS ix_trainer_hotkey_bindings_sort
                    ON trainer_hotkey_bindings(trainer_id, sort_order ASC);

                CREATE TABLE IF NOT EXISTS pre_trainer_snapshots (
                    snapshot_id             TEXT PRIMARY KEY,
                    game_id                 TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                    trainer_id              TEXT REFERENCES trainer_catalog(trainer_id) ON DELETE SET NULL,
                    trigger_kind            TEXT NOT NULL DEFAULT 'PreTrainer',
                    status                  TEXT NOT NULL DEFAULT 'Completed',
                    skip_reason             TEXT,
                    file_count              INTEGER NOT NULL DEFAULT 0,
                    total_bytes             INTEGER NOT NULL DEFAULT 0,
                    manifest_relative_path  TEXT,
                    created_at_utc          TEXT NOT NULL,
                    restored_at_utc         TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_pre_trainer_snapshots_game_created
                    ON pre_trainer_snapshots(game_id, created_at_utc DESC);

                INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
                VALUES (4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await ddlCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is long v ? (int)v : Convert.ToInt32(scalar ?? 0, CultureInfo.InvariantCulture);
    }

    // -------------------------------------------------------------------------
    // Games M3 Extensions (VerifiedOfflineBypass & Steam Build Fingerprint)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Updates a game's <c>verified_offline_bypass</c> state without altering its static <c>anti_cheat_tier</c>.
    /// </summary>
    public async Task<bool> SetGameVerifiedOfflineBypassAsync(
        string gameId,
        bool enabled,
        DateTimeOffset? confirmedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset? effectiveTimestamp = enabled ? (confirmedAtUtc ?? DateTimeOffset.UtcNow) : null;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE games
                SET verified_offline_bypass = $enabled,
                    verified_offline_bypass_at_utc = $confirmed_at_utc
                WHERE game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$confirmed_at_utc", FormatNullableUtc(effectiveTimestamp));
            int affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(bool Enabled, DateTimeOffset? ConfirmedAtUtc, AntiCheatTier StaticTier)?> GetGameVerifiedOfflineBypassAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT verified_offline_bypass, verified_offline_bypass_at_utc, anti_cheat_tier
            FROM games
            WHERE game_id = $game_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        bool enabled = !reader.IsDBNull(0) && reader.GetInt32(0) != 0;
        DateTimeOffset? confirmedAt = ParseNullableUtc(reader, "verified_offline_bypass_at_utc");
        var tier = (AntiCheatTier)Math.Clamp(reader.GetInt32(2), 0, 3);
        return (enabled, confirmedAt, tier);
    }

    public async Task<bool> UpdateGameSteamBuildFingerprintAsync(
        string gameId,
        string? steamBuildId,
        DateTimeOffset? steamLastUpdatedUtc,
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
                SET steam_build_id = $build_id,
                    steam_last_updated = $last_updated
                WHERE game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$build_id", (object?)steamBuildId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$last_updated", FormatNullableUtc(steamLastUpdatedUtc));
            int affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // trainer_catalog CRUD
    // -------------------------------------------------------------------------

    public async Task UpsertTrainerAsync(
        TrainerCatalogRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.TrainerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SandboxRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.FileSha256);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO trainer_catalog (
                    trainer_id, game_id, provider, display_name, version_label,
                    source_page_url, source_updated_at, sandbox_relative_path,
                    file_sha256, file_size, pe_machine, requires_admin,
                    authenticode_status, parse_mode, bound_build_id,
                    bound_exe_size, bound_exe_last_write_utc,
                    imported_at_utc, last_launched_at_utc
                ) VALUES (
                    $trainer_id, $game_id, $provider, $display_name, $version_label,
                    $source_page_url, $source_updated_at, $sandbox_relative_path,
                    $file_sha256, $file_size, $pe_machine, $requires_admin,
                    $authenticode_status, $parse_mode, $bound_build_id,
                    $bound_exe_size, $bound_exe_last_write_utc,
                    $imported_at_utc, $last_launched_at_utc
                )
                ON CONFLICT(trainer_id) DO UPDATE SET
                    game_id = excluded.game_id,
                    provider = excluded.provider,
                    display_name = excluded.display_name,
                    version_label = excluded.version_label,
                    source_page_url = excluded.source_page_url,
                    source_updated_at = excluded.source_updated_at,
                    sandbox_relative_path = excluded.sandbox_relative_path,
                    file_sha256 = excluded.file_sha256,
                    file_size = excluded.file_size,
                    pe_machine = excluded.pe_machine,
                    requires_admin = excluded.requires_admin,
                    authenticode_status = excluded.authenticode_status,
                    parse_mode = excluded.parse_mode,
                    bound_build_id = excluded.bound_build_id,
                    bound_exe_size = excluded.bound_exe_size,
                    bound_exe_last_write_utc = excluded.bound_exe_last_write_utc,
                    imported_at_utc = excluded.imported_at_utc,
                    last_launched_at_utc = COALESCE(excluded.last_launched_at_utc, trainer_catalog.last_launched_at_utc);
                """;
            cmd.Parameters.AddWithValue("$trainer_id", record.TrainerId);
            cmd.Parameters.AddWithValue("$game_id", record.GameId);
            cmd.Parameters.AddWithValue("$provider", record.Provider.ToString());
            cmd.Parameters.AddWithValue("$display_name", (object?)record.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$version_label", (object?)record.VersionLabel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_page_url", (object?)record.SourcePageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_updated_at", (object?)record.SourceUpdatedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sandbox_relative_path", record.SandboxRelativePath);
            cmd.Parameters.AddWithValue("$file_sha256", record.FileSha256);
            cmd.Parameters.AddWithValue("$file_size", record.FileSize.HasValue ? record.FileSize.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$pe_machine", (object?)record.PeMachine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$requires_admin", record.RequiresAdmin ? 1 : 0);
            cmd.Parameters.AddWithValue("$authenticode_status", record.AuthenticodeStatus.ToString());
            cmd.Parameters.AddWithValue("$parse_mode", record.ParseMode.ToString());
            cmd.Parameters.AddWithValue("$bound_build_id", (object?)record.BoundBuildId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bound_exe_size", record.BoundExeSize.HasValue ? record.BoundExeSize.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$bound_exe_last_write_utc", FormatNullableUtc(record.BoundExeLastWriteUtc));
            cmd.Parameters.AddWithValue("$imported_at_utc", FormatUtc(record.ImportedAtUtc));
            cmd.Parameters.AddWithValue("$last_launched_at_utc", FormatNullableUtc(record.LastLaunchedAtUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TrainerCatalogRecord?> GetTrainerByIdAsync(
        string trainerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trainerId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM trainer_catalog WHERE trainer_id = $trainer_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$trainer_id", trainerId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapTrainerCatalogRecord(reader)
            : null;
    }

    public async Task<IReadOnlyList<TrainerCatalogRecord>> GetTrainersByGameIdAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM trainer_catalog
            WHERE game_id = $game_id
            ORDER BY imported_at_utc DESC, trainer_id ASC;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);

        var list = new List<TrainerCatalogRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapTrainerCatalogRecord(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<TrainerCatalogRecord>> GetAllTrainersAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM trainer_catalog ORDER BY imported_at_utc DESC, trainer_id ASC;";

        var list = new List<TrainerCatalogRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapTrainerCatalogRecord(reader));
        }

        return list;
    }

    public async Task<bool> UpdateTrainerLastLaunchedAsync(
        string trainerId,
        DateTimeOffset launchedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE trainer_catalog
                SET last_launched_at_utc = $launched_at_utc
                WHERE trainer_id = $trainer_id;
                """;
            cmd.Parameters.AddWithValue("$trainer_id", trainerId);
            cmd.Parameters.AddWithValue("$launched_at_utc", FormatUtc(launchedAtUtc));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateTrainerSandboxRelativePathAsync(
        string trainerId,
        string newSandboxRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSandboxRelativePath);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE trainer_catalog
                SET sandbox_relative_path = $rel_path
                WHERE trainer_id = $trainer_id;
                """;
            cmd.Parameters.AddWithValue("$trainer_id", trainerId);
            cmd.Parameters.AddWithValue("$rel_path", newSandboxRelativePath);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteTrainerAsync(
        string trainerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trainerId))
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM trainer_catalog WHERE trainer_id = $trainer_id;";
            cmd.Parameters.AddWithValue("$trainer_id", trainerId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // trainer_title_map CRUD
    // -------------------------------------------------------------------------

    public async Task UpsertTrainerTitleMapAsync(
        TrainerTitleMapRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EnglishTitle);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO trainer_title_map (
                    game_id, english_title, title_source, user_search_override,
                    fling_page_url, updated_at_utc
                ) VALUES (
                    $game_id, $english_title, $title_source, $user_search_override,
                    $fling_page_url, $updated_at_utc
                )
                ON CONFLICT(game_id) DO UPDATE SET
                    english_title = excluded.english_title,
                    title_source = excluded.title_source,
                    user_search_override = excluded.user_search_override,
                    fling_page_url = excluded.fling_page_url,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$game_id", record.GameId);
            cmd.Parameters.AddWithValue("$english_title", record.EnglishTitle);
            cmd.Parameters.AddWithValue("$title_source", record.TitleSource.ToString());
            cmd.Parameters.AddWithValue("$user_search_override", (object?)record.UserSearchOverride ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fling_page_url", (object?)record.FlingPageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(record.UpdatedAtUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TrainerTitleMapRecord?> GetTrainerTitleMapAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM trainer_title_map WHERE game_id = $game_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$game_id", gameId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrainerTitleMapRecord
        {
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            EnglishTitle = reader.GetString(reader.GetOrdinal("english_title")),
            TitleSource = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("title_source")), TrainerTitleSource.Cleaner),
            UserSearchOverride = GetNullableString(reader, "user_search_override"),
            FlingPageUrl = GetNullableString(reader, "fling_page_url"),
            UpdatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
        };
    }

    public async Task<bool> UpdateTrainerUserSearchOverrideAsync(
        string gameId,
        string? userSearchOverride,
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
                UPDATE trainer_title_map
                SET user_search_override = $override,
                    updated_at_utc = $updated_at_utc
                WHERE game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue(
                "$override",
                string.IsNullOrWhiteSpace(userSearchOverride) ? DBNull.Value : userSearchOverride.Trim());
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(DateTimeOffset.UtcNow));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteTrainerTitleMapAsync(
        string gameId,
        CancellationToken cancellationToken = default)
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
            cmd.CommandText = "DELETE FROM trainer_title_map WHERE game_id = $game_id;";
            cmd.Parameters.AddWithValue("$game_id", gameId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // trainer_hotkey_bindings CRUD
    // -------------------------------------------------------------------------

    public async Task ReplaceTrainerHotkeyBindingsAsync(
        string trainerId,
        IReadOnlyList<TrainerHotkeyBindingRecord> bindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);
        ArgumentNullException.ThrowIfNull(bindings);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM trainer_hotkey_bindings WHERE trainer_id = $trainer_id;";
                delCmd.Parameters.AddWithValue("$trainer_id", trainerId);
                await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var binding in bindings)
            {
                await using var insCmd = conn.CreateCommand();
                insCmd.Transaction = tx;
                BindHotkeyUpsertCommand(insCmd, trainerId, binding);
                await insCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertTrainerHotkeyBindingAsync(
        TrainerHotkeyBindingRecord binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TrainerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.EntryId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            BindHotkeyUpsertCommand(cmd, binding.TrainerId, binding);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetTrainerHotkeyBindingsAsync(
        string trainerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trainerId))
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM trainer_hotkey_bindings
            WHERE trainer_id = $trainer_id
            ORDER BY sort_order ASC, entry_id COLLATE NOCASE ASC;
            """;
        cmd.Parameters.AddWithValue("$trainer_id", trainerId);

        var list = new List<TrainerHotkeyBindingRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new TrainerHotkeyBindingRecord
            {
                TrainerId = reader.GetString(reader.GetOrdinal("trainer_id")),
                EntryId = reader.GetString(reader.GetOrdinal("entry_id")),
                Label = reader.GetString(reader.GetOrdinal("label")),
                Tooltip = GetNullableString(reader, "tooltip"),
                OriginalChord = reader.GetString(reader.GetOrdinal("original_chord")),
                MappedChord = GetNullableString(reader, "mapped_chord"),
                EntryKind = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("entry_kind")), TrainerHotkeyEntryKind.Toggle),
                ProxyState = reader.GetInt32(reader.GetOrdinal("proxy_state")) != 0,
                SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
            });
        }

        return list;
    }

    public async Task<bool> UpdateTrainerHotkeyMappedChordAsync(
        string trainerId,
        string entryId,
        string? mappedChord,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE trainer_hotkey_bindings
                SET mapped_chord = $mapped_chord
                WHERE trainer_id = $trainer_id AND entry_id = $entry_id;
                """;
            cmd.Parameters.AddWithValue("$trainer_id", trainerId);
            cmd.Parameters.AddWithValue("$entry_id", entryId);
            cmd.Parameters.AddWithValue(
                "$mapped_chord",
                string.IsNullOrWhiteSpace(mappedChord) ? DBNull.Value : mappedChord.Trim());
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateTrainerHotkeyProxyStateAsync(
        string trainerId,
        string entryId,
        bool proxyState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE trainer_hotkey_bindings
                SET proxy_state = $proxy_state
                WHERE trainer_id = $trainer_id AND entry_id = $entry_id;
                """;
            cmd.Parameters.AddWithValue("$trainer_id", trainerId);
            cmd.Parameters.AddWithValue("$entry_id", entryId);
            cmd.Parameters.AddWithValue("$proxy_state", proxyState ? 1 : 0);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ResetTrainerHotkeyProxyStatesAsync(
        string trainerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE trainer_hotkey_bindings
                SET proxy_state = 0
                WHERE trainer_id = $trainer_id AND proxy_state <> 0;
                """;
            cmd.Parameters.AddWithValue("$trainer_id", trainerId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteTrainerHotkeyBindingAsync(
        string trainerId,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trainerId) || string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM trainer_hotkey_bindings
                WHERE trainer_id = $trainer_id AND entry_id = $entry_id;
                """;
            cmd.Parameters.AddWithValue("$trainer_id", trainerId);
            cmd.Parameters.AddWithValue("$entry_id", entryId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // pre_trainer_snapshots CRUD
    // -------------------------------------------------------------------------

    public async Task UpsertPreTrainerSnapshotAsync(
        PreTrainerSnapshotRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.GameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pre_trainer_snapshots (
                    snapshot_id, game_id, trainer_id, trigger_kind, status,
                    skip_reason, file_count, total_bytes, manifest_relative_path,
                    created_at_utc, restored_at_utc
                ) VALUES (
                    $snapshot_id, $game_id, $trainer_id, $trigger_kind, $status,
                    $skip_reason, $file_count, $total_bytes, $manifest_relative_path,
                    $created_at_utc, $restored_at_utc
                )
                ON CONFLICT(snapshot_id) DO UPDATE SET
                    trainer_id = excluded.trainer_id,
                    trigger_kind = excluded.trigger_kind,
                    status = excluded.status,
                    skip_reason = excluded.skip_reason,
                    file_count = excluded.file_count,
                    total_bytes = excluded.total_bytes,
                    manifest_relative_path = excluded.manifest_relative_path,
                    restored_at_utc = COALESCE(excluded.restored_at_utc, pre_trainer_snapshots.restored_at_utc);
                """;
            cmd.Parameters.AddWithValue("$snapshot_id", record.SnapshotId);
            cmd.Parameters.AddWithValue("$game_id", record.GameId);
            cmd.Parameters.AddWithValue("$trainer_id", (object?)record.TrainerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$trigger_kind", record.TriggerKind.ToString());
            cmd.Parameters.AddWithValue("$status", record.Status.ToString());
            cmd.Parameters.AddWithValue("$skip_reason", (object?)record.SkipReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$file_count", Math.Max(0, record.FileCount));
            cmd.Parameters.AddWithValue("$total_bytes", Math.Max(0, record.TotalBytes));
            cmd.Parameters.AddWithValue("$manifest_relative_path", (object?)record.ManifestRelativePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at_utc", FormatUtc(record.CreatedAtUtc));
            cmd.Parameters.AddWithValue("$restored_at_utc", FormatNullableUtc(record.RestoredAtUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<PreTrainerSnapshotRecord?> GetPreTrainerSnapshotByIdAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pre_trainer_snapshots WHERE snapshot_id = $snapshot_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapPreTrainerSnapshotRecord(reader)
            : null;
    }

    public async Task<IReadOnlyList<PreTrainerSnapshotRecord>> GetPreTrainerSnapshotsByGameAsync(
        string gameId,
        PreTrainerSnapshotTriggerKind? triggerKindFilter = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = triggerKindFilter.HasValue
            ? """
              SELECT * FROM pre_trainer_snapshots
              WHERE game_id = $game_id AND trigger_kind = $trigger_kind
              ORDER BY created_at_utc DESC, snapshot_id DESC
              LIMIT $limit;
              """
            : """
              SELECT * FROM pre_trainer_snapshots
              WHERE game_id = $game_id
              ORDER BY created_at_utc DESC, snapshot_id DESC
              LIMIT $limit;
              """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        if (triggerKindFilter.HasValue)
        {
            cmd.Parameters.AddWithValue("$trigger_kind", triggerKindFilter.Value.ToString());
        }

        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var list = new List<PreTrainerSnapshotRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapPreTrainerSnapshotRecord(reader));
        }

        return list;
    }

    public async Task<bool> MarkPreTrainerSnapshotRestoredAsync(
        string snapshotId,
        DateTimeOffset restoredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE pre_trainer_snapshots
                SET restored_at_utc = $restored_at_utc
                WHERE snapshot_id = $snapshot_id;
                """;
            cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
            cmd.Parameters.AddWithValue("$restored_at_utc", FormatUtc(restoredAtUtc));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeletePreTrainerSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM pre_trainer_snapshots WHERE snapshot_id = $snapshot_id;";
            cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // TrainerSettingsDto Persistence (app_settings)
    // -------------------------------------------------------------------------

    public async Task<TrainerSettingsDto> GetTrainerSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT setting_key, setting_value FROM app_settings WHERE setting_key LIKE 'trainer.%';";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                map[reader.GetString(0)] = reader.GetString(1);
            }
        }

        string? customSandboxRoot = map.TryGetValue(AppSettingKeys.TrainerCustomSandboxRoot, out string? rawRoot)
                                    && !string.IsNullOrWhiteSpace(rawRoot)
            ? rawRoot.Trim()
            : null;

        var audioMode = map.TryGetValue(AppSettingKeys.TrainerAudioMode, out string? rawAudio)
            ? ParseEnumOrDefault(rawAudio, TrainerAudioMode.FullMute)
            : TrainerAudioMode.FullMute;

        double cueVolume = map.TryGetValue(AppSettingKeys.TrainerCueVolume, out string? rawVol)
                           && double.TryParse(rawVol, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVol)
            ? Math.Clamp(parsedVol, 0.0, 1.0)
            : 0.35;

        bool enableA0 = !map.TryGetValue(AppSettingKeys.TrainerEnableA0PreMute, out string? rawA0)
                        || !bool.TryParse(rawA0, out bool parsedA0)
                        || parsedA0;

        int hotkeyHoldMs = map.TryGetValue(AppSettingKeys.TrainerHotkeyHoldMs, out string? rawHold)
                           && int.TryParse(rawHold, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedHold)
            ? Math.Clamp(parsedHold, TrainerSettingsDto.MinHotkeyHoldMs, TrainerSettingsDto.MaxHotkeyHoldMs)
            : TrainerSettingsDto.DefaultHotkeyHoldMs;

        bool createPreSnap = !map.TryGetValue(AppSettingKeys.TrainerPreSnapshotEnabled, out string? rawSnap)
                             || !bool.TryParse(rawSnap, out bool parsedSnap)
                             || parsedSnap;

        int retention = map.TryGetValue(AppSettingKeys.TrainerPreSnapshotRetentionCount, out string? rawRet)
                        && int.TryParse(rawRet, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRet)
            ? Math.Clamp(parsedRet, 1, 50)
            : TrainerSettingsDto.DefaultPreSnapshotRetentionCount;

        int maxSizeMb = map.TryGetValue(AppSettingKeys.TrainerPreSnapshotMaxSizeMb, out string? rawMaxMb)
                        && int.TryParse(rawMaxMb, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMaxMb)
            ? Math.Clamp(parsedMaxMb, 16, 4096)
            : TrainerSettingsDto.DefaultPreSnapshotMaxSizeMb;

        bool stopWithGame = !map.TryGetValue(AppSettingKeys.TrainerStopWithGameExit, out string? rawStop)
                            || !bool.TryParse(rawStop, out bool parsedStop)
                            || parsedStop;

        bool hideOriginal = map.TryGetValue(AppSettingKeys.TrainerHideOriginalWindowOnLaunch, out string? rawHide)
                            && bool.TryParse(rawHide, out bool parsedHide) && parsedHide;

        bool strictVanguard = map.TryGetValue(AppSettingKeys.TrainerStrictVanguardMode, out string? rawVg)
                              && bool.TryParse(rawVg, out bool parsedVg) && parsedVg;

        bool includeArchive = map.TryGetValue(AppSettingKeys.TrainerIncludeFlingArchive, out string? rawArch)
                              && bool.TryParse(rawArch, out bool parsedArch) && parsedArch;

        bool scanCodeMode = map.TryGetValue(AppSettingKeys.TrainerDefaultScanCodeMode, out string? rawScan)
                            && bool.TryParse(rawScan, out bool parsedScan) && parsedScan;

        return new TrainerSettingsDto
        {
            CustomSandboxRoot = customSandboxRoot,
            AudioMode = audioMode,
            CueVolume = cueVolume,
            EnableA0PreMute = enableA0,
            HotkeyHoldMs = hotkeyHoldMs,
            CreatePreTrainerSnapshot = createPreSnap,
            PreTrainerSnapshotRetentionCount = retention,
            PreTrainerSnapshotMaxSizeMb = maxSizeMb,
            StopWithGameExit = stopWithGame,
            HideOriginalWindowOnLaunch = hideOriginal,
            StrictVanguardMode = strictVanguard,
            IncludeFlingArchiveInSearch = includeArchive,
            DefaultScanCodeMode = scanCodeMode,
        };
    }

    public async Task SaveTrainerSettingsAsync(
        TrainerSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        int clampedHoldMs = Math.Clamp(settings.HotkeyHoldMs, TrainerSettingsDto.MinHotkeyHoldMs, TrainerSettingsDto.MaxHotkeyHoldMs);
        double clampedCueVolume = Math.Clamp(settings.CueVolume, 0.0, 1.0);
        int clampedRetention = Math.Clamp(settings.PreTrainerSnapshotRetentionCount, 1, 50);
        int clampedMaxMb = Math.Clamp(settings.PreTrainerSnapshotMaxSizeMb, 16, 4096);
        string nowText = FormatUtc(DateTimeOffset.UtcNow);

        (string Key, string? Value)[] entries =
        [
            (AppSettingKeys.TrainerCustomSandboxRoot, string.IsNullOrWhiteSpace(settings.CustomSandboxRoot) ? null : settings.CustomSandboxRoot.Trim()),
            (AppSettingKeys.TrainerAudioMode, settings.AudioMode.ToString()),
            (AppSettingKeys.TrainerCueVolume, clampedCueVolume.ToString("0.###", CultureInfo.InvariantCulture)),
            (AppSettingKeys.TrainerEnableA0PreMute, settings.EnableA0PreMute ? "true" : "false"),
            (AppSettingKeys.TrainerHotkeyHoldMs, clampedHoldMs.ToString(CultureInfo.InvariantCulture)),
            (AppSettingKeys.TrainerPreSnapshotEnabled, settings.CreatePreTrainerSnapshot ? "true" : "false"),
            (AppSettingKeys.TrainerPreSnapshotRetentionCount, clampedRetention.ToString(CultureInfo.InvariantCulture)),
            (AppSettingKeys.TrainerPreSnapshotMaxSizeMb, clampedMaxMb.ToString(CultureInfo.InvariantCulture)),
            (AppSettingKeys.TrainerStopWithGameExit, settings.StopWithGameExit ? "true" : "false"),
            (AppSettingKeys.TrainerHideOriginalWindowOnLaunch, settings.HideOriginalWindowOnLaunch ? "true" : "false"),
            (AppSettingKeys.TrainerStrictVanguardMode, settings.StrictVanguardMode ? "true" : "false"),
            (AppSettingKeys.TrainerIncludeFlingArchive, settings.IncludeFlingArchiveInSearch ? "true" : "false"),
            (AppSettingKeys.TrainerDefaultScanCodeMode, settings.DefaultScanCodeMode ? "true" : "false"),
        ];

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var (key, val) in entries)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (val is null)
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
                    cmd.Parameters.AddWithValue("$val", val);
                    cmd.Parameters.AddWithValue("$updated", nowText);
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // General Key-Value Cache (Steam appdetails categories / English titles, etc.)
    // -------------------------------------------------------------------------

    public Task SetCacheEntryAsync(
        string cacheKey,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(value);
        return SetSettingAsync(FormatCacheStorageKey(cacheKey), value, cancellationToken);
    }

    public async Task<(string Value, DateTimeOffset CachedAtUtc)?> GetCacheEntryAsync(
        string cacheKey,
        TimeSpan? maxAge = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setting_value, updated_at_utc FROM app_settings WHERE setting_key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", FormatCacheStorageKey(cacheKey));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string value = reader.GetString(0);
        DateTimeOffset cachedAt = ParseUtc(reader.GetString(1));
        if (maxAge.HasValue && (maxAge.Value <= TimeSpan.Zero || DateTimeOffset.UtcNow - cachedAt > maxAge.Value))
        {
            return null;
        }

        return (value, cachedAt);
    }

    public Task SetCacheJsonAsync<T>(
        string cacheKey,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        string json = JsonSerializer.Serialize(value, JsonOptions);
        return SetCacheEntryAsync(cacheKey, json, cancellationToken);
    }

    public async Task<T?> GetCacheJsonAsync<T>(
        string cacheKey,
        TimeSpan? maxAge = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetCacheEntryAsync(cacheKey, maxAge, cancellationToken).ConfigureAwait(false);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Value.Value))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(entry.Value.Value, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public Task DeleteCacheEntryAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return Task.CompletedTask;
        }

        return SetSettingAsync(FormatCacheStorageKey(cacheKey), null, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // M3 Mappers & Internal Helpers
    // -------------------------------------------------------------------------

    private static string FormatCacheStorageKey(string cacheKey)
    {
        string trimmed = cacheKey.Trim();
        return trimmed.StartsWith(CacheKeyPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : CacheKeyPrefix + trimmed;
    }

    private static void BindHotkeyUpsertCommand(
        SqliteCommand cmd,
        string trainerId,
        TrainerHotkeyBindingRecord binding)
    {
        cmd.CommandText = """
            INSERT INTO trainer_hotkey_bindings (
                trainer_id, entry_id, label, tooltip, original_chord,
                mapped_chord, entry_kind, proxy_state, sort_order
            ) VALUES (
                $trainer_id, $entry_id, $label, $tooltip, $original_chord,
                $mapped_chord, $entry_kind, $proxy_state, $sort_order
            )
            ON CONFLICT(trainer_id, entry_id) DO UPDATE SET
                label = excluded.label,
                tooltip = excluded.tooltip,
                original_chord = excluded.original_chord,
                mapped_chord = excluded.mapped_chord,
                entry_kind = excluded.entry_kind,
                proxy_state = excluded.proxy_state,
                sort_order = excluded.sort_order;
            """;
        cmd.Parameters.AddWithValue("$trainer_id", trainerId);
        cmd.Parameters.AddWithValue("$entry_id", binding.EntryId);
        cmd.Parameters.AddWithValue("$label", binding.Label);
        cmd.Parameters.AddWithValue("$tooltip", (object?)binding.Tooltip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$original_chord", binding.OriginalChord);
        cmd.Parameters.AddWithValue("$mapped_chord", (object?)binding.MappedChord ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$entry_kind", binding.EntryKind.ToString());
        cmd.Parameters.AddWithValue("$proxy_state", binding.ProxyState ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort_order", binding.SortOrder);
    }

    private static TrainerCatalogRecord MapTrainerCatalogRecord(SqliteDataReader reader) =>
        new()
        {
            TrainerId = reader.GetString(reader.GetOrdinal("trainer_id")),
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            Provider = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("provider")), TrainerProviderKind.LocalImport),
            DisplayName = GetNullableString(reader, "display_name"),
            VersionLabel = GetNullableString(reader, "version_label"),
            SourcePageUrl = GetNullableString(reader, "source_page_url"),
            SourceUpdatedAt = GetNullableString(reader, "source_updated_at"),
            SandboxRelativePath = reader.GetString(reader.GetOrdinal("sandbox_relative_path")),
            FileSha256 = reader.GetString(reader.GetOrdinal("file_sha256")),
            FileSize = GetNullableInt64(reader, "file_size"),
            PeMachine = GetNullableString(reader, "pe_machine"),
            RequiresAdmin = reader.GetInt32(reader.GetOrdinal("requires_admin")) != 0,
            AuthenticodeStatus = ParseEnumOrDefault(GetNullableString(reader, "authenticode_status"), TrainerAuthenticodeStatus.Unknown),
            ParseMode = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("parse_mode")), TrainerParseMode.FreeHosted),
            BoundBuildId = GetNullableString(reader, "bound_build_id"),
            BoundExeSize = GetNullableInt64(reader, "bound_exe_size"),
            BoundExeLastWriteUtc = ParseNullableUtc(reader, "bound_exe_last_write_utc"),
            ImportedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("imported_at_utc"))),
            LastLaunchedAtUtc = ParseNullableUtc(reader, "last_launched_at_utc"),
        };

    private static PreTrainerSnapshotRecord MapPreTrainerSnapshotRecord(SqliteDataReader reader) =>
        new()
        {
            SnapshotId = reader.GetString(reader.GetOrdinal("snapshot_id")),
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            TrainerId = GetNullableString(reader, "trainer_id"),
            TriggerKind = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("trigger_kind")), PreTrainerSnapshotTriggerKind.PreTrainer),
            Status = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("status")), PreTrainerSnapshotStatus.Completed),
            SkipReason = GetNullableString(reader, "skip_reason"),
            FileCount = reader.GetInt32(reader.GetOrdinal("file_count")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("total_bytes")),
            ManifestRelativePath = GetNullableString(reader, "manifest_relative_path"),
            CreatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            RestoredAtUtc = ParseNullableUtc(reader, "restored_at_utc"),
        };

    private static long? GetNullableInt64(SqliteDataReader reader, string columnName)
    {
        int ord = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ord) ? null : reader.GetInt64(ord);
    }
}
