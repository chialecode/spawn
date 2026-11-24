#:property PublishAot=false
#:property UseSharedCompilation=false

using System.Security.Cryptography;
using System.Text;

const string SigningKeyEnvVar = "SPAWN_UPDATE_SIGNING_KEY";
const string NistP256Oid = "1.2.840.10045.3.1.7";

if (args.Length < 1)
{
    PrintUsage();
    return 2;
}

string command = args[0].Trim().ToLowerInvariant();
try
{
    return command switch
    {
        "sign" => RunSign(args),
        "verify" => RunVerify(args),
        "keygen" => RunKeyGen(args),
        _ => UnknownCommand(command),
    };
}
catch (Exception ex)
{
    // Never include sensitive env values or key paths in error output.
    Console.Error.WriteLine($"sign_update error ({ex.GetType().Name})");
    return 1;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run scripts/sign_update.cs -- sign <input-file> <output-sig-file>");
    Console.Error.WriteLine("  dotnet run scripts/sign_update.cs -- verify <input-file> <sig-file> <public-key-pem>");
    Console.Error.WriteLine("  dotnet run scripts/sign_update.cs -- keygen <private-key-pem-out> <public-key-pem-out>");
}

static void EnsureNistP256(ECDsa ecdsa)
{
    if (ecdsa.KeySize != 256)
    {
        throw new CryptographicException($"Expected 256-bit ECDSA key, got {ecdsa.KeySize}-bit.");
    }

    ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: false);
    string? oid = parameters.Curve.Oid?.Value;
    string? friendly = parameters.Curve.Oid?.FriendlyName;
    bool isP256 =
        string.Equals(oid, NistP256Oid, StringComparison.Ordinal) ||
        string.Equals(friendly, "nistP256", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(friendly, "ECDSA_P256", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(friendly, "prime256v1", StringComparison.OrdinalIgnoreCase);

    if (!isP256)
    {
        throw new CryptographicException("Key curve is not NIST P-256.");
    }
}

static int RunSign(string[] args)
{
    if (args.Length != 3)
    {
        PrintUsage();
        return 2;
    }

    string inputPath = args[1];
    string outputSigPath = args[2];

    string? keyPath = Environment.GetEnvironmentVariable(SigningKeyEnvVar);
    if (string.IsNullOrWhiteSpace(keyPath))
    {
        Console.Error.WriteLine($"Environment variable {SigningKeyEnvVar} is not set.");
        return 2;
    }

    keyPath = keyPath.Trim();
    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine($"Signing key referenced by {SigningKeyEnvVar} does not exist.");
        return 2;
    }

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file does not exist: {Path.GetFileName(inputPath)}");
        return 2;
    }

    string privatePem;
    try
    {
        privatePem = File.ReadAllText(keyPath, Encoding.UTF8);
    }
    catch
    {
        Console.Error.WriteLine($"Failed to read signing key referenced by {SigningKeyEnvVar}.");
        return 1;
    }

    using ECDsa ecdsa = ECDsa.Create();
    try
    {
        ecdsa.ImportFromPem(privatePem);
        EnsureNistP256(ecdsa);
    }
    catch
    {
        Console.Error.WriteLine($"Failed to import ECDSA P-256 private key from {SigningKeyEnvVar}.");
        return 1;
    }
    finally
    {
        privatePem = string.Empty;
    }

    byte[] payload = File.ReadAllBytes(inputPath);
    byte[] signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
    {
        Console.Error.WriteLine("Self-verification of generated signature failed.");
        return 1;
    }

    string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputSigPath));
    if (!string.IsNullOrEmpty(outDir))
    {
        Directory.CreateDirectory(outDir);
    }

    string tempOut = outputSigPath + $".tmp-{Environment.ProcessId}";
    File.WriteAllBytes(tempOut, signature);
    File.Move(tempOut, outputSigPath, overwrite: true);

    Console.WriteLine($"Signed {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputSigPath)} ({signature.Length} bytes DER ECDSA-P256-SHA256)");
    return 0;
}

static int RunVerify(string[] args)
{
    if (args.Length != 4)
    {
        PrintUsage();
        return 2;
    }

    string inputPath = args[1];
    string sigPath = args[2];
    string publicKeyPath = args[3];

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file does not exist: {Path.GetFileName(inputPath)}");
        return 2;
    }

    if (!File.Exists(sigPath))
    {
        Console.Error.WriteLine($"Signature file does not exist: {Path.GetFileName(sigPath)}");
        return 2;
    }

    if (!File.Exists(publicKeyPath))
    {
        Console.Error.WriteLine($"Public key file does not exist: {Path.GetFileName(publicKeyPath)}");
        return 2;
    }

    string publicPem = File.ReadAllText(publicKeyPath, Encoding.UTF8);
    using ECDsa ecdsa = ECDsa.Create();
    try
    {
        ecdsa.ImportFromPem(publicPem);
        EnsureNistP256(ecdsa);
    }
    catch
    {
        Console.Error.WriteLine("Failed to import ECDSA P-256 public key.");
        return 1;
    }

    byte[] payload = File.ReadAllBytes(inputPath);
    byte[] signature = File.ReadAllBytes(sigPath);
    if (signature.Length == 0)
    {
        Console.Error.WriteLine("Signature verification failed (empty signature).");
        return 1;
    }

    bool valid;
    try
    {
        valid = ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
    catch (CryptographicException)
    {
        valid = false;
    }

    if (!valid)
    {
        Console.Error.WriteLine("Signature verification failed.");
        return 1;
    }

    Console.WriteLine($"Verified OK: {Path.GetFileName(inputPath)} ({signature.Length} bytes DER ECDSA-P256-SHA256)");
    return 0;
}

static int RunKeyGen(string[] args)
{
    if (args.Length != 3)
    {
        PrintUsage();
        return 2;
    }

    string privateOut = args[1];
    string publicOut = args[2];

    using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string privatePem = ecdsa.ExportPkcs8PrivateKeyPem().Replace("\r\n", "\n", StringComparison.Ordinal);
    if (!privatePem.EndsWith('\n'))
    {
        privatePem += "\n";
    }

    string publicPem = ecdsa.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n", StringComparison.Ordinal);
    if (!publicPem.EndsWith('\n'))
    {
        publicPem += "\n";
    }

    string? privDir = Path.GetDirectoryName(Path.GetFullPath(privateOut));
    if (!string.IsNullOrEmpty(privDir))
    {
        Directory.CreateDirectory(privDir);
    }

    string? pubDir = Path.GetDirectoryName(Path.GetFullPath(publicOut));
    if (!string.IsNullOrEmpty(pubDir))
    {
        Directory.CreateDirectory(pubDir);
    }

    var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    File.WriteAllText(privateOut, privatePem, utf8NoBom);
    File.WriteAllText(publicOut, publicPem, utf8NoBom);

    Console.WriteLine("Generated ephemeral ECDSA P-256 key pair.");
    return 0;
}
