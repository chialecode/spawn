using System.Text;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class SteamShortcutsVdfSerializerTests
{
    [Fact]
    public void BinaryVdf_RoundTrip_PreservesAllValueTypesNestedTagsUnknownFieldsAndExactByteOrder()
    {
        var root = new BinaryVdfMap();
        var shortcuts = new BinaryVdfMap();
        root.Fields.Add(new BinaryVdfField("shortcuts", BinaryVdfValueType.Map, shortcuts));

        var tags = new BinaryVdfMap();
        tags.Fields.Add(new BinaryVdfField("0", BinaryVdfValueType.String, "favorite"));
        tags.Fields.Add(new BinaryVdfField("1", BinaryVdfValueType.String, "控制器"));

        var futureNested = new BinaryVdfMap();
        futureNested.Fields.Add(new BinaryVdfField("NestedFlag", BinaryVdfValueType.Int32, 42));
        futureNested.Fields.Add(new BinaryVdfField("NestedEmpty", BinaryVdfValueType.String, string.Empty));

        var entry0 = new BinaryVdfMap();
        entry0.Fields.Add(new BinaryVdfField("appid", BinaryVdfValueType.Int32, unchecked((int)0x81234567u)));
        entry0.Fields.Add(new BinaryVdfField("AppName", BinaryVdfValueType.String, "黑神话：悟空 (Unicode Test)"));
        entry0.Fields.Add(new BinaryVdfField("Exe", BinaryVdfValueType.String, "\"D:\\Games\\Sample\\game.exe\""));
        entry0.Fields.Add(new BinaryVdfField("StartDir", BinaryVdfValueType.String, "\"D:\\Games\\Sample\""));
        entry0.Fields.Add(new BinaryVdfField("UnknownFloat", BinaryVdfValueType.Float32, 3.25f));
        entry0.Fields.Add(new BinaryVdfField("UnknownPointer", BinaryVdfValueType.Pointer, 0x12345678u));
        entry0.Fields.Add(new BinaryVdfField("UnknownWideString", BinaryVdfValueType.WideString, "宽字符_Ω_Test"));
        entry0.Fields.Add(new BinaryVdfField("UnknownColor", BinaryVdfValueType.Color, 0xFF884422u));
        entry0.Fields.Add(new BinaryVdfField("UnknownUInt64", BinaryVdfValueType.UInt64, 0xFEDCBA9876543210UL));
        entry0.Fields.Add(new BinaryVdfField("FutureNestedMap", BinaryVdfValueType.Map, futureNested));
        entry0.Fields.Add(new BinaryVdfField("tags", BinaryVdfValueType.Map, tags));

        shortcuts.Fields.Add(new BinaryVdfField("0", BinaryVdfValueType.Map, entry0));

        byte[] firstBytes = SteamShortcutsVdfSerializer.Write(root);

        // Verify canonical 0x08 map end terminators at the tail (entry0 + shortcuts + root)
        Assert.True(firstBytes.Length >= 3);
        Assert.Equal((byte)BinaryVdfValueType.MapEnd, firstBytes[^1]);
        Assert.Equal((byte)BinaryVdfValueType.MapEnd, firstBytes[^2]);

        BinaryVdfMap parsed = SteamShortcutsVdfSerializer.Read(firstBytes);
        byte[] secondBytes = SteamShortcutsVdfSerializer.Write(parsed);

        // Exact byte-for-byte round-trip equality
        Assert.Equal(firstBytes, secondBytes);

        // Verify every field type, key casing, value, and exact field ordering
        BinaryVdfMap? parsedShortcuts = parsed.GetMap("shortcuts");
        Assert.NotNull(parsedShortcuts);
        BinaryVdfMap? parsedEntry = parsedShortcuts!.GetMap("0");
        Assert.NotNull(parsedEntry);

        string[] expectedKeys =
        [
            "appid",
            "AppName",
            "Exe",
            "StartDir",
            "UnknownFloat",
            "UnknownPointer",
            "UnknownWideString",
            "UnknownColor",
            "UnknownUInt64",
            "FutureNestedMap",
            "tags",
        ];
        Assert.Equal(expectedKeys, parsedEntry!.Fields.Select(f => f.Key).ToArray());

        Assert.Equal(unchecked((int)0x81234567u), parsedEntry.GetInt32("appid"));
        Assert.Equal("黑神话：悟空 (Unicode Test)", parsedEntry.GetString("AppName"));
        Assert.Equal(3.25f, Assert.IsType<float>(parsedEntry.FindField("UnknownFloat")!.Value));
        Assert.Equal(0x12345678u, Assert.IsType<uint>(parsedEntry.FindField("UnknownPointer")!.Value));
        Assert.Equal(BinaryVdfValueType.WideString, parsedEntry.FindField("UnknownWideString")!.Type);
        Assert.Equal("宽字符_Ω_Test", parsedEntry.GetString("UnknownWideString"));
        Assert.Equal(0xFF884422u, Assert.IsType<uint>(parsedEntry.FindField("UnknownColor")!.Value));
        Assert.Equal(0xFEDCBA9876543210UL, Assert.IsType<ulong>(parsedEntry.FindField("UnknownUInt64")!.Value));

        BinaryVdfMap? parsedTags = parsedEntry.GetMap("tags");
        Assert.NotNull(parsedTags);
        Assert.Equal("favorite", parsedTags!.GetString("0"));
        Assert.Equal("控制器", parsedTags.GetString("1"));

        BinaryVdfMap? parsedNested = parsedEntry.GetMap("FutureNestedMap");
        Assert.NotNull(parsedNested);
        Assert.Equal(42, parsedNested!.GetInt32("NestedFlag"));
        Assert.Equal(string.Empty, parsedNested.GetString("NestedEmpty"));
    }

    [Fact]
    public void EmptyDocument_WireFormat_AndAlternate0x0BMapTerminator_ParseCleanly()
    {
        BinaryVdfMap emptyDoc = SteamShortcutsVdfSerializer.CreateEmptyShortcutsDocument();
        byte[] emptyBytes = SteamShortcutsVdfSerializer.Write(emptyDoc);

        // Canonical Steam empty shortcuts.vdf: 0x00 "shortcuts" 0x00 0x08 0x08
        byte[] expected = [0x00, .. Encoding.UTF8.GetBytes("shortcuts"), 0x00, 0x08, 0x08];
        Assert.Equal(expected, emptyBytes);

        // Alternate 0x0B map end byte accepted on read and normalized to 0x08 on write
        byte[] altEndBytes = [0x00, .. Encoding.UTF8.GetBytes("shortcuts"), 0x00, 0x0B, 0x0B];
        BinaryVdfMap fromAlt = SteamShortcutsVdfSerializer.Read(altEndBytes);
        Assert.Equal(expected, SteamShortcutsVdfSerializer.Write(fromAlt));
    }

    [Fact]
    public void ComputeShortcutAppId_AndRunGameIdUri_MatchBitLevelValveFormula()
    {
        // Independent IEEE 802.3 CRC32 reference oracle covering both high-bit-clear and high-bit-set raw CRCs
        string[] sampleInputs =
        [
            "\"D:\\Games\\Hades\\Hades.exe\"|Hades",
            "\"D:\\Games\\Hades\\Hades.exe\"|1145360",
            "\"C:\\Games\\LIMBO\\limbo.exe\"|LIMBO",
            "\"E:\\RPG\\中文路径\\game.exe\"|测试游戏",
        ];

        bool sawRawCrcHighBitZero = false;
        bool sawRawCrcHighBitOne = false;

        foreach (string sample in sampleInputs)
        {
            string[] parts = sample.Split('|');
            string storedExe = parts[0];
            string appName = parts[1];

            uint rawCrc = ComputeReferenceIeeeCrc32(Encoding.UTF8.GetBytes(storedExe + appName));
            if ((rawCrc & 0x80000000u) == 0)
            {
                sawRawCrcHighBitZero = true;
            }
            else
            {
                sawRawCrcHighBitOne = true;
            }

            uint expectedUnsignedAppId = rawCrc | 0x80000000u;
            int actualAppId = SteamShortcutsVdfSerializer.ComputeShortcutAppId(storedExe, appName);

            Assert.Equal(unchecked((int)expectedUnsignedAppId), actualAppId);
            Assert.True((unchecked((uint)actualAppId) & 0x80000000u) != 0);

            ulong expectedRunGameId = ((ulong)expectedUnsignedAppId << 32) | 0x02000000UL;
            ulong actualRunGameId = SteamShortcutsVdfSerializer.ComputeRunGameId(actualAppId);
            Assert.Equal(expectedRunGameId, actualRunGameId);
            Assert.Equal(0x02000000UL, actualRunGameId & 0xFFFFFFFFUL);
            Assert.Equal(expectedUnsignedAppId, (uint)(actualRunGameId >> 32));
            Assert.Equal($"steam://rungameid/{expectedRunGameId}", SteamShortcutsVdfSerializer.BuildRunGameIdUri(actualAppId));
        }

        Assert.True(sawRawCrcHighBitZero, "Test vectors should exercise raw CRC32 with MSB=0.");
        Assert.True(sawRawCrcHighBitOne, "Test vectors should exercise raw CRC32 with MSB=1.");

        // Bit boundary vectors for ComputeRunGameId (no sign-extension bugs on negative int32)
        Assert.Equal(0x8000000102000000UL, SteamShortcutsVdfSerializer.ComputeRunGameId(unchecked((int)0x80000001u)));
        Assert.Equal(0xFFFFFFFF02000000UL, SteamShortcutsVdfSerializer.ComputeRunGameId(-1));
        Assert.Equal("steam://rungameid/1145360", SteamShortcutsVdfSerializer.BuildOfficialRunGameIdUri(1145360));
    }

    [Fact]
    public void UpsertShortcutEntry_MatchesByRegisteredAppIdFirst_ThenNormalizedExe_AndDetectsFieldDrift()
    {
        BinaryVdfMap doc = SteamShortcutsVdfSerializer.CreateEmptyShortcutsDocument();

        string exePath = @"D:\Games\Hades\Hades.exe";
        string startDir = @"D:\Games\Hades";
        var desiredTitle = new SteamShortcutDesiredSpec(
            AppName: "Hades",
            NormalizedExeFullPath: exePath,
            QuotedExe: SteamShortcutsVdfSerializer.QuoteShortcutPath(exePath),
            NormalizedStartDir: startDir,
            QuotedStartDir: SteamShortcutsVdfSerializer.QuoteShortcutPath(startDir),
            IconPath: exePath,
            LaunchOptions: string.Empty);

        // 1. Initial insert
        (int firstAppId, bool firstModified) = SteamShortcutsVdfSerializer.UpsertShortcutEntry(doc, desiredTitle);
        Assert.True(firstModified);
        Assert.Equal(SteamShortcutsVdfSerializer.ComputeShortcutAppId(desiredTitle.QuotedExe, desiredTitle.AppName), firstAppId);

        BinaryVdfMap? inserted = SteamShortcutsVdfSerializer.FindShortcutEntry(doc, firstAppId, exePath);
        Assert.NotNull(inserted);
        Assert.True(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted!, desiredTitle));

        // Add an unknown future field and custom user tag to ensure updates preserve them verbatim
        inserted!.Fields.Add(new BinaryVdfField("CustomSteamFutureField", BinaryVdfValueType.Int32, 777));
        inserted.GetOrCreateMap("tags").SetString("0", "Action");

        // 2. Idempotent second upsert -> Modified == false
        (int idempotentId, bool idempotentModified) = SteamShortcutsVdfSerializer.UpsertShortcutEntry(doc, desiredTitle, firstAppId);
        Assert.Equal(firstAppId, idempotentId);
        Assert.False(idempotentModified);

        // 3. Simulate user renaming AppName in Steam UI -> matching by RegisteredShortcutAppId preserves stored appid!
        inserted.SetString("AppName", "My Custom Steam Name");
        Assert.False(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredTitle));

        BinaryVdfMap? matchedById = SteamShortcutsVdfSerializer.FindShortcutEntry(doc, firstAppId, @"D:\Different\Other.exe");
        Assert.Same(inserted, matchedById);

        // Switch to SteamAppId community layout mode ("1145360") -> updates AppName, keeps stored appid & custom fields!
        var desiredCommunityLayout = desiredTitle with { AppName = "1145360" };
        (int layoutAppId, bool layoutModified) = SteamShortcutsVdfSerializer.UpsertShortcutEntry(doc, desiredCommunityLayout, firstAppId);
        Assert.True(layoutModified);
        Assert.Equal(firstAppId, layoutAppId);
        Assert.Equal("1145360", inserted.GetString("AppName"));
        Assert.Equal(777, inserted.GetInt32("CustomSteamFutureField"));
        Assert.Equal("Action", inserted.GetMap("tags")!.GetString("0"));

        // 4. Fallback matching by normalized Exe path (different slash style / case / unquoted) when RegisteredShortcutAppId is null
        inserted.SetString("Exe", "d:/games/hades/hades.exe");
        BinaryVdfMap? matchedByExe = SteamShortcutsVdfSerializer.FindShortcutEntry(doc, registeredShortcutAppId: null, exePath);
        Assert.Same(inserted, matchedByExe);

        // 5. IsEntryUpToDate detects each individual field change
        Assert.True(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout));
        Assert.False(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout with { LaunchOptions = "-vulkan" }));
        Assert.False(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout with { NormalizedExeFullPath = @"D:\Games\Hades\Hades64.exe" }));
        Assert.False(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout with { NormalizedStartDir = @"D:\Games\Hades\x64" }));
        Assert.False(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout with { AppName = "Hades" }));

        inserted.SetInt32("AllowOverlay", 0);
        Assert.False(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout));
        inserted.SetInt32("AllowOverlay", 1);

        inserted.SetInt32("AllowDesktopConfig", 0);
        Assert.False(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout));
        inserted.SetInt32("AllowDesktopConfig", 1);
        Assert.True(SteamShortcutsVdfSerializer.IsEntryUpToDate(inserted, desiredCommunityLayout));

        // 6. Second distinct game appends as index "1" without disturbing index "0"
        string secondExe = @"D:\Games\Hollow\hollow.exe";
        var secondSpec = new SteamShortcutDesiredSpec(
            AppName: "Hollow",
            NormalizedExeFullPath: secondExe,
            QuotedExe: SteamShortcutsVdfSerializer.QuoteShortcutPath(secondExe),
            NormalizedStartDir: @"D:\Games\Hollow",
            QuotedStartDir: SteamShortcutsVdfSerializer.QuoteShortcutPath(@"D:\Games\Hollow"),
            IconPath: secondExe,
            LaunchOptions: "-windowed");

        (int secondAppId, bool secondModified) = SteamShortcutsVdfSerializer.UpsertShortcutEntry(doc, secondSpec);
        Assert.True(secondModified);
        Assert.NotEqual(firstAppId, secondAppId);
        Assert.Equal(2, doc.GetMap("shortcuts")!.Fields.Count);
        Assert.Equal("0", doc.GetMap("shortcuts")!.Fields[0].Key);
        Assert.Equal("1", doc.GetMap("shortcuts")!.Fields[1].Key);
    }

    [Fact]
    public async Task WriteFileAtomically_CreatesBackupAvoidsLeftoverTmp_AndHandlesEmptyOrCorruptFiles()
    {
        await using var sandbox = new TestSandbox();
        string shortcutsPath = Path.Combine(sandbox.RootDirectory, "userdata", "12345", "config", "shortcuts.vdf");
        string backupPath = shortcutsPath + ".spawn.bak";
        string tempPath = shortcutsPath + ".spawn.tmp";

        // 1. Missing file reads cleanly as empty document
        BinaryVdfMap missingDoc = SteamShortcutsVdfSerializer.ReadFile(shortcutsPath);
        Assert.NotNull(missingDoc.GetMap("shortcuts"));
        Assert.Empty(missingDoc.GetMap("shortcuts")!.Fields);

        // 2. First atomic write creates parent folders, no leftover .spawn.tmp, no .spawn.bak yet
        var firstSpec = new SteamShortcutDesiredSpec(
            AppName: "Game One",
            NormalizedExeFullPath: @"D:\Games\One\one.exe",
            QuotedExe: "\"D:\\Games\\One\\one.exe\"",
            NormalizedStartDir: @"D:\Games\One",
            QuotedStartDir: "\"D:\\Games\\One\"",
            IconPath: @"D:\Games\One\one.exe",
            LaunchOptions: string.Empty);
        SteamShortcutsVdfSerializer.UpsertShortcutEntry(missingDoc, firstSpec);
        SteamShortcutsVdfSerializer.WriteFileAtomically(shortcutsPath, missingDoc);

        Assert.True(File.Exists(shortcutsPath));
        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(backupPath));
        byte[] originalBytes = await File.ReadAllBytesAsync(shortcutsPath);

        // 3. Second atomic write creates .spawn.bak matching original bytes and cleanly replaces shortcuts.vdf
        BinaryVdfMap reloaded = SteamShortcutsVdfSerializer.ReadFile(shortcutsPath);
        var secondSpec = new SteamShortcutDesiredSpec(
            AppName: "Game Two",
            NormalizedExeFullPath: @"D:\Games\Two\two.exe",
            QuotedExe: "\"D:\\Games\\Two\\two.exe\"",
            NormalizedStartDir: @"D:\Games\Two",
            QuotedStartDir: "\"D:\\Games\\Two\"",
            IconPath: @"D:\Games\Two\two.exe",
            LaunchOptions: "--fast");
        SteamShortcutsVdfSerializer.UpsertShortcutEntry(reloaded, secondSpec);
        SteamShortcutsVdfSerializer.WriteFileAtomically(shortcutsPath, reloaded);

        Assert.True(File.Exists(backupPath));
        Assert.False(File.Exists(tempPath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath));

        BinaryVdfMap finalDoc = SteamShortcutsVdfSerializer.ReadFile(shortcutsPath);
        Assert.Equal(2, finalDoc.GetMap("shortcuts")!.Fields.Count);

        // 4. Empty (0-byte) file reads cleanly as empty document
        string emptyPath = Path.Combine(sandbox.RootDirectory, "empty_shortcuts.vdf");
        await File.WriteAllBytesAsync(emptyPath, []);
        BinaryVdfMap fromZeroBytes = SteamShortcutsVdfSerializer.ReadFile(emptyPath);
        Assert.Empty(fromZeroBytes.GetMap("shortcuts")!.Fields);

        // 5. Corrupt Binary VDF throws InvalidDataException cleanly
        byte[] unknownTypePayload = [0x7F, (byte)'k', 0x00, 0x08];
        Assert.Throws<InvalidDataException>(() => SteamShortcutsVdfSerializer.Read(unknownTypePayload));

        byte[] truncatedInt32Payload = [0x02, (byte)'a', (byte)'p', (byte)'p', 0x00, 0x01, 0x02];
        Assert.Throws<InvalidDataException>(() => SteamShortcutsVdfSerializer.Read(truncatedInt32Payload));

        byte[] unterminatedStringPayload = [0x01, (byte)'k', 0x00, (byte)'a', (byte)'b'];
        Assert.Throws<InvalidDataException>(() => SteamShortcutsVdfSerializer.Read(unterminatedStringPayload));
    }

    private static uint ComputeReferenceIeeeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
