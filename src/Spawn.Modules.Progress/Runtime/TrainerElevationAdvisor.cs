using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Runtime;

/// <summary>
/// Read-only inspection result for a Windows PE executable's embedded or side-by-side UAC application manifest.
/// </summary>
public sealed record PeManifestElevationResult(
    bool HasManifest,
    string? RequestedExecutionLevel,
    bool RequiresAdministrator);

/// <summary>
/// Read-only inspection result for a running process's Windows Mandatory Integrity Level.
/// </summary>
public sealed record ProcessIntegrityInspectionResult(
    bool Succeeded,
    int IntegrityLevelRid,
    bool IsElevatedOrHigher,
    bool AccessDeniedOnTokenQuery = false);

/// <summary>
/// Read-only PE manifest inspector determining whether an external trainer declares <c>requireAdministrator</c>.
/// </summary>
public interface IPeManifestInspector
{
    PeManifestElevationResult InspectExecutable(string executablePath);
}

/// <summary>
/// Read-only process integrity probe using strictly <c>PROCESS_QUERY_LIMITED_INFORMATION (0x1000)</c> + <c>TOKEN_QUERY</c>.
/// </summary>
public interface IProcessIntegrityProbe
{
    ProcessIntegrityInspectionResult InspectProcessIntegrity(int processId);
}

/// <summary>
/// Tiered on-demand elevation advisor (D-11 / ADR-0006): chooses Level 1 (<c>CreateProcessW</c>, 0 UAC) vs
/// Level 2 (<c>ShellExecuteExW runas</c>, 1 UAC per session) based on trainer PE manifest and target game IL.
/// </summary>
public interface ITrainerElevationAdvisor
{
    PeManifestElevationResult InspectExecutableManifest(string executablePath);
    bool IsProcessElevated(int processId);
    TrainerElevationLevel DetermineElevationLevel(bool trainerRequiresAdmin, bool targetGameIsElevated);
    TrainerElevationLevel DetermineElevationLevel(
        string? trainerExecutablePath,
        bool catalogRequiresAdmin,
        IReadOnlyList<int>? targetGameProcessIds);
}

/// <summary>
/// Pure managed <see cref="PEReader"/> implementation that walks the PE <c>.rsrc</c> directory for <c>RT_MANIFEST (24)</c>
/// without loading or executing the binary.
/// </summary>
public sealed class PeResourceManifestInspector : IPeManifestInspector
{
    private const uint RtManifestTypeId = 24;

    public static PeResourceManifestInspector Instance { get; } = new();

