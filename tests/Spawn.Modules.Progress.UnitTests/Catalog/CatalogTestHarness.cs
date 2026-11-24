using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Microsoft.Data.Sqlite;
using Spawn.Core;

namespace Spawn.Modules.Progress.UnitTests.Catalog;

/// <summary>
/// Per-test isolated sandbox for <c>Spawn.Modules.Progress.UnitTests</c>, guaranteeing 100% separation
/// from <c>%LOCALAPPDATA%\spawn</c> and <c>%LOCALAPPDATA%\spawn-dev</c>.
/// </summary>
internal sealed class ProgressTestSandbox : IDisposable, IAsyncDisposable
{
    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public SpawnPaths Paths { get; }
    public SpawnDatabase Database { get; }

    public ProgressTestSandbox()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), $"spawn-progress-tests-{Guid.NewGuid():N}");
        DataDirectory = Path.Combine(RootDirectory, "data");
        Directory.CreateDirectory(DataDirectory);

        Paths = SpawnPaths.CreateIsolated(DataDirectory, workspaceRootHint: LocateRepositoryRoot());
        Paths.EnsureDirectoriesExist();
        Database = new SpawnDatabase(Paths);
    }

    public string CreateSubDirectory(params string[] segments)
    {
        string path = Path.Combine([RootDirectory, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string LocateRepositoryRoot()
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

        throw new DirectoryNotFoundException("Could not locate repository root containing Spawn.sln.");
    }

    public static string GetCatalogFixturePath(string fileName) =>
        Path.Combine(LocateRepositoryRoot(), "tests", "Spawn.Modules.Progress.UnitTests", "Fixtures", "Catalog", fileName);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>
/// Deterministic controllable <see cref="TimeProvider"/> for 24h / 7-day cache expiration tests.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset? initialUtc = null)
    {
        _utcNow = initialUtc ?? new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}

/// <summary>
/// Recorded outbound HTTP call metadata for robots.txt, concurrency, Referer, Accept, Cookie, and User-Agent assertions.
/// </summary>
internal sealed record RecordedHttpCall(
    Uri Uri,
    string? UserAgent,
    Uri? Referer,
    int ActiveConcurrencyAtEntry,
    int FlingConcurrencyAtEntry,
    string? Accept = null,
    string? AcceptLanguage = null,
    string? Cookie = null);

/// <summary>
/// Offline mock <see cref="HttpMessageHandler"/> recording every request and peak concurrency.
/// </summary>
internal sealed class RecordingMockHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly Lock _logLock = new();
    private readonly List<RecordedHttpCall> _calls = [];
    private int _inFlight;
    private int _flingInFlight;
    private int _maxObservedConcurrency;

    public RecordingMockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> syncResponder)
        : this((req, _) => Task.FromResult(syncResponder(req)))
    {
    }

    public RecordingMockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> asyncResponder)
    {
        _responder = asyncResponder;
    }

    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    public IReadOnlyList<RecordedHttpCall> Calls
    {
        get
        {
            lock (_logLock)
            {
                return [.. _calls];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool isFling = request.RequestUri is not null
            && request.RequestUri.Host.EndsWith("flingtrainer.com", StringComparison.OrdinalIgnoreCase);

        int active = Interlocked.Increment(ref _inFlight);
        int flingActive = isFling ? Interlocked.Increment(ref _flingInFlight) : 0;
        UpdateMaxConcurrency(active);

        try
        {
            string? ua = request.Headers.TryGetValues("User-Agent", out var values)
                ? string.Join(' ', values)
                : null;
            string? accept = request.Headers.TryGetValues("Accept", out var acceptValues)
                ? string.Join(", ", acceptValues)
                : null;
            string? acceptLang = request.Headers.TryGetValues("Accept-Language", out var langValues)
                ? string.Join(", ", langValues)
                : null;
            string? cookie = request.Headers.TryGetValues("Cookie", out var cookieValues)
                ? string.Join("; ", cookieValues)
                : null;
            var entry = new RecordedHttpCall(
                request.RequestUri!,
                ua,
                request.Headers.Referrer,
                active,
                flingActive,
                accept,
                acceptLang,
                cookie);
            lock (_logLock)
            {
                _calls.Add(entry);
            }

            return await _responder(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (isFling)
            {
                Interlocked.Decrement(ref _flingInFlight);
            }

            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void UpdateMaxConcurrency(int candidate)
    {
        int current;
        while (candidate > (current = Volatile.Read(ref _maxObservedConcurrency)))
        {
            if (Interlocked.CompareExchange(ref _maxObservedConcurrency, candidate, current) == current)
            {
                break;
            }
        }
    }
}

/// <summary>
/// Generates 100% harmless, inert synthetic Windows PE stubs (zero executable instructions)
/// and spec-compliant synthetic <c>.zip</c>, <c>.rar</c> (RAR4 / RAR5 / Solid), and invalid containers.
/// </summary>
internal static class SyntheticFixtureFactory
{
    /// <summary>
    /// Creates an inert minimal PE32/PE32+ stub with valid DOS/COFF/Optional headers and an optional
    /// embedded Win32 RT_MANIFEST resource (<c>asInvoker</c> or <c>requireAdministrator</c>).
    /// </summary>
    public static byte[] CreateSyntheticPeExecutable(
        bool is64Bit = true,
        bool requireAdmin = false,
        bool corruptCertificateTable = false,
        byte salt = 0)
    {
        string level = requireAdmin ? "requireAdministrator" : "asInvoker";
        string manifestXml = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security>
                  <requestedPrivileges>
                    <requestedExecutionLevel level="{level}" uiAccess="false"/>
                  </requestedPrivileges>
                </security>
              </trustInfo>
            </assembly>
            """;
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestXml);

        byte[] buf = new byte[1024];

        // DOS header ("MZ") + e_lfanew = 0x80
        buf[0] = 0x4D;
        buf[1] = 0x5A;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C, 4), 0x80);

        // PE\0\0 signature at 0x80
        buf[0x80] = (byte)'P';
        buf[0x81] = (byte)'E';

        // COFF Header (20 bytes at 0x84)
        ushort machine = is64Bit ? (ushort)0x8664 : (ushort)0x014C;
        ushort optSize = is64Bit ? (ushort)0xF0 : (ushort)0xE0;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x84, 2), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x86, 2), 1); // NumberOfSections = 1
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x94, 2), optSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x96, 2), 0x0002); // IMAGE_FILE_EXECUTABLE_IMAGE

        // Optional Header at 0x98
        int opt = 0x98;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(opt, 2), is64Bit ? (ushort)0x020B : (ushort)0x010B);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 32, 4), 0x1000); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 36, 4), 0x200);  // FileAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 56, 4), 0x2000); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(opt + 60, 4), 0x200);  // SizeOfHeaders
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(opt + 68, 2), 3);      // Subsystem: WindowsCui

        int numRvaOffset = is64Bit ? opt + 108 : opt + 92;
        int dataDirOffset = numRvaOffset + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(numRvaOffset, 4), 16);

        // Data Directory #2 (Resource Table): RVA 0x1000, Size 0x200
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(dataDirOffset + (2 * 8), 4), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(dataDirOffset + (2 * 8) + 4, 4), 0x200);

        if (corruptCertificateTable)
        {
            // Data Directory #4 (Certificate Table): deliberately points past EOF to simulate invalid signature table.
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(dataDirOffset + (4 * 8), 4), 0x3F0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(dataDirOffset + (4 * 8) + 4, 4), 0x200);
        }

        // Section Header #0 (.rsrc) at opt + optSize
        int sec = opt + optSize;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(buf, sec);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sec + 8, 4), 0x200);  // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sec + 12, 4), 0x1000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sec + 16, 4), 0x200);  // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sec + 20, 4), 0x200);  // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sec + 36, 4), 0x40000040);

        // .rsrc 3-level Resource Directory at file offset 0x200 (RVA 0x1000)
        int rsrc = 0x200;
        // Level 1: Type = 24 (RT_MANIFEST) -> subDir at 0x18
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(rsrc + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 16, 4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 20, 4), 0x80000018u);

        // Level 2: Name ID = 1 -> subDir at 0x30
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(rsrc + 0x18 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 0x18 + 16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 0x18 + 20, 4), 0x80000030u);

        // Level 3: Lang ID = 1033 -> dataEntry at 0x48
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(rsrc + 0x30 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 0x30 + 16, 4), 1033);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 0x30 + 20, 4), 0x48u);

        // IMAGE_RESOURCE_DATA_ENTRY at 0x48: RVA 0x1058, Size = manifestBytes.Length
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 0x48, 4), 0x1058u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(rsrc + 0x48 + 4, 4), (uint)manifestBytes.Length);

        manifestBytes.CopyTo(buf, rsrc + 0x58);
        buf[^1] = salt;
        return buf;
    }

    public static byte[] CreateZipBytes(params (string EntryName, byte[] Content, int ExternalAttributes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, extAttrs) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                if (extAttrs != 0)
                {
                    entry.ExternalAttributes = extAttrs;
                }

                using Stream s = entry.Open();
                s.Write(content, 0, content.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a spec-compliant RAR4 STORE archive (<c>52 61 72 21 1A 07 00</c>), optionally marked Solid (<c>MHD_SOLID</c>).
    /// </summary>
    public static byte[] CreateRar4ArchiveBytes(bool isSolid, params (string EntryName, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();

        // RAR4 7-byte marker block: Rar!\x1A\x07\x00
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        // Main Archive Header (type 0x73, 13 bytes)
        byte[] mainHdr = new byte[13];
        mainHdr[2] = 0x73;
        ushort mainFlags = isSolid ? (ushort)0x0008 : (ushort)0x0000; // MHD_SOLID = 0x0008
        BinaryPrimitives.WriteUInt16LittleEndian(mainHdr.AsSpan(3, 2), mainFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(mainHdr.AsSpan(5, 2), (ushort)mainHdr.Length);
        ushort mainCrc = (ushort)(Crc32.HashToUInt32(mainHdr.AsSpan(2)) & 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(mainHdr.AsSpan(0, 2), mainCrc);
        ms.Write(mainHdr);

        // File Headers (type 0x74, STORE method 0x30)
        for (int i = 0; i < entries.Length; i++)
        {
            var (name, content) = entries[i];
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            int headerSize = 32 + nameBytes.Length;
            byte[] fileHdr = new byte[headerSize];

            fileHdr[2] = 0x74;
            ushort fileFlags = 0x8000; // LONG_BLOCK
            if (isSolid && i > 0)
            {
                fileFlags |= 0x0010; // LHD_SOLID for subsequent entries
            }

            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(3, 2), fileFlags);
            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(5, 2), (ushort)headerSize);
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(7, 4), (uint)content.Length);  // PACK_SIZE
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(11, 4), (uint)content.Length); // UNP_SIZE
            fileHdr[15] = 0x02; // HOST_OS = Win32
            uint fileCrc = Crc32.HashToUInt32(content);
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(16, 4), fileCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(20, 4), 0x5A906000u); // FTIME
            fileHdr[24] = 20;   // UNP_VER
            fileHdr[25] = 0x30; // METHOD = STORE
            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(26, 2), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(28, 4), 0x00000020u); // FILE_ATTRIBUTE_ARCHIVE
            nameBytes.CopyTo(fileHdr, 32);

            ushort hdrCrc = (ushort)(Crc32.HashToUInt32(fileHdr.AsSpan(2)) & 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(0, 2), hdrCrc);

            ms.Write(fileHdr);
            ms.Write(content);
        }

        // End of Archive Header (type 0x7B, 7 bytes)
        byte[] endHdr = new byte[7];
        endHdr[2] = 0x7B;
        BinaryPrimitives.WriteUInt16LittleEndian(endHdr.AsSpan(3, 2), 0x4000);
        BinaryPrimitives.WriteUInt16LittleEndian(endHdr.AsSpan(5, 2), (ushort)endHdr.Length);
        ushort endCrc = (ushort)(Crc32.HashToUInt32(endHdr.AsSpan(2)) & 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(endHdr.AsSpan(0, 2), endCrc);
        ms.Write(endHdr);

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a spec-compliant RAR5 STORE archive (<c>52 61 72 21 1A 07 01 00</c>), optionally marked Solid (<c>MHFL_SOLID</c>).
    /// </summary>
    public static byte[] CreateRar5ArchiveBytes(bool isSolid, params (string EntryName, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();

        // RAR5 8-byte signature: Rar!\x1A\x07\x01\x00
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);

        // Main Archive Header (type = 1)
        using (var body = new MemoryStream())
        {
            WriteVint(body, 1); // Header type = MAIN
            WriteVint(body, 0); // Header flags = 0
            WriteVint(body, isSolid ? 0x0004UL : 0UL); // Archive flags (MHFL_SOLID = 0x0004)
            WriteRar5Block(ms, body.ToArray(), dataArea: null);
        }

        // File Headers (type = 2, STORE method = 0)
        Span<byte> crcBuf = stackalloc byte[4];
        for (int i = 0; i < entries.Length; i++)
        {
            var (name, content) = entries[i];
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            uint dataCrc = Crc32.HashToUInt32(content);

            using var body = new MemoryStream();
            WriteVint(body, 2); // Header type = FILE
            WriteVint(body, 0x0002UL); // Header flags = HFL_DATA
            WriteVint(body, (ulong)content.Length); // Data size (packed == unpacked for STORE)
            WriteVint(body, 0x0004UL); // File flags = FHFL_CRC32
            WriteVint(body, (ulong)content.Length); // Unpacked size
            WriteVint(body, 0x20UL); // Attributes = Archive
            BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, dataCrc);
            body.Write(crcBuf);
            // Compression info: version=0, solid bit (0x40) on 2nd+ solid file, method=0 (STORE)
            ulong compInfo = (isSolid && i > 0) ? 0x40UL : 0UL;
            WriteVint(body, compInfo);
            WriteVint(body, 0UL); // HostOS = Windows
            WriteVint(body, (ulong)nameBytes.Length);
            body.Write(nameBytes);

            WriteRar5Block(ms, body.ToArray(), dataArea: content);
        }

        // End of Archive Header (type = 5)
        using (var body = new MemoryStream())
        {
            WriteVint(body, 5); // Header type = ENDARC
            WriteVint(body, 0); // Header flags
            WriteVint(body, 0); // End-of-archive flags
            WriteRar5Block(ms, body.ToArray(), dataArea: null);
        }

        return ms.ToArray();
    }

    private static void WriteRar5Block(Stream output, byte[] headerBody, byte[]? dataArea)
    {
        using var sizeAndBody = new MemoryStream();
        WriteVint(sizeAndBody, (ulong)headerBody.Length);
        sizeAndBody.Write(headerBody);
        byte[] raw = sizeAndBody.ToArray();

        uint crc = Crc32.HashToUInt32(raw);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
        output.Write(crcBytes);
        output.Write(raw);

        if (dataArea is not null && dataArea.Length > 0)
        {
            output.Write(dataArea);
        }
    }

    private static void WriteVint(Stream stream, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            stream.WriteByte(b);
        }
        while (value != 0);
    }
}
