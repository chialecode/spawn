using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Spawn.Core.Ipc;

/// <summary>
/// Exception thrown when inverted cross-IL Named Pipe mutual authentication (PID, executable path, or single-use nonce) fails.
/// </summary>
public sealed class TrainerPipeAuthenticationException : UnauthorizedAccessException
{
    public string ReasonCode { get; }

    public TrainerPipeAuthenticationException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }
}

/// <summary>
/// Read-only peer process inspection abstraction for Named Pipe mutual authentication.
/// Production implementation uses <c>GetNamedPipeClientProcessId</c>, <c>GetNamedPipeServerProcessId</c>,
/// and <see cref="SafeProcessInspector.TryGetProcessExecutablePath"/> (<c>0x1000</c>).
/// </summary>
public interface ITrainerPipePeerInspector
{
    bool TryGetClientProcessId(SafeHandle pipeHandle, out int processId);
    bool TryGetServerProcessId(SafeHandle pipeHandle, out int processId);
    string? TryGetProcessExecutablePath(int processId);
}

/// <summary>
/// Default Win32 implementation of <see cref="ITrainerPipePeerInspector"/> using strictly Clean-Host-safe APIs.
/// </summary>
public sealed partial class Win32TrainerPipePeerInspector : ITrainerPipePeerInspector
{
    public static Win32TrainerPipePeerInspector Instance { get; } = new();

    private Win32TrainerPipePeerInspector()
    {
    }

    public bool TryGetClientProcessId(SafeHandle pipeHandle, out int processId)
    {
        processId = 0;
        if (pipeHandle is null || pipeHandle.IsInvalid || pipeHandle.IsClosed)
        {
            return false;
        }

        bool addedRef = false;
        try
        {
            pipeHandle.DangerousAddRef(ref addedRef);
            if (!GetNamedPipeClientProcessId(pipeHandle.DangerousGetHandle(), out uint pid) || pid <= 4)
            {
                return false;
            }

            processId = checked((int)pid);
            return true;
        }
        finally
        {
            if (addedRef)
            {
                pipeHandle.DangerousRelease();
            }
        }
    }

    public bool TryGetServerProcessId(SafeHandle pipeHandle, out int processId)
    {
        processId = 0;
        if (pipeHandle is null || pipeHandle.IsInvalid || pipeHandle.IsClosed)
        {
            return false;
        }

        bool addedRef = false;
        try
        {
            pipeHandle.DangerousAddRef(ref addedRef);
            if (!GetNamedPipeServerProcessId(pipeHandle.DangerousGetHandle(), out uint pid) || pid <= 4)
            {
                return false;
            }

            processId = checked((int)pid);
            return true;
        }
        finally
        {
            if (addedRef)
            {
                pipeHandle.DangerousRelease();
            }
        }
    }

    public string? TryGetProcessExecutablePath(int processId) =>
        SafeProcessInspector.TryGetProcessExecutablePath(processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);
}

/// <summary>
/// Single-use, constant-time 256-bit CSPRNG <c>LaunchNonce</c> verifier that scrubs its backing buffer
/// immediately upon first verification attempt (whether accepted or rejected).
/// </summary>
public sealed class SingleUseLaunchNonce : IDisposable
{
    public const int NonceByteLength = 32; // 256 bits

    private readonly Lock _gate = new();
    private byte[]? _expectedBytes;
    private int _consumed;

    private SingleUseLaunchNonce(byte[] rawBytes, string encodedValue)
    {
        _expectedBytes = rawBytes;
        EncodedValue = encodedValue;
    }

    /// <summary>
    /// Base64Url-encoded 256-bit launch nonce passed to the spawned worker via command-line arguments.
    /// </summary>
    public string EncodedValue { get; }

    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;

    public static SingleUseLaunchNonce Generate()
    {
        byte[] bytes = new byte[NonceByteLength];
        RandomNumberGenerator.Fill(bytes);
        string encoded = Base64Url.EncodeToString(bytes);
        return new SingleUseLaunchNonce(bytes, encoded);
    }

    public static SingleUseLaunchNonce FromEncoded(string encodedNonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedNonce);
        byte[] decoded = DecodeOrUtf8(encodedNonce, out bool isExact256Bit);
        if (!isExact256Bit)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new ArgumentException("LaunchNonce must be a valid 256-bit Base64Url value.", nameof(encodedNonce));
        }

        return new SingleUseLaunchNonce(decoded, encodedNonce.Trim());
    }

    /// <summary>
    /// Verifies <paramref name="candidateNonce"/> in constant time and immediately burns the nonce so it can never be replayed.
    /// </summary>
    public bool VerifyAndBurn(string? candidateNonce)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            return false;
        }

        lock (_gate)
        {
            byte[]? expected = _expectedBytes;
            _expectedBytes = null;
            if (expected is null)
            {
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(candidateNonce))
                {
                    return false;
                }

                byte[] provided = DecodeOrUtf8(candidateNonce, out bool isExact256Bit);
                try
                {
                    return isExact256Bit
                           && provided.Length == expected.Length
                           && CryptographicOperations.FixedTimeEquals(expected, provided);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(provided);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _consumed, 1);
        lock (_gate)
        {
            if (_expectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(_expectedBytes);
                _expectedBytes = null;
            }
        }
    }

    private static byte[] DecodeOrUtf8(string value, out bool isExact256Bit)
    {
        string trimmed = value.Trim();
        Span<byte> stackBuf = stackalloc byte[NonceByteLength + 8];
        if (Base64Url.TryDecodeFromChars(trimmed, stackBuf, out int written) && written == NonceByteLength)
        {
            isExact256Bit = true;
            byte[] exact = stackBuf[..NonceByteLength].ToArray();
            CryptographicOperations.ZeroMemory(stackBuf);
            return exact;
        }

        CryptographicOperations.ZeroMemory(stackBuf);
        isExact256Bit = false;
        return Encoding.UTF8.GetBytes(trimmed);
    }
}