    public PeManifestElevationResult InspectExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new PeManifestElevationResult(false, null, false);
        }

        string? manifestXml = TryExtractEmbeddedManifestXml(executablePath);
        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            string externalManifestPath = executablePath + ".manifest";
            if (File.Exists(externalManifestPath))
            {
                try
                {
                    manifestXml = File.ReadAllText(externalManifestPath, Encoding.UTF8);
                }
                catch
                {
                    manifestXml = null;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            return new PeManifestElevationResult(false, null, false);
        }

        return ParseRequestedExecutionLevel(manifestXml);
    }

    public static PeManifestElevationResult ParseRequestedExecutionLevel(string manifestXml)
    {
        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            return new PeManifestElevationResult(false, null, false);
        }

        try
        {
            string trimmed = manifestXml.TrimStart('\uFEFF', '\0', ' ', '\t', '\r', '\n').TrimEnd('\0', ' ', '\t', '\r', '\n');
            XDocument doc = XDocument.Parse(trimmed);
            XElement? levelElement = doc
                .Descendants()
                .FirstOrDefault(static e => string.Equals(e.Name.LocalName, "requestedExecutionLevel", StringComparison.OrdinalIgnoreCase));

            string? level = levelElement?.Attribute("level")?.Value?.Trim();
            bool requiresAdmin = string.Equals(level, "requireAdministrator", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(level, "highestAvailable", StringComparison.OrdinalIgnoreCase);

            return new PeManifestElevationResult(
                HasManifest: true,
                RequestedExecutionLevel: level,
                RequiresAdministrator: requiresAdmin);
        }
        catch
        {
            return new PeManifestElevationResult(false, null, false);
        }
    }

    private static string? TryExtractEmbeddedManifestXml(string executablePath)
    {
        try
        {
            using var fs = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(fs);
            PEHeader? peHeader = peReader.PEHeaders.PEHeader;
            if (peHeader is null)
            {
                return null;
            }

            DirectoryEntry rsrcDir = peHeader.ResourceTableDirectory;
            if (rsrcDir.RelativeVirtualAddress <= 0 || rsrcDir.Size <= 0)
            {
                return null;
            }

            PEMemoryBlock rsrcBlock = peReader.GetSectionData(rsrcDir.RelativeVirtualAddress);
            ReadOnlySpan<byte> rsrcBytes = rsrcBlock.GetContent().AsSpan();
            if (rsrcBytes.Length < 16)
            {
                return null;
            }

            // Level 1: Resource Type directory -> find integer ID 24 (RT_MANIFEST).
            if (!TryFindDirectoryEntryById(rsrcBytes, directoryOffset: 0, RtManifestTypeId, out uint nameDirOffset, out bool nameIsSubDir)
                || !nameIsSubDir)
            {
                return null;
            }

            // Level 2: Resource Name/ID directory -> pick first manifest entry (typically ID 1 or 2).
            if (!TryGetFirstDirectoryEntry(rsrcBytes, (int)nameDirOffset, out uint langDirOffset, out bool langIsSubDir)
                || !langIsSubDir)
            {
                return null;
            }

            // Level 3: Language directory -> pick first data leaf (IMAGE_RESOURCE_DATA_ENTRY).
            if (!TryGetFirstDirectoryEntry(rsrcBytes, (int)langDirOffset, out uint dataEntryOffset, out bool dataIsSubDir)
                || dataIsSubDir)
            {
                return null;
            }

            int leafOffset = checked((int)dataEntryOffset);
            if (leafOffset < 0 || leafOffset + 16 > rsrcBytes.Length)
            {
                return null;
            }

            int dataRva = BinaryPrimitives.ReadInt32LittleEndian(rsrcBytes.Slice(leafOffset, 4));
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(rsrcBytes.Slice(leafOffset + 4, 4));
            if (dataRva <= 0 || dataSize <= 0 || dataSize > 1024 * 1024)
            {
                return null;
            }

            PEMemoryBlock dataBlock = peReader.GetSectionData(dataRva);
            ReadOnlySpan<byte> payload = dataBlock.GetContent().AsSpan();
            if (payload.Length < dataSize)
            {
                dataSize = payload.Length;
            }

            ReadOnlySpan<byte> manifestBytes = payload[..dataSize];
            return DecodeManifestBytes(manifestBytes);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryFindDirectoryEntryById(
        ReadOnlySpan<byte> rsrcBytes,
        int directoryOffset,
        uint targetId,
        out uint offsetToTarget,
        out bool isSubDirectory)
    {
        offsetToTarget = 0;
        isSubDirectory = false;

        if (directoryOffset < 0 || directoryOffset + 16 > rsrcBytes.Length)
        {
            return false;
        }

        ushort namedCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrcBytes.Slice(directoryOffset + 12, 2));
        ushort idCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrcBytes.Slice(directoryOffset + 14, 2));
        int entriesStart = directoryOffset + 16 + (namedCount * 8);

        for (int i = 0; i < idCount; i++)
        {
            int entryPos = entriesStart + (i * 8);
            if (entryPos + 8 > rsrcBytes.Length)
            {
                break;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(entryPos, 4));
            if ((id & 0x8000_0000u) == 0 && (id & 0xFFFFu) == targetId)
            {
                uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(entryPos + 4, 4));
                isSubDirectory = (rawOffset & 0x8000_0000u) != 0;
                offsetToTarget = rawOffset & 0x7FFF_FFFFu;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetFirstDirectoryEntry(
        ReadOnlySpan<byte> rsrcBytes,
        int directoryOffset,
        out uint offsetToTarget,
        out bool isSubDirectory)
    {
        offsetToTarget = 0;
        isSubDirectory = false;

        if (directoryOffset < 0 || directoryOffset + 16 > rsrcBytes.Length)
        {
            return false;
        }

        ushort namedCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrcBytes.Slice(directoryOffset + 12, 2));
        ushort idCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrcBytes.Slice(directoryOffset + 14, 2));
        int totalEntries = namedCount + idCount;
        if (totalEntries <= 0 || directoryOffset + 16 + 8 > rsrcBytes.Length)
        {
            return false;
        }

        uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(directoryOffset + 20, 4));
        isSubDirectory = (rawOffset & 0x8000_0000u) != 0;
        offsetToTarget = rawOffset & 0x7FFF_FFFFu;
        return true;
    }

    private static string DecodeManifestBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// Clean-Host compliant Win32 process integrity probe using strictly <c>OpenProcess(0x1000)</c> + <c>OpenProcessToken(TOKEN_QUERY)</c>
/// + <c>GetTokenInformation(TokenIntegrityLevel)</c>. Zero VM read access.
/// </summary>
public sealed partial class Win32ProcessIntegrityProbe : IProcessIntegrityProbe
{
    public const int LowIntegrityRid = 0x1000;
    public const int MediumIntegrityRid = 0x2000;
    public const int HighIntegrityRid = 0x3000;
    public const int SystemIntegrityRid = 0x4000;

    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelClass = 25;
    private const int ErrorAccessDenied = 5;

    public static Win32ProcessIntegrityProbe Instance { get; } = new();

