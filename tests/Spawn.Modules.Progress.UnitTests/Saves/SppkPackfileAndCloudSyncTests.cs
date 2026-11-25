using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Blake3;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Storage;
using Spawn.Modules.Progress.Saves.Cloud;
using Xunit;
using ZstdSharp;

namespace Spawn.Modules.Progress.UnitTests.Saves;

public sealed class SppkPackfileAndCloudSyncTests : IDisposable
{
    private readonly string _tempRoot;

    public SppkPackfileAndCloudSyncTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-cloud-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(40);
            }
        }
    }

    // =========================================================================
    // 1. SPPK v1 Binary Layout, Integrity, Splitting & Tail-Range Tests
    // =========================================================================

    [Fact]
    public void SppkPackfile_ByteLevelLayout_MatchesSpecificationExactly()
    {
        byte[] raw1 = Encoding.UTF8.GetBytes("SPAWN Save Slot 1 — Dragon Boss Checkpoint " + new string('A', 2048));
        byte[] raw2 = Encoding.UTF8.GetBytes("SPAWN Save Slot 2 — Inventory & Quest Flags " + new string('B', 4096));

        SppkChunkInput c1 = CreateSppkChunk(raw1);
        SppkChunkInput c2 = CreateSppkChunk(raw2);

        const uint customFlags = 0x0012_3456u;
        SppkPackBuildResult built = SppkPackfileFormat.WritePack([c1, c2], flags: customFlags);
        byte[] bytes = built.PackBytes;

        // Header (8 bytes): Magic "SPPK" + Version 0x01 + 3-byte LE flags
        Assert.Equal((byte)'S', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'P', bytes[2]);
        Assert.Equal((byte)'K', bytes[3]);
        Assert.Equal(0x01, bytes[4]);
        Assert.Equal(0x56, bytes[5]);
        Assert.Equal(0x34, bytes[6]);
        Assert.Equal(0x12, bytes[7]);

        // Payload Body starts at offset 8
        int payloadLen = c1.CompressedBytes.Length + c2.CompressedBytes.Length;
        Assert.True(bytes.AsSpan(8, c1.CompressedBytes.Length).SequenceEqual(c1.CompressedBytes.Span));
        Assert.True(bytes.AsSpan(8 + c1.CompressedBytes.Length, c2.CompressedBytes.Length).SequenceEqual(c2.CompressedBytes.Span));

        // Footer Directory Index: 4 + N * 48 bytes
        int footerOffset = 8 + payloadLen;
        int expectedFooterLen = 4 + 2 * 48;
        Assert.Equal(expectedFooterLen, bytes.Length - footerOffset - 36);

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footerOffset, 4));
        Assert.Equal(2u, entryCount);

        // Verify Entry 0 (48 bytes)
        int e0 = footerOffset + 4;
        Assert.Equal(c1.ChunkBlake3Hex, Convert.ToHexStringLower(bytes.AsSpan(e0, 32)));
        Assert.Equal(8UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(e0 + 32, 8)));
        Assert.Equal((uint)c1.CompressedBytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(e0 + 40, 4)));
        Assert.Equal((uint)raw1.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(e0 + 44, 4)));

        // Verify Entry 1 (48 bytes)
        int e1 = e0 + 48;
        Assert.Equal(c2.ChunkBlake3Hex, Convert.ToHexStringLower(bytes.AsSpan(e1, 32)));
        Assert.Equal((ulong)(8 + c1.CompressedBytes.Length), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(e1 + 32, 8)));
        Assert.Equal((uint)c2.CompressedBytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(e1 + 40, 4)));
        Assert.Equal((uint)raw2.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(e1 + 44, 4)));

        // Trailer (36 bytes): FooterByteLength (4B LE) + PackBlake3 (32B over 0x0000..End-32)
        int trailerOffset = bytes.Length - 36;
        uint footerByteLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerOffset, 4));
        Assert.Equal((uint)expectedFooterLen, footerByteLen);

        string expectedPackHash = Hasher.Hash(bytes.AsSpan(0, bytes.Length - 32)).ToString();
        string storedPackHash = Convert.ToHexStringLower(bytes.AsSpan(bytes.Length - 32, 32));
        Assert.Equal(expectedPackHash, storedPackHash);
        Assert.Equal(expectedPackHash, built.PackBlake3Hex);
        Assert.Equal($"pack-{expectedPackHash}", built.PackId);

        // Full verification + single-chunk extraction
        SppkParsedFooter parsed = SppkPackfileFormat.VerifyAndReadFullPack(bytes);
        Assert.Equal(0x01, parsed.Version);
        Assert.Equal(customFlags, parsed.Flags);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal(raw1, SppkPackfileFormat.ExtractAndVerifyChunk(bytes, parsed.Entries[0]));
        Assert.Equal(raw2, SppkPackfileFormat.ExtractAndVerifyChunk(bytes, parsed.Entries[1]));
    }

    [Fact]
    public void SppkPackfile_RejectsCorruptedHeaderPayloadFooterAndChunkBytes()
    {
        SppkChunkInput chunk = CreateSppkChunk(Encoding.UTF8.GetBytes("integrity-check-save-payload-1234567890"));
        SppkPackBuildResult built = SppkPackfileFormat.WritePack([chunk]);

        // Corrupt magic
        byte[] badMagic = (byte[])built.PackBytes.Clone();
        badMagic[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => SppkPackfileFormat.VerifyAndReadFullPack(badMagic));

        // Corrupt payload byte (PackBlake3 mismatch)
        byte[] badPayload = (byte[])built.PackBytes.Clone();
        badPayload[SppkPackfileFormat.HeaderSize] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => SppkPackfileFormat.VerifyAndReadFullPack(badPayload));

        // Corrupt trailer hash
        byte[] badTrailer = (byte[])built.PackBytes.Clone();
        badTrailer[^1] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => SppkPackfileFormat.VerifyAndReadFullPack(badTrailer));

        // Single-chunk BLAKE3 mismatch on compressed bytes extracted via Range
        SppkChunkInput wrongChunk = CreateSppkChunk(Encoding.UTF8.GetBytes("different-payload"));
        Assert.Throws<InvalidDataException>(() =>
            SppkPackfileFormat.VerifyCompressedChunkBytes(wrongChunk.CompressedBytes.Span, chunk.ChunkBlake3Hex));
    }

    [Fact]
    public void SppkPackfile_PartitionChunksForPacks_SplitsAtLimitAndAllowsOversizedSoloChunk()
    {
        var rng = new Random(20261125);
        SppkChunkInput MakeRandomChunk(int size)
        {
            byte[] raw = new byte[size];
            rng.NextBytes(raw);
            return CreateSppkChunk(raw);
        }

        SppkChunkInput smallA = MakeRandomChunk(1024);
        SppkChunkInput smallB = MakeRandomChunk(1024);
        SppkChunkInput oversized = MakeRandomChunk(5000); // > 2500 limit -> solo pack
        SppkChunkInput smallC = MakeRandomChunk(1024);

        // Include a duplicate of smallA to verify deduplication
        IReadOnlyList<IReadOnlyList<SppkChunkInput>> batches = SppkPackfileFormat.PartitionChunksForPacks(
            [smallA, smallB, smallA, oversized, smallC],
            maxPayloadBytes: 2500);

        Assert.Equal(3, batches.Count);
        Assert.Equal(2, batches[0].Count); // smallA + smallB
        Assert.Single(batches[1]);         // oversized solo pack
        Assert.Equal(oversized.ChunkBlake3Hex, batches[1][0].ChunkBlake3Hex);
        Assert.Single(batches[2]);         // smallC
        Assert.Equal(smallC.ChunkBlake3Hex, batches[2][0].ChunkBlake3Hex);
    }

    [Fact]
    public void SppkPackfile_TailOnlyRangeParsing_HandlesSingleShotAndMultiShotLargeFooter()
    {
        // 120 chunks -> footer = 4 + 120 * 48 = 5764 bytes (> 4096 default tail read!)
        var chunks = new List<SppkChunkInput>(120);
        for (int i = 0; i < 120; i++)
        {
            chunks.Add(CreateSppkChunk(Encoding.UTF8.GetBytes($"chunk-index-{i:D4}-payload-content")));
        }

        SppkPackBuildResult built = SppkPackfileFormat.WritePack(chunks);
        byte[] pack = built.PackBytes;

        // First simulated Range: bytes=-4096 -> must report exact requiredTailBytes (5764 + 36 = 5800)
        ReadOnlySpan<byte> firstTail = pack.AsSpan(pack.Length - SppkPackfileFormat.DefaultTailReadBytes);
        bool parsedOnFirst = SppkPackfileFormat.TryParseTailFooter(
            firstTail,
            pack.Length,
            out SppkParsedFooter? footer1,
            out int requiredTailBytes);

        Assert.False(parsedOnFirst);
        Assert.Null(footer1);
        Assert.Equal(4 + 120 * 48 + 36, requiredTailBytes);

        // Second simulated Range: bytes=-5800 -> succeeds and yields all 120 entries
        ReadOnlySpan<byte> exactTail = pack.AsSpan(pack.Length - requiredTailBytes);
        bool parsedOnSecond = SppkPackfileFormat.TryParseTailFooter(
            exactTail,
            pack.Length,
            out SppkParsedFooter? footer2,
            out _);

        Assert.True(parsedOnSecond);
        Assert.NotNull(footer2);
        Assert.Equal(120, footer2!.Entries.Count);
        Assert.Equal(built.PackBlake3Hex, footer2.PackBlake3Hex);

        // Verify 73rd chunk via direct offset slice
        SppkIndexEntry e73 = footer2.Entries[73];
        byte[] slice73 = pack.AsSpan((int)e73.PackOffset, (int)e73.ZstdLength).ToArray();
        byte[] raw73 = SppkPackfileFormat.VerifyCompressedChunkBytes(slice73, e73.ChunkBlake3Hex, e73.RawLength);
        Assert.Equal("chunk-index-0073-payload-content", Encoding.UTF8.GetString(raw73));
    }

    // =========================================================================
    // 2. AWS SigV4 Deterministic Test Vectors & Mock S3 / WebDAV Transports
    // =========================================================================

    [Fact]
    public void S3SigV4Signer_MatchesOfficialAwsTestVector_AndSupportsPathAndVirtualHostStyles()
    {
        // Official AWS S3 SigV4 Example: GET Object with Range: bytes=0-9
        var uri = new Uri("https://examplebucket.s3.amazonaws.com/test.txt");
        var timestamp = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

        S3SigV4SignatureResult sig = S3SigV4Signer.CreateSignature(
            httpMethod: "GET",
            requestUri: uri,
            payloadSha256: S3SigV4Signer.EmptyPayloadSha256,
            accessKey: "AKIAIOSFODNN7EXAMPLE",
            secretKey: "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            region: "us-east-1",
            timestampUtc: timestamp,
            service: "s3",
            additionalSignedHeaders: new Dictionary<string, string> { ["range"] = "bytes=0-9" });

        Assert.Equal("20130524T000000Z", sig.AmzDate);
        Assert.Equal("20130524/us-east-1/s3/aws4_request", sig.CredentialScope);
        Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", sig.Signature);
        Assert.Contains("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date", sig.AuthorizationHeader, StringComparison.Ordinal);

        // Verify Path-style vs Virtual-hosted-style URI builders
        Uri pathStyle = S3SigV4Signer.BuildUri(
            "https://r2.example.invalid",
            "spawn-saves",
            "spawn-sync/packs/pack-abc.sppk",
            pathStyleAddressing: true);
        Assert.Equal("https://r2.example.invalid/spawn-saves/spawn-sync/packs/pack-abc.sppk", pathStyle.AbsoluteUri);

        Uri virtualHost = S3SigV4Signer.BuildUri(
            "https://s3.us-east-1.amazonaws.com",
            "spawn-saves",
            "spawn-sync/packs/pack-abc.sppk",
            pathStyleAddressing: false);
        Assert.Equal("https://spawn-saves.s3.us-east-1.amazonaws.com/spawn-sync/packs/pack-abc.sppk", virtualHost.AbsoluteUri);
    }

    [Fact]
    public async Task S3SyncTransport_OfflineMockProtocol_PutGetRangeHeadAndPaginatedListObjectsV2()
    {
        var store = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seenAuthHeaders = new List<string>();
        var handler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            Assert.True(req.Headers.TryGetValues("Authorization", out IEnumerable<string>? authVals));
            string auth = authVals!.Single();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIA-TEST/", auth, StringComparison.Ordinal);
            Assert.True(req.Headers.Contains("x-amz-date"));
            Assert.True(req.Headers.Contains("x-amz-content-sha256"));
            seenAuthHeaders.Add(auth);

            string absPath = req.RequestUri!.AbsolutePath;
            string query = req.RequestUri.Query;

            if (req.Method == HttpMethod.Get && query.Contains("list-type=2", StringComparison.Ordinal))
            {
                // Return paginated ListObjectsV2 XML (page 1 -> page 2)
                string[] allKeys = store.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToArray();
                bool isPage2 = query.Contains("continuation-token=page-2", StringComparison.Ordinal);
                string[] pageKeys = !isPage2 && allKeys.Length > 1 ? allKeys[..1] : (isPage2 ? allKeys[1..] : allKeys);
                bool truncated = !isPage2 && allKeys.Length > 1;

                XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
                var root = new XElement(
                    ns + "ListBucketResult",
                    new XElement(ns + "IsTruncated", truncated ? "true" : "false"),
                    truncated ? new XElement(ns + "NextContinuationToken", "page-2") : null,
                    pageKeys.Select(k => new XElement(
                        ns + "Contents",
                        new XElement(ns + "Key", k),
                        new XElement(ns + "Size", store[k].Length.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "LastModified", "2026-11-25T10:00:00.000Z"))));

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(root.ToString(), Encoding.UTF8, "application/xml"),
                };
            }

            // Path-style: /my-bucket/<key>
            const string bucketPrefix = "/my-bucket/";
            Assert.StartsWith(bucketPrefix, absPath, StringComparison.Ordinal);
            string key = Uri.UnescapeDataString(absPath[bucketPrefix.Length..]);

            if (req.Method == HttpMethod.Put)
            {
                store[key] = await req.Content!.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (req.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(store.ContainsKey(key) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }

            if (req.Method == HttpMethod.Get)
            {
                if (!store.TryGetValue(key, out byte[]? data))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (req.Headers.Range is not null)
                {
                    RangeItemHeaderValue range = req.Headers.Range.Ranges.Single();
                    long start;
                    long end;
                    if (range.From.HasValue)
                    {
                        start = range.From.Value;
                        end = Math.Min(data.Length - 1, range.To ?? data.Length - 1);
                    }
                    else
                    {
                        long suffixLen = range.To!.Value;
                        start = Math.Max(0, data.Length - suffixLen);
                        end = data.Length - 1;
                    }

                    byte[] slice = data[(int)start..((int)end + 1)];
                    var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new ByteArrayContent(slice),
                    };
                    partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
                    return partial;
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        using var http = new HttpClient(handler);
        using var transport = new S3SyncTransport(
            endpointUrl: "https://s3.local.invalid",
            bucketOrRoot: "my-bucket",
            region: "auto",
            pathStyleAddressing: true,
            secrets: new CloudSyncEndpointSecrets("AKIA-TEST", "SECRET-TEST"),
            httpClient: http);

        CloudSyncEndpointTestResult testRes = await transport.TestConnectionAsync();
        Assert.True(testRes.Succeeded);
        Assert.True(await transport.ExistsAsync("repo.json"));

        byte[] samplePayload = Encoding.UTF8.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        await transport.PutBytesAsync("packs/pack-demo.sppk", samplePayload);

        Assert.Equal(samplePayload, await transport.GetBytesAsync("packs/pack-demo.sppk"));

        // Exact offset range
        CloudSyncRangeResult? mid = await transport.GetRangeAsync("packs/pack-demo.sppk", 10, 6);
        Assert.NotNull(mid);
        Assert.Equal("ABCDEF", Encoding.UTF8.GetString(mid!.Data));
        Assert.Equal(samplePayload.Length, mid.TotalLength);

        // Tail suffix range (-6)
        CloudSyncRangeResult? tail = await transport.GetRangeAsync("packs/pack-demo.sppk", -1, 6);
        Assert.NotNull(tail);
        Assert.Equal("UVWXYZ", Encoding.UTF8.GetString(tail!.Data));
        Assert.Equal(samplePayload.Length - 6, tail.Offset);

        // Paginated ListObjectsV2 returns both repo.json and packs/pack-demo.sppk
        IReadOnlyList<CloudSyncObjectInfo> listed = await transport.ListPrefixAsync(string.Empty);
        Assert.Contains(listed, static o => o.RelativePath == "repo.json");
        Assert.Contains(listed, static o => o.RelativePath == "packs/pack-demo.sppk");
        Assert.NotEmpty(seenAuthHeaders);
    }

    [Fact]
    public async Task WebDavSyncTransport_OfflineMockProtocol_MkcolPutPropfindRangeAndBasicAuth()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var collections = new HashSet<string>(StringComparer.Ordinal);
        var methodsSeen = new List<string>();

        var handler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            Assert.NotNull(req.Headers.Authorization);
            Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
            string decodedCreds = Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization.Parameter!));
            Assert.Equal("alice:hunter2", decodedCreds);
            methodsSeen.Add(req.Method.Method);

            string path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
            if (req.Method.Method == "MKCOL")
            {
                collections.Add(path.TrimEnd('/') + "/");
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (req.Method == HttpMethod.Put)
            {
                files[path] = await req.Content!.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (req.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(files.ContainsKey(path) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }

            if (req.Method.Method == "PROPFIND")
            {
                Assert.Equal("1", req.Headers.GetValues("Depth").Single());
                string dirPrefix = path.TrimEnd('/') + "/";
                XNamespace dav = "DAV:";
                var responses = new List<XElement>
                {
                    new(dav + "response",
                        new XElement(dav + "href", dirPrefix),
                        new XElement(dav + "propstat",
                            new XElement(dav + "prop",
                                new XElement(dav + "resourcetype", new XElement(dav + "collection"))))),
                };

                foreach (var (filePath, content) in files)
                {
                    if (filePath.StartsWith(dirPrefix, StringComparison.Ordinal))
                    {
                        responses.Add(new XElement(
                            dav + "response",
                            new XElement(dav + "href", filePath),
                            new XElement(dav + "propstat",
                                new XElement(dav + "prop",
                                    new XElement(dav + "resourcetype"),
                                    new XElement(dav + "getcontentlength", content.Length.ToString(CultureInfo.InvariantCulture)),
                                    new XElement(dav + "getlastmodified", "Wed, 25 Nov 2026 10:00:00 GMT")))));
                    }
                }

                var multiStatus = new XElement(dav + "multistatus", responses);
                return new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(multiStatus.ToString(), Encoding.UTF8, "application/xml"),
                };
            }

            if (req.Method == HttpMethod.Get)
            {
                if (!files.TryGetValue(path, out byte[]? data))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                // Intentionally return 200 OK full body on Range to test graceful fallback slicing too!
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        using var http = new HttpClient(handler);
        using var transport = new WebDavSyncTransport(
            endpointUrl: "https://dav.local.invalid/remote.php/dav/files/alice",
            rootSubPath: "My Saves",
            secrets: new CloudSyncEndpointSecrets("alice", "hunter2"),
            httpClient: http);

        CloudSyncEndpointTestResult testRes = await transport.TestConnectionAsync();
        Assert.True(testRes.Succeeded);
        Assert.Contains("MKCOL", methodsSeen);

        byte[] headPayload = Encoding.UTF8.GetBytes("{\"headSnapshotId\":\"01DEMO\"}");
        await transport.PutBytesAsync("heads/game-1/pc-a.json", headPayload, "application/json");

        IReadOnlyList<CloudSyncObjectInfo> listed = await transport.ListPrefixAsync("heads/game-1/");
        CloudSyncObjectInfo item = Assert.Single(listed);
        Assert.Equal("heads/game-1/pc-a.json", item.RelativePath);
        Assert.Equal(headPayload.Length, item.SizeBytes);

        // Verify 200 OK Range fallback slices accurately
        CloudSyncRangeResult? tail = await transport.GetRangeAsync("heads/game-1/pc-a.json", -1, 8);
        Assert.NotNull(tail);
        Assert.Equal("01DEMO\"}", Encoding.UTF8.GetString(tail!.Data));
    }

    // =========================================================================
    // 3. DPAPI Endpoint CRUD & Secret Encryption Verification
    // =========================================================================

    [Fact]
    public async Task EndpointCrud_ProtectsSecretsWithDpapiAndNeverStoresPlaintextInSqlite()
    {
        using var node = new MachineNode(_tempRoot, "dpapi-test", "Desktop-DPAPI");
        string localSyncDir = Path.Combine(_tempRoot, "dpapi-sync-root");

        CloudSyncEndpointRecord saved = await node.SyncService.SaveEndpointAsync(
            new CloudSyncEndpointRecord
            {
                EndpointId = string.Empty,
                DisplayName = "NAS WebDAV",
                ProviderType = CloudSyncProviderType.WebDav,
                EndpointUrl = "https://nas.local.invalid/dav",
                BucketOrRoot = "spawn",
                IsDefault = true,
            },
            new CloudSyncEndpointSecrets("user-secret-id", "super-secret-password-999"));

        Assert.False(string.IsNullOrWhiteSpace(saved.EndpointId));
        Assert.True(saved.HasSecretConfigured);
        Assert.NotNull(saved.SecretCredentialId);

        // Verify .sec file exists in isolated secrets/ dir and does NOT contain plaintext
        string secPath = Path.Combine(node.Paths.SecretsDirectory, $"{saved.SecretCredentialId}.sec");
        Assert.True(File.Exists(secPath));
        string rawCipherText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(secPath));
        Assert.DoesNotContain("super-secret-password-999", rawCipherText, StringComparison.Ordinal);

        IReadOnlyList<CloudSyncEndpointRecord> listed = await node.SyncService.ListEndpointsAsync();
        Assert.Single(listed);
        Assert.True(listed[0].HasSecretConfigured);

        // LocalFolder endpoint connectivity test
        CloudSyncEndpointRecord localEp = await node.SyncService.SaveEndpointAsync(
            new CloudSyncEndpointRecord
            {
                EndpointId = string.Empty,
                DisplayName = "Local Folder",
                ProviderType = CloudSyncProviderType.LocalFolder,
                EndpointUrl = localSyncDir,
                IsDefault = false,
            });
        CloudSyncEndpointTestResult connTest = await node.SyncService.TestEndpointConnectionAsync(localEp.EndpointId);
        Assert.True(connTest.Succeeded);
        Assert.True(File.Exists(Path.Combine(localSyncDir, "spawn-sync", "repo.json")));

        // Delete removes DB row AND deletes DPAPI .sec file
        Assert.True(await node.SyncService.DeleteEndpointAsync(saved.EndpointId));
        Assert.False(File.Exists(secPath));
    }

    // =========================================================================
    // 4. End-to-End 2-Machine LocalFolder Sync + True Divergence Simulation
    // =========================================================================

    [Fact]
    public async Task TwoMachineLocalFolderSync_PushPullProgressDivergeAndPreserveBothBranches()
    {
        string sharedRemoteRoot = Path.Combine(_tempRoot, "shared-nas");
        const string gameId = "game-elden-001";

        using var machineA = new MachineNode(_tempRoot, "machine-a", "Desktop-A");
        using var machineB = new MachineNode(_tempRoot, "machine-b", "Handheld-B");

        await machineA.SeedGameAndLocalEndpointAsync(gameId, sharedRemoteRoot);
        await machineB.SeedGameAndLocalEndpointAsync(gameId, sharedRemoteRoot);

        // ---- Step 1: Machine A creates initial snapshot S1 and pushes ----
        SaveSnapshotRecord s1 = await machineA.CommitLocalSaveAsync(
            gameId,
            parentSnapshotId: null,
            note: "Act 1 Clear",
            playtimeSeconds: 3600,
            files: new Dictionary<string, string>
            {
                ["<winAppData>/Elden/slot0.sl2"] = "SAVE-V1-HERO-LVL-10-" + new string('X', 2048),
                ["<winAppData>/Elden/meta.dat"] = "META-V1-CONSTANT-BLOCK",
            });

        CloudSyncPlanPreview planPush1 = await machineA.SyncService.PlanSyncAsync(gameId);
        Assert.Equal(CloudSyncRelation.FastForwardPush, planPush1.Relation);
        Assert.True(planPush1.MissingRemoteChunkCount >= 2);
        Assert.True(planPush1.EstimatedUploadBytes > 0);

        CloudSyncExecutionReport push1 = await machineA.SyncService.ExecuteSyncAsync(gameId);
        Assert.True(push1.Succeeded);
        Assert.Equal(CloudSyncRelation.FastForwardPush, push1.Relation);
        Assert.Equal(1, push1.UploadedPackCount);
        Assert.Equal(s1.SnapshotId, push1.ResultHeadSnapshotId);

        // Verify remote layout: repo.json, packs/*.sppk, snapshots/<GameId>/<S1>.json, heads/<GameId>/<MachineA>.json
        string syncDir = Path.Combine(sharedRemoteRoot, "spawn-sync");
        Assert.True(File.Exists(Path.Combine(syncDir, "repo.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(syncDir, "packs"), "pack-*.sppk"));
        Assert.True(File.Exists(Path.Combine(syncDir, "snapshots", gameId, $"{s1.SnapshotId}.json")));
        Assert.True(File.Exists(Path.Combine(syncDir, "heads", gameId, $"{machineA.MachineId}.json")));

        // Verify Machine A's cas_chunks.pack_id & save_game_state.last_synced_at_utc updated
        SaveGameStateRecord? stateAAfterPush1 = await machineA.Database.GetSaveGameStateAsync(gameId);
        Assert.NotNull(stateAAfterPush1?.LastSyncedAtUtc);
        Assert.Equal(CloudSyncRelation.UpToDate, (await machineA.SyncService.PlanSyncAsync(gameId)).Relation);

        // ---- Step 2: Machine B pulls S1 and verifies UpToDate ----
        CloudSyncPlanPreview planPullB1 = await machineB.SyncService.PlanSyncAsync(gameId);
        Assert.Equal(CloudSyncRelation.FastForwardPull, planPullB1.Relation);
        Assert.Equal(s1.SnapshotId, planPullB1.RemoteHead?.HeadSnapshotId);
        Assert.True(planPullB1.MissingLocalChunkCount >= 2);

        CloudSyncExecutionReport pullB1 = await machineB.SyncService.ExecuteSyncAsync(gameId);
        Assert.True(pullB1.Succeeded);
        Assert.Equal(CloudSyncRelation.FastForwardPull, pullB1.Relation);
        Assert.Equal(s1.SnapshotId, pullB1.ResultHeadSnapshotId);
        Assert.True(pullB1.DownloadedChunkCount >= 2);
        Assert.Equal(s1.SnapshotId, machineB.FakeTimeMachine.LastRolledBackSnapshotId);
        Assert.Equal("SAVE-V1-HERO-LVL-10-" + new string('X', 2048), machineB.ReadRestoredSave("<winAppData>/Elden/slot0.sl2"));

        // Machine B is now UpToDate
        Assert.Equal(CloudSyncRelation.UpToDate, (await machineB.SyncService.PlanSyncAsync(gameId)).Relation);

        // ---- Step 3: Machine B progresses offline to S2 (parent S1) & pushes delta ----
        SaveSnapshotRecord s2 = await machineB.CommitLocalSaveAsync(
            gameId,
            parentSnapshotId: s1.SnapshotId,
            note: "Act 2 Boss Defeated on Handheld",
            playtimeSeconds: 7200,
            files: new Dictionary<string, string>
            {
                ["<winAppData>/Elden/slot0.sl2"] = "SAVE-V2-HERO-LVL-35-" + new string('Y', 2048),
                ["<winAppData>/Elden/meta.dat"] = "META-V1-CONSTANT-BLOCK", // unchanged -> deduplicated!
            });

        CloudSyncPlanPreview planPushB2 = await machineB.SyncService.PlanSyncAsync(gameId);
        Assert.Equal(CloudSyncRelation.FastForwardPush, planPushB2.Relation);
        Assert.Equal(1, planPushB2.MissingRemoteChunkCount); // Only changed slot0.sl2 chunk!

        CloudSyncExecutionReport pushB2 = await machineB.SyncService.ExecuteSyncAsync(gameId);
        Assert.True(pushB2.Succeeded);
        Assert.Equal(1, pushB2.UploadedPackCount);

        // ---- Step 4: Machine A runs FastForwardPull to S2 ----
        CloudSyncPlanPreview planPullA2 = await machineA.SyncService.PlanSyncAsync(gameId);
        Assert.Equal(CloudSyncRelation.FastForwardPull, planPullA2.Relation);
        Assert.Equal(s2.SnapshotId, planPullA2.RemoteHead?.HeadSnapshotId);
        Assert.Equal(1, planPullA2.MissingLocalChunkCount); // Only delta chunk!

        CloudSyncExecutionReport pullA2 = await machineA.SyncService.ExecuteSyncAsync(gameId);
        Assert.True(pullA2.Succeeded);
        Assert.Equal(1, pullA2.DownloadedChunkCount);
        Assert.Equal("SAVE-V2-HERO-LVL-35-" + new string('Y', 2048), machineA.ReadRestoredSave("<winAppData>/Elden/slot0.sl2"));
        Assert.Equal(CloudSyncRelation.UpToDate, (await machineA.SyncService.PlanSyncAsync(gameId)).Relation);

        // ---- Step 5: TRUE DIVERGENCE (Both offline edit from S2!) ----
        SaveSnapshotRecord s3a = await machineA.CommitLocalSaveAsync(
            gameId,
            parentSnapshotId: s2.SnapshotId,
            note: "Desktop Ending A (Ranni)",
            playtimeSeconds: 10800,
            files: new Dictionary<string, string>
            {
                ["<winAppData>/Elden/slot0.sl2"] = "SAVE-V3A-DESKTOP-ENDING-A",
                ["<winAppData>/Elden/meta.dat"] = "META-V1-CONSTANT-BLOCK",
            });
        Assert.True((await machineA.SyncService.ExecuteSyncAsync(gameId)).Succeeded);

        SaveSnapshotRecord s3b = await machineB.CommitLocalSaveAsync(
            gameId,
            parentSnapshotId: s2.SnapshotId,
            note: "Handheld Ending B (Frenzied Flame)",
            playtimeSeconds: 10200,
            files: new Dictionary<string, string>
            {
                ["<winAppData>/Elden/slot0.sl2"] = "SAVE-V3B-HANDHELD-ENDING-B",
                ["<winAppData>/Elden/meta.dat"] = "META-V1-CONSTANT-BLOCK",
            });

        // Machine B detects True Divergence with CommonAncestor == S2
        CloudSyncPlanPreview divergedPlan = await machineB.SyncService.PlanSyncAsync(gameId);
        Assert.Equal(CloudSyncRelation.Diverged, divergedPlan.Relation);
        Assert.Equal(s2.SnapshotId, divergedPlan.CommonAncestorSnapshotId);
        Assert.Equal(s3b.SnapshotId, divergedPlan.LocalHeadSnapshot?.SnapshotId);
        Assert.Equal(s3a.SnapshotId, divergedPlan.RemoteHead?.HeadSnapshotId);

        // Refuses silent overwrite when resolution is null!
        CloudSyncExecutionReport refused = await machineB.SyncService.ExecuteSyncAsync(gameId, divergenceResolution: null);
        Assert.False(refused.Succeeded);
        Assert.Equal(CloudSyncRelation.Diverged, refused.Relation);
        Assert.True((await machineB.Database.GetSaveGameStateAsync(gameId))!.HasUnresolvedCloudConflict);

        // ---- Step 5A: Resolve via AdoptRemoteAndBackupLocal ----
        CloudSyncExecutionReport adopted = await machineB.SyncService.ExecuteSyncAsync(
            gameId,
            divergenceResolution: CloudDivergenceResolution.AdoptRemoteAndBackupLocal);

        Assert.True(adopted.Succeeded);
        Assert.Equal(CloudSyncRelation.Diverged, adopted.Relation);
        Assert.Equal(s3a.SnapshotId, adopted.ResultHeadSnapshotId);
        Assert.False(string.IsNullOrWhiteSpace(adopted.DivergenceBackupBranchId));
        Assert.StartsWith("backup-Handheld-B-", adopted.DivergenceBackupBranchName!, StringComparison.Ordinal);

        // Verify conflict cleared, disk has Desktop's S3a, AND Handheld's S3b is preserved on a separate pinned branch!
        SaveGameStateRecord stateBAfterAdopt = (await machineB.Database.GetSaveGameStateAsync(gameId))!;
        Assert.False(stateBAfterAdopt.HasUnresolvedCloudConflict);
        Assert.Equal("SAVE-V3A-DESKTOP-ENDING-A", machineB.ReadRestoredSave("<winAppData>/Elden/slot0.sl2"));

        IReadOnlyList<SaveBranchRecord> branchesB = await machineB.Database.GetSaveBranchesByGameAsync(gameId);
        Assert.Equal(2, branchesB.Count);
        SaveBranchRecord backupBranchB = branchesB.Single(b => b.BranchId == adopted.DivergenceBackupBranchId);
        Assert.True(backupBranchB.SnapshotCount >= 1);
        Assert.NotNull(backupBranchB.HeadSnapshotId);
        SaveSnapshotRecord? preservedLocalTip = await machineB.Database.GetSaveSnapshotByIdAsync(s3b.SnapshotId);
        Assert.True(preservedLocalTip!.IsPinned);
        Assert.Equal(CloudSyncRelation.UpToDate, (await machineB.SyncService.PlanSyncAsync(gameId)).Relation);

        // ---- Step 5B: Second Divergence -> Resolve via KeepLocalAndBackupRemote ----
        SaveSnapshotRecord s4a = await machineA.CommitLocalSaveAsync(
            gameId,
            parentSnapshotId: s3a.SnapshotId,
            note: "Desktop NG+ Prep",
            playtimeSeconds: 12000,
            files: new Dictionary<string, string>
            {
                ["<winAppData>/Elden/slot0.sl2"] = "SAVE-V4A-DESKTOP-NGPLUS",
                ["<winAppData>/Elden/meta.dat"] = "META-V1-CONSTANT-BLOCK",
            });
        Assert.True((await machineA.SyncService.ExecuteSyncAsync(gameId)).Succeeded);

        SaveSnapshotRecord s4b = await machineB.CommitLocalSaveAsync(
            gameId,
            parentSnapshotId: s3a.SnapshotId,
            note: "Handheld DLC Boss Rush",
            playtimeSeconds: 12500,
            files: new Dictionary<string, string>
            {
                ["<winAppData>/Elden/slot0.sl2"] = "SAVE-V4B-HANDHELD-DLC",
                ["<winAppData>/Elden/meta.dat"] = "META-V1-CONSTANT-BLOCK",
            });

        Assert.Equal(CloudSyncRelation.Diverged, (await machineB.SyncService.PlanSyncAsync(gameId)).Relation);

        CloudSyncExecutionReport keptLocal = await machineB.SyncService.ExecuteSyncAsync(
            gameId,
            divergenceResolution: CloudDivergenceResolution.KeepLocalAndBackupRemote);

        Assert.True(keptLocal.Succeeded);
        Assert.Equal(CloudSyncRelation.Diverged, keptLocal.Relation);
        Assert.False(string.IsNullOrWhiteSpace(keptLocal.DivergenceBackupBranchId));
        Assert.StartsWith("remote-Desktop-A-", keptLocal.DivergenceBackupBranchName!, StringComparison.Ordinal);
        Assert.NotNull(keptLocal.ResultHeadSnapshotId);

        // Verify Machine B now has 3 branches: main (merge commit), backup-Handheld-B, remote-Desktop-A!
        IReadOnlyList<SaveBranchRecord> branchesBAfterKeep = await machineB.Database.GetSaveBranchesByGameAsync(gameId);
        Assert.Equal(3, branchesBAfterKeep.Count);
        SaveBranchRecord remotePreservedBranch = branchesBAfterKeep.Single(b => b.BranchId == keptLocal.DivergenceBackupBranchId);
        Assert.Equal(s4a.SnapshotId, remotePreservedBranch.HeadSnapshotId);
        Assert.True((await machineB.Database.GetSaveSnapshotByIdAsync(s4a.SnapshotId))!.IsPinned);

        SaveSnapshotRecord mergeTip = (await machineB.Database.GetSaveSnapshotByIdAsync(keptLocal.ResultHeadSnapshotId!))!;
        Assert.Equal(s4b.SnapshotId, mergeTip.ParentSnapshotId);
        Assert.Equal(s4a.SnapshotId, mergeTip.MergeParentSnapshotId);

        // Machine B is UpToDate, and Machine A can now FastForwardPull the resolved tip cleanly!
        Assert.Equal(CloudSyncRelation.UpToDate, (await machineB.SyncService.PlanSyncAsync(gameId)).Relation);
        CloudSyncPlanPreview planAFinal = await machineA.SyncService.PlanSyncAsync(gameId);
        Assert.Equal(CloudSyncRelation.FastForwardPull, planAFinal.Relation);
        Assert.Equal(mergeTip.SnapshotId, planAFinal.RemoteHead?.HeadSnapshotId);

        CloudSyncExecutionReport pullAFinal = await machineA.SyncService.ExecuteSyncAsync(gameId);
        Assert.True(pullAFinal.Succeeded);
        Assert.Equal("SAVE-V4B-HANDHELD-DLC", machineA.ReadRestoredSave("<winAppData>/Elden/slot0.sl2"));
        Assert.Equal(CloudSyncRelation.UpToDate, (await machineA.SyncService.PlanSyncAsync(gameId)).Relation);
    }

    [Fact]
    public async Task SyncAllConfiguredGamesAsync_SyncsMultipleGamesIndependently()
    {
        string sharedRemoteRoot = Path.Combine(_tempRoot, "sync-all-nas");
        using var machine = new MachineNode(_tempRoot, "sync-all-pc", "Desktop-All");

        await machine.SeedGameAndLocalEndpointAsync("game-alpha", sharedRemoteRoot);
        await machine.SeedGameAndLocalEndpointAsync("game-beta", sharedRemoteRoot);

        await machine.CommitLocalSaveAsync(
            "game-alpha",
            null,
            "Alpha S1",
            600,
            new Dictionary<string, string> { ["<winAppData>/Alpha/s.sav"] = "ALPHA-1" });
        await machine.CommitLocalSaveAsync(
            "game-beta",
            null,
            "Beta S1",
            1200,
            new Dictionary<string, string> { ["<winAppData>/Beta/s.sav"] = "BETA-1" });

        IReadOnlyList<CloudSyncExecutionReport> reports = await machine.SyncService.SyncAllConfiguredGamesAsync();
        Assert.Equal(2, reports.Count);
        Assert.All(reports, static r =>
        {
            Assert.True(r.Succeeded);
            Assert.Equal(CloudSyncRelation.FastForwardPush, r.Relation);
        });

        // Second pass -> both UpToDate
        IReadOnlyList<CloudSyncExecutionReport> secondPass = await machine.SyncService.SyncAllConfiguredGamesAsync();
        Assert.Equal(2, secondPass.Count);
        Assert.All(secondPass, static r =>
        {
            Assert.True(r.Succeeded);
            Assert.Equal(CloudSyncRelation.UpToDate, r.Relation);
        });
    }

    // =========================================================================
    // Test Helpers & Offline Fakes
    // =========================================================================

    private static SppkChunkInput CreateSppkChunk(byte[] rawBytes)
    {
        string hashHex = Hasher.Hash(rawBytes).ToString();
        byte[] compressed;
        using (var compressor = new Compressor(3))
        {
            compressed = compressor.Wrap(rawBytes).ToArray();
        }

        return new SppkChunkInput(hashHex, compressed, (uint)rawBytes.Length);
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class MachineNode : IDisposable
    {
        public string MachineId { get; }
        public string MachineName { get; }
        public SpawnPaths Paths { get; }
        public SpawnDatabase Database { get; }
        public CasChunkStore CasStore { get; }
        public DpapiSecretStore SecretStore { get; }
        public FakeSaveTimeMachineService FakeTimeMachine { get; }
        public CloudSaveSyncService SyncService { get; }

        public MachineNode(string tempRoot, string folderName, string machineName)
        {
            MachineId = $"pc-{folderName}";
            MachineName = machineName;

            string dataDir = Path.Combine(tempRoot, folderName, "data");
            Directory.CreateDirectory(dataDir);

            Paths = SpawnPaths.CreateIsolated(dataDir, workspaceRootHint: dataDir);
            Paths.EnsureDirectoriesExist();
            Database = new SpawnDatabase(Paths);
            CasStore = new CasChunkStore(Paths, Database);
            SecretStore = new DpapiSecretStore(Paths);
            FakeTimeMachine = new FakeSaveTimeMachineService(Database, CasStore);
            SyncService = new CloudSaveSyncService(Database, CasStore, SecretStore, FakeTimeMachine);
        }

        public async Task SeedGameAndLocalEndpointAsync(string gameId, string localSyncRoot)
        {
            await Database.SaveSaveSettingsAsync(new SaveSettingsDto
            {
                MachineId = MachineId,
                MachineDisplayName = MachineName,
            });

            await Database.UpsertGameAsync(new GameRecord
            {
                GameId = gameId,
                Title = $"Test Game {gameId}",
                SortTitle = $"Test Game {gameId}",
                PlatformSource = PlatformSource.Standalone,
                InstallDir = Path.Combine(Paths.DataRoot, "games", gameId),
                ExecutableRelPath = "game.exe",
            });

            await Database.EnsureDefaultSaveBranchAsync(gameId);

            IReadOnlyList<CloudSyncEndpointRecord> existing = await SyncService.ListEndpointsAsync();
            if (!existing.Any(e => string.Equals(e.EndpointUrl, localSyncRoot, StringComparison.OrdinalIgnoreCase)))
            {
                await SyncService.SaveEndpointAsync(new CloudSyncEndpointRecord
                {
                    EndpointId = string.Empty,
                    DisplayName = "Shared NAS Folder",
                    ProviderType = CloudSyncProviderType.LocalFolder,
                    EndpointUrl = localSyncRoot,
                    IsDefault = true,
                });
            }
        }

        public async Task<SaveSnapshotRecord> CommitLocalSaveAsync(
            string gameId,
            string? parentSnapshotId,
            string note,
            long playtimeSeconds,
            Dictionary<string, string> files)
        {
            SaveGameStateRecord state = await Database.GetOrCreateSaveGameStateAsync(gameId);
            string branchId = state.ActiveBranchId!;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string snapId = SpawnId.NewUlid(now);

            var fileRecords = new List<SaveSnapshotFileRecord>(files.Count);
            long totalRaw = 0;
            long totalStored = 0;

            foreach (var (logicalPath, content) in files)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                CasFileIngestResult ingested = await CasStore.StoreBytesAsync(bytes);
                totalRaw += ingested.RawSizeBytes;
                totalStored += ingested.StoredCompressedBytes;

                fileRecords.Add(new SaveSnapshotFileRecord
                {
                    SnapshotId = snapId,
                    LogicalPath = logicalPath,
                    OriginalPath = logicalPath,
                    Category = SaveFileCategory.Save,
                    FileSize = ingested.RawSizeBytes,
                    LastWriteTimeUtc = now,
                    FileBlake3 = ingested.FileBlake3,
                    ChunkHashes = ingested.ChunkHashes,
                });

                FakeTimeMachine.LiveDiskFiles[logicalPath] = content;
            }

            var snapshot = new SaveSnapshotRecord
            {
                SnapshotId = snapId,
                GameId = gameId,
                BranchId = branchId,
                ParentSnapshotId = parentSnapshotId,
                ModProfileHash = string.Empty,
                TriggerKind = SaveSnapshotTriggerKind.Manual,
                Note = note,
                FileCount = fileRecords.Count,
                RegistryKeyCount = 0,
                TotalRawBytes = totalRaw,
                TotalStoredBytes = totalStored,
                CreatedMachineId = MachineId,
                PlaytimeActiveSeconds = playtimeSeconds,
                CreatedAtUtc = now,
            };

            await Database.CreateSaveSnapshotWithFilesAsync(snapshot, fileRecords, advanceBranchHead: true, updateGameActiveHead: true);
            return snapshot;
        }

        public string ReadRestoredSave(string logicalPath) =>
            FakeTimeMachine.LiveDiskFiles[logicalPath];

        public void Dispose()
        {
            Database.Dispose();
        }
    }

    private sealed class FakeSaveTimeMachineService(SpawnDatabase database, CasChunkStore casStore) : ISaveTimeMachineService
    {
        public Dictionary<string, string> LiveDiskFiles { get; } = new(StringComparer.Ordinal);
        public string? LastRolledBackSnapshotId { get; private set; }

        public event EventHandler<SaveTreeChangedEvent>? SaveTreeChanged
        {
            add { }
            remove { }
        }

        public async Task<SaveRestoreOutcome> RollbackToSnapshotAsync(RollbackSaveRequest request, CancellationToken ct = default)
        {
            SaveSnapshotRecord? snap = await database.GetSaveSnapshotByIdAsync(request.SnapshotId, ct);
            if (snap is null)
            {
                return new SaveRestoreOutcome { Succeeded = false, Message = "Snapshot not found" };
            }

            IReadOnlyList<SaveSnapshotFileRecord> files = await database.GetSnapshotFilesAsync(request.SnapshotId, ct);
            foreach (SaveSnapshotFileRecord file in files)
            {
                using var ms = new MemoryStream();
                await casStore.ReconstructToStreamAsync(file.ChunkHashes, ms, file.FileBlake3, ct);
                LiveDiskFiles[file.LogicalPath] = Encoding.UTF8.GetString(ms.ToArray());
            }

            LastRolledBackSnapshotId = request.SnapshotId;
            return new SaveRestoreOutcome
            {
                Succeeded = true,
                Message = "Restored in fake disk",
                TargetSnapshot = snap,
                ActiveBranchId = snap.BranchId,
                RestoredFileCount = files.Count,
            };
        }

        public Task<SaveGameStateRecord> GetGameStateAsync(string gameId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveGameStateRecord> UpdateGameStateAsync(SaveGameStateRecord state, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveSettingsDto> GetSettingsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveSettingsAsync(SaveSettingsDto settings, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveTreeGraphDto> GetSaveTreeGraphAsync(string gameId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SaveLibraryGameOverviewDto>> GetLibraryOverviewAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SaveSnapshotFileRecord>> GetSnapshotFilesAsync(string snapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CasVaultStatsDto> GetVaultStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveSnapshotDiffDto> DiffSnapshotsAsync(string leftSnapshotId, string rightSnapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveSnapshotDiffDto> DiffSnapshotWithDiskAsync(string snapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CreateSaveSnapshotResult> CreateSnapshotAsync(CreateSaveSnapshotRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ForkBranchResult> CreateBranchAsync(ForkBranchRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveRestoreOutcome> SwitchBranchAsync(SwitchBranchRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveBranchRecord?> RenameBranchAsync(string branchId, string newName, string? description = null, string? colorHex = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DeleteBranchResult> DeleteBranchAsync(string branchId, bool deleteSnapshots = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetSnapshotPinnedAsync(string snapshotId, bool isPinned, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateSnapshotNoteAsync(string snapshotId, string? note, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DeleteSnapshotResult> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExportSnapshotResult> ExportSnapshotToFolderAsync(string snapshotId, string targetDirectory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<LegacyPreTrainerImportReport> ImportLegacyPreTrainerSnapshotsAsync(string gameId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SaveGcReport> RunCasGarbageCollectionAsync(TimeSpan? gracePeriodOverride = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
