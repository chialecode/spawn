using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Catalog;

/// <summary>
/// Recognized payload container / executable format identified strictly by leading magic bytes.
/// </summary>
public enum TrainerPayloadFormat
{
    Unknown = 0,
    RawPeExecutable = 1,
    ZipArchive = 2,
    RarArchive = 3,
    SevenZipArchive = 4,
}

/// <summary>
/// Read-only structural inspection summary of a Windows PE executable.
/// </summary>
public sealed record PeInspectionSummary(
    bool IsValidPe,
    string? Machine,
    bool RequiresAdmin,
    TrainerAuthenticodeStatus AuthenticodeStatus,
    long FileSize,
    string? ErrorMessage = null);

/// <summary>
/// Clean-Host compliant read-only PE structural validator, magic sniffer, SHA-256 helper,
/// Win32 antivirus/SAC error classifier, and <c>GetShortPathNameW</c> 8.3 short-path helper.
/// </summary>
public static partial class PeBinaryInspector
{
    public const int ErrorVirusInfected = 225;
    public const int ErrorVirusDeleted = 226;
    public const int ErrorSmartAppControlBlocked = 4551;

    private const uint RtManifestResourceTypeId = 24;

    /// <summary>
    /// Sniffs the leading magic bytes of <paramref name="filePath"/> to classify the payload format.
    /// Accepts ONLY <c>4D 5A</c> (PE), <c>50 4B 03 04</c> (ZIP), <c>52 61 72 21</c> (RAR4/RAR5),
    /// and <c>37 7A BC AF</c> (7z). Everything else returns <see cref="TrainerPayloadFormat.Unknown"/>.
    /// </summary>
    public static TrainerPayloadFormat SniffMagicFormat(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        Span<byte> header = stackalloc byte[8];
        int read;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = fs.Read(header);
        }

