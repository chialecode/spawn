using System.IO;
using System.Security.Cryptography;
using System.Text;
using Spawn.Core.Update;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Hard parity gate (<c>ADR-0007 §3.3</c> / <c>M3-A-22</c>): asserts that the ECDSA P-256 public key
/// compiled into <c>Spawn.Core</c> (<see cref="UpdateSigningPublicKey.Pem"/>) matches
/// <c>installer/update-signing-public.pem</c> byte-for-byte and imports cleanly as a NIST P-256 key.
/// </summary>
public sealed class UpdatePublicKeyParityGateTests
{
    [Fact]
    public void CompiledInPublicKey_MatchesRepositoryPemFileByteForByteAndFingerprints()
    {
        string repoRoot = LocateRepositoryRoot();
        string pemPath = Path.Combine(repoRoot, "installer", "update-signing-public.pem");
        Assert.True(File.Exists(pemPath), $"Missing release update public key file: {pemPath}");

        byte[] fileBytes = File.ReadAllBytes(pemPath);
        byte[] compiledBytes = Encoding.UTF8.GetBytes(UpdateSigningPublicKey.Pem);

        // Must be UTF-8 without BOM, LF-only, and byte-for-byte identical to the compiled-in constant.
        Assert.False(
            fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF,
            "installer/update-signing-public.pem must be UTF-8 without BOM.");
        Assert.DoesNotContain((byte)'\r', fileBytes);
        Assert.Equal(fileBytes, compiledBytes);

        // Verify PEM and SubjectPublicKeyInfo DER SHA-256 fingerprints and NIST P-256 curve.
        string pemSha256 = Convert.ToHexStringLower(SHA256.HashData(fileBytes));
        Assert.Equal(UpdateSigningPublicKey.ExpectedPemSha256Hex, pemSha256);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(UpdateSigningPublicKey.Pem.AsSpan());
        Assert.Equal(256, ecdsa.KeySize);
        ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: false);
        Assert.Equal("1.2.840.10045.3.1.7", ecParams.Curve.Oid?.Value);

        byte[] spkiDer = ecdsa.ExportSubjectPublicKeyInfo();
        string spkiSha256 = Convert.ToHexStringLower(SHA256.HashData(spkiDer));
        Assert.Equal(UpdateSigningPublicKey.ExpectedSpkiDerSha256Hex, spkiSha256);
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

        throw new DirectoryNotFoundException("Could not locate repository root containing Spawn.sln.");
    }
}