    public unsafe ProcessIntegrityInspectionResult InspectProcessIntegrity(int processId)
    {
        if (processId <= 4)
        {
            return new ProcessIntegrityInspectionResult(false, 0, false);
        }

        nint hProcess = OpenProcess(SafeProcessInspector.ProcessQueryLimitedInformation, bInheritHandle: false, (uint)processId);
        if (hProcess == nint.Zero)
        {
            return new ProcessIntegrityInspectionResult(false, 0, false);
        }

        try
        {
            if (!OpenProcessToken(hProcess, TokenQuery, out nint hToken))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ErrorAccessDenied)
                {
                    // Medium IL caller opening a live process with 0x1000 succeeds, while OpenProcessToken(TOKEN_QUERY)
                    // returns ERROR_ACCESS_DENIED when the target runs at High/Protected IL.
                    return new ProcessIntegrityInspectionResult(
                        Succeeded: true,
                        IntegrityLevelRid: HighIntegrityRid,
                        IsElevatedOrHigher: true,
                        AccessDeniedOnTokenQuery: true);
                }

                return new ProcessIntegrityInspectionResult(false, 0, false);
            }

            try
            {
                _ = GetTokenInformation(hToken, TokenIntegrityLevelClass, nint.Zero, 0, out uint requiredLength);
                if (requiredLength == 0 || requiredLength > 4096)
                {
                    return new ProcessIntegrityInspectionResult(false, 0, false);
                }

                byte* buffer = (byte*)NativeMemory.AllocZeroed(requiredLength);
                try
                {
                    if (!GetTokenInformation(hToken, TokenIntegrityLevelClass, (nint)buffer, requiredLength, out _))
                    {
                        return new ProcessIntegrityInspectionResult(false, 0, false);
                    }

                    // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                    nint sidPtr = *(nint*)buffer;
                    if (sidPtr == nint.Zero)
                    {
                        return new ProcessIntegrityInspectionResult(false, 0, false);
                    }

                    byte* subAuthCountPtr = GetSidSubAuthorityCount(sidPtr);
                    if (subAuthCountPtr is null || *subAuthCountPtr == 0)
                    {
                        return new ProcessIntegrityInspectionResult(false, 0, false);
                    }

                    uint* ridPtr = GetSidSubAuthority(sidPtr, (uint)(*subAuthCountPtr - 1));
                    if (ridPtr is null)
                    {
                        return new ProcessIntegrityInspectionResult(false, 0, false);
                    }

                    int rid = checked((int)*ridPtr);
                    return new ProcessIntegrityInspectionResult(
                        Succeeded: true,
                        IntegrityLevelRid: rid,
                        IsElevatedOrHigher: rid >= HighIntegrityRid);
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }
            finally
            {
                _ = CloseHandle(hToken);
            }
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll")]
    private static unsafe partial byte* GetSidSubAuthorityCount(nint pSid);

    [LibraryImport("advapi32.dll")]
    private static unsafe partial uint* GetSidSubAuthority(nint pSid, uint nSubAuthority);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}

/// <summary>
/// Default implementation of <see cref="ITrainerElevationAdvisor"/> combining read-only PE manifest parsing and target game IL probing.
/// </summary>
public sealed class TrainerElevationAdvisor : ITrainerElevationAdvisor
{
    private readonly IPeManifestInspector _manifestInspector;
    private readonly IProcessIntegrityProbe _integrityProbe;

    public TrainerElevationAdvisor(
        IPeManifestInspector? manifestInspector = null,
        IProcessIntegrityProbe? integrityProbe = null)
    {
        _manifestInspector = manifestInspector ?? PeResourceManifestInspector.Instance;
        _integrityProbe = integrityProbe ?? Win32ProcessIntegrityProbe.Instance;
    }

    public PeManifestElevationResult InspectExecutableManifest(string executablePath) =>
        _manifestInspector.InspectExecutable(executablePath);

    public bool IsProcessElevated(int processId)
    {
        if (processId <= 4)
        {
            return false;
        }

        ProcessIntegrityInspectionResult result = _integrityProbe.InspectProcessIntegrity(processId);
        return result.Succeeded && result.IsElevatedOrHigher;
    }

    public TrainerElevationLevel DetermineElevationLevel(bool trainerRequiresAdmin, bool targetGameIsElevated) =>
        trainerRequiresAdmin || targetGameIsElevated
            ? TrainerElevationLevel.Level2HighIlUac
            : TrainerElevationLevel.Level1MediumIl;

    public TrainerElevationLevel DetermineElevationLevel(
        string? trainerExecutablePath,
        bool catalogRequiresAdmin,
        IReadOnlyList<int>? targetGameProcessIds)
    {
        bool manifestRequiresAdmin = !string.IsNullOrWhiteSpace(trainerExecutablePath)
                                     && _manifestInspector.InspectExecutable(trainerExecutablePath).RequiresAdministrator;

        bool gameElevated = false;
        if (targetGameProcessIds is { Count: > 0 })
        {
            foreach (int pid in targetGameProcessIds)
            {
                if (IsProcessElevated(pid))
                {
                    gameElevated = true;
                    break;
                }
            }
        }

        return DetermineElevationLevel(catalogRequiresAdmin || manifestRequiresAdmin, gameElevated);
    }
}
