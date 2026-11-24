using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core.Update;

public enum ManifestVerificationStatus
{
    ValidNewerVersion = 0,
    SameVersion = 1,
    InvalidSignature = 2,
    InvalidPublicKey = 3,
    MalformedManifest = 4,
    UnsupportedSchemaVersion = 5,
    InvalidChannel = 6,
    InvalidVersionFormat = 7,
    DowngradeRejected = 8,
    InvalidInstallerMetadata = 9,
    InvalidInstallerUrl = 10,
    InstallerSizeExceedsCap = 11,
    InvalidNotesUrl = 12,
}

public sealed record ManifestVerificationResult(
    ManifestVerificationStatus Status,
    UpdateManifestDto? Manifest,
    UpdateSemanticVersion ParsedVersion,
    string NormalizedSha256,
    Uri? ValidatedInstallerUri,
    string Message)
{
    public bool IsEligibleForDownload => Status == ManifestVerificationStatus.ValidNewerVersion;
}

/// <summary>
/// Pure BCL cryptographic and structural verifier for <c>update-windows-x64.json</c> + <c>.sig</c>.
/// Enforces ECDSA P-256 / SHA-256 DER verification BEFORE trusting any JSON field, strict schema/channel/version
/// checks, anti-downgrade protection, exact release download URL prefix, and safe installer filename validation.
/// </summary>
public static class UpdateManifestVerifier
{
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static ManifestVerificationResult Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes,
        string publicKeyPem,
        string currentVersion,
        UpdateServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (manifestBytes.IsEmpty || manifestBytes.Length > options.MaxManifestBytes)
        {
            return Fail(ManifestVerificationStatus.MalformedManifest, "更新清单为空或超过大小上限。");
        }

        if (signatureBytes.IsEmpty || signatureBytes.Length > options.MaxSignatureBytes)
        {
            return Fail(ManifestVerificationStatus.InvalidSignature, "更新清单签名缺失或大小异常。");
        }

        // 1. Cryptographic signature verification FIRST (fail closed before parsing untrusted JSON).
        if (!TryVerifyEcdsaP256DerSignature(manifestBytes, signatureBytes, publicKeyPem, out bool invalidKey))
        {
            return invalidKey
                ? Fail(ManifestVerificationStatus.InvalidPublicKey, "内置更新验签公钥无效或不是 ECDSA P-256 公钥。")
                : Fail(ManifestVerificationStatus.InvalidSignature, "更新清单 ECDSA P-256 数字签名校验未通过，已拒绝。");
        }

