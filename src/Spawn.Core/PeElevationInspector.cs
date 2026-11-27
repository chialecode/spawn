using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

namespace Spawn.Core;

/// <summary>
/// Pure managed <see cref="PEReader"/> embedded <c>RT_MANIFEST</c> (and optional side-by-side <c>.manifest</c>)
/// elevation inspector. Clean-Host compliant (zero Win32 P/Invoke) and fail-safe on missing or malformed executables.
/// </summary>
public static class PeElevationInspector
{
    private const uint RtManifestResourceTypeId = 24;
    private const int MaxManifestBytes = 512 * 1024;

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="executablePath"/> declares
    /// <c>requireAdministrator</c> or <c>highestAvailable</c> in its embedded PE <c>RT_MANIFEST</c>
    /// or an adjacent <c>&lt;exe&gt;.manifest</c> file; safely returns <see langword="false"/> on any error.
    /// </summary>
    public static bool RequiresElevation(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(executablePath))
            {
                return false;
            }

            var fi = new FileInfo(executablePath);
            if (fi.Length >= 64)
            {
                using var fs = new FileStream(
                    executablePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (RequiresElevation(fs))
                {
                    return true;
                }
            }

            // Windows loader also honors side-by-side external manifests (<app>.exe.manifest).
            string externalManifest = executablePath + ".manifest";
            if (File.Exists(externalManifest))
            {
                var manifestInfo = new FileInfo(externalManifest);
                if (manifestInfo.Length is > 0 and <= MaxManifestBytes)
                {
                    byte[] raw = File.ReadAllBytes(externalManifest);
                    if (ParseRequiresElevationFromManifestXml(DecodeManifestXml(raw)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Inspects a readable PE stream for an embedded <c>RT_MANIFEST</c> requesting elevation.
    /// </summary>
    public static bool RequiresElevation(Stream peStream)
    {
        ArgumentNullException.ThrowIfNull(peStream);

        try
        {
            using var peReader = new PEReader(peStream, PEStreamOptions.LeaveOpen);
            PEHeader? peHeader = peReader.PEHeaders.PEHeader;
            if (peHeader is null)
            {
                return false;
            }

            DirectoryEntry resDir = peHeader.ResourceTableDirectory;
            if (resDir.RelativeVirtualAddress <= 0 || resDir.Size < 16)
            {
                return false;
            }

            PEMemoryBlock resBlock = peReader.GetSectionData(resDir.RelativeVirtualAddress);
            ReadOnlySpan<byte> rsrc = resBlock.GetContent().AsSpan();
            if (rsrc.Length < 16)
            {
                return false;
            }

            // Level 1: Resource Type directory -> RT_MANIFEST (ID 24).
            if (!TryFindResourceEntryOffset(rsrc, 0, RtManifestResourceTypeId, out int typeEntryOffset, out bool isTypeSubDir)
                || !isTypeSubDir)
            {
                return false;
            }

            // Level 2: Resource Name/ID directory -> iterate all manifest entries.
            foreach ((int nameOffset, bool isNameSubDir) in EnumerateResourceEntries(rsrc, typeEntryOffset))
            {
                if (!isNameSubDir)
                {
                    continue;
                }

                // Level 3: Resource Language directory -> inspect each manifest data entry.
                foreach ((int dataEntryOffset, bool isLangSubDir) in EnumerateResourceEntries(rsrc, nameOffset))
                {
                    if (isLangSubDir || dataEntryOffset < 0 || dataEntryOffset + 16 > rsrc.Length)
                    {
                        continue;
                    }

                    int dataRva = BinaryPrimitives.ReadInt32LittleEndian(rsrc.Slice(dataEntryOffset, 4));
                    int dataSize = BinaryPrimitives.ReadInt32LittleEndian(rsrc.Slice(dataEntryOffset + 4, 4));
                    if (dataRva <= 0 || dataSize <= 0 || dataSize > MaxManifestBytes)
                    {
                        continue;
                    }

                    PEMemoryBlock manifestBlock = peReader.GetSectionData(dataRva);
                    ReadOnlySpan<byte> manifestBytes = manifestBlock.GetContent().AsSpan();
                    int clampedSize = Math.Min(dataSize, manifestBytes.Length);
                    if (clampedSize <= 0)
                    {
                        continue;
                    }

                    string xml = DecodeManifestXml(manifestBytes[..clampedSize]);
                    if (ParseRequiresElevationFromManifestXml(xml))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static bool ParseRequiresElevationFromManifestXml(string xmlText)
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

    private static List<(int ChildOffset, bool IsSubDirectory)> EnumerateResourceEntries(
        ReadOnlySpan<byte> rsrc,
        int dirOffset)
    {
        var results = new List<(int, bool)>();
        if (dirOffset < 0 || dirOffset + 16 > rsrc.Length)
        {
            return results;
        }

        ushort namedCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 12, 2));
        ushort idCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 14, 2));
        int total = Math.Min(namedCount + idCount, 64);
        int entriesStart = dirOffset + 16;

        for (int i = 0; i < total; i++)
        {
            int entryPos = entriesStart + (i * 8);
            if (entryPos + 8 > rsrc.Length)
            {
                break;
            }

            uint offsetToData = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.Slice(entryPos + 4, 4));
            bool isSubDir = (offsetToData & 0x80000000u) != 0;
            int childOffset = (int)(offsetToData & 0x7FFFFFFFu);
            results.Add((childOffset, isSubDir));
        }

        return results;
    }

    private static string DecodeManifestXml(ReadOnlySpan<byte> bytes)
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
