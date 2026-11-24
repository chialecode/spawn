using System.Buffers.Binary;
using System.Text;
using Spawn.Contracts;
using Spawn.Modules.Progress.Runtime;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Runtime;

/// <summary>
/// M3-A-10 Host-side elevation level determination (PE manifest <c>requestedExecutionLevel</c> + target game IL)
/// and hotkey chord parser tests. Zero real UAC prompts.
/// </summary>
public sealed class TrainerElevationAndChordTests
{
    [Fact]
    public void PeResourceManifestInspector_RealCompiledTrainerWorkerBinary_DetectsEmbeddedAsInvokerManifest()
    {
        string repoRoot = LocateRepositoryRoot();
        string workerExe = LocateBinary(repoRoot, "Spawn.TrainerWorker", "Spawn.TrainerWorker.exe");

        PeManifestElevationResult result = PeResourceManifestInspector.Instance.InspectExecutable(workerExe);

        Assert.True(result.HasManifest, $"Expected embedded RT_MANIFEST in {workerExe}");
        Assert.Equal("asInvoker", result.RequestedExecutionLevel, ignoreCase: true);
        Assert.False(result.RequiresAdministrator);
    }

    [Fact]
    public void PeResourceManifestInspector_SyntheticPeWithRequireAdministratorResource_DetectsRequireAdministrator()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-pe-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "SyntheticTrainer.exe");
            const string adminManifest = """
                <?xml version="1.0" encoding="utf-8"?>
                <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
                  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
                    <security>
                      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
                      </requestedPrivileges>
                    </security>
                  </trustInfo>
                </assembly>
                """;

            File.WriteAllBytes(exePath, BuildMinimalPeWithEmbeddedManifest(adminManifest));

            PeManifestElevationResult result = PeResourceManifestInspector.Instance.InspectExecutable(exePath);
            Assert.True(result.HasManifest);
            Assert.Equal("requireAdministrator", result.RequestedExecutionLevel);
            Assert.True(result.RequiresAdministrator);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("asInvoker", false)]
    [InlineData("requireAdministrator", true)]
    [InlineData("highestAvailable", true)]
    public void PeResourceManifestInspector_ParseRequestedExecutionLevel_ClassifiesCorrectly(string level, bool expectedAdmin)
    {
        string xml = $"<assembly xmlns='urn:schemas-microsoft-com:asm.v1'><trustInfo xmlns='urn:schemas-microsoft-com:asm.v2'><security><requestedPrivileges xmlns='urn:schemas-microsoft-com:asm.v3'><requestedExecutionLevel level='{level}' uiAccess='false'/></requestedPrivileges></security></trustInfo></assembly>";
        PeManifestElevationResult parsed = PeResourceManifestInspector.ParseRequestedExecutionLevel(xml);

        Assert.True(parsed.HasManifest);
        Assert.Equal(level, parsed.RequestedExecutionLevel);
        Assert.Equal(expectedAdmin, parsed.RequiresAdministrator);
    }

    [Fact]
    public void Win32ProcessIntegrityProbe_CurrentProcess_ReturnsValidIntegrityRidUsingLimitedQueryOnly()
    {
        ProcessIntegrityInspectionResult result = Win32ProcessIntegrityProbe.Instance.InspectProcessIntegrity(Environment.ProcessId);

        Assert.True(result.Succeeded);
        Assert.True(result.IntegrityLevelRid >= Win32ProcessIntegrityProbe.LowIntegrityRid);
    }

    [Theory]
    [InlineData(false, false, TrainerElevationLevel.Level1MediumIl)]
    [InlineData(true, false, TrainerElevationLevel.Level2HighIlUac)]
    [InlineData(false, true, TrainerElevationLevel.Level2HighIlUac)]
    [InlineData(true, true, TrainerElevationLevel.Level2HighIlUac)]
    public void TrainerElevationAdvisor_LevelMatrix_SelectsLevel1VsLevel2Correctly(
        bool manifestRequiresAdmin,
        bool gameIsHighIl,
        TrainerElevationLevel expectedLevel)
    {
        var manifestInspector = new FakeManifestInspector(manifestRequiresAdmin);
        var integrityProbe = new FakeIntegrityProbe(gameIsHighIl ? Win32ProcessIntegrityProbe.HighIntegrityRid : Win32ProcessIntegrityProbe.MediumIntegrityRid);
        var advisor = new TrainerElevationAdvisor(manifestInspector, integrityProbe);

        TrainerElevationLevel level = advisor.DetermineElevationLevel(
            trainerExecutablePath: @"C:\Sandbox\trainer.exe",
            catalogRequiresAdmin: false,
            targetGameProcessIds: [12345]);

        Assert.Equal(expectedLevel, level);
    }

    [Theory]
    [InlineData("Num 1", 0x61, false, new ushort[0])]
    [InlineData("Num /", 0x6F, false, new ushort[0])]
    [InlineData("Num Enter", 0x0D, true, new ushort[0])]
    [InlineData("Enter", 0x0D, false, new ushort[0])]
    [InlineData("Ctrl+Num /", 0x6F, false, new ushort[] { 0x11 })]
    [InlineData("Alt+Num Enter", 0x0D, true, new ushort[] { 0x12 })]
    [InlineData("Ctrl+Num +", 0x6B, false, new ushort[] { 0x11 })]
    [InlineData("Ctrl+Shift+Home", 0x24, false, new ushort[] { 0x11, 0x10 })]
    [InlineData("Insert", 0x2D, false, new ushort[0])]
    [InlineData("F12", 0x7B, false, new ushort[0])]
    [InlineData("Page Down", 0x22, false, new ushort[0])]
    public void HotkeyChordParser_ParsesFlingChordsAccurately(
        string chord,
        ushort expectedVk,
        bool expectedExtendedOverride,
        ushort[] expectedModifiers)
    {
        Assert.True(HotkeyChordParser.TryParse(chord, out ParsedHotkeyChord? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(expectedVk, parsed!.VirtualKey);
        Assert.Equal(expectedExtendedOverride, parsed.IsExtendedKeyOverride);
        Assert.Equal(expectedModifiers, parsed.Modifiers);
    }

    /// <summary>
    /// Constructs a minimal valid PE32+ image containing a single <c>.rsrc</c> section with an embedded <c>RT_MANIFEST (24)</c> resource.
    /// </summary>
    private static byte[] BuildMinimalPeWithEmbeddedManifest(string manifestXml)
    {
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestXml);

        // Layout:
        // 0x000..0x1FF: DOS header + PE signature + COFF header + PE32+ Optional Header + 1 Section Header (.rsrc)
        // 0x200..     : .rsrc section mapped at RVA 0x1000
        const int rsrcFileOffset = 0x200;
        const int rsrcRva = 0x1000;

        // Inside .rsrc:
        // 0x00 (24B): Level 1 dir (1 ID entry: ID=24 -> subdir at 0x18)
        // 0x18 (24B): Level 2 dir (1 ID entry: ID=1 -> subdir at 0x30)
        // 0x30 (24B): Level 3 dir (1 ID entry: ID=1033 -> data entry at 0x48)
        // 0x48 (16B): IMAGE_RESOURCE_DATA_ENTRY (OffsetToData = RVA 0x1058, Size = manifestBytes.Length)
        // 0x58      : manifest bytes
        const int manifestOffsetInSection = 0x58;
        int rsrcVirtualSize = manifestOffsetInSection + manifestBytes.Length;
        int rsrcRawSize = ((rsrcVirtualSize + 0x1FF) / 0x200) * 0x200;
        byte[] pe = new byte[rsrcFileOffset + rsrcRawSize];

        // DOS header
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        const int peOffset = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C, 4), peOffset);

        // PE\0\0
        pe[peOffset + 0] = (byte)'P';
        pe[peOffset + 1] = (byte)'E';

        // COFF Header (20 bytes at peOffset + 4)
        int coff = peOffset + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 0, 2), 0x8664); // AMD64
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 2, 2), 1);      // NumberOfSections = 1
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 16, 2), 0xF0);  // SizeOfOptionalHeader = 240
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 18, 2), 0x0022); // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

        // Optional Header PE32+ (240 bytes at coff + 20)
        int opt = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(opt + 0, 2), 0x20B);   // PE32+
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 32, 4), 0x1000); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 36, 4), 0x200);  // FileAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 56, 4), 0x2000); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 60, 4), rsrcFileOffset); // SizeOfHeaders
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(opt + 68, 2), 2);      // WINDOWS_GUI
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 108, 4), 16);    // NumberOfRvaAndSizes

        // DataDirectory[2] = Resource Table (offset 112 + 2*8 = 128)
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 128, 4), rsrcRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(opt + 132, 4), (uint)rsrcVirtualSize);

        // Section Header ".rsrc" (40 bytes at opt + 240)
        int sec = opt + 240;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(pe.AsSpan(sec, 5));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sec + 8, 4), (uint)rsrcVirtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sec + 12, 4), rsrcRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sec + 16, 4), (uint)rsrcRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sec + 20, 4), rsrcFileOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sec + 36, 4), 0x40000040); // INITIALIZED_DATA | MEM_READ

        // Resource tree inside .rsrc
        Span<byte> rsrc = pe.AsSpan(rsrcFileOffset, rsrcRawSize);

        // Level 1 (0x00): 1 ID entry (ID=24, subdir=0x18)
        BinaryPrimitives.WriteUInt16LittleEndian(rsrc.Slice(0x00 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x00 + 16, 4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x00 + 20, 4), 0x8000_0000u | 0x18u);

        // Level 2 (0x18): 1 ID entry (ID=1, subdir=0x30)
        BinaryPrimitives.WriteUInt16LittleEndian(rsrc.Slice(0x18 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x18 + 16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x18 + 20, 4), 0x8000_0000u | 0x30u);

        // Level 3 (0x30): 1 ID entry (ID=1033, leaf=0x48)
        BinaryPrimitives.WriteUInt16LittleEndian(rsrc.Slice(0x30 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x30 + 16, 4), 1033);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x30 + 20, 4), 0x48u);

        // Data leaf (0x48)
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x48 + 0, 4), (uint)(rsrcRva + manifestOffsetInSection));
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.Slice(0x48 + 4, 4), (uint)manifestBytes.Length);

        manifestBytes.CopyTo(rsrc.Slice(manifestOffsetInSection));
        return pe;
    }

    private static string LocateRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Spawn.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root containing Spawn.sln.");
    }

    private static string LocateBinary(string repoRoot, string projectName, string fileName)
    {
        foreach (string cfg in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(repoRoot, "src", projectName, "bin", cfg, "net10.0-windows", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Compiled binary {projectName}/{fileName} not found.");
    }

    private sealed class FakeManifestInspector(bool requiresAdmin) : IPeManifestInspector
    {
        public PeManifestElevationResult InspectExecutable(string executablePath) =>
            new(true, requiresAdmin ? "requireAdministrator" : "asInvoker", requiresAdmin);
    }

    private sealed class FakeIntegrityProbe(int rid) : IProcessIntegrityProbe
    {
        public ProcessIntegrityInspectionResult InspectProcessIntegrity(int processId) =>
            new(true, rid, rid >= Win32ProcessIntegrityProbe.HighIntegrityRid);
    }
}