        // 2. Parse frozen manifest schema.
        UpdateManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifestDto>(manifestBytes, StrictJsonOptions);
        }
        catch (JsonException)
        {
            return Fail(ManifestVerificationStatus.MalformedManifest, "更新清单 JSON 格式无效。");
        }

        if (manifest is null)
        {
            return Fail(ManifestVerificationStatus.MalformedManifest, "更新清单内容为空。");
        }

        // 3. Schema version & channel checks.
        if (manifest.SchemaVersion != 1)
        {
            return Fail(
                ManifestVerificationStatus.UnsupportedSchemaVersion,
                $"不支持的更新清单 schemaVersion={manifest.SchemaVersion}（仅支持 1）。");
        }

        if (!string.Equals(manifest.Channel, "release", StringComparison.Ordinal))
        {
            return Fail(
                ManifestVerificationStatus.InvalidChannel,
                $"更新清单通道不是 release（实际：{manifest.Channel}）。");
        }

        // 4. Strict numeric Major.Minor.Patch comparison (strictly greater than current).
        if (!UpdateSemanticVersion.TryParseStrict(manifest.Version, out UpdateSemanticVersion targetVersion))
        {
            return Fail(
                ManifestVerificationStatus.InvalidVersionFormat,
                $"更新清单版本号格式非法：{manifest.Version}");
        }

        if (!UpdateSemanticVersion.TryParseStrict(currentVersion, out UpdateSemanticVersion installedVersion))
        {
            return Fail(
                ManifestVerificationStatus.InvalidVersionFormat,
                $"当前版本号不是有效正式版语义版本：{currentVersion}");
        }

        int cmp = targetVersion.CompareTo(installedVersion);
        if (cmp < 0)
        {
            return Fail(
                ManifestVerificationStatus.DowngradeRejected,
                $"已拒绝降级清单：远端版本 v{targetVersion} 低于当前版本 v{installedVersion}。");
        }

        if (cmp == 0)
        {
            return new ManifestVerificationResult(
                ManifestVerificationStatus.SameVersion,
                manifest,
                targetVersion,
                string.Empty,
                null,
                $"当前已是最新版本 (v{installedVersion})。");
        }

        // 5. Installer metadata validation (name, size, sha256, exact release URL prefix).
        if (manifest.Installer is null)
        {
            return Fail(ManifestVerificationStatus.InvalidInstallerMetadata, "更新清单缺少 installer 字段。");
        }

        if (!IsSafeInstallerFileName(manifest.Installer.Name))
        {
            return Fail(
                ManifestVerificationStatus.InvalidInstallerMetadata,
                $"安装包文件名不安全或非法：{manifest.Installer.Name}");
        }

        if (manifest.Installer.Size <= 0)
        {
            return Fail(ManifestVerificationStatus.InvalidInstallerMetadata, "安装包声明大小必须大于 0。");
        }

        if (manifest.Installer.Size > options.MaxInstallerBytes)
        {
            return Fail(
                ManifestVerificationStatus.InstallerSizeExceedsCap,
                $"安装包声明大小 {manifest.Installer.Size} 字节超过上限 {options.MaxInstallerBytes} 字节。");
        }

        if (!TryNormalizeSha256Hex(manifest.Installer.Sha256, out string normalizedSha256))
        {
            return Fail(ManifestVerificationStatus.InvalidInstallerMetadata, "安装包 SHA-256 哈希格式无效。");
        }

        string expectedPrefix = string.Format(
            CultureInfo.InvariantCulture,
            options.InstallerUrlPrefixTemplate,
            targetVersion.ToString());

        if (string.IsNullOrEmpty(manifest.Installer.Url)
            || !manifest.Installer.Url.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return Fail(
                ManifestVerificationStatus.InvalidInstallerUrl,
                $"安装包 URL 必须以 {expectedPrefix} 开头。");
        }

        string urlRemainder = manifest.Installer.Url[expectedPrefix.Length..];
        if (!string.Equals(urlRemainder, manifest.Installer.Name, StringComparison.Ordinal))
        {
            return Fail(
                ManifestVerificationStatus.InvalidInstallerUrl,
                "安装包 URL 文件名与 installer.name 不一致或包含非法附加路径/参数。");
        }

        if (!Uri.TryCreate(manifest.Installer.Url, UriKind.Absolute, out Uri? installerUri)
            || !string.Equals(installerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(installerUri.UserInfo)
            || !string.IsNullOrEmpty(installerUri.Query)
            || !string.IsNullOrEmpty(installerUri.Fragment)
            || !options.AllowedHosts.Contains(installerUri.Host))
        {
            return Fail(ManifestVerificationStatus.InvalidInstallerUrl, "安装包 URL 未通过 HTTPS 与主机白名单校验。");
        }

        // 6. Optional notesUrl validation (must be HTTPS github.com if provided).
        if (!string.IsNullOrWhiteSpace(manifest.NotesUrl))
        {
            if (!Uri.TryCreate(manifest.NotesUrl, UriKind.Absolute, out Uri? notesUri)
                || !string.Equals(notesUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(notesUri.UserInfo)
                || !string.Equals(notesUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(ManifestVerificationStatus.InvalidNotesUrl, "发布说明 URL 必须为 https://github.com 链接。");
            }
        }

        return new ManifestVerificationResult(
            ManifestVerificationStatus.ValidNewerVersion,
            manifest,
            targetVersion,
            normalizedSha256,
            installerUri,
            $"发现新版本 v{targetVersion}（签名验证通过）。");
    }

    public static bool TryVerifyEcdsaP256DerSignature(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> derSignature,
        string publicKeyPem,
        out bool invalidPublicKey)
    {
        invalidPublicKey = false;
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            invalidPublicKey = true;
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem.AsSpan());

            if (ecdsa.KeySize != 256)
            {
                invalidPublicKey = true;
                return false;
            }

            ECParameters exported = ecdsa.ExportParameters(includePrivateParameters: false);
            if (!string.Equals(exported.Curve.Oid?.Value, NistP256Oid, StringComparison.Ordinal))
            {
                invalidPublicKey = true;
                return false;
            }

            return ecdsa.VerifyData(
                payload,
                derSignature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            // Distinguish corrupt/invalid PEM from invalid signature.
            try
            {
                using var probe = ECDsa.Create();
                probe.ImportFromPem(publicKeyPem.AsSpan());
            }
            catch
            {
                invalidPublicKey = true;
            }

            return false;
        }
        catch (ArgumentException)
        {
            invalidPublicKey = true;
            return false;
        }
    }

    public static bool IsSafeInstallerFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 128)
        {
            return false;
        }

        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal) || fileName.Contains(':'))
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char ch in fileName)
        {
            bool allowed = ch is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '.' or '-' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryNormalizeSha256Hex(string? raw, out string normalizedLowerHex)
    {
        normalizedLowerHex = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.Length != 64)
        {
            return false;
        }

        foreach (char ch in trimmed)
        {
            bool isHex = ch is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        normalizedLowerHex = trimmed.ToLowerInvariant();
        return true;
    }

    private static ManifestVerificationResult Fail(ManifestVerificationStatus status, string message) =>
        new(status, null, default, string.Empty, null, message);
}
