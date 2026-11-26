using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Aggregate per-game Save Time Machine metrics projected from SQLite for fast library overviews.
/// </summary>
public sealed record SaveGameAggregateStats(
    string GameId,
    int BranchCount,
    int SnapshotCount,
    int PinnedCount,
    long TotalRawBytes,
    long TotalStoredBytes,
    DateTimeOffset? LastSnapshotAtUtc,
    int UnimportedLegacyPreTrainerCount);

public sealed partial class SpawnDatabase
{
    public const string DefaultSaveBranchName = "main";
    public const string SaveAutoCloudPushOnGameExitSettingKey = "saves.auto_cloud_push_on_exit";
    public const string SaveCheckRemoteUpdatesBeforeLaunchSettingKey = "saves.check_remote_before_launch";

    // -------------------------------------------------------------------------
    // Schema v4 -> v5 Idempotent Migration (Save Time Machine, Branches & Cloud Sync)
    // -------------------------------------------------------------------------

    private static async Task ApplyV5SaveTimeMachineMigrationsAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var ddlCmd = conn.CreateCommand())
        {
            ddlCmd.Transaction = tx;
            ddlCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS save_branches (
                    branch_id           TEXT PRIMARY KEY,
                    game_id             TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                    name                TEXT NOT NULL,
                    description         TEXT,
                    color_hex           TEXT NOT NULL DEFAULT '#1677FF',
                    head_snapshot_id    TEXT,
                    base_snapshot_id    TEXT,
                    is_default          INTEGER NOT NULL DEFAULT 0,
                    is_archived         INTEGER NOT NULL DEFAULT 0,
                    created_at_utc      TEXT NOT NULL,
                    updated_at_utc      TEXT NOT NULL,
                    UNIQUE (game_id, name COLLATE NOCASE)
                );

                CREATE INDEX IF NOT EXISTS ix_save_branches_game
                    ON save_branches(game_id, is_default DESC, created_at_utc ASC);

                CREATE TABLE IF NOT EXISTS cloud_sync_endpoints (
                    endpoint_id             TEXT PRIMARY KEY,
                    display_name            TEXT NOT NULL,
                    provider_type           TEXT NOT NULL DEFAULT 'LocalFolder',
                    endpoint_url            TEXT NOT NULL,
                    bucket_or_root          TEXT NOT NULL DEFAULT '',
                    region                  TEXT,
                    path_style_addressing   INTEGER NOT NULL DEFAULT 1,
                    secret_credential_id    TEXT,
                    is_default              INTEGER NOT NULL DEFAULT 0,
                    created_at_utc          TEXT NOT NULL,
                    updated_at_utc          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS save_snapshots (
                    snapshot_id                     TEXT PRIMARY KEY,
                    game_id                         TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                    branch_id                       TEXT NOT NULL REFERENCES save_branches(branch_id) ON DELETE CASCADE,
                    parent_snapshot_id              TEXT REFERENCES save_snapshots(snapshot_id) ON DELETE SET NULL,
                    merge_parent_snapshot_id        TEXT REFERENCES save_snapshots(snapshot_id) ON DELETE SET NULL,
                    profile_id                      TEXT,
                    mod_profile_hash                TEXT NOT NULL DEFAULT '',
                    trigger_kind                    TEXT NOT NULL DEFAULT 'Manual',
                    note                            TEXT,
                    file_count                      INTEGER NOT NULL DEFAULT 0,
                    registry_key_count              INTEGER NOT NULL DEFAULT 0,
                    total_raw_bytes                 INTEGER NOT NULL DEFAULT 0,
                    total_stored_bytes              INTEGER NOT NULL DEFAULT 0,
                    created_machine_id              TEXT NOT NULL,
                    playtime_active_seconds         INTEGER NOT NULL DEFAULT 0,
                    created_at_utc                  TEXT NOT NULL,
                    is_pinned                       INTEGER NOT NULL DEFAULT 0,
                    legacy_pre_trainer_snapshot_id  TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_save_snapshots_game_time
                    ON save_snapshots(game_id, created_at_utc DESC, snapshot_id DESC);

                CREATE INDEX IF NOT EXISTS ix_save_snapshots_branch_time
                    ON save_snapshots(branch_id, created_at_utc DESC, snapshot_id DESC);

                CREATE INDEX IF NOT EXISTS ix_save_snapshots_parent
                    ON save_snapshots(parent_snapshot_id) WHERE parent_snapshot_id IS NOT NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS ux_save_snapshots_legacy
                    ON save_snapshots(legacy_pre_trainer_snapshot_id) WHERE legacy_pre_trainer_snapshot_id IS NOT NULL;

                CREATE TABLE IF NOT EXISTS save_game_state (
                    game_id                         TEXT PRIMARY KEY REFERENCES games(game_id) ON DELETE CASCADE,
                    active_branch_id                TEXT REFERENCES save_branches(branch_id) ON DELETE SET NULL,
                    current_head_snapshot_id        TEXT REFERENCES save_snapshots(snapshot_id) ON DELETE SET NULL,
                    auto_watch_enabled              INTEGER NOT NULL DEFAULT 1,
                    post_exit_snapshot_enabled      INTEGER NOT NULL DEFAULT 1,
                    include_config_files            INTEGER NOT NULL DEFAULT 0,
                    max_rolling_snapshots           INTEGER NOT NULL DEFAULT 30,
                    cloud_endpoint_id               TEXT REFERENCES cloud_sync_endpoints(endpoint_id) ON DELETE SET NULL,
                    cloud_game_key                  TEXT,
                    sync_all_branches               INTEGER NOT NULL DEFAULT 0,
                    last_synced_at_utc              TEXT,
                    has_unresolved_cloud_conflict   INTEGER NOT NULL DEFAULT 0,
                    updated_at_utc                  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS save_synced_snapshots (
                    game_id         TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                    endpoint_id     TEXT NOT NULL,
                    snapshot_id     TEXT NOT NULL REFERENCES save_snapshots(snapshot_id) ON DELETE CASCADE,
                    synced_at_utc   TEXT NOT NULL,
                    PRIMARY KEY (game_id, endpoint_id, snapshot_id)
                );

                CREATE INDEX IF NOT EXISTS ix_save_synced_snapshots_game_endpoint
                    ON save_synced_snapshots(game_id, endpoint_id);

                CREATE TABLE IF NOT EXISTS save_custom_locations (
                    location_id          TEXT PRIMARY KEY,
                    game_id              TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                    path_or_registry_key TEXT NOT NULL,
                    location_kind        TEXT NOT NULL DEFAULT 'Directory',
                    category             TEXT NOT NULL DEFAULT 'Save',
                    label                TEXT,
                    created_at_utc       TEXT NOT NULL,
                    UNIQUE (game_id, path_or_registry_key COLLATE NOCASE)
                );

                CREATE INDEX IF NOT EXISTS ix_save_custom_locations_game
                    ON save_custom_locations(game_id, created_at_utc ASC);

                CREATE TABLE IF NOT EXISTS snapshot_files (
                    snapshot_id         TEXT NOT NULL REFERENCES save_snapshots(snapshot_id) ON DELETE CASCADE,
                    logical_path        TEXT NOT NULL,
                    original_path       TEXT NOT NULL,
                    category            TEXT NOT NULL DEFAULT 'Save',
                    file_size           INTEGER NOT NULL DEFAULT 0,
                    last_write_time_utc TEXT NOT NULL,
                    file_blake3         TEXT NOT NULL,
                    chunk_sequence_json TEXT NOT NULL DEFAULT '[]',
                    PRIMARY KEY (snapshot_id, logical_path)
                );

                CREATE TABLE IF NOT EXISTS cas_chunks (
                    chunk_blake3            TEXT PRIMARY KEY,
                    raw_size_bytes          INTEGER NOT NULL DEFAULT 0,
                    compressed_size_bytes   INTEGER NOT NULL DEFAULT 0,
                    ref_count               INTEGER NOT NULL DEFAULT 0,
                    pack_id                 TEXT,
                    created_at_utc          TEXT NOT NULL,
                    last_unreferenced_at_utc TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_cas_chunks_unreferenced
                    ON cas_chunks(ref_count, last_unreferenced_at_utc) WHERE ref_count <= 0;

                CREATE TABLE IF NOT EXISTS ludusavi_rules (
                    rule_name           TEXT PRIMARY KEY COLLATE NOCASE,
                    steam_app_id        INTEGER,
                    gog_id              TEXT,
                    aliases_json        TEXT NOT NULL DEFAULT '[]',
                    save_paths_json     TEXT NOT NULL DEFAULT '[]',
                    config_paths_json   TEXT NOT NULL DEFAULT '[]',
                    registry_rules_json TEXT NOT NULL DEFAULT '[]',
                    updated_at_utc      TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_ludusavi_rules_steam
                    ON ludusavi_rules(steam_app_id) WHERE steam_app_id IS NOT NULL;

                CREATE INDEX IF NOT EXISTS ix_ludusavi_rules_gog
                    ON ludusavi_rules(gog_id) WHERE gog_id IS NOT NULL;

                INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
                VALUES (5, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await ddlCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Idempotent column upgrades for existing v5 databases.
        var existingStateColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.Transaction = tx;
            pragmaCmd.CommandText = "PRAGMA table_info(save_game_state);";
            await using var reader = await pragmaCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingStateColumns.Add(reader.GetString(1));
            }
        }

        if (!existingStateColumns.Contains("cloud_game_key"))
        {
            await using var alterCmd = conn.CreateCommand();
            alterCmd.Transaction = tx;
            alterCmd.CommandText = "ALTER TABLE save_game_state ADD COLUMN cloud_game_key TEXT;";
            await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existingStateColumns.Contains("sync_all_branches"))
        {
            await using var alterCmd = conn.CreateCommand();
            alterCmd.Transaction = tx;
            alterCmd.CommandText = "ALTER TABLE save_game_state ADD COLUMN sync_all_branches INTEGER NOT NULL DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // save_branches & save_game_state CRUD
    // -------------------------------------------------------------------------

    /// <summary>
    /// Ensures the default <c>"main"</c> branch and <c>save_game_state</c> row exist for <paramref name="gameId"/>.
    /// </summary>
    public async Task<SaveBranchRecord> EnsureDefaultSaveBranchAsync(
        string gameId,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset timestamp = nowUtc ?? DateTimeOffset.UtcNow;
        SaveSettingsDto defaults = await GetSaveSettingsAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            SaveBranchRecord? existingDefault = await FindDefaultOrMainBranchCoreAsync(conn, tx, gameId, cancellationToken)
                .ConfigureAwait(false);

            if (existingDefault is null)
            {
                string branchId = SpawnId.NewUlid(timestamp);
                await using var insBranch = conn.CreateCommand();
                insBranch.Transaction = tx;
                insBranch.CommandText = """
                    INSERT INTO save_branches (
                        branch_id, game_id, name, description, color_hex,
                        head_snapshot_id, base_snapshot_id, is_default, is_archived,
                        created_at_utc, updated_at_utc
                    ) VALUES (
                        $branch_id, $game_id, $name, $description, $color_hex,
                        NULL, NULL, 1, 0,
                        $created_at_utc, $updated_at_utc
                    );
                    """;
                insBranch.Parameters.AddWithValue("$branch_id", branchId);
                insBranch.Parameters.AddWithValue("$game_id", gameId);
                insBranch.Parameters.AddWithValue("$name", DefaultSaveBranchName);
                insBranch.Parameters.AddWithValue("$description", "主存档线");
                insBranch.Parameters.AddWithValue("$color_hex", SaveBranchColors.DefaultMainColorHex);
                insBranch.Parameters.AddWithValue("$created_at_utc", FormatUtc(timestamp));
                insBranch.Parameters.AddWithValue("$updated_at_utc", FormatUtc(timestamp));
                await insBranch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                existingDefault = new SaveBranchRecord
                {
                    BranchId = branchId,
                    GameId = gameId,
                    Name = DefaultSaveBranchName,
                    Description = "主存档线",
                    ColorHex = SaveBranchColors.DefaultMainColorHex,
                    IsDefault = true,
                    CreatedAtUtc = timestamp,
                    UpdatedAtUtc = timestamp,
                };
            }

            await using (var insState = conn.CreateCommand())
            {
                insState.Transaction = tx;
                insState.CommandText = """
                    INSERT INTO save_game_state (
                        game_id, active_branch_id, current_head_snapshot_id,
                        auto_watch_enabled, post_exit_snapshot_enabled, include_config_files,
                        max_rolling_snapshots, cloud_endpoint_id, cloud_game_key, sync_all_branches,
                        last_synced_at_utc, has_unresolved_cloud_conflict, updated_at_utc
                    ) VALUES (
                        $game_id, $active_branch_id, $current_head_snapshot_id,
                        $auto_watch_enabled, $post_exit_snapshot_enabled, $include_config_files,
                        $max_rolling_snapshots, NULL, NULL, 0,
                        NULL, 0, $updated_at_utc
                    )
                    ON CONFLICT(game_id) DO UPDATE SET
                        active_branch_id = COALESCE(save_game_state.active_branch_id, excluded.active_branch_id);
                    """;
                insState.Parameters.AddWithValue("$game_id", gameId);
                insState.Parameters.AddWithValue("$active_branch_id", existingDefault.BranchId);
                insState.Parameters.AddWithValue("$current_head_snapshot_id", (object?)existingDefault.HeadSnapshotId ?? DBNull.Value);
                insState.Parameters.AddWithValue("$auto_watch_enabled", defaults.AutoWatchByDefault ? 1 : 0);
                insState.Parameters.AddWithValue("$post_exit_snapshot_enabled", defaults.PostExitSnapshotByDefault ? 1 : 0);
                insState.Parameters.AddWithValue("$include_config_files", defaults.IncludeConfigFilesByDefault ? 1 : 0);
                insState.Parameters.AddWithValue("$max_rolling_snapshots", Math.Clamp(defaults.DefaultMaxRollingSnapshots, 1, 500));
                insState.Parameters.AddWithValue("$updated_at_utc", FormatUtc(timestamp));
                await insState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existingDefault;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertSaveBranchAsync(
        SaveBranchRecord branch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch.BranchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch.Name);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO save_branches (
                    branch_id, game_id, name, description, color_hex,
                    head_snapshot_id, base_snapshot_id, is_default, is_archived,
                    created_at_utc, updated_at_utc
                ) VALUES (
                    $branch_id, $game_id, $name, $description, $color_hex,
                    $head_snapshot_id, $base_snapshot_id, $is_default, $is_archived,
                    $created_at_utc, $updated_at_utc
                )
                ON CONFLICT(branch_id) DO UPDATE SET
                    name = excluded.name,
                    description = excluded.description,
                    color_hex = excluded.color_hex,
                    head_snapshot_id = excluded.head_snapshot_id,
                    base_snapshot_id = COALESCE(excluded.base_snapshot_id, save_branches.base_snapshot_id),
                    is_default = excluded.is_default,
                    is_archived = excluded.is_archived,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$branch_id", branch.BranchId);
            cmd.Parameters.AddWithValue("$game_id", branch.GameId);
            cmd.Parameters.AddWithValue("$name", branch.Name.Trim());
            cmd.Parameters.AddWithValue("$description", (object?)branch.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$color_hex", string.IsNullOrWhiteSpace(branch.ColorHex) ? SaveBranchColors.DefaultMainColorHex : branch.ColorHex.Trim());
            cmd.Parameters.AddWithValue("$head_snapshot_id", (object?)branch.HeadSnapshotId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$base_snapshot_id", (object?)branch.BaseSnapshotId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$is_default", branch.IsDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$is_archived", branch.IsArchived ? 1 : 0);
            cmd.Parameters.AddWithValue("$created_at_utc", FormatUtc(branch.CreatedAtUtc));
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(branch.UpdatedAtUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SaveBranchRecord?> GetSaveBranchByIdAsync(
        string branchId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.*,
                   (SELECT COUNT(*) FROM save_snapshots s WHERE s.branch_id = b.branch_id) AS snapshot_count
            FROM save_branches b
            WHERE b.branch_id = $branch_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$branch_id", branchId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapSaveBranchRecord(reader)
            : null;
    }

    public async Task<SaveBranchRecord?> GetSaveBranchByNameAsync(
        string gameId,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(branchName))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.*,
                   (SELECT COUNT(*) FROM save_snapshots s WHERE s.branch_id = b.branch_id) AS snapshot_count
            FROM save_branches b
            WHERE b.game_id = $game_id AND b.name = $name COLLATE NOCASE
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        cmd.Parameters.AddWithValue("$name", branchName.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapSaveBranchRecord(reader)
            : null;
    }

    public async Task<IReadOnlyList<SaveBranchRecord>> GetSaveBranchesByGameAsync(
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
            SELECT b.*,
                   (SELECT COUNT(*) FROM save_snapshots s WHERE s.branch_id = b.branch_id) AS snapshot_count
            FROM save_branches b
            WHERE b.game_id = $game_id
            ORDER BY b.is_default DESC, b.created_at_utc ASC, b.branch_id ASC;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);

        var list = new List<SaveBranchRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapSaveBranchRecord(reader));
        }

        return list;
    }

    public async Task<bool> UpdateSaveBranchHeadAsync(
        string branchId,
        string? headSnapshotId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE save_branches
                SET head_snapshot_id = $head_snapshot_id,
                    updated_at_utc = $updated_at_utc
                WHERE branch_id = $branch_id;
                """;
            cmd.Parameters.AddWithValue("$branch_id", branchId);
            cmd.Parameters.AddWithValue("$head_snapshot_id", (object?)headSnapshotId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateSaveBranchBaseAsync(
        string branchId,
        string? baseSnapshotId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE save_branches
                SET base_snapshot_id = $base_snapshot_id,
                    updated_at_utc = $updated_at_utc
                WHERE branch_id = $branch_id;
                """;
            cmd.Parameters.AddWithValue("$branch_id", branchId);
            cmd.Parameters.AddWithValue("$base_snapshot_id", (object?)baseSnapshotId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateSaveBranchMetadataAsync(
        string branchId,
        string newName,
        string? description,
        string? colorHex,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE save_branches
                SET name = $name,
                    description = COALESCE($description, description),
                    color_hex = COALESCE($color_hex, color_hex),
                    updated_at_utc = $updated_at_utc
                WHERE branch_id = $branch_id;
                """;
            cmd.Parameters.AddWithValue("$branch_id", branchId);
            cmd.Parameters.AddWithValue("$name", newName.Trim());
            cmd.Parameters.AddWithValue("$description", description is null ? DBNull.Value : description);
            cmd.Parameters.AddWithValue("$color_hex", string.IsNullOrWhiteSpace(colorHex) ? DBNull.Value : colorHex.Trim());
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Deletes a non-default save branch. When <paramref name="deleteSnapshots"/> is <see langword="true"/>,
    /// removes all snapshots belonging to the branch and decrements their CAS chunk reference counts atomically.
    /// </summary>
    public async Task<(bool Deleted, int DeletedSnapshotCount)> DeleteSaveBranchAsync(
        string branchId,
        bool deleteSnapshots,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchId))
        {
            return (false, 0);
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var snapshotIds = new List<string>();
            await using (var snapCmd = conn.CreateCommand())
            {
                snapCmd.Transaction = tx;
                snapCmd.CommandText = "SELECT snapshot_id FROM save_snapshots WHERE branch_id = $branch_id;";
                snapCmd.Parameters.AddWithValue("$branch_id", branchId);
                await using var reader = await snapCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    snapshotIds.Add(reader.GetString(0));
                }
            }

            if (snapshotIds.Count > 0 && !deleteSnapshots)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return (false, 0);
            }

            if (snapshotIds.Count > 0)
            {
                var chunkDecrementCounts = await CollectUniqueChunksPerSnapshotsCoreAsync(conn, tx, snapshotIds, cancellationToken)
                    .ConfigureAwait(false);
                await DecrementChunkRefCountsCoreAsync(conn, tx, chunkDecrementCounts, timestamp, cancellationToken)
                    .ConfigureAwait(false);
            }

            int affected;
            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM save_branches WHERE branch_id = $branch_id;";
                delCmd.Parameters.AddWithValue("$branch_id", branchId);
                affected = await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (affected > 0, snapshotIds.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SaveGameStateRecord?> GetSaveGameStateAsync(
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
        cmd.CommandText = "SELECT * FROM save_game_state WHERE game_id = $game_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$game_id", gameId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapSaveGameStateRecord(reader)
            : null;
    }

    public async Task<SaveGameStateRecord> GetOrCreateSaveGameStateAsync(
        string gameId,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        SaveGameStateRecord? existing = await GetSaveGameStateAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.ActiveBranchId))
        {
            return existing;
        }

        await EnsureDefaultSaveBranchAsync(gameId, nowUtc, cancellationToken).ConfigureAwait(false);
        return (await GetSaveGameStateAsync(gameId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task UpsertSaveGameStateAsync(
        SaveGameStateRecord state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.GameId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO save_game_state (
                    game_id, active_branch_id, current_head_snapshot_id,
                    auto_watch_enabled, post_exit_snapshot_enabled, include_config_files,
                    max_rolling_snapshots, cloud_endpoint_id, cloud_game_key, sync_all_branches,
                    last_synced_at_utc, has_unresolved_cloud_conflict, updated_at_utc
                ) VALUES (
                    $game_id, $active_branch_id, $current_head_snapshot_id,
                    $auto_watch_enabled, $post_exit_snapshot_enabled, $include_config_files,
                    $max_rolling_snapshots, $cloud_endpoint_id, $cloud_game_key, $sync_all_branches,
                    $last_synced_at_utc, $has_unresolved_cloud_conflict, $updated_at_utc
                )
                ON CONFLICT(game_id) DO UPDATE SET
                    active_branch_id = excluded.active_branch_id,
                    current_head_snapshot_id = excluded.current_head_snapshot_id,
                    auto_watch_enabled = excluded.auto_watch_enabled,
                    post_exit_snapshot_enabled = excluded.post_exit_snapshot_enabled,
                    include_config_files = excluded.include_config_files,
                    max_rolling_snapshots = excluded.max_rolling_snapshots,
                    cloud_endpoint_id = excluded.cloud_endpoint_id,
                    cloud_game_key = excluded.cloud_game_key,
                    sync_all_branches = excluded.sync_all_branches,
                    last_synced_at_utc = excluded.last_synced_at_utc,
                    has_unresolved_cloud_conflict = excluded.has_unresolved_cloud_conflict,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$game_id", state.GameId);
            cmd.Parameters.AddWithValue("$active_branch_id", (object?)state.ActiveBranchId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$current_head_snapshot_id", (object?)state.CurrentHeadSnapshotId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$auto_watch_enabled", state.AutoWatchEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$post_exit_snapshot_enabled", state.PostExitSnapshotEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$include_config_files", state.IncludeConfigFiles ? 1 : 0);
            cmd.Parameters.AddWithValue("$max_rolling_snapshots", Math.Clamp(state.MaxRollingSnapshots, 1, 500));
            cmd.Parameters.AddWithValue("$cloud_endpoint_id", (object?)state.CloudEndpointId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cloud_game_key", string.IsNullOrWhiteSpace(state.CloudGameKey) ? DBNull.Value : state.CloudGameKey.Trim());
            cmd.Parameters.AddWithValue("$sync_all_branches", state.SyncAllBranchesToCloud ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_synced_at_utc", FormatNullableUtc(state.LastSyncedAtUtc));
            cmd.Parameters.AddWithValue("$has_unresolved_cloud_conflict", state.HasUnresolvedCloudConflict ? 1 : 0);
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(state.UpdatedAtUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetActiveSaveHeadAsync(
        string gameId,
        string? activeBranchId,
        string? currentHeadSnapshotId,
        DateTimeOffset updatedAtUtc,
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
                UPDATE save_game_state
                SET active_branch_id = $active_branch_id,
                    current_head_snapshot_id = $current_head_snapshot_id,
                    updated_at_utc = $updated_at_utc
                WHERE game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$active_branch_id", (object?)activeBranchId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$current_head_snapshot_id", (object?)currentHeadSnapshotId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetSaveGameCloudSyncStatusAsync(
        string gameId,
        DateTimeOffset? lastSyncedAtUtc,
        bool hasUnresolvedConflict,
        DateTimeOffset? updatedAtUtc = null,
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
                UPDATE save_game_state
                SET last_synced_at_utc = COALESCE($last_synced_at_utc, last_synced_at_utc),
                    has_unresolved_cloud_conflict = $has_conflict,
                    updated_at_utc = $updated_at_utc
                WHERE game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$last_synced_at_utc", FormatNullableUtc(lastSyncedAtUtc));
            cmd.Parameters.AddWithValue("$has_conflict", hasUnresolvedConflict ? 1 : 0);
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc ?? DateTimeOffset.UtcNow));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SaveGameStateRecord>> GetAllSaveGameStatesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM save_game_state;";

        var list = new List<SaveGameStateRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapSaveGameStateRecord(reader));
        }

        return list;
    }

    public async Task<bool> UpdateSaveGameCloudKeyAsync(
        string gameId,
        string? cloudGameKey,
        DateTimeOffset? updatedAtUtc = null,
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
                UPDATE save_game_state
                SET cloud_game_key = $cloud_game_key,
                    updated_at_utc = $updated_at_utc
                WHERE game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$cloud_game_key", string.IsNullOrWhiteSpace(cloudGameKey) ? DBNull.Value : cloudGameKey.Trim());
            cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc ?? DateTimeOffset.UtcNow));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Persists the exact set of snapshot IDs confirmed uploaded to or downloaded from a cloud sync endpoint.
    /// </summary>
    public async Task<int> MarkSnapshotsCloudSyncedAsync(
        string gameId,
        string endpointId,
        IEnumerable<string> snapshotIds,
        DateTimeOffset syncedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(snapshotIds);

        string[] uniqueIds = snapshotIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uniqueIds.Length == 0)
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            int affected = 0;
            string syncedStr = FormatUtc(syncedAtUtc);

            foreach (string snapshotId in uniqueIds)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO save_synced_snapshots (game_id, endpoint_id, snapshot_id, synced_at_utc)
                    SELECT $game_id, $endpoint_id, $snapshot_id, $synced_at_utc
                    WHERE EXISTS (SELECT 1 FROM save_snapshots WHERE snapshot_id = $snapshot_id)
                    ON CONFLICT(game_id, endpoint_id, snapshot_id) DO UPDATE SET
                        synced_at_utc = excluded.synced_at_utc;
                    """;
                cmd.Parameters.AddWithValue("$game_id", gameId);
                cmd.Parameters.AddWithValue("$endpoint_id", endpointId);
                cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
                cmd.Parameters.AddWithValue("$synced_at_utc", syncedStr);
                affected += await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Returns the exact set of snapshot IDs recorded as synced to cloud for <paramref name="gameId"/>.
    /// </summary>
    public async Task<HashSet<string>> GetCloudSyncedSnapshotIdsForGameAsync(
        string gameId,
        string? endpointId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return result;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(endpointId)
            ? "SELECT DISTINCT snapshot_id FROM save_synced_snapshots WHERE game_id = $game_id;"
            : "SELECT snapshot_id FROM save_synced_snapshots WHERE game_id = $game_id AND endpoint_id = $endpoint_id;";
        cmd.Parameters.AddWithValue("$game_id", gameId);
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            cmd.Parameters.AddWithValue("$endpoint_id", endpointId);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // save_custom_locations CRUD
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<SaveCustomLocationRecord>> GetSaveCustomLocationsByGameAsync(
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
            SELECT * FROM save_custom_locations
            WHERE game_id = $game_id
            ORDER BY created_at_utc ASC, location_id ASC;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);

        var list = new List<SaveCustomLocationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapSaveCustomLocationRecord(reader));
        }

        return list;
    }

    public async Task UpsertSaveCustomLocationAsync(
        SaveCustomLocationRecord location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(location.LocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(location.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(location.PathOrRegistryKey);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO save_custom_locations (
                    location_id, game_id, path_or_registry_key, location_kind, category, label, created_at_utc
                ) VALUES (
                    $location_id, $game_id, $path_or_registry_key, $location_kind, $category, $label, $created_at_utc
                )
                ON CONFLICT(game_id, path_or_registry_key) DO UPDATE SET
                    location_kind = excluded.location_kind,
                    category = excluded.category,
                    label = excluded.label;
                """;
            cmd.Parameters.AddWithValue("$location_id", location.LocationId);
            cmd.Parameters.AddWithValue("$game_id", location.GameId);
            cmd.Parameters.AddWithValue("$path_or_registry_key", location.PathOrRegistryKey.Trim());
            cmd.Parameters.AddWithValue("$location_kind", location.LocationKind.ToString());
            cmd.Parameters.AddWithValue("$category", location.Category.ToString());
            cmd.Parameters.AddWithValue("$label", string.IsNullOrWhiteSpace(location.Label) ? DBNull.Value : location.Label.Trim());
            cmd.Parameters.AddWithValue("$created_at_utc", FormatUtc(location.CreatedAtUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteSaveCustomLocationAsync(
        string locationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM save_custom_locations WHERE location_id = $location_id;";
            cmd.Parameters.AddWithValue("$location_id", locationId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // save_snapshots & snapshot_files Atomic CRUD
    // -------------------------------------------------------------------------

    /// <summary>
    /// Atomically commits a new snapshot, its file manifest rows, CAS chunk ref-count increments,
    /// and optional branch head + active game HEAD pointer updates in a single SQLite transaction.
    /// </summary>
    public async Task CreateSaveSnapshotWithFilesAsync(
        SaveSnapshotRecord snapshot,
        IReadOnlyList<SaveSnapshotFileRecord> files,
        bool advanceBranchHead = true,
        bool updateGameActiveHead = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.SnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.BranchId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var snapCmd = conn.CreateCommand())
            {
                snapCmd.Transaction = tx;
                snapCmd.CommandText = """
                    INSERT INTO save_snapshots (
                        snapshot_id, game_id, branch_id, parent_snapshot_id, merge_parent_snapshot_id,
                        profile_id, mod_profile_hash, trigger_kind, note,
                        file_count, registry_key_count, total_raw_bytes, total_stored_bytes,
                        created_machine_id, playtime_active_seconds, created_at_utc,
                        is_pinned, legacy_pre_trainer_snapshot_id
                    ) VALUES (
                        $snapshot_id, $game_id, $branch_id, $parent_snapshot_id, $merge_parent_snapshot_id,
                        $profile_id, $mod_profile_hash, $trigger_kind, $note,
                        $file_count, $registry_key_count, $total_raw_bytes, $total_stored_bytes,
                        $created_machine_id, $playtime_active_seconds, $created_at_utc,
                        $is_pinned, $legacy_pre_trainer_snapshot_id
                    );
                    """;
                snapCmd.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
                snapCmd.Parameters.AddWithValue("$game_id", snapshot.GameId);
                snapCmd.Parameters.AddWithValue("$branch_id", snapshot.BranchId);
                snapCmd.Parameters.AddWithValue("$parent_snapshot_id", (object?)snapshot.ParentSnapshotId ?? DBNull.Value);
                snapCmd.Parameters.AddWithValue("$merge_parent_snapshot_id", (object?)snapshot.MergeParentSnapshotId ?? DBNull.Value);
                snapCmd.Parameters.AddWithValue("$profile_id", (object?)snapshot.ProfileId ?? DBNull.Value);
                snapCmd.Parameters.AddWithValue("$mod_profile_hash", snapshot.ModProfileHash ?? string.Empty);
                snapCmd.Parameters.AddWithValue("$trigger_kind", snapshot.TriggerKind.ToString());
                snapCmd.Parameters.AddWithValue("$note", (object?)snapshot.Note ?? DBNull.Value);
                snapCmd.Parameters.AddWithValue("$file_count", Math.Max(0, snapshot.FileCount));
                snapCmd.Parameters.AddWithValue("$registry_key_count", Math.Max(0, snapshot.RegistryKeyCount));
                snapCmd.Parameters.AddWithValue("$total_raw_bytes", Math.Max(0, snapshot.TotalRawBytes));
                snapCmd.Parameters.AddWithValue("$total_stored_bytes", Math.Max(0, snapshot.TotalStoredBytes));
                snapCmd.Parameters.AddWithValue("$created_machine_id", snapshot.CreatedMachineId);
                snapCmd.Parameters.AddWithValue("$playtime_active_seconds", Math.Max(0, snapshot.PlaytimeActiveSeconds));
                snapCmd.Parameters.AddWithValue("$created_at_utc", FormatUtc(snapshot.CreatedAtUtc));
                snapCmd.Parameters.AddWithValue("$is_pinned", snapshot.IsPinned ? 1 : 0);
                snapCmd.Parameters.AddWithValue("$legacy_pre_trainer_snapshot_id", (object?)snapshot.LegacyPreTrainerSnapshotId ?? DBNull.Value);
                await snapCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Track unique chunks referenced by this snapshot so ref_count increments once per snapshot reference.
            var uniqueChunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SaveSnapshotFileRecord file in files)
            {
                foreach (string chunk in file.ChunkHashes)
                {
                    if (!string.IsNullOrWhiteSpace(chunk))
                    {
                        uniqueChunks.Add(chunk.Trim().ToLowerInvariant());
                    }
                }

                await using var fileCmd = conn.CreateCommand();
                fileCmd.Transaction = tx;
                fileCmd.CommandText = """
                    INSERT INTO snapshot_files (
                        snapshot_id, logical_path, original_path, category,
                        file_size, last_write_time_utc, file_blake3, chunk_sequence_json
                    ) VALUES (
                        $snapshot_id, $logical_path, $original_path, $category,
                        $file_size, $last_write_time_utc, $file_blake3, $chunk_sequence_json
                    );
                    """;
                fileCmd.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
                fileCmd.Parameters.AddWithValue("$logical_path", file.LogicalPath);
                fileCmd.Parameters.AddWithValue("$original_path", file.OriginalPath);
                fileCmd.Parameters.AddWithValue("$category", file.Category.ToString());
                fileCmd.Parameters.AddWithValue("$file_size", Math.Max(0, file.FileSize));
                fileCmd.Parameters.AddWithValue("$last_write_time_utc", FormatUtc(file.LastWriteTimeUtc));
                fileCmd.Parameters.AddWithValue("$file_blake3", file.FileBlake3);
                fileCmd.Parameters.AddWithValue("$chunk_sequence_json", JsonSerializer.Serialize(file.ChunkHashes, JsonOptions));
                await fileCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (string chunkHash in uniqueChunks)
            {
                await using var refCmd = conn.CreateCommand();
                refCmd.Transaction = tx;
                refCmd.CommandText = """
                    UPDATE cas_chunks
                    SET ref_count = ref_count + 1,
                        last_unreferenced_at_utc = NULL
                    WHERE chunk_blake3 = $hash;
                    """;
                refCmd.Parameters.AddWithValue("$hash", chunkHash);
                int updatedRows = await refCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (updatedRows != 1)
                {
                    throw new InvalidOperationException(
                        $"CAS chunk '{chunkHash}' is missing from cas_chunks while committing snapshot '{snapshot.SnapshotId}'.");
                }
            }

            if (advanceBranchHead)
            {
                await using var branchCmd = conn.CreateCommand();
                branchCmd.Transaction = tx;
                branchCmd.CommandText = """
                    UPDATE save_branches
                    SET head_snapshot_id = $snapshot_id,
                        updated_at_utc = $updated_at_utc
                    WHERE branch_id = $branch_id;
                    """;
                branchCmd.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
                branchCmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(snapshot.CreatedAtUtc));
                branchCmd.Parameters.AddWithValue("$branch_id", snapshot.BranchId);
                await branchCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (updateGameActiveHead)
            {
                await using var stateCmd = conn.CreateCommand();
                stateCmd.Transaction = tx;
                stateCmd.CommandText = """
                    INSERT INTO save_game_state (
                        game_id, active_branch_id, current_head_snapshot_id, updated_at_utc
                    ) VALUES (
                        $game_id, $branch_id, $snapshot_id, $updated_at_utc
                    )
                    ON CONFLICT(game_id) DO UPDATE SET
                        active_branch_id = excluded.active_branch_id,
                        current_head_snapshot_id = excluded.current_head_snapshot_id,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                stateCmd.Parameters.AddWithValue("$game_id", snapshot.GameId);
                stateCmd.Parameters.AddWithValue("$branch_id", snapshot.BranchId);
                stateCmd.Parameters.AddWithValue("$snapshot_id", snapshot.SnapshotId);
                stateCmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(snapshot.CreatedAtUtc));
                await stateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SaveSnapshotRecord?> GetSaveSnapshotByIdAsync(
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
        cmd.CommandText = "SELECT * FROM save_snapshots WHERE snapshot_id = $snapshot_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapSaveSnapshotRecord(reader)
            : null;
    }

    public async Task<SaveSnapshotRecord?> GetSaveSnapshotByLegacyPreTrainerIdAsync(
        string legacyPreTrainerSnapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(legacyPreTrainerSnapshotId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM save_snapshots WHERE legacy_pre_trainer_snapshot_id = $legacy_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$legacy_id", legacyPreTrainerSnapshotId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapSaveSnapshotRecord(reader)
            : null;
    }

    public async Task<IReadOnlyList<SaveSnapshotRecord>> GetSaveSnapshotsByGameAsync(
        string gameId,
        string? branchId = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(branchId)
            ? """
              SELECT * FROM save_snapshots
              WHERE game_id = $game_id
              ORDER BY created_at_utc DESC, snapshot_id DESC
              LIMIT $limit;
              """
            : """
              SELECT * FROM save_snapshots
              WHERE game_id = $game_id AND branch_id = $branch_id
              ORDER BY created_at_utc DESC, snapshot_id DESC
              LIMIT $limit;
              """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        if (!string.IsNullOrWhiteSpace(branchId))
        {
            cmd.Parameters.AddWithValue("$branch_id", branchId);
        }

        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var list = new List<SaveSnapshotRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapSaveSnapshotRecord(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<SaveSnapshotRecord>> GetChildSaveSnapshotsAsync(
        string parentSnapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parentSnapshotId))
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM save_snapshots
            WHERE parent_snapshot_id = $parent_id OR merge_parent_snapshot_id = $parent_id
            ORDER BY created_at_utc ASC, snapshot_id ASC;
            """;
        cmd.Parameters.AddWithValue("$parent_id", parentSnapshotId);

        var list = new List<SaveSnapshotRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapSaveSnapshotRecord(reader));
        }

        return list;
    }

    /// <summary>
    /// Walks <c>parent_snapshot_id</c> upward from <paramref name="snapshotId"/> (inclusive) up to <paramref name="maxDepth"/>
    /// generations (default 64 for SPPK cloud sync lineage checking), with cycle protection.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSaveSnapshotAncestorIdsAsync(
        string snapshotId,
        int maxDepth = 64,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) || maxDepth <= 0)
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var ancestors = new List<string>(Math.Min(maxDepth, 64));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = snapshotId;

        while (!string.IsNullOrWhiteSpace(cursor) && ancestors.Count < maxDepth && visited.Add(cursor))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT parent_snapshot_id FROM save_snapshots WHERE snapshot_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", cursor);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            ancestors.Add(cursor);
            cursor = reader.IsDBNull(0) ? null : reader.GetString(0);
        }

        return ancestors;
    }

    public async Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM snapshot_files
            WHERE snapshot_id = $snapshot_id
            ORDER BY category ASC, logical_path COLLATE NOCASE ASC;
            """;
        cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);

        var list = new List<SaveSnapshotFileRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapSaveSnapshotFileRecord(reader));
        }

        return list;
    }

    public async Task<bool> SetSaveSnapshotPinnedAsync(
        string snapshotId,
        bool isPinned,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE save_snapshots SET is_pinned = $is_pinned WHERE snapshot_id = $snapshot_id;";
            cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
            cmd.Parameters.AddWithValue("$is_pinned", isPinned ? 1 : 0);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateSaveSnapshotNoteAsync(
        string snapshotId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE save_snapshots SET note = $note WHERE snapshot_id = $snapshot_id;";
            cmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
            cmd.Parameters.AddWithValue("$note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically deletes a snapshot, cascades <c>snapshot_files</c>, decrements referenced <c>cas_chunks.ref_count</c>,
    /// and stamps <c>last_unreferenced_at_utc</c> on any chunk whose ref-count reaches zero.
    /// </summary>
    public async Task<(bool Deleted, int UnreferencedChunkCount)> DeleteSaveSnapshotAsync(
        string snapshotId,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return (false, 0);
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var chunkCounts = await CollectUniqueChunksPerSnapshotsCoreAsync(conn, tx, [snapshotId], cancellationToken)
                .ConfigureAwait(false);

            int affected;
            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM save_snapshots WHERE snapshot_id = $snapshot_id;";
                delCmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
                affected = await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (affected == 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return (false, 0);
            }

            int newlyUnreferenced = await DecrementChunkRefCountsCoreAsync(conn, tx, chunkCounts, timestamp, cancellationToken)
                .ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (true, newlyUnreferenced);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically deletes a snapshot node while re-linking direct child pointers (<c>parent_snapshot_id</c>,
    /// <c>merge_parent_snapshot_id</c>, and <c>save_branches.base_snapshot_id</c>) to
    /// <paramref name="relinkParentSnapshotId"/>, preserving the DAG lineage chain.
    /// </summary>
    public async Task<(bool Deleted, int UnreferencedChunkCount)> DeleteSaveSnapshotAndRelinkChildrenAsync(
        string snapshotId,
        string? relinkParentSnapshotId,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return (false, 0);
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var chunkCounts = await CollectUniqueChunksPerSnapshotsCoreAsync(conn, tx, [snapshotId], cancellationToken)
                .ConfigureAwait(false);

            // Re-link children before deleting so ON DELETE SET NULL never severs the chain.
            await using (var relinkParentCmd = conn.CreateCommand())
            {
                relinkParentCmd.Transaction = tx;
                relinkParentCmd.CommandText = """
                    UPDATE save_snapshots
                    SET parent_snapshot_id = $relink_parent_id
                    WHERE parent_snapshot_id = $snapshot_id;
                    """;
                relinkParentCmd.Parameters.AddWithValue("$relink_parent_id", (object?)relinkParentSnapshotId ?? DBNull.Value);
                relinkParentCmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
                await relinkParentCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var relinkMergeCmd = conn.CreateCommand())
            {
                relinkMergeCmd.Transaction = tx;
                relinkMergeCmd.CommandText = """
                    UPDATE save_snapshots
                    SET merge_parent_snapshot_id = CASE
                        WHEN parent_snapshot_id = $relink_parent_id THEN NULL
                        ELSE $relink_parent_id
                    END
                    WHERE merge_parent_snapshot_id = $snapshot_id;
                    """;
                relinkMergeCmd.Parameters.AddWithValue("$relink_parent_id", (object?)relinkParentSnapshotId ?? DBNull.Value);
                relinkMergeCmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
                await relinkMergeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var relinkBaseCmd = conn.CreateCommand())
            {
                relinkBaseCmd.Transaction = tx;
                relinkBaseCmd.CommandText = """
                    UPDATE save_branches
                    SET base_snapshot_id = $relink_parent_id
                    WHERE base_snapshot_id = $snapshot_id;
                    """;
                relinkBaseCmd.Parameters.AddWithValue("$relink_parent_id", (object?)relinkParentSnapshotId ?? DBNull.Value);
                relinkBaseCmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
                await relinkBaseCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int affected;
            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM save_snapshots WHERE snapshot_id = $snapshot_id;";
                delCmd.Parameters.AddWithValue("$snapshot_id", snapshotId);
                affected = await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (affected == 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return (false, 0);
            }

            int newlyUnreferenced = await DecrementChunkRefCountsCoreAsync(conn, tx, chunkCounts, timestamp, cancellationToken)
                .ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (true, newlyUnreferenced);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> GetUnimportedLegacyPreTrainerCountForGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM pre_trainer_snapshots p
            WHERE p.game_id = $game_id
              AND p.status = 'Completed'
              AND NOT EXISTS (
                  SELECT 1 FROM save_snapshots s WHERE s.legacy_pre_trainer_snapshot_id = p.snapshot_id
              );
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is long v ? (int)v : Convert.ToInt32(scalar ?? 0, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, SaveGameAggregateStats>> GetSaveGameAggregateStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                g.game_id,
                (SELECT COUNT(*) FROM save_branches b WHERE b.game_id = g.game_id AND b.is_archived = 0) AS branch_count,
                (SELECT COUNT(*) FROM save_snapshots s WHERE s.game_id = g.game_id) AS snapshot_count,
                (SELECT COUNT(*) FROM save_snapshots s WHERE s.game_id = g.game_id AND s.is_pinned = 1) AS pinned_count,
                (SELECT COALESCE(SUM(s.total_raw_bytes), 0) FROM save_snapshots s WHERE s.game_id = g.game_id) AS total_raw_bytes,
                (SELECT COALESCE(SUM(s.total_stored_bytes), 0) FROM save_snapshots s WHERE s.game_id = g.game_id) AS total_stored_bytes,
                (SELECT MAX(s.created_at_utc) FROM save_snapshots s WHERE s.game_id = g.game_id) AS last_snapshot_at_utc,
                (SELECT COUNT(*) FROM pre_trainer_snapshots p
                 WHERE p.game_id = g.game_id
                   AND p.status = 'Completed'
                   AND NOT EXISTS (
                       SELECT 1 FROM save_snapshots s WHERE s.legacy_pre_trainer_snapshot_id = p.snapshot_id
                   )) AS unimported_legacy_count
            FROM games g;
            """;

        var result = new Dictionary<string, SaveGameAggregateStats>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string gameId = reader.GetString(0);
            result[gameId] = new SaveGameAggregateStats(
                GameId: gameId,
                BranchCount: reader.GetInt32(1),
                SnapshotCount: reader.GetInt32(2),
                PinnedCount: reader.GetInt32(3),
                TotalRawBytes: reader.GetInt64(4),
                TotalStoredBytes: reader.GetInt64(5),
                LastSnapshotAtUtc: reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)),
                UnimportedLegacyPreTrainerCount: reader.GetInt32(7));
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // cas_chunks CRUD & Deduplication Stats
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers a newly written CAS chunk if absent (<c>ref_count = 0</c> until committed by a snapshot).
    /// </summary>
    public async Task RegisterCasChunkIfAbsentAsync(
        string chunkBlake3,
        long rawSizeBytes,
        long compressedSizeBytes,
        DateTimeOffset createdAtUtc,
        string? packId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkBlake3);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        string hash = chunkBlake3.Trim().ToLowerInvariant();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cas_chunks (
                    chunk_blake3, raw_size_bytes, compressed_size_bytes,
                    ref_count, pack_id, created_at_utc, last_unreferenced_at_utc
                ) VALUES (
                    $hash, $raw_size, $compressed_size,
                    0, $pack_id, $created_at_utc, $created_at_utc
                )
                ON CONFLICT(chunk_blake3) DO UPDATE SET
                    pack_id = COALESCE(cas_chunks.pack_id, excluded.pack_id),
                    last_unreferenced_at_utc = CASE
                        WHEN cas_chunks.ref_count <= 0 THEN excluded.last_unreferenced_at_utc
                        ELSE cas_chunks.last_unreferenced_at_utc
                    END;
                """;
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$raw_size", Math.Max(0, rawSizeBytes));
            cmd.Parameters.AddWithValue("$compressed_size", Math.Max(0, compressedSizeBytes));
            cmd.Parameters.AddWithValue("$pack_id", (object?)packId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at_utc", FormatUtc(createdAtUtc));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CasChunkRecord?> GetCasChunkByHashAsync(
        string chunkBlake3,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chunkBlake3))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM cas_chunks WHERE chunk_blake3 = $hash LIMIT 1;";
        cmd.Parameters.AddWithValue("$hash", chunkBlake3.Trim().ToLowerInvariant());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapCasChunkRecord(reader)
            : null;
    }

    public async Task<IReadOnlyDictionary<string, CasChunkRecord>> GetCasChunksByHashesAsync(
        IEnumerable<string> chunkHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkHashes);
        string[] normalized = chunkHashes
            .Where(static h => !string.IsNullOrWhiteSpace(h))
            .Select(static h => h.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return new Dictionary<string, CasChunkRecord>(StringComparer.Ordinal);
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, CasChunkRecord>(normalized.Length, StringComparer.Ordinal);

        const int batchSize = 400;
        for (int offset = 0; offset < normalized.Length; offset += batchSize)
        {
            int count = Math.Min(batchSize, normalized.Length - offset);
            await using var cmd = conn.CreateCommand();
            var placeholders = new string[count];
            for (int i = 0; i < count; i++)
            {
                string param = $"$h{i}";
                placeholders[i] = param;
                cmd.Parameters.AddWithValue(param, normalized[offset + i]);
            }

            cmd.CommandText = $"SELECT * FROM cas_chunks WHERE chunk_blake3 IN ({string.Join(", ", placeholders)});";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                CasChunkRecord rec = MapCasChunkRecord(reader);
                map[rec.ChunkBlake3] = rec;
            }
        }

        return map;
    }

    public async Task<int> UpdateCasChunkPackIdsAsync(
        IEnumerable<string> chunkHashes,
        string packId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkHashes);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        string[] normalized = chunkHashes
            .Where(static h => !string.IsNullOrWhiteSpace(h))
            .Select(static h => h.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            int total = 0;

            foreach (string hash in normalized)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE cas_chunks SET pack_id = $pack_id WHERE chunk_blake3 = $hash;";
                cmd.Parameters.AddWithValue("$pack_id", packId);
                cmd.Parameters.AddWithValue("$hash", hash);
                total += await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return total;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<CasChunkRecord>> GetUnreferencedCasChunksBeforeAsync(
        DateTimeOffset unreferencedBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM cas_chunks
            WHERE ref_count <= 0
              AND (last_unreferenced_at_utc IS NULL OR last_unreferenced_at_utc <= $cutoff)
            ORDER BY created_at_utc ASC;
            """;
        cmd.Parameters.AddWithValue("$cutoff", FormatUtc(unreferencedBeforeUtc));

        var list = new List<CasChunkRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapCasChunkRecord(reader));
        }

        return list;
    }

    public async Task<int> DeleteUnreferencedCasChunksAsync(
        IEnumerable<string> chunkHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkHashes);
        string[] normalized = chunkHashes
            .Where(static h => !string.IsNullOrWhiteSpace(h))
            .Select(static h => h.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            int deleted = 0;

            foreach (string hash in normalized)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM cas_chunks WHERE chunk_blake3 = $hash AND ref_count <= 0;";
                cmd.Parameters.AddWithValue("$hash", hash);
                deleted += await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CasVaultStatsDto> GetCasVaultStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) AS total_count,
                COALESCE(SUM(CASE WHEN ref_count > 0 THEN 1 ELSE 0 END), 0) AS ref_count,
                COALESCE(SUM(CASE WHEN ref_count <= 0 THEN 1 ELSE 0 END), 0) AS unref_count,
                COALESCE(SUM(raw_size_bytes), 0) AS total_raw,
                COALESCE(SUM(compressed_size_bytes), 0) AS total_compressed,
                (SELECT COALESCE(SUM(total_raw_bytes), 0) FROM save_snapshots) AS snap_raw
            FROM cas_chunks;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new CasVaultStatsDto();
        }

        return new CasVaultStatsDto
        {
            TotalChunkCount = reader.GetInt32(0),
            ReferencedChunkCount = reader.GetInt32(1),
            UnreferencedChunkCount = reader.GetInt32(2),
            TotalRawBytes = reader.GetInt64(3),
            TotalCompressedBytes = reader.GetInt64(4),
            TotalSnapshotRawBytes = reader.GetInt64(5),
        };
    }

    public async Task<long> GetGameUniqueChunkStoredBytesAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var uniqueChunks = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT sf.chunk_sequence_json
                FROM snapshot_files sf
                INNER JOIN save_snapshots ss ON ss.snapshot_id = sf.snapshot_id
                WHERE ss.game_id = $game_id;
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (string h in ParseStringArrayJson(reader.GetString(0)))
                {
                    uniqueChunks.Add(h.Trim().ToLowerInvariant());
                }
            }
        }

        if (uniqueChunks.Count == 0)
        {
            return 0;
        }

        IReadOnlyDictionary<string, CasChunkRecord> chunks = await GetCasChunksByHashesAsync(uniqueChunks, cancellationToken)
            .ConfigureAwait(false);
        long total = 0;
        foreach (CasChunkRecord rec in chunks.Values)
        {
            total += rec.CompressedSizeBytes;
        }

        return total;
    }

    // -------------------------------------------------------------------------
    // ludusavi_rules CRUD & Fast Lookup (< 1ms indexed)
    // -------------------------------------------------------------------------

    public async Task<int> UpsertLudusaviRulesBatchAsync(
        IReadOnlyList<LudusaviRuleRecord> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            int upserted = 0;

            foreach (LudusaviRuleRecord rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.RuleName))
                {
                    continue;
                }

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO ludusavi_rules (
                        rule_name, steam_app_id, gog_id, aliases_json,
                        save_paths_json, config_paths_json, registry_rules_json, updated_at_utc
                    ) VALUES (
                        $rule_name, $steam_app_id, $gog_id, $aliases_json,
                        $save_paths_json, $config_paths_json, $registry_rules_json, $updated_at_utc
                    )
                    ON CONFLICT(rule_name) DO UPDATE SET
                        steam_app_id = excluded.steam_app_id,
                        gog_id = excluded.gog_id,
                        aliases_json = excluded.aliases_json,
                        save_paths_json = excluded.save_paths_json,
                        config_paths_json = excluded.config_paths_json,
                        registry_rules_json = excluded.registry_rules_json,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                cmd.Parameters.AddWithValue("$rule_name", rule.RuleName.Trim());
                cmd.Parameters.AddWithValue("$steam_app_id", rule.SteamAppId.HasValue ? rule.SteamAppId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$gog_id", string.IsNullOrWhiteSpace(rule.GogId) ? DBNull.Value : rule.GogId.Trim());
                cmd.Parameters.AddWithValue("$aliases_json", JsonSerializer.Serialize(rule.Aliases, JsonOptions));
                cmd.Parameters.AddWithValue("$save_paths_json", JsonSerializer.Serialize(rule.SavePaths, JsonOptions));
                cmd.Parameters.AddWithValue("$config_paths_json", JsonSerializer.Serialize(rule.ConfigPaths, JsonOptions));
                cmd.Parameters.AddWithValue("$registry_rules_json", JsonSerializer.Serialize(rule.RegistryRules, JsonOptions));
                cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(rule.UpdatedAtUtc));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                upserted++;
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return upserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> GetLudusaviRuleCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ludusavi_rules;";
        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is long v ? (int)v : Convert.ToInt32(scalar ?? 0, CultureInfo.InvariantCulture);
    }

    public async Task<LudusaviRuleRecord?> FindLudusaviRuleAsync(
        int? steamAppId = null,
        string? gogId = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (steamAppId is > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM ludusavi_rules WHERE steam_app_id = $steam_id LIMIT 1;";
            cmd.Parameters.AddWithValue("$steam_id", steamAppId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return MapLudusaviRuleRecord(reader);
            }
        }

        if (!string.IsNullOrWhiteSpace(gogId))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM ludusavi_rules WHERE gog_id = $gog_id LIMIT 1;";
            cmd.Parameters.AddWithValue("$gog_id", gogId.Trim());
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return MapLudusaviRuleRecord(reader);
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            string trimmedTitle = title.Trim();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM ludusavi_rules WHERE rule_name = $name COLLATE NOCASE LIMIT 1;";
                cmd.Parameters.AddWithValue("$name", trimmedTitle);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return MapLudusaviRuleRecord(reader);
                }
            }

            // Fallback alias scan (case-insensitive exact match within aliases_json).
            await using (var aliasCmd = conn.CreateCommand())
            {
                aliasCmd.CommandText = "SELECT * FROM ludusavi_rules WHERE aliases_json <> '[]';";
                await using var reader = await aliasCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    LudusaviRuleRecord candidate = MapLudusaviRuleRecord(reader);
                    if (candidate.Aliases.Any(a => string.Equals(a, trimmedTitle, StringComparison.OrdinalIgnoreCase)))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // cloud_sync_endpoints CRUD
    // -------------------------------------------------------------------------

    public async Task UpsertCloudSyncEndpointAsync(
        CloudSyncEndpointRecord endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.EndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.EndpointUrl);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (endpoint.IsDefault)
            {
                await using var clearCmd = conn.CreateCommand();
                clearCmd.Transaction = tx;
                clearCmd.CommandText = "UPDATE cloud_sync_endpoints SET is_default = 0 WHERE endpoint_id <> $endpoint_id;";
                clearCmd.Parameters.AddWithValue("$endpoint_id", endpoint.EndpointId);
                await clearCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO cloud_sync_endpoints (
                        endpoint_id, display_name, provider_type, endpoint_url,
                        bucket_or_root, region, path_style_addressing, secret_credential_id,
                        is_default, created_at_utc, updated_at_utc
                    ) VALUES (
                        $endpoint_id, $display_name, $provider_type, $endpoint_url,
                        $bucket_or_root, $region, $path_style_addressing, $secret_credential_id,
                        $is_default, $created_at_utc, $updated_at_utc
                    )
                    ON CONFLICT(endpoint_id) DO UPDATE SET
                        display_name = excluded.display_name,
                        provider_type = excluded.provider_type,
                        endpoint_url = excluded.endpoint_url,
                        bucket_or_root = excluded.bucket_or_root,
                        region = excluded.region,
                        path_style_addressing = excluded.path_style_addressing,
                        secret_credential_id = excluded.secret_credential_id,
                        is_default = excluded.is_default,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                cmd.Parameters.AddWithValue("$endpoint_id", endpoint.EndpointId);
                cmd.Parameters.AddWithValue("$display_name", endpoint.DisplayName.Trim());
                cmd.Parameters.AddWithValue("$provider_type", endpoint.ProviderType.ToString());
                cmd.Parameters.AddWithValue("$endpoint_url", endpoint.EndpointUrl.Trim());
                cmd.Parameters.AddWithValue("$bucket_or_root", endpoint.BucketOrRoot?.Trim() ?? string.Empty);
                cmd.Parameters.AddWithValue("$region", string.IsNullOrWhiteSpace(endpoint.Region) ? DBNull.Value : endpoint.Region.Trim());
                cmd.Parameters.AddWithValue("$path_style_addressing", endpoint.PathStyleAddressing ? 1 : 0);
                cmd.Parameters.AddWithValue("$secret_credential_id", string.IsNullOrWhiteSpace(endpoint.SecretCredentialId) ? DBNull.Value : endpoint.SecretCredentialId.Trim());
                cmd.Parameters.AddWithValue("$is_default", endpoint.IsDefault ? 1 : 0);
                cmd.Parameters.AddWithValue("$created_at_utc", FormatUtc(endpoint.CreatedAtUtc));
                cmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(endpoint.UpdatedAtUtc));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CloudSyncEndpointRecord?> GetCloudSyncEndpointByIdAsync(
        string endpointId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM cloud_sync_endpoints WHERE endpoint_id = $endpoint_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$endpoint_id", endpointId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapCloudSyncEndpointRecord(reader)
            : null;
    }

    public async Task<CloudSyncEndpointRecord?> GetDefaultCloudSyncEndpointAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM cloud_sync_endpoints
            ORDER BY is_default DESC, created_at_utc ASC
            LIMIT 1;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapCloudSyncEndpointRecord(reader)
            : null;
    }

    public async Task<IReadOnlyList<CloudSyncEndpointRecord>> GetAllCloudSyncEndpointsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM cloud_sync_endpoints ORDER BY is_default DESC, display_name COLLATE NOCASE ASC;";

        var list = new List<CloudSyncEndpointRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapCloudSyncEndpointRecord(reader));
        }

        return list;
    }

    public async Task<bool> DeleteCloudSyncEndpointAsync(
        string endpointId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cloud_sync_endpoints WHERE endpoint_id = $endpoint_id;";
            cmd.Parameters.AddWithValue("$endpoint_id", endpointId);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetDefaultCloudSyncEndpointAsync(
        string endpointId,
        DateTimeOffset? updatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            int updated;
            await using (var setCmd = conn.CreateCommand())
            {
                setCmd.Transaction = tx;
                setCmd.CommandText = """
                    UPDATE cloud_sync_endpoints
                    SET is_default = 1,
                        updated_at_utc = $updated_at_utc
                    WHERE endpoint_id = $endpoint_id;
                    """;
                setCmd.Parameters.AddWithValue("$endpoint_id", endpointId);
                setCmd.Parameters.AddWithValue("$updated_at_utc", FormatUtc(updatedAtUtc ?? DateTimeOffset.UtcNow));
                updated = await setCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (updated == 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using (var clearCmd = conn.CreateCommand())
            {
                clearCmd.Transaction = tx;
                clearCmd.CommandText = "UPDATE cloud_sync_endpoints SET is_default = 0 WHERE endpoint_id <> $endpoint_id;";
                clearCmd.Parameters.AddWithValue("$endpoint_id", endpointId);
                await clearCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Global SaveSettingsDto Persistence (app_settings)
    // -------------------------------------------------------------------------

    public async Task<SaveSettingsDto> GetSaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT setting_key, setting_value FROM app_settings WHERE setting_key LIKE 'saves.%';";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                map[reader.GetString(0)] = reader.GetString(1);
            }
        }

        string machineId = map.TryGetValue(AppSettingKeys.SaveMachineId, out string? existingMachineId)
                           && !string.IsNullOrWhiteSpace(existingMachineId)
            ? existingMachineId.Trim()
            : string.Empty;

        if (string.IsNullOrEmpty(machineId))
        {
            Span<byte> rand = stackalloc byte[8];
            RandomNumberGenerator.Fill(rand);
            machineId = $"pc-{Convert.ToHexStringLower(rand)}";
            await SetSettingAsync(AppSettingKeys.SaveMachineId, machineId, cancellationToken).ConfigureAwait(false);
        }

        string machineDisplayName = map.TryGetValue(AppSettingKeys.SaveMachineDisplayName, out string? rawName)
                                    && !string.IsNullOrWhiteSpace(rawName)
            ? rawName.Trim()
            : Environment.MachineName;

        bool autoWatch = !map.TryGetValue(AppSettingKeys.SaveAutoWatchByDefault, out string? rawAutoWatch)
                         || !bool.TryParse(rawAutoWatch, out bool parsedAutoWatch)
                         || parsedAutoWatch;

        bool postExit = !map.TryGetValue(AppSettingKeys.SavePostExitSnapshotByDefault, out string? rawPostExit)
                        || !bool.TryParse(rawPostExit, out bool parsedPostExit)
                        || parsedPostExit;

        int debounceMs = map.TryGetValue(AppSettingKeys.SaveQuiescentDebounceMs, out string? rawDebounce)
                         && int.TryParse(rawDebounce, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDebounce)
            ? Math.Clamp(parsedDebounce, SaveSettingsDto.MinQuiescentDebounceMs, SaveSettingsDto.MaxQuiescentDebounceMs)
            : SaveSettingsDto.DefaultQuiescentDebounceMs;

        int maxRolling = map.TryGetValue(AppSettingKeys.SaveDefaultMaxRollingSnapshots, out string? rawRolling)
                         && int.TryParse(rawRolling, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRolling)
            ? Math.Clamp(parsedRolling, 1, 500)
            : SaveSettingsDto.DefaultRollingRetentionCount;

        int maxSizeMb = map.TryGetValue(AppSettingKeys.SaveDefaultMaxSnapshotSizeMb, out string? rawMaxSize)
                        && int.TryParse(rawMaxSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMaxSize)
            ? Math.Clamp(parsedMaxSize, 16, 8192)
            : SaveSettingsDto.DefaultSnapshotMaxSizeMb;

        bool includeConfig = map.TryGetValue(AppSettingKeys.SaveIncludeConfigFilesByDefault, out string? rawConfig)
                             && bool.TryParse(rawConfig, out bool parsedConfig)
                             && parsedConfig;

        int gcGraceHours = map.TryGetValue(AppSettingKeys.SaveGcGracePeriodHours, out string? rawGc)
                           && int.TryParse(rawGc, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedGc)
            ? Math.Clamp(parsedGc, 0, 720)
            : SaveSettingsDto.DefaultGcGracePeriodHours;

        bool autoCloudPush = map.TryGetValue(SaveAutoCloudPushOnGameExitSettingKey, out string? rawAutoPush)
                             && bool.TryParse(rawAutoPush, out bool parsedAutoPush)
                             && parsedAutoPush;

        bool checkRemoteBeforeLaunch = map.TryGetValue(SaveCheckRemoteUpdatesBeforeLaunchSettingKey, out string? rawCheckRemote)
                                       && bool.TryParse(rawCheckRemote, out bool parsedCheckRemote)
                                       && parsedCheckRemote;

        return new SaveSettingsDto
        {
            AutoWatchByDefault = autoWatch,
            PostExitSnapshotByDefault = postExit,
            AutoCloudPushOnGameExit = autoCloudPush,
            CheckRemoteUpdatesBeforeLaunch = checkRemoteBeforeLaunch,
            QuiescentDebounceMs = debounceMs,
            DefaultMaxRollingSnapshots = maxRolling,
            DefaultMaxSnapshotSizeMb = maxSizeMb,
            IncludeConfigFilesByDefault = includeConfig,
            GcGracePeriodHours = gcGraceHours,
            MachineId = machineId,
            MachineDisplayName = machineDisplayName,
        };
    }

    public async Task SaveSaveSettingsAsync(SaveSettingsDto settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.MachineId))
        {
            await SetSettingAsync(AppSettingKeys.SaveMachineId, settings.MachineId.Trim(), cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(settings.MachineDisplayName))
        {
            await SetSettingAsync(AppSettingKeys.SaveMachineDisplayName, settings.MachineDisplayName.Trim(), cancellationToken).ConfigureAwait(false);
        }

        await SetSettingAsync(AppSettingKeys.SaveAutoWatchByDefault, settings.AutoWatchByDefault ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(AppSettingKeys.SavePostExitSnapshotByDefault, settings.PostExitSnapshotByDefault ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(SaveAutoCloudPushOnGameExitSettingKey, settings.AutoCloudPushOnGameExit ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(SaveCheckRemoteUpdatesBeforeLaunchSettingKey, settings.CheckRemoteUpdatesBeforeLaunch ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(
            AppSettingKeys.SaveQuiescentDebounceMs,
            Math.Clamp(settings.QuiescentDebounceMs, SaveSettingsDto.MinQuiescentDebounceMs, SaveSettingsDto.MaxQuiescentDebounceMs)
                .ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(
            AppSettingKeys.SaveDefaultMaxRollingSnapshots,
            Math.Clamp(settings.DefaultMaxRollingSnapshots, 1, 500).ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(
            AppSettingKeys.SaveDefaultMaxSnapshotSizeMb,
            Math.Clamp(settings.DefaultMaxSnapshotSizeMb, 16, 8192).ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(AppSettingKeys.SaveIncludeConfigFilesByDefault, settings.IncludeConfigFilesByDefault ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await SetSettingAsync(
            AppSettingKeys.SaveGcGracePeriodHours,
            Math.Clamp(settings.GcGracePeriodHours, 0, 720).ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Internal Helpers & Record Mappers
    // -------------------------------------------------------------------------

    private static async Task<SaveBranchRecord?> FindDefaultOrMainBranchCoreAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string gameId,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT b.*,
                   (SELECT COUNT(*) FROM save_snapshots s WHERE s.branch_id = b.branch_id) AS snapshot_count
            FROM save_branches b
            WHERE b.game_id = $game_id
            ORDER BY b.is_default DESC, b.created_at_utc ASC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapSaveBranchRecord(reader)
            : null;
    }

    private static async Task<Dictionary<string, int>> CollectUniqueChunksPerSnapshotsCoreAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<string> snapshotIds,
        CancellationToken cancellationToken)
    {
        var decrementCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string snapId in snapshotIds)
        {
            var uniqueInSnap = new HashSet<string>(StringComparer.Ordinal);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT chunk_sequence_json FROM snapshot_files WHERE snapshot_id = $snapshot_id;";
            cmd.Parameters.AddWithValue("$snapshot_id", snapId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (string hash in ParseStringArrayJson(reader.GetString(0)))
                {
                    if (!string.IsNullOrWhiteSpace(hash))
                    {
                        uniqueInSnap.Add(hash.Trim().ToLowerInvariant());
                    }
                }
            }

            foreach (string hash in uniqueInSnap)
            {
                decrementCounts[hash] = decrementCounts.TryGetValue(hash, out int current) ? current + 1 : 1;
            }
        }

        return decrementCounts;
    }

    private static async Task<int> DecrementChunkRefCountsCoreAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        Dictionary<string, int> decrementCounts,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        int newlyUnreferenced = 0;
        string nowStr = FormatUtc(timestamp);

        foreach (var (hash, dec) in decrementCounts)
        {
            await using var updCmd = conn.CreateCommand();
            updCmd.Transaction = tx;
            updCmd.CommandText = """
                UPDATE cas_chunks
                SET ref_count = MAX(0, ref_count - $dec),
                    last_unreferenced_at_utc = CASE
                        WHEN MAX(0, ref_count - $dec) = 0 THEN COALESCE(last_unreferenced_at_utc, $now)
                        ELSE last_unreferenced_at_utc
                    END
                WHERE chunk_blake3 = $hash;
                """;
            updCmd.Parameters.AddWithValue("$dec", dec);
            updCmd.Parameters.AddWithValue("$now", nowStr);
            updCmd.Parameters.AddWithValue("$hash", hash);
            await updCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var chkCmd = conn.CreateCommand();
            chkCmd.Transaction = tx;
            chkCmd.CommandText = "SELECT ref_count FROM cas_chunks WHERE chunk_blake3 = $hash LIMIT 1;";
            chkCmd.Parameters.AddWithValue("$hash", hash);
            object? scalar = await chkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is long rc && rc <= 0)
            {
                newlyUnreferenced++;
            }
        }

        return newlyUnreferenced;
    }

    private static SaveBranchRecord MapSaveBranchRecord(SqliteDataReader reader) =>
        new()
        {
            BranchId = reader.GetString(reader.GetOrdinal("branch_id")),
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Description = GetNullableString(reader, "description"),
            ColorHex = reader.GetString(reader.GetOrdinal("color_hex")),
            HeadSnapshotId = GetNullableString(reader, "head_snapshot_id"),
            BaseSnapshotId = GetNullableString(reader, "base_snapshot_id"),
            IsDefault = reader.GetInt32(reader.GetOrdinal("is_default")) != 0,
            IsArchived = reader.GetInt32(reader.GetOrdinal("is_archived")) != 0,
            CreatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            UpdatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
            SnapshotCount = TryGetInt32Column(reader, "snapshot_count"),
        };

    private static SaveGameStateRecord MapSaveGameStateRecord(SqliteDataReader reader) =>
        new()
        {
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            ActiveBranchId = GetNullableString(reader, "active_branch_id"),
            CurrentHeadSnapshotId = GetNullableString(reader, "current_head_snapshot_id"),
            AutoWatchEnabled = reader.GetInt32(reader.GetOrdinal("auto_watch_enabled")) != 0,
            PostExitSnapshotEnabled = reader.GetInt32(reader.GetOrdinal("post_exit_snapshot_enabled")) != 0,
            IncludeConfigFiles = reader.GetInt32(reader.GetOrdinal("include_config_files")) != 0,
            MaxRollingSnapshots = reader.GetInt32(reader.GetOrdinal("max_rolling_snapshots")),
            CloudEndpointId = GetNullableString(reader, "cloud_endpoint_id"),
            CloudGameKey = TryGetStringColumn(reader, "cloud_game_key"),
            SyncAllBranchesToCloud = TryGetInt32Column(reader, "sync_all_branches") != 0,
            LastSyncedAtUtc = ParseNullableUtc(reader, "last_synced_at_utc"),
            HasUnresolvedCloudConflict = reader.GetInt32(reader.GetOrdinal("has_unresolved_cloud_conflict")) != 0,
            UpdatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
        };

    private static SaveCustomLocationRecord MapSaveCustomLocationRecord(SqliteDataReader reader) =>
        new()
        {
            LocationId = reader.GetString(reader.GetOrdinal("location_id")),
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            PathOrRegistryKey = reader.GetString(reader.GetOrdinal("path_or_registry_key")),
            LocationKind = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("location_kind")), SaveCustomLocationKind.Directory),
            Category = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("category")), SaveFileCategory.Save),
            Label = GetNullableString(reader, "label"),
            CreatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_at_utc"))),
        };

    private static SaveSnapshotRecord MapSaveSnapshotRecord(SqliteDataReader reader) =>
        new()
        {
            SnapshotId = reader.GetString(reader.GetOrdinal("snapshot_id")),
            GameId = reader.GetString(reader.GetOrdinal("game_id")),
            BranchId = reader.GetString(reader.GetOrdinal("branch_id")),
            ParentSnapshotId = GetNullableString(reader, "parent_snapshot_id"),
            MergeParentSnapshotId = GetNullableString(reader, "merge_parent_snapshot_id"),
            ProfileId = GetNullableString(reader, "profile_id"),
            ModProfileHash = reader.GetString(reader.GetOrdinal("mod_profile_hash")),
            TriggerKind = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("trigger_kind")), SaveSnapshotTriggerKind.Manual),
            Note = GetNullableString(reader, "note"),
            FileCount = reader.GetInt32(reader.GetOrdinal("file_count")),
            RegistryKeyCount = reader.GetInt32(reader.GetOrdinal("registry_key_count")),
            TotalRawBytes = reader.GetInt64(reader.GetOrdinal("total_raw_bytes")),
            TotalStoredBytes = reader.GetInt64(reader.GetOrdinal("total_stored_bytes")),
            CreatedMachineId = reader.GetString(reader.GetOrdinal("created_machine_id")),
            PlaytimeActiveSeconds = reader.GetInt64(reader.GetOrdinal("playtime_active_seconds")),
            CreatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            IsPinned = reader.GetInt32(reader.GetOrdinal("is_pinned")) != 0,
            LegacyPreTrainerSnapshotId = GetNullableString(reader, "legacy_pre_trainer_snapshot_id"),
        };

    private static SaveSnapshotFileRecord MapSaveSnapshotFileRecord(SqliteDataReader reader) =>
        new()
        {
            SnapshotId = reader.GetString(reader.GetOrdinal("snapshot_id")),
            LogicalPath = reader.GetString(reader.GetOrdinal("logical_path")),
            OriginalPath = reader.GetString(reader.GetOrdinal("original_path")),
            Category = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("category")), SaveFileCategory.Save),
            FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
            LastWriteTimeUtc = ParseUtc(reader.GetString(reader.GetOrdinal("last_write_time_utc"))),
            FileBlake3 = reader.GetString(reader.GetOrdinal("file_blake3")),
            ChunkHashes = ParseStringArrayJson(reader.GetString(reader.GetOrdinal("chunk_sequence_json"))),
        };

    private static CasChunkRecord MapCasChunkRecord(SqliteDataReader reader) =>
        new()
        {
            ChunkBlake3 = reader.GetString(reader.GetOrdinal("chunk_blake3")),
            RawSizeBytes = reader.GetInt64(reader.GetOrdinal("raw_size_bytes")),
            CompressedSizeBytes = reader.GetInt64(reader.GetOrdinal("compressed_size_bytes")),
            RefCount = reader.GetInt32(reader.GetOrdinal("ref_count")),
            PackId = GetNullableString(reader, "pack_id"),
            CreatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            LastUnreferencedAtUtc = ParseNullableUtc(reader, "last_unreferenced_at_utc"),
        };

    private static LudusaviRuleRecord MapLudusaviRuleRecord(SqliteDataReader reader) =>
        new()
        {
            RuleName = reader.GetString(reader.GetOrdinal("rule_name")),
            SteamAppId = GetNullableInt32(reader, "steam_app_id"),
            GogId = GetNullableString(reader, "gog_id"),
            Aliases = ParseStringArrayJson(reader.GetString(reader.GetOrdinal("aliases_json"))),
            SavePaths = ParseStringArrayJson(reader.GetString(reader.GetOrdinal("save_paths_json"))),
            ConfigPaths = ParseStringArrayJson(reader.GetString(reader.GetOrdinal("config_paths_json"))),
            RegistryRules = ParseStringArrayJson(reader.GetString(reader.GetOrdinal("registry_rules_json"))),
            UpdatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
        };

    private static CloudSyncEndpointRecord MapCloudSyncEndpointRecord(SqliteDataReader reader)
    {
        string? secretId = GetNullableString(reader, "secret_credential_id");
        return new CloudSyncEndpointRecord
        {
            EndpointId = reader.GetString(reader.GetOrdinal("endpoint_id")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            ProviderType = ParseEnumOrDefault(reader.GetString(reader.GetOrdinal("provider_type")), CloudSyncProviderType.LocalFolder),
            EndpointUrl = reader.GetString(reader.GetOrdinal("endpoint_url")),
            BucketOrRoot = reader.GetString(reader.GetOrdinal("bucket_or_root")),
            Region = GetNullableString(reader, "region"),
            PathStyleAddressing = reader.GetInt32(reader.GetOrdinal("path_style_addressing")) != 0,
            SecretCredentialId = secretId,
            IsDefault = reader.GetInt32(reader.GetOrdinal("is_default")) != 0,
            HasSecretConfigured = !string.IsNullOrWhiteSpace(secretId),
            CreatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            UpdatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
        };
    }

    private static string[] ParseStringArrayJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int TryGetInt32Column(SqliteDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            }
        }

        return 0;
    }

    private static string? TryGetStringColumn(SqliteDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return reader.IsDBNull(i) ? null : reader.GetString(i);
            }
        }

        return null;
    }
}