        return SniffMagicFormat(header[..read]);
    }

    public static TrainerPayloadFormat SniffMagicFormat(ReadOnlySpan<byte> leadingBytes)
    {
        if (leadingBytes.Length >= 2 && leadingBytes[0] == 0x4D && leadingBytes[1] == 0x5A)
        {
            return TrainerPayloadFormat.RawPeExecutable;
        }

        if (leadingBytes.Length >= 4
            && leadingBytes[0] == 0x50
            && leadingBytes[1] == 0x4B
            && leadingBytes[2] == 0x03
            && leadingBytes[3] == 0x04)
        {
            return TrainerPayloadFormat.ZipArchive;
        }

        if (leadingBytes.Length >= 4
            && leadingBytes[0] == 0x52
            && leadingBytes[1] == 0x61
            && leadingBytes[2] == 0x72
            && leadingBytes[3] == 0x21)
        {
            return TrainerPayloadFormat.RarArchive;
        }

        if (leadingBytes.Length >= 4
            && leadingBytes[0] == 0x37
            && leadingBytes[1] == 0x7A
            && leadingBytes[2] == 0xBC
            && leadingBytes[3] == 0xAF)
        {
            // Full 7z signature is 37 7A BC AF 27 1C; verify bytes 4-5 when present.
            if (leadingBytes.Length >= 6 && (leadingBytes[4] != 0x27 || leadingBytes[5] != 0x1C))
            {
                return TrainerPayloadFormat.Unknown;
            }

            return TrainerPayloadFormat.SevenZipArchive;
        }

        return TrainerPayloadFormat.Unknown;
    }

    /// <summary>
    /// Performs read-only <see cref="PEReader"/> header validation, machine architecture extraction,
    /// embedded RT_MANIFEST <c>requestedExecutionLevel</c> inspection, and offline Authenticode status check.
    /// </summary>
    public static PeInspectionSummary InspectExecutable(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return new PeInspectionSummary(false, null, false, TrainerAuthenticodeStatus.Unknown, 0, "文件不存在。");
        }

        try
        {
            var fi = new FileInfo(filePath);
            long fileSize = fi.Length;
            if (fileSize < 64)
            {
                return new PeInspectionSummary(false, null, false, TrainerAuthenticodeStatus.Unknown, fileSize, "PE 文件过小或头部不完整。");
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(fs);

            PEHeader? peHeader = peReader.PEHeaders.PEHeader;
            if (peHeader is null)
            {
                return new PeInspectionSummary(false, null, false, TrainerAuthenticodeStatus.Unknown, fileSize, "缺少有效的 PE 可选头。");
            }

            CoffHeader coff = peReader.PEHeaders.CoffHeader;
            if ((coff.Characteristics & Characteristics.Dll) != 0)
            {
                return new PeInspectionSummary(false, FormatMachine(coff.Machine), false, TrainerAuthenticodeStatus.Unknown, fileSize, "目标文件是 DLL 库而非可执行 EXE。");
            }

            string machine = FormatMachine(coff.Machine);
            bool requiresAdmin = TryReadManifestRequiresAdmin(peReader, peHeader);
            TrainerAuthenticodeStatus authStatus = InspectAuthenticodeOffline(filePath, peHeader, fileSize);

            return new PeInspectionSummary(
                IsValidPe: true,
                Machine: machine,
                RequiresAdmin: requiresAdmin,
                AuthenticodeStatus: authStatus,
                FileSize: fileSize);
        }
        catch (BadImageFormatException ex)
        {
            return new PeInspectionSummary(false, null, false, TrainerAuthenticodeStatus.Unknown, 0, $"PE 头结构无效：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Re-throw AV quarantine / SAC OS errors so caller can classify Win32 225/226/4551 accurately.
            if (TryExtractWin32ErrorCode(ex) is ErrorVirusInfected or ErrorVirusDeleted or ErrorSmartAppControlBlocked)
            {
                throw;
            }

            return new PeInspectionSummary(false, null, false, TrainerAuthenticodeStatus.Unknown, 0, $"无法读取 PE 文件：{ex.Message}");
        }
    }

    /// <summary>
    /// Computes the lowercase 64-character SHA-256 hex digest of <paramref name="filePath"/>.
    /// </summary>
    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Extracts a Windows Win32 error code (such as 225, 226, or 4551) from an exception chain.
    /// </summary>
    public static int? TryExtractWin32ErrorCode(Exception? ex)
    {
        Exception? current = ex;
        while (current is not null)
        {
            if (current is Win32Exception win32 && win32.NativeErrorCode != 0)
            {
                return win32.NativeErrorCode;
            }

            uint hr = unchecked((uint)current.HResult);
            if ((hr & 0xFFFF0000u) == 0x80070000u)
            {
                int code = (int)(hr & 0xFFFFu);
                if (code != 0)
                {
                    return code;
                }
            }

            current = current.InnerException;
        }

        return null;
    }

    /// <summary>
    /// Maps known Win32 antivirus / Smart App Control error codes to <see cref="TrainerImportFailureKind"/>.
    /// </summary>
    public static TrainerImportFailureKind ClassifyOsSecurityError(int? win32ErrorCode) =>
        win32ErrorCode switch
        {
            ErrorVirusInfected or ErrorVirusDeleted => TrainerImportFailureKind.AntivirusQuarantined,
            ErrorSmartAppControlBlocked => TrainerImportFailureKind.SmartAppControlBlocked,
            _ => TrainerImportFailureKind.None,
        };

    /// <summary>
    /// Resolves an 8.3 short path via <c>GetShortPathNameW</c> when <paramref name="fullPath"/> contains non-ASCII
    /// characters (improving compatibility with legacy external trainers when the user keeps a non-ASCII sandbox path).
    /// Returns the original path unchanged when already pure ASCII or if 8.3 short names are disabled on the volume.
    /// </summary>
    public static unsafe (string LaunchPath, bool UsedShortPath) TryResolveLaunchPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        string normalized = Path.GetFullPath(fullPath);

        if (SpawnPaths.IsPureAsciiPath(normalized))
        {
            return (normalized, false);
        }

        if (!File.Exists(normalized) && !Directory.Exists(normalized))
        {
            return (normalized, false);
        }

        try
        {
            fixed (char* pLong = normalized)
            {
                uint required = GetShortPathNameW(pLong, null, 0);
                if (required == 0 || required > 32768)
                {
                    return (normalized, false);
                }

                char[] buffer = new char[required];
                fixed (char* pShort = buffer)
                {
                    uint written = GetShortPathNameW(pLong, pShort, (uint)buffer.Length);
                    if (written > 0 && written < buffer.Length)
                    {
                        string shortPath = new(buffer, 0, (int)written);
                        if (SpawnPaths.IsPureAsciiPath(shortPath)
                            && (File.Exists(shortPath) || Directory.Exists(shortPath)))
                        {
                            return (shortPath, true);
                        }
                    }
                }
            }
        }
        catch
        {
            // 8.3 short name generation disabled or unsupported volume; fall back gracefully.
        }

        return (normalized, false);
    }

    private static string FormatMachine(Machine machine) =>
        machine switch
        {
            Machine.Amd64 => "x64",
            Machine.I386 => "x86",
            Machine.Arm64 => "ARM64",
            _ => machine.ToString(),
        };

    private static bool TryReadManifestRequiresAdmin(PEReader peReader, PEHeader peHeader)
    {
        DirectoryEntry resDir = peHeader.ResourceTableDirectory;
        if (resDir.RelativeVirtualAddress <= 0 || resDir.Size < 16)
        {
            return false;
        }

        try
        {
            PEMemoryBlock resBlock = peReader.GetSectionData(resDir.RelativeVirtualAddress);
            ReadOnlySpan<byte> rsrc = resBlock.GetContent().AsSpan();
            if (rsrc.Length < 16)
            {
                return false;
            }

            // Level 1: Resource Type directory -> locate RT_MANIFEST (ID = 24).
            if (!TryFindResourceEntryOffset(rsrc, 0, RtManifestResourceTypeId, out int typeEntryOffset, out bool isTypeSubDir)
                || !isTypeSubDir)
            {
                return false;
            }

            // Level 2: Resource Name/ID directory -> take first entry (typically ID 1).
            if (!TryGetFirstResourceEntry(rsrc, typeEntryOffset, out int nameEntryOffset, out bool isNameSubDir)
                || !isNameSubDir)
            {
                return false;
            }

            // Level 3: Resource Language directory -> take first data entry.
            if (!TryGetFirstResourceEntry(rsrc, nameEntryOffset, out int dataEntryOffset, out bool isLangSubDir)
                || isLangSubDir
                || dataEntryOffset < 0
                || dataEntryOffset + 16 > rsrc.Length)
            {
                return false;
            }

            int dataRva = BinaryPrimitives.ReadInt32LittleEndian(rsrc.Slice(dataEntryOffset, 4));
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(rsrc.Slice(dataEntryOffset + 4, 4));
            if (dataRva <= 0 || dataSize <= 0 || dataSize > 512 * 1024)
            {
                return false;
            }

            PEMemoryBlock manifestBlock = peReader.GetSectionData(dataRva);
            ReadOnlySpan<byte> manifestBytes = manifestBlock.GetContent().AsSpan();
            if (manifestBytes.Length < dataSize)
            {
                dataSize = manifestBytes.Length;
            }

            string xmlText = DecodeManifestXml(manifestBytes[..dataSize]);
            return ParseRequiresAdminFromManifestXml(xmlText);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindResourceEntryOffset(
        ReadOnlySpan<byte> rsrc,
        int dirOffset,
        uint targetId,
        out int childOffset,
        out bool isSubDirectory)
    {
        childOffset = 0;
        isSubDirectory = false;

        if (dirOffset < 0 || dirOffset + 16 > rsrc.Length)
        {
            return false;
        }

        ushort namedCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 12, 2));
        ushort idCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 14, 2));
        int total = namedCount + idCount;
        int entriesStart = dirOffset + 16;

        for (int i = 0; i < total; i++)
        {
            int entryPos = entriesStart + (i * 8);
            if (entryPos + 8 > rsrc.Length)
            {
                break;
            }

            uint nameOrId = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.Slice(entryPos, 4));
            uint offsetToData = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.Slice(entryPos + 4, 4));
            if ((nameOrId & 0x80000000u) == 0 && nameOrId == targetId)
            {
                isSubDirectory = (offsetToData & 0x80000000u) != 0;
                childOffset = (int)(offsetToData & 0x7FFFFFFFu);
                return true;
            }
        }

        return false;
    }

    private static bool TryGetFirstResourceEntry(
        ReadOnlySpan<byte> rsrc,
        int dirOffset,
        out int childOffset,
        out bool isSubDirectory)
    {
        childOffset = 0;
        isSubDirectory = false;

        if (dirOffset < 0 || dirOffset + 24 > rsrc.Length)
        {
            return false;
        }

        ushort namedCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 12, 2));
        ushort idCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 14, 2));
        if (namedCount + idCount == 0)
        {
            return false;
        }

        uint offsetToData = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.Slice(dirOffset + 20, 4));
        isSubDirectory = (offsetToData & 0x80000000u) != 0;
        childOffset = (int)(offsetToData & 0x7FFFFFFFu);
        return true;
    }

    private static string DecodeManifestXml(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool ParseRequiresAdminFromManifestXml(string xmlText)
    {
        if (string.IsNullOrWhiteSpace(xmlText))
        {
            return false;
        }

        try
        {
            XDocument doc = XDocument.Parse(xmlText.Trim('\0', ' ', '\t', '\r', '\n'));
            XElement? execLevel = doc.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "requestedExecutionLevel", StringComparison.OrdinalIgnoreCase));
            string? level = execLevel?.Attribute("level")?.Value?.Trim();
            return string.Equals(level, "requireAdministrator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(level, "highestAvailable", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return xmlText.Contains("requireAdministrator", StringComparison.OrdinalIgnoreCase)
                || xmlText.Contains("highestAvailable", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static unsafe TrainerAuthenticodeStatus InspectAuthenticodeOffline(
        string filePath,
        PEHeader peHeader,
        long fileSize)
    {
        // Note: per PE/COFF spec, CertificateTableDirectory.RelativeVirtualAddress is a raw FILE OFFSET, not an RVA.
        DirectoryEntry certDir = peHeader.CertificateTableDirectory;
        if (certDir.RelativeVirtualAddress <= 0 || certDir.Size < 8)
        {
            return TrainerAuthenticodeStatus.Unsigned;
        }

        if ((long)certDir.RelativeVirtualAddress + certDir.Size > fileSize)
        {
            return TrainerAuthenticodeStatus.InvalidOrUntrustedSignature;
        }

        try
        {
            Guid actionId = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            fixed (char* pPath = filePath)
            {
                var fileInfo = new WinTrustFileInfo
                {
                    cbStruct = (uint)sizeof(WinTrustFileInfo),
                    pcwszFilePath = pPath,
                    hFile = nint.Zero,
                    pgKnownSubject = nint.Zero,
                };

                var trustData = new WinTrustData
                {
                    cbStruct = (uint)sizeof(WinTrustData),
                    dwUIChoice = 2, // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE (strictly offline)
                    dwUnionChoice = 1, // WTD_CHOICE_FILE
                    pFile = &fileInfo,
                    dwStateAction = 1, // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x00000010u | 0x00001000u, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
                };

                int status = WinVerifyTrust(nint.Zero, ref actionId, ref trustData);

                // Always close WinVerifyTrust state handle.
                trustData.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                _ = WinVerifyTrust(nint.Zero, ref actionId, ref trustData);

                const int TrustENoSignature = unchecked((int)0x800B0100);
                return status switch
                {
                    0 => TrainerAuthenticodeStatus.ValidSigned,
                    TrustENoSignature => TrainerAuthenticodeStatus.Unsigned,
                    _ => TrainerAuthenticodeStatus.InvalidOrUntrustedSignature,
                };
            }
        }
        catch
        {
            return TrainerAuthenticodeStatus.Unknown;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustFileInfo
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WinTrustFileInfo* pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true)]
    private static unsafe partial uint GetShortPathNameW(
        char* lpszLongPath,
        char* lpszShortPath,
        uint cchBuffer);

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust", SetLastError = false)]
    private static partial int WinVerifyTrust(
        nint hwnd,
        ref Guid pgActionID,
        ref WinTrustData pWVTData);
}
