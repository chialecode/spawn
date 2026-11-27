namespace Spawn.Core.Update;

/// <summary>
/// Compiled-in ECDSA P-256 (<c>prime256v1</c> / <c>nistP256</c>) SubjectPublicKeyInfo PEM used to verify
/// <c>update-windows-x64.json</c> against <c>update-windows-x64.json.sig</c>.
/// Must stay byte-for-byte identical to <c>release-keys/98ada0750903e1ab.pub.pem</c> (enforced by
/// <c>UpdatePublicKeyParityGateTests</c>).
/// </summary>
public static class UpdateSigningPublicKey
{
    public const string KeyId = "98ada0750903e1ab";
    public const string RepositoryRelativePath = "release-keys/" + KeyId + ".pub.pem";

    public const string Pem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEviFYm4m0nzmSQeNkdg2Be4LQJTDi\n" +
        "Qgawn0qHJOkFXtlEixDcC3lGBS6lXDsLaAFzV3YH9rh9Z+cgj5grqhKBzA==\n" +
        "-----END PUBLIC KEY-----\n";

    public const string ExpectedPemSha256Hex = "72fe38c35d5c1d7d016addad2c5579e4fe34ea40050bcd6649bd2cd946694c1e";
    public const string ExpectedSpkiDerSha256Hex = "98ada0750903e1ab461d64fc7a9715e2c3fbce8bcb84e4c868474c9e16dc68f6";
}
