using System.Text.Json;
using Microsoft.Win32;
using Spawn.Modules.Progress.Snapshots;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Snapshots;

public sealed class DeterministicHkcuRegistrySerializerTests
{
    [Fact]
    public void Snapshots_HkcuRegistrySerializer_ExportsAllValueKindsInUtf8OrderAndRoundTripsHashIdentically()
    {
        string testGuid = Guid.NewGuid().ToString("N");
        string subRoot = $@"Software\SPAWN-Test\{testGuid}";
        string gameSubKeyA = $@"{subRoot}\StudioA\GameOne";
        string gameSubKeyB = $@"{subRoot}\StudioB\GameOne";

        try
        {
            byte[] binPayload = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42, 0xFF];
            string[] multiStrings = ["SlotAlpha", "SlotBeta", "中文存档"];
            int highBitDword = unchecked((int)0xDEADBEEF);
            long qwordVal = 9_007_199_254_740_991L;

            // Populate GameOne under StudioA in reverse order (z -> m -> a) and StudioB in forward order (a -> m -> z).
            using (var keyA = Registry.CurrentUser.CreateSubKey(gameSubKeyA, writable: true)!)
            {
                keyA.SetValue("z_binary", binPayload, RegistryValueKind.Binary);
                keyA.SetValue("q_qword", qwordVal, RegistryValueKind.QWord);
                keyA.SetValue("m_multi", multiStrings, RegistryValueKind.MultiString);
                keyA.SetValue("e_expand", @"%USERPROFILE%\Documents\MySaves", RegistryValueKind.ExpandString);
                keyA.SetValue("d_dword_high", highBitDword, RegistryValueKind.DWord);
                keyA.SetValue("a_string", "Hello_SPAWN", RegistryValueKind.String);
                keyA.SetValue(string.Empty, "DefaultRootValue", RegistryValueKind.String);

                using (var subZ = keyA.CreateSubKey("Zeta", writable: true)!)
                {
                    subZ.SetValue("Flag", 1, RegistryValueKind.DWord);
                }

                using (var subA = keyA.CreateSubKey("Alpha", writable: true)!)
                {
                    subA.SetValue("Note", "FirstInUtf8Order", RegistryValueKind.String);
                }
            }

            using (var keyB = Registry.CurrentUser.CreateSubKey(gameSubKeyB, writable: true)!)
            {
                keyB.SetValue(string.Empty, "DefaultRootValue", RegistryValueKind.String);
                keyB.SetValue("a_string", "Hello_SPAWN", RegistryValueKind.String);
                keyB.SetValue("d_dword_high", highBitDword, RegistryValueKind.DWord);
                keyB.SetValue("e_expand", @"%USERPROFILE%\Documents\MySaves", RegistryValueKind.ExpandString);
                keyB.SetValue("m_multi", multiStrings, RegistryValueKind.MultiString);
                keyB.SetValue("q_qword", qwordVal, RegistryValueKind.QWord);
                keyB.SetValue("z_binary", binPayload, RegistryValueKind.Binary);

                using (var subA = keyB.CreateSubKey("Alpha", writable: true)!)
                {
                    subA.SetValue("Note", "FirstInUtf8Order", RegistryValueKind.String);
                }

                using (var subZ = keyB.CreateSubKey("Zeta", writable: true)!)
                {
                    subZ.SetValue("Flag", 1, RegistryValueKind.DWord);
                }
            }

            string fullKeyA = $@"HKCU\{gameSubKeyA}";
            string fullKeyB = $@"HKEY_CURRENT_USER\{gameSubKeyB}";

            // Export StudioA twice in different input shapes and verify byte-for-byte determinism.
            var snapA1 = DeterministicHkcuRegistrySerializer.ExportKeys([fullKeyA]);
            var snapA2 = DeterministicHkcuRegistrySerializer.ExportKeys([fullKeyA.Replace(@"HKCU\", @"HKEY_CURRENT_USER\", StringComparison.Ordinal)]);
            string jsonA1 = DeterministicHkcuRegistrySerializer.SerializeToJson(snapA1);
            string jsonA2 = DeterministicHkcuRegistrySerializer.SerializeToJson(snapA2);
            string shaA1 = DeterministicHkcuRegistrySerializer.ComputeSha256Hex(snapA1);
            string shaA2 = DeterministicHkcuRegistrySerializer.ComputeSha256Hex(snapA2);

            Assert.Equal(jsonA1, jsonA2);
            Assert.Equal(shaA1, shaA2);
            Assert.DoesNotContain("\r\n", jsonA1, StringComparison.Ordinal);

            // Verify StudioA (created reverse) and StudioB (created forward) produce identical value/subkey JSON structure.
            var snapB = DeterministicHkcuRegistrySerializer.ExportKeys([fullKeyB]);
            string normalizedB = DeterministicHkcuRegistrySerializer.SerializeToJson(snapB)
                .Replace("StudioB", "StudioA", StringComparison.Ordinal);
            Assert.Equal(jsonA1, normalizedB);

            // Verify explicit ValueKind tags, unexpanded REG_EXPAND_SZ, Base64 REG_BINARY, and UTF-8 sorting.
            var rootNode = Assert.Single(snapA1.Keys);
            Assert.Equal(fullKeyA, rootNode.KeyPath);
            Assert.Equal(["", "a_string", "d_dword_high", "e_expand", "m_multi", "q_qword", "z_binary"], rootNode.Values.Select(v => v.Name).ToArray());
            Assert.Equal(["Alpha", "Zeta"], rootNode.SubKeys.Select(s => s.Name).ToArray());

            var kindByName = rootNode.Values.ToDictionary(v => v.Name, v => v.ValueKind);
            Assert.Equal("REG_SZ", kindByName[""]);
            Assert.Equal("REG_SZ", kindByName["a_string"]);
            Assert.Equal("REG_DWORD", kindByName["d_dword_high"]);
            Assert.Equal("REG_EXPAND_SZ", kindByName["e_expand"]);
            Assert.Equal("REG_MULTI_SZ", kindByName["m_multi"]);
            Assert.Equal("REG_QWORD", kindByName["q_qword"]);
            Assert.Equal("REG_BINARY", kindByName["z_binary"]);

            // REG_EXPAND_SZ MUST preserve literal %USERPROFILE%, never expanding host machine path.
            var expandEntry = rootNode.Values.Single(v => v.Name == "e_expand");
            Assert.Equal(@"%USERPROFILE%\Documents\MySaves", expandEntry.Data.GetString());

            // REG_BINARY MUST be encoded as Base64.
            var binEntry = rootNode.Values.Single(v => v.Name == "z_binary");
            Assert.Equal(Convert.ToBase64String(binPayload), binEntry.Data.GetString());

            // Mutate and pollute StudioA registry tree, then restore from serialized JSON and assert identical SHA-256!
            using (var keyA = Registry.CurrentUser.OpenSubKey(gameSubKeyA, writable: true)!)
            {
                keyA.SetValue("a_string", "CORRUPTED_BY_TRAINER", RegistryValueKind.String);
                keyA.SetValue("extra_cheat_flag", 999, RegistryValueKind.DWord);
                using var extraSub = keyA.CreateSubKey("InjectedSubKey", writable: true)!;
                extraSub.SetValue("Bad", "Data", RegistryValueKind.String);
                keyA.DeleteSubKeyTree("Alpha", throwOnMissingSubKey: false);
            }

            var deserialized = DeterministicHkcuRegistrySerializer.Deserialize(jsonA1);
            int restoredKeys = DeterministicHkcuRegistrySerializer.Restore(deserialized);
            Assert.Equal(1, restoredKeys);

            var afterRestore = DeterministicHkcuRegistrySerializer.ExportKeys([fullKeyA]);
            Assert.Equal(shaA1, DeterministicHkcuRegistrySerializer.ComputeSha256Hex(afterRestore));
            Assert.Equal(jsonA1, DeterministicHkcuRegistrySerializer.SerializeToJson(afterRestore));

            // Directly inspect restored Windows Registry values via Microsoft.Win32.Registry.
            using (var restored = Registry.CurrentUser.OpenSubKey(gameSubKeyA, writable: false)!)
            {
                Assert.Null(restored.GetValue("extra_cheat_flag"));
                Assert.Null(restored.OpenSubKey("InjectedSubKey", writable: false));
                Assert.Equal("Hello_SPAWN", restored.GetValue("a_string"));
                Assert.Equal(highBitDword, restored.GetValue("d_dword_high"));
                Assert.Equal(qwordVal, restored.GetValue("q_qword"));
                Assert.Equal(binPayload, (byte[])restored.GetValue("z_binary")!);
                Assert.Equal(multiStrings, (string[])restored.GetValue("m_multi")!);
                Assert.Equal(RegistryValueKind.ExpandString, restored.GetValueKind("e_expand"));
                Assert.Equal(
                    @"%USERPROFILE%\Documents\MySaves",
                    restored.GetValue("e_expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            }
        }
        finally
        {
            SnapshotTestSandbox.CleanupTemporaryHkcuKey(subRoot);
        }
    }

    [Fact]
    public void Snapshots_HkcuRegistrySerializer_RejectsHklmOtherHivesAndTraversal()
    {
        Assert.Throws<ArgumentException>(() => DeterministicHkcuRegistrySerializer.ExportKeys([@"HKLM\SOFTWARE\Microsoft\Windows"]));
        Assert.Throws<ArgumentException>(() => DeterministicHkcuRegistrySerializer.ExportKeys([@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet"]));
        Assert.Throws<ArgumentException>(() => DeterministicHkcuRegistrySerializer.ExportKeys([@"HKCR\.exe"]));
        Assert.Throws<ArgumentException>(() => DeterministicHkcuRegistrySerializer.ExportKeys([@"HKCU"]));
        Assert.Throws<ArgumentException>(() => DeterministicHkcuRegistrySerializer.ExportKeys([@"HKCU\Software\SPAWN-Test\..\Microsoft"]));

        var evilSnapshot = new DeterministicRegistrySnapshot
        {
            Keys =
            [
                new DeterministicRegistryRootKey
                {
                    KeyPath = @"HKLM\SOFTWARE\SPAWN-Evil",
                    Values =
                    [
                        new DeterministicRegistryValueEntry
                        {
                            Name = "Bad",
                            ValueKind = "REG_SZ",
                            Data = JsonSerializer.SerializeToElement("Nope"),
                        },
                    ],
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => DeterministicHkcuRegistrySerializer.Restore(evilSnapshot));
    }
}
