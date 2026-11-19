using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Achievement;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class AchievementFormatsAndCometTests
{
    [Fact]
    public void AchievementParsers_GoldbergAndGbeForkObjectArrayAndConfigsUserIni_ParseAccurately()
    {
        // 1. gbe_fork / Goldberg player unlock JSON Object (with UTF-8 BOM)
        byte[] gbeObjectBytes =
        [
            0xEF, 0xBB, 0xBF,
            .. """
            {
              "ACH_1": { "earned": true, "earned_time": 1730000000, "progress": 10, "max_progress": 10 },
              "ACH_2": { "earned": false, "earned_time": 0, "progress": 3, "max_progress": 10 }
            }
            """u8.ToArray(),
        ];

        var parsedUnlocks = SteamEmulatorFormatParsers.ParseGoldbergOrGbeForkJson(gbeObjectBytes);
        Assert.Equal(2, parsedUnlocks.Count);
        Assert.True(parsedUnlocks[0].IsUnlocked);
        Assert.Equal("ACH_1", parsedUnlocks[0].ApiName);
        Assert.Equal(1730000000L, parsedUnlocks[0].UnlockedUtc!.Value.ToUnixTimeSeconds());
        Assert.False(parsedUnlocks[1].IsUnlocked);
        Assert.Equal(3.0, parsedUnlocks[1].CurrentProgress);

        // 2. gbe_fork steam_settings/achievements.json Array MUST return empty from unlock parser (not false unlocks!),
        //    and parse localized schema definitions cleanly via ParseLocalGbeSchemaJson.
        byte[] gbeSchemaArrayBytes = """
            [
              {
                "name": "ACH_1",
                "displayName": { "english": "Wrong Way", "schinese": "方向错误" },
                "description": { "english": "That's not right", "schinese": "这是不对的。" },
                "hidden": 0
              },
              {
                "name": "ACH_13",
                "displayName": { "english": "DING!", "schinese": "叮当声！" },
                "description": { "schinese": "逃跑是没用的" },
                "hidden": "1"
              }
            ]
            """u8.ToArray();

        Assert.Empty(SteamEmulatorFormatParsers.ParseGoldbergOrGbeForkJson(gbeSchemaArrayBytes));

        var parsedSchema = SteamEmulatorFormatParsers.ParseLocalGbeSchemaJson(gbeSchemaArrayBytes, preferredLanguage: "schinese");
        Assert.Equal(2, parsedSchema.Count);
        Assert.Equal("方向错误", parsedSchema[0].DisplayName);
        Assert.True(parsedSchema[1].IsHidden);
        Assert.Equal("逃跑是没用的", parsedSchema[1].HiddenSpoilerDescription);

        // 3. configs.user.ini custom save folder & local_save_path override
        var (folderName, localPath) = SteamEmulatorFormatParsers.ParseGbeConfigsUserIni("""
            [user::saves]
            saves_folder_name = "Custom GSE Vault"
            local_save_path = "./PortableSaves"
            """);
        Assert.Equal("Custom GSE Vault", folderName);
        Assert.Equal("./PortableSaves", localPath);
    }

    [Fact]
    public void AchievementParsers_RuneCodexEmpressTenokeOnlineFixAli213ThreeDmSkidrowCreamApi_ParseAccurately()
    {
        // 4. RUNE / CODEX / PLAZA (must skip [SteamAchievements] header index!)
        var runeEntries = SteamEmulatorFormatParsers.ParseCodexRunePlazaIni("""
            [SteamAchievements]
            Count=2
            00000=ACH_BOSS_1
            00001=ACH_SECRET_2

            [ACH_BOSS_1]
            Achieved=1
            CurProgress=1
            MaxProgress=1
            UnlockTime=1710000500

            [ACH_SECRET_2]
            Achieved=0
            UnlockTime=0
            """);
        Assert.Equal(2, runeEntries.Count);
        Assert.DoesNotContain(runeEntries, e => e.ApiName.Equals("SteamAchievements", StringComparison.OrdinalIgnoreCase));
        Assert.True(runeEntries[0].IsUnlocked);
        Assert.Equal(1710000500L, runeEntries[0].UnlockedUtc!.Value.ToUnixTimeSeconds());
        Assert.False(runeEntries[1].IsUnlocked);

        // 5. EMPRESS JSON & INI via ParseBytesByFlavor
        var empressEntries = SteamEmulatorAchievementProvider.ParseBytesByFlavor(
            EmulatorFlavor.Empress,
            "achievements.json",
            """{"ACH_EMP_01": {"earned": true, "earned_time": 1720000111}}"""u8);
        Assert.Single(empressEntries);
        Assert.True(empressEntries[0].IsUnlocked);

        // 6. TENOKE dual format (modern TOML inline table + legacy boolean INI)
        var tenokeEntries = SteamEmulatorFormatParsers.ParseTenokeIni("""
            [ACHIEVEMENTS]
            "ACH_TENOKE_NEW" = {unlocked = true, time = 1712345678, progress = 50}
            "ACH_TENOKE_LOCKED" = {unlocked = false, time = 0}
            ACH_TENOKE_LEGACY = true
            """);
        Assert.Equal(3, tenokeEntries.Count);
        Assert.True(tenokeEntries[0].IsUnlocked);
        Assert.Equal(1712345678L, tenokeEntries[0].UnlockedUtc!.Value.ToUnixTimeSeconds());
        Assert.False(tenokeEntries[1].IsUnlocked);
        Assert.True(tenokeEntries[2].IsUnlocked);

        // 7. Online-Fix (lowercase achieved=true + timestamp)
        var onlineFixEntries = SteamEmulatorFormatParsers.ParseOnlineFixIni("""
            [ACH_COOP_WIN]
            achieved=true
            timestamp=1715000999

            [ACH_COOP_FAIL]
            achieved=false
            timestamp=0
            """);
        Assert.Equal(2, onlineFixEntries.Count);
        Assert.True(onlineFixEntries[0].IsUnlocked);
        Assert.Equal(1715000999L, onlineFixEntries[0].UnlockedUtc!.Value.ToUnixTimeSeconds());
        Assert.False(onlineFixEntries[1].IsUnlocked);

        // 8. ALI213 disguised .Bin UTF-8 BOM text INI (both [ACH] HaveAchieved=1 and flat [Achievements] ACH=1)
        byte[] ali213Bytes =
        [
            0xEF, 0xBB, 0xBF,
            .. """
            [ACH_ALI_SECTION]
            HaveAchieved=1
            HaveAchievedTime=1709998877

            [Achievements]
            ACH_ALI_FLAT=1
            ACH_ALI_LOCKED=0
            """u8.ToArray(),
        ];
        var aliEntries = SteamEmulatorFormatParsers.ParseAli213BinIni(ali213Bytes);
        Assert.Equal(3, aliEntries.Count);
        Assert.Contains(aliEntries, e => e.ApiName == "ACH_ALI_SECTION" && e.IsUnlocked && e.UnlockedUtc!.Value.ToUnixTimeSeconds() == 1709998877L);
        Assert.Contains(aliEntries, e => e.ApiName == "ACH_ALI_FLAT" && e.IsUnlocked);
        Assert.Contains(aliEntries, e => e.ApiName == "ACH_ALI_LOCKED" && !e.IsUnlocked);

        // 9. 3DM & RLD! 8-char Little-Endian Hex timestamp ("80513b65" -> LE uint32 0x653b5180 = 1698386304)
        var threeDmEntries = SteamEmulatorFormatParsers.ParseThreeDmAndReloadedIni("""
            [State]
            ACH_3DM_SPLIT=0101
            ACH_3DM_LOCKED=0000

            [Time]
            ACH_3DM_SPLIT=80513b65

            [ACH_3DM_SECTION]
            State=01000000
            Time=80513b65
            """);
        Assert.Equal(3, threeDmEntries.Count);
        var splitItem = Assert.Single(threeDmEntries, e => e.ApiName == "ACH_3DM_SPLIT");
        Assert.True(splitItem.IsUnlocked);
        Assert.Equal(0x653b5180L, splitItem.UnlockedUtc!.Value.ToUnixTimeSeconds());

        // 10. SKIDROW / Hoodlum ([Achievements] + [AchievementsUnlockTimes]) & CreamAPI (7-digit truncated timestamp fix)
        var skidrowEntries = SteamEmulatorFormatParsers.ParseSkidrowHoodlum("""
            [Achievements]
            ACH_SKID_1=1
            [AchievementsUnlockTimes]
            ACH_SKID_1=1700001234
            """u8);
        Assert.Single(skidrowEntries);
        Assert.True(skidrowEntries[0].IsUnlocked);
        Assert.Equal(1700001234L, skidrowEntries[0].UnlockedUtc!.Value.ToUnixTimeSeconds());

        var creamEntries = SteamEmulatorFormatParsers.ParseCreamApiCfg("""
            [ACH_CREAM_1]
            achieved=true
            unlocktime=1710000
            """);
        Assert.Single(creamEntries);
        Assert.True(creamEntries[0].IsUnlocked);
        Assert.Equal(1710000000L, creamEntries[0].UnlockedUtc!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void AchievementParsers_SmartSteamEmuBinaryAndSteamBinaryVdf_ParseCrc32AndBitmasksAccurately()
    {
        // 11. SmartSteamEmu stats.bin: 4B count + N * 24B (CRC32 + UnlockTime64 + Value32)
        var crcLookup = AchievementSourceContext.BuildCrc32Lookup(["ACH_SSE_UNLOCKED", "ACH_SSE_LOCKED", "STAT_KILLS"]);
        byte[] sseBin = new byte[4 + (3 * 24)];
        BinaryPrimitives.WriteInt32LittleEndian(sseBin.AsSpan(0, 4), 3);

        // Record 0: unlocked achievement (value = 1)
        BinaryPrimitives.WriteUInt32LittleEndian(sseBin.AsSpan(4, 4), AchievementSourceContext.ComputeAsciiCrc32("ACH_SSE_UNLOCKED"));
        BinaryPrimitives.WriteInt64LittleEndian(sseBin.AsSpan(12, 8), 1725000000L);
        BinaryPrimitives.WriteInt32LittleEndian(sseBin.AsSpan(24, 4), 1);

        // Record 1: locked achievement (value = 0)
        BinaryPrimitives.WriteUInt32LittleEndian(sseBin.AsSpan(28, 4), AchievementSourceContext.ComputeAsciiCrc32("ACH_SSE_LOCKED"));
        BinaryPrimitives.WriteInt64LittleEndian(sseBin.AsSpan(36, 8), 0L);
        BinaryPrimitives.WriteInt32LittleEndian(sseBin.AsSpan(48, 4), 0);

        // Record 2: stat counter (value = 99 -> must be ignored as non-boolean stat!)
        BinaryPrimitives.WriteUInt32LittleEndian(sseBin.AsSpan(52, 4), AchievementSourceContext.ComputeAsciiCrc32("STAT_KILLS"));
        BinaryPrimitives.WriteInt32LittleEndian(sseBin.AsSpan(72, 4), 99);

        var sseParsed = SteamEmulatorFormatParsers.ParseSmartSteamEmuStatsBin(sseBin, crcLookup);
        Assert.Equal(2, sseParsed.Count);
        Assert.Contains(sseParsed, e => e.ApiName == "ACH_SSE_UNLOCKED" && e.IsUnlocked && e.UnlockedUtc!.Value.ToUnixTimeSeconds() == 1725000000L);
        Assert.Contains(sseParsed, e => e.ApiName == "ACH_SSE_LOCKED" && !e.IsUnlocked);

        // 12. Steam Local Cache Binary VDF: UserGameStatsSchema_48000.bin + UserGameStats_12345_48000.bin
        byte[] schemaBin = BuildSampleSteamSchemaBinaryVdf();
        var mappings = SteamBinaryVdfParser.ParseSchemaBin(schemaBin, appId: 48000, preferredLanguage: "schinese");
        Assert.Equal(2, mappings.Count);
        Assert.Equal("方向错误", mappings[0].Definition.DisplayName);
        Assert.True(mappings[1].Definition.IsHidden);
        Assert.Equal("隐藏关卡终点", mappings[1].Definition.HiddenSpoilerDescription);

        // Bit 0 unlocked, Bit 1 locked -> mask = 0b01 = 1
        byte[] statsBin = BuildSampleSteamUserStatsBinaryVdf(blockId: 1, dataMask: 0b01, bit0UnlockSec: 1728000100);
        var vdfUnlocks = SteamBinaryVdfParser.ParseUserStatsBin(statsBin, mappings);
        Assert.Equal(2, vdfUnlocks.Count);
        Assert.True(vdfUnlocks[0].IsUnlocked);
        Assert.Equal(1728000100L, vdfUnlocks[0].UnlockedUtc!.Value.ToUnixTimeSeconds());
        Assert.False(vdfUnlocks[1].IsUnlocked);
    }

    [Fact]
    public async Task AchievementProviders_GreenLumaRegistryNemirtingasGogDbAndNativeDeclarativeRules_ParseCleanly()
    {
        await using var sandbox = new TestSandbox();

        // 13. GreenLuma isolated temporary HKCU subkey (cleaned up in finally)
        string testRegRoot = $@"SOFTWARE\SpawnUnitTestGlr_{Guid.NewGuid():N}";
        string achSubKey = $@"{testRegRoot}\AppID\48000\Achievements";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(achSubKey, writable: true))
            {
                key!.SetValue("ACH_GL_1", 1, RegistryValueKind.DWord);
                key.SetValue("ACH_GL_1_Time", 1721000222, RegistryValueKind.DWord);
                key.SetValue("ACH_GL_2", 0, RegistryValueKind.DWord);
            }

            var glParsed = SteamEmulatorAchievementProvider.ReadGreenLumaRegistry(achSubKey);
            Assert.Equal(2, glParsed.Count);
            Assert.Contains(glParsed, e => e.ApiName == "ACH_GL_1" && e.IsUnlocked && e.UnlockedUtc!.Value.ToUnixTimeSeconds() == 1721000222L);
            Assert.Contains(glParsed, e => e.ApiName == "ACH_GL_2" && !e.IsUnlocked);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRegRoot, throwOnMissingSubKey: false);
        }

        // 14. NemirtingasGalaxyEmu JSON
        var nemParsed = SteamEmulatorFormatParsers.ParseNemirtingasGalaxyJson("""
            {
              "achievements": {
                "GOG_ACH_01": { "unlocked": true, "unlock_time": 1722000333 }
              }
            }
            """u8);
        Assert.Single(nemParsed);
        Assert.True(nemParsed[0].IsUnlocked);

        // 15. GOG Galaxy gameplay.db read-only SQLite inspection
        string gameplayDbPath = Path.Combine(sandbox.RootDirectory, "gameplay.db");
        await using (var conn = new SqliteConnection($"Data Source={gameplayDbPath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE achievements (achievement_key TEXT PRIMARY KEY, unlock_time INTEGER);
                INSERT INTO achievements VALUES ('ACH_GALAXY_DB', 1723000444);
                INSERT INTO achievements VALUES ('ACH_GALAXY_LOCKED', NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var dbEntries = await GogAchievementProvider.ReadGogGalaxySqliteAsync(gameplayDbPath, "1724299977", "49494844895097688");
        Assert.Equal(2, dbEntries.Count);
        Assert.Contains(dbEntries, e => e.ApiName == "ACH_GALAXY_DB" && e.IsUnlocked);
        Assert.Contains(dbEntries, e => e.ApiName == "ACH_GALAXY_LOCKED" && !e.IsUnlocked);

        // 16. NativeSaveAchievementProvider declarative extractors (AES Limbo Bitmask + RegexKeyValue + JsonPath + BinaryBitOffset)
        var limboRule = NativeSaveAchievementProvider.CreateBuiltInLimboRule();
        byte[] encryptedLimboSave = LimboSaveDecoder.EncryptFromText("lastsavepoint = \"5\"\nachievements = \"5\"\n");
        var limboDecoded = NativeSaveAchievementProvider.EvaluateRule(limboRule, encryptedLimboSave);
        Assert.Equal(13, limboDecoded.Count);
        Assert.True(limboDecoded[0].IsUnlocked);  // Bit 0 -> ACH_1
        Assert.False(limboDecoded[1].IsUnlocked); // Bit 1 -> ACH_2
        Assert.True(limboDecoded[2].IsUnlocked);  // Bit 2 -> ACH_3

        var jsonPathRule = new NativeSaveAchievementRule
        {
            RuleId = "test.jsonpath",
            TitleHint = "SampleJsonGame",
            ExtractorKind = NativeSaveExtractorKind.JsonPath,
            SavePathTemplates = [],
            Mappings =
            [
                new NativeSaveSingleMapping { ApiName = "ACH_JP_BOOL", JsonPathExpression = "$.progress.bossDefeated" },
                new NativeSaveSingleMapping { ApiName = "ACH_JP_SCORE", JsonPathExpression = "$.stats.coins", MinNumericThreshold = 100 },
            ],
        };
        var jpParsed = NativeSaveAchievementProvider.EvaluateRule(
            jsonPathRule,
            """{"progress": {"bossDefeated": true}, "stats": {"coins": 150}}"""u8);
        Assert.Equal(2, jpParsed.Count);
        Assert.All(jpParsed, e => Assert.True(e.IsUnlocked));

        var binBitRule = new NativeSaveAchievementRule
        {
            RuleId = "test.binbit",
            TitleHint = "SampleBinGame",
            ExtractorKind = NativeSaveExtractorKind.BinaryBitOffset,
            SavePathTemplates = [],
            Mappings =
            [
                new NativeSaveSingleMapping { ApiName = "ACH_BIT_3", ByteOffset = 2, BitOffset = 3 },
                new NativeSaveSingleMapping { ApiName = "ACH_BIT_4", ByteOffset = 2, BitOffset = 4 },
            ],
        };
        var binParsed = NativeSaveAchievementProvider.EvaluateRule(binBitRule, [0x00, 0x00, 0b0000_1000, 0x00]);
        Assert.True(binParsed[0].IsUnlocked);
        Assert.False(binParsed[1].IsUnlocked);
    }

    [Fact]
    public async Task CometLocalDaemon_EphemeralLoopbackHandshakeAndProtobufCapture_CapturesUnlockAndYieldsWhenPortBusy()
    {
        await using var sandbox = new TestSandbox();
        await using var daemon = new GogCometLocalDaemon(sandbox.Paths);

        var capturedTcs = new TaskCompletionSource<GogCometAchievementCapturedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        daemon.AchievementCaptured += (args, _) =>
        {
            capturedTcs.TrySetResult(args);
            return Task.CompletedTask;
        };

        daemon.RegisterGameSession(gogGameId: "1724299977", gogClientId: "49494844895097688");

        // Bind to ephemeral loopback port 127.0.0.1:0 (zero firewall prompts!)
        bool started = await daemon.StartAsync(port: 0);
        Assert.True(started);
        Assert.True(daemon.BoundPort > 0);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, daemon.BoundPort);
            await using var netStream = client.GetStream();

            // 1. Send Galaxy AuthInfoRequest Protobuf frame
            byte[] authPayload = GogCometLocalDaemon.EncodeAuthInfoRequestPayload("49494844895097688", "secret");
            byte[] authFrame = GogCometLocalDaemon.EncodeCometProtobufFrame(sort: 1, messageType: 1, oseq: 1, authPayload);
            await netStream.WriteAsync(authFrame);
            await netStream.FlushAsync();

            byte[] replyHeaderPrefix = new byte[2];
            await netStream.ReadExactlyAsync(replyHeaderPrefix);
            ushort authRespHdrLen = BinaryPrimitives.ReadUInt16BigEndian(replyHeaderPrefix);
            Assert.True(authRespHdrLen > 0);
            // Drain rest of auth response
            await Task.Delay(20);
            byte[] drainBuf = new byte[512];
            if (netStream.DataAvailable)
            {
                _ = await netStream.ReadAsync(drainBuf);
            }

            // 2. Send Galaxy SetAchievement / UnlockAchievement Protobuf frame
            byte[] unlockPayload = GogCometLocalDaemon.EncodeUnlockAchievementPayload("ACH_COMET_LIVE", unlocked: true, unlockTimeUnixSec: 1731000555UL);
            byte[] unlockFrame = GogCometLocalDaemon.EncodeCometProtobufFrame(sort: 2, messageType: 7, oseq: 2, unlockPayload);
            await netStream.WriteAsync(unlockFrame);
            await netStream.FlushAsync();
        }

        var captured = await capturedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("1724299977", captured.GameId);
        Assert.Equal("49494844895097688", captured.ClientId);
        Assert.Equal("ACH_COMET_LIVE", captured.Entry.ApiName);
        Assert.True(captured.Entry.IsUnlocked);
        Assert.True(File.Exists(captured.PersistedStateFilePath));

        // 3. Verify graceful yield when port is already occupied (simulating official GOG Galaxy running)
        await using var secondDaemon = new GogCometLocalDaemon(sandbox.Paths);
        bool secondStarted = await secondDaemon.StartAsync(port: daemon.BoundPort);
        Assert.False(secondStarted);
        Assert.True(secondDaemon.IsYieldedToOfficialGalaxy);
    }

    private static byte[] BuildSampleSteamSchemaBinaryVdf()
    {
        using var ms = new MemoryStream();
        WriteObjStart(ms, "48000");
        WriteObjStart(ms, "stats");

        WriteObjStart(ms, "1");
        WriteInt32(ms, "id", 1);
        WriteObjStart(ms, "bits");

        // Bit 0: ACH_1 (visible)
        WriteObjStart(ms, "0");
        WriteInt32(ms, "bit", 0);
        WriteStr(ms, "name", "ACH_1");
        WriteObjStart(ms, "display");
        WriteObjStart(ms, "name");
        WriteStr(ms, "schinese", "方向错误");
        WriteObjEnd(ms);
        WriteObjStart(ms, "desc");
        WriteStr(ms, "schinese", "这是不对的。");
        WriteObjEnd(ms);
        WriteInt32(ms, "hidden", 0);
        WriteObjEnd(ms);
        WriteObjEnd(ms);

        // Bit 1: ACH_13 (hidden=1 with unstripped Chinese spoiler truth!)
        WriteObjStart(ms, "1");
        WriteInt32(ms, "bit", 1);
        WriteStr(ms, "name", "ACH_13");
        WriteObjStart(ms, "display");
        WriteObjStart(ms, "name");
        WriteStr(ms, "schinese", "叮当声！");
        WriteObjEnd(ms);
        WriteObjStart(ms, "desc");
        WriteStr(ms, "schinese", "隐藏关卡终点");
        WriteObjEnd(ms);
        WriteInt32(ms, "hidden", 1);
        WriteObjEnd(ms);
        WriteObjEnd(ms);

        WriteObjEnd(ms); // bits
        WriteObjEnd(ms); // 1
        WriteObjEnd(ms); // stats
        WriteObjEnd(ms); // 48000
        WriteObjEnd(ms); // root
        return ms.ToArray();
    }

    private static byte[] BuildSampleSteamUserStatsBinaryVdf(int blockId, int dataMask, int bit0UnlockSec)
    {
        using var ms = new MemoryStream();
        WriteObjStart(ms, "stats");
        WriteObjStart(ms, blockId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteInt32(ms, "data", dataMask);
        WriteObjStart(ms, "AchievementTimes");
        WriteInt32(ms, "0", bit0UnlockSec);
        WriteObjEnd(ms);
        WriteObjEnd(ms);
        WriteObjEnd(ms);
        WriteObjEnd(ms);
        return ms.ToArray();
    }

    private static void WriteObjStart(Stream s, string key)
    {
        s.WriteByte(0x00);
        WriteCString(s, key);
    }

    private static void WriteStr(Stream s, string key, string val)
    {
        s.WriteByte(0x01);
        WriteCString(s, key);
        WriteCString(s, val);
    }

    private static void WriteInt32(Stream s, string key, int val)
    {
        s.WriteByte(0x02);
        WriteCString(s, key);
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, val);
        s.Write(buf);
    }

    private static void WriteObjEnd(Stream s) => s.WriteByte(0x08);

    private static void WriteCString(Stream s, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        s.Write(utf8);
        s.WriteByte(0x00);
    }
}
