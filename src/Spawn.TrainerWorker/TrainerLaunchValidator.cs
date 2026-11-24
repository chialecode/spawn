using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Spawn.Contracts;

namespace Spawn.TrainerWorker;

/// <summary>
/// Result of validating a <see cref="TrainerLaunchRequest"/> against the Worker command whitelist.
/// </summary>
internal sealed record ValidatedTrainerLaunchTarget(
    bool Succeeded,
    string? ExecutableFullPath = null,
    string? WorkingDirectory = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Defense-in-depth Worker command whitelist: ensures target executables and working directories reside strictly
/// inside <c>SandboxRoot</c> without symlink/junction escape, and recomputes local SHA-256 to match Host's pinned digest.
/// </summary>
internal static class TrainerLaunchValidator
{
    public static ValidatedTrainerLaunchTarget Validate(TrainerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Fail("INVALID_SESSION_ID", "SessionId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(request.SandboxRoot) || !Path.IsPathFullyQualified(request.SandboxRoot.Trim()))
        {
            return Fail("INVALID_SANDBOX_ROOT", "SandboxRoot must be a fully qualified directory path.");
        }

        string normalizedSandboxRoot;
        try
        {
            normalizedSandboxRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SandboxRoot.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail("INVALID_SANDBOX_ROOT", $"Malformed SandboxRoot: {ex.Message}");
        }

        string? rootAnchor = Path.GetPathRoot(normalizedSandboxRoot);
        if (string.IsNullOrWhiteSpace(rootAnchor)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(rootAnchor),
                normalizedSandboxRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return Fail("INVALID_SANDBOX_ROOT", "SandboxRoot cannot be a filesystem drive root.");
        }

        if (!Directory.Exists(normalizedSandboxRoot))
        {
            return Fail("SANDBOX_ROOT_MISSING", $"SandboxRoot directory does not exist: {normalizedSandboxRoot}");
        }

        if (IsReparsePoint(normalizedSandboxRoot))
        {
            return Fail("SANDBOX_REPARSE_REJECTED", "SandboxRoot cannot be a symlink or directory junction.");
        }

        if (string.IsNullOrWhiteSpace(request.SandboxRelativePath))
        {
            return Fail("INVALID_RELATIVE_PATH", "SandboxRelativePath must not be empty.");
        }

        string rawRel = request.SandboxRelativePath.Trim();
        if (Path.IsPathRooted(rawRel) || rawRel.StartsWith(@"\\", StringComparison.Ordinal) || rawRel.Contains(':', StringComparison.Ordinal))
        {
            return Fail("PATH_OUTSIDE_SANDBOX", "SandboxRelativePath must be a strictly relative path without drive or UNC prefixes.");
        }

        string[] segments = rawRel.Split(['\\', '/'], StringSplitOptions.None);
        if (segments.Any(static s => string.IsNullOrWhiteSpace(s) || s is "." or ".."))
        {
            return Fail("PATH_TRAVERSAL_REJECTED", "SandboxRelativePath contains empty or relative traversal segments.");
        }

        string fullExePath;
        try
        {
            fullExePath = Path.GetFullPath(Path.Combine(normalizedSandboxRoot, rawRel));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail("INVALID_RELATIVE_PATH", $"Unable to resolve SandboxRelativePath: {ex.Message}");
        }

        if (!IsStrictlyInsideRoot(fullExePath, normalizedSandboxRoot))
        {
            return Fail("PATH_OUTSIDE_SANDBOX", "Resolved executable path escapes SandboxRoot.");
        }

        if (!string.Equals(Path.GetExtension(fullExePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("INVALID_EXECUTABLE_EXTENSION", "Only .exe targets inside the sandbox may be launched.");
        }

        if (HasReparsePointBetween(normalizedSandboxRoot, fullExePath))
        {
            return Fail("SANDBOX_REPARSE_REJECTED", "Executable path traverses a reparse point (symlink/junction).");
        }

        if (!File.Exists(fullExePath))
        {
            return Fail("EXECUTABLE_NOT_FOUND", $"Target executable does not exist inside sandbox: {fullExePath}");
        }

        // Validate WorkingDirectory (defaults to executable parent directory).
        string workingDir = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Path.GetDirectoryName(fullExePath)!
            : request.WorkingDirectory.Trim();

        string normalizedWorkDir;
        try
        {
            normalizedWorkDir = Path.IsPathFullyQualified(workingDir)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDir))
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(normalizedSandboxRoot, workingDir)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail("INVALID_WORKING_DIRECTORY", $"Invalid WorkingDirectory: {ex.Message}");
        }

        bool workDirInside = string.Equals(normalizedWorkDir, normalizedSandboxRoot, StringComparison.OrdinalIgnoreCase)
                             || IsStrictlyInsideRoot(normalizedWorkDir, normalizedSandboxRoot);
        if (!workDirInside)
        {
            return Fail("WORKDIR_OUTSIDE_SANDBOX", "WorkingDirectory must reside inside SandboxRoot.");
        }

        if (!Directory.Exists(normalizedWorkDir) || HasReparsePointBetween(normalizedSandboxRoot, normalizedWorkDir))
        {
            return Fail("INVALID_WORKING_DIRECTORY", "WorkingDirectory does not exist or traverses a reparse point.");
        }

        // Verify pinned SHA-256 hash using constant-time byte comparison.
        string expectedHex = NormalizeSha256Hex(request.Sha256);
        if (expectedHex.Length != 64)
        {
            return Fail("INVALID_SHA256", "Pinned SHA-256 digest must be a valid 64-character hex string.");
        }

        string actualHex;
        try
        {
            actualHex = ComputeFileSha256Hex(fullExePath);
        }
        catch (Exception ex)
        {
            return Fail("SHA256_READ_FAILED", $"Unable to hash target executable: {ex.Message}");
        }

        byte[] expectedBytes = Encoding.ASCII.GetBytes(expectedHex);
        byte[] actualBytes = Encoding.ASCII.GetBytes(actualHex);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            return Fail("SHA256_MISMATCH", $"SHA-256 mismatch: expected {expectedHex}, actual {actualHex}.");
        }

        // Optional PE architecture check.
        if (!string.IsNullOrWhiteSpace(request.ExpectedArchitecture))
        {
            string? actualArch = TryReadPeMachineLabel(fullExePath);
            if (!ArchitectureMatches(request.ExpectedArchitecture, actualArch))
            {
                return Fail(
                    "ARCHITECTURE_MISMATCH",
                    $"PE architecture mismatch: expected '{request.ExpectedArchitecture}', actual '{actualArch ?? "unknown"}'.");
            }
        }

        return new ValidatedTrainerLaunchTarget(
            Succeeded: true,
            ExecutableFullPath: fullExePath,
            WorkingDirectory: normalizedWorkDir);
    }

    /// <summary>
    /// Closes the TOCTOU window between SHA-256 verification and <c>CreateProcessW</c>: opens the validated image with
    /// <c>FileShare.Read</c> (denying write/delete/rename to every other opener), re-hashes through that same handle and
    /// returns it to be held until the process has been created. Returns <see langword="null"/> when the file cannot be
    /// locked or no longer matches <paramref name="expectedSha256"/>.
    /// </summary>
    public static FileStream? OpenVerifiedImageLock(string executableFullPath, string? expectedSha256)
    {
        string expectedHex = NormalizeSha256Hex(expectedSha256);
        if (expectedHex.Length != 64)
        {
            return null;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(executableFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] actual = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(stream)));
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHex), actual))
            {
                return stream;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treated as verification failure below.
        }

        stream?.Dispose();
        return null;
    }

    public static string ComputeFileSha256Hex(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsStrictlyInsideRoot(string fullCandidate, string normalizedRoot)
    {
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointBetween(string normalizedRoot, string fullTargetPath)
    {
        string? current = fullTargetPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (IsReparsePoint(current))
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeSha256Hex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed.Length != 64)
        {
            return string.Empty;
        }

        foreach (char c in trimmed)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return string.Empty;
            }
        }

        return trimmed;
    }

    private static string? TryReadPeMachineLabel(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(fs);
            return pe.PEHeaders.CoffHeader.Machine switch
            {
                Machine.I386 => "I386",
                Machine.Amd64 => "AMD64",
                Machine.Arm64 => "ARM64",
                var other => other.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ArchitectureMatches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        string normExpected = NormalizeArch(expected);
        string normActual = NormalizeArch(actual);
        return string.Equals(normExpected, normActual, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeArch(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "X86" or "I386" or "WIN32" => "I386",
            "X64" or "AMD64" or "X86_64" => "AMD64",
            "ARM64" => "ARM64",
            var other => other,
        };

    private static ValidatedTrainerLaunchTarget Fail(string code, string message) =>
        new(Succeeded: false, ErrorCode: code, ErrorMessage: message);
}