/// <summary>
/// Canonical executable path resolution and matching rules for <c>Spawn.Host</c> ↔ <c>Spawn.TrainerWorker</c> mutual authentication.
/// </summary>
public static class TrainerPipePathResolver
{
    public const string WorkerExecutableFileName = "Spawn.TrainerWorker.exe";

    private static readonly string[] AllowedHostFileNames =
    [
        "Spawn.Host.exe",
        "spawn.exe",
        "spawn-dev.exe",
    ];

    public static string? NormalizeExecutablePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            string trimmed = rawPath.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return null;
            }

            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool PathsEqual(string? left, string? right)
    {
        string? normalizedLeft = NormalizeExecutablePath(left);
        string? normalizedRight = NormalizeExecutablePath(right);
        return normalizedLeft is not null
               && normalizedRight is not null
               && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesAnyPath(string? actualPath, IEnumerable<string> allowedPaths)
    {
        string? normalizedActual = NormalizeExecutablePath(actualPath);
        if (normalizedActual is null)
        {
            return false;
        }

        foreach (string allowed in allowedPaths)
        {
            string? normalizedAllowed = NormalizeExecutablePath(allowed);
            if (normalizedAllowed is not null
                && string.Equals(normalizedActual, normalizedAllowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the default expected <c>Spawn.TrainerWorker.exe</c> locations relative to the active Host installation or source-build directory.
    /// </summary>
    public static IReadOnlyList<string> ResolveDefaultWorkerExecutablePaths(string? hostBaseDirectory = null)
    {
        string baseDir = NormalizeDirectory(hostBaseDirectory ?? AppContext.BaseDirectory);
        var results = new List<string>(4);

        // 1. Packaged install layout: <InstallDir>\Workers\Trainer\Spawn.TrainerWorker.exe
        AddUnique(results, Path.Combine(baseDir, "Workers", "Trainer", WorkerExecutableFileName));

        // 2. Co-located test/dev build output directory
        AddUnique(results, Path.Combine(baseDir, WorkerExecutableFileName));

        // 3. Source-tree sibling project build output: src\Spawn.Host\bin\<cfg>\<tfm> -> src\Spawn.TrainerWorker\bin\<cfg>\<tfm>
        TryAddSiblingProjectExecutable(results, baseDir, "Spawn.TrainerWorker", WorkerExecutableFileName);

        return results;
    }

    /// <summary>
    /// Resolves the allowed Host executable paths (<c>Spawn.Host.exe</c>, <c>spawn.exe</c>, <c>spawn-dev.exe</c>)
    /// located in the same installation root as <paramref name="workerExecutablePath"/>.
    /// </summary>
    public static IReadOnlyList<string> ResolveDefaultAllowedHostExecutablePaths(string? workerExecutablePath = null)
    {
        string? normalizedWorker = NormalizeExecutablePath(workerExecutablePath ?? Environment.ProcessPath);
        string workerDir = normalizedWorker is not null
            ? NormalizeDirectory(Path.GetDirectoryName(normalizedWorker)!)
            : NormalizeDirectory(AppContext.BaseDirectory);

        var results = new List<string>(8);

        // 1. Packaged install layout: <InstallDir>\Workers\Trainer\Spawn.TrainerWorker.exe -> <InstallDir>\<Host>.exe
        DirectoryInfo? dir = new(workerDir);
        if (dir.Name.Equals("Trainer", StringComparison.OrdinalIgnoreCase)
            && dir.Parent is { } workersDir
            && workersDir.Name.Equals("Workers", StringComparison.OrdinalIgnoreCase)
            && workersDir.Parent is { } installRoot)
        {
            foreach (string hostFile in AllowedHostFileNames)
            {
                AddUnique(results, Path.Combine(installRoot.FullName, hostFile));
            }
        }

        // 2. Same directory (source/unit-test co-located layout)
        foreach (string hostFile in AllowedHostFileNames)
        {
            AddUnique(results, Path.Combine(workerDir, hostFile));
        }

        // 3. Source-tree sibling project build output: src\Spawn.TrainerWorker\bin\<cfg>\<tfm> -> src\Spawn.Host\bin\<cfg>\<tfm>
        foreach (string hostFile in AllowedHostFileNames)
        {
            TryAddSiblingProjectExecutable(results, workerDir, "Spawn.Host", hostFile);
        }

        return results;
    }

    private static void TryAddSiblingProjectExecutable(
        List<string> sink,
        string currentOutputDir,
        string siblingProjectName,
        string executableFileName)
    {
        // Typical layout: <repo>\src\<Project>\bin\<Config>\<Tfm>
        var tfmDir = new DirectoryInfo(currentOutputDir);
        var cfgDir = tfmDir.Parent;
        var binDir = cfgDir?.Parent;
        var projDir = binDir?.Parent;
        var srcDir = projDir?.Parent;

        if (binDir is null || cfgDir is null || srcDir is null)
        {
            return;
        }

        if (!binDir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string candidate = Path.Combine(srcDir.FullName, siblingProjectName, "bin", cfgDir.Name, tfmDir.Name, executableFileName);
        AddUnique(sink, candidate);
    }

    private static string NormalizeDirectory(string dir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    private static void AddUnique(List<string> sink, string path)
    {
        string normalized = Path.GetFullPath(path);
        if (!sink.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            sink.Add(normalized);
        }
    }
}
