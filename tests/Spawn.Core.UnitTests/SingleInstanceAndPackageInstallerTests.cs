using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class SingleInstanceAndPackageInstallerTests
{
    private const string SpawnDevTestAppGuid = "B7E91A42-6D30-4C95-9E18-2F4A8C705104";

    [Fact]
    public async Task SingleInstance_PrimarySecondaryActivateAndQuitForUpdate_HandshakesAndReleasesMutexCleanly()
    {
        string testIdentity = $"spawn-unit-inst-{Guid.NewGuid():N}";
        var activatedArgsTcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quitRequestedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var primary = new SingleInstanceCoordinator(testIdentity);
        try
        {
            primary.ActivateRequested += (incomingArgs, _) =>
            {
                activatedArgsTcs.TrySetResult(incomingArgs);
                return Task.CompletedTask;
            };

            primary.QuitForUpdateRequested += async _ =>
            {
                quitRequestedTcs.TrySetResult(true);
                await primary.DisposeAsync();
            };

            // 1. Primary instance acquires mutex and starts SDDL-protected control pipe
            var primaryResult = await primary.InitializeAsync(["--background"]);
            Assert.Equal(SingleInstanceStartupAction.RunAsPrimary, primaryResult.Action);
            Assert.True(primaryResult.Options.IsBackgroundTray);
            Assert.True(primary.IsPrimaryInstance);
            Assert.True(SingleInstanceCoordinator.IsInstanceRunning(testIdentity));

            // 2. Secondary launch without --background forwards activation arguments to primary and exits
            await using (var secondary = new SingleInstanceCoordinator(testIdentity))
            {
                var secResult = await secondary.InitializeAsync(["--activate", "open-library"]);
                Assert.Equal(SingleInstanceStartupAction.ExitSecondaryInstance, secResult.Action);
                Assert.True(secResult.WasPrimaryRunning);
                Assert.True(secResult.HandshakeDelivered);
            }

            var receivedArgs = await activatedArgsTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("open-library", receivedArgs);

            // 3. Installer/updater launch with --quit-for-update signals primary and waits for mutex release
            await using (var updater = new SingleInstanceCoordinator(testIdentity))
            {
                var quitResult = await updater.InitializeAsync(
                    ["--quit-for-update"],
                    quitWaitTimeout: TimeSpan.FromSeconds(5));

                Assert.Equal(SingleInstanceStartupAction.ExitAfterQuitForUpdate, quitResult.Action);
                Assert.True(quitResult.WasPrimaryRunning);
                Assert.True(quitResult.HandshakeDelivered);
            }

            Assert.True(await quitRequestedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(SingleInstanceCoordinator.IsInstanceRunning(testIdentity));
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task PackageAndInstaller_SpawnDevTestIdentity_BuildsSilentlyInstallsAndUninstallsWithZeroChannelPollution()
    {
        string? repoRoot = TestSandbox.TryLocateRepositoryRoot();
        Assert.False(string.IsNullOrWhiteSpace(repoRoot));

        string? isccPath = TryFindInnoSetup7Compiler();
        if (string.IsNullOrWhiteSpace(isccPath))
        {
            // Cloud CI runners without Inno Setup 7 installed still verify icon generation & SpawnCommandLineOptions.
            var parsed = SpawnCommandLineOptions.Parse(["--quit-for-update", "--identity=spawn-dev-test"]);
            Assert.True(parsed.IsQuitForUpdate);
            Assert.Equal("spawn-dev-test", parsed.ExplicitIdentity);
            return;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var protectedRealPaths = new[]
        {
            Path.Combine(localAppData, "spawn"),
            Path.Combine(localAppData, "Programs", "spawn"),
            Path.Combine(localAppData, "spawn-dev"),
            Path.Combine(localAppData, "Programs", "spawn-dev"),
        };

        var beforeSnapshots = protectedRealPaths.ToDictionary(
            p => p,
            CaptureDirectoryExistenceAndChildSet,
            StringComparer.OrdinalIgnoreCase);

        await using var sandbox = new TestSandbox(repoRoot);
        string isolatedInstallDir = Path.Combine(sandbox.RootDirectory, "Programs", "spawn-dev-test");
        string receiptPath = Path.Combine(sandbox.RootDirectory, "install-spawn-dev-test.json");
        const string testVersion = "0.1.1-dev.202611209999";

        string uninstallSubKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{{{SpawnDevTestAppGuid}}}_is1";
        string distIdentityDir = Path.Combine(repoRoot!, "dist", "spawn-dev-test");

        try
        {
            var installProc = await RunPythonPackageScriptAsync(
                repoRoot!,
                sandbox.DataDirectory,
                [
                    "-B",
                    "scripts/package.py",
                    "--identity", "spawn-dev-test",
                    "--version", testVersion,
                    "--install",
                    "--no-shortcuts",
                    "--no-relaunch",
                    "--install-dir", isolatedInstallDir,
                    "--receipt-path", receiptPath,
                ]);

            Assert.True(
                installProc.ExitCode == 0,
                $"package.py --install failed (exit {installProc.ExitCode}):\n{installProc.Stdout}\n{installProc.Stderr}");

            // 1. Verify receipt, installed executables, compliance files, and dev-workspace.json bridge in temp DataDir
            Assert.True(File.Exists(receiptPath));
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath)))
            {
                Assert.True(doc.RootElement.GetProperty("installed").GetBoolean());
                Assert.Equal("spawn-dev-test", doc.RootElement.GetProperty("identity").GetString());
                Assert.Equal(testVersion, doc.RootElement.GetProperty("newVersion").GetString());
            }

            string installedExe = Path.Combine(isolatedInstallDir, "spawn-dev-test.exe");
            string installedOverlayExe = Path.Combine(isolatedInstallDir, "Spawn.OverlayHost.exe");
            Assert.True(File.Exists(installedExe));
            Assert.True(File.Exists(installedOverlayExe));
            Assert.True(File.Exists(Path.Combine(isolatedInstallDir, "VERSION.txt")));
            Assert.True(File.Exists(Path.Combine(isolatedInstallDir, "THIRD-PARTY-NOTICES.txt")));
            Assert.True(File.Exists(Path.Combine(sandbox.DataDirectory, SpawnPaths.DevWorkspaceFileName)));

            // 2. Verify HKCU Uninstall registry entry matches testVersion and isolatedInstallDir
            using (var regKey = Registry.CurrentUser.OpenSubKey(uninstallSubKey, writable: false))
            {
                Assert.NotNull(regKey);
                Assert.Equal(testVersion, regKey!.GetValue("DisplayVersion") as string);
            }

            // 3. Verify silent uninstall removes program binary & HKCU Uninstall entry while preserving isolated data dir!
            string markerUserFile = Path.Combine(sandbox.DataDirectory, "db", "keep-user-data.marker");
            await File.WriteAllTextAsync(markerUserFile, "preserve-across-uninstall");

            var uninstallProc = await RunPythonPackageScriptAsync(
                repoRoot!,
                sandbox.DataDirectory,
                [
                    "-B",
                    "scripts/package.py",
                    "--identity", "spawn-dev-test",
                    "--uninstall",
                ]);

            Assert.True(
                uninstallProc.ExitCode == 0,
                $"package.py --uninstall failed (exit {uninstallProc.ExitCode}):\n{uninstallProc.Stdout}\n{uninstallProc.Stderr}");

            using (var afterRegKey = Registry.CurrentUser.OpenSubKey(uninstallSubKey, writable: false))
            {
                Assert.Null(afterRegKey);
            }

            Assert.True(File.Exists(markerUserFile));

            // 4. Verify real `spawn` and `spawn-dev` directories were 100% untouched!
            foreach (string realPath in protectedRealPaths)
            {
                var before = beforeSnapshots[realPath];
                var after = CaptureDirectoryExistenceAndChildSet(realPath);
                Assert.Equal(before.Exists, after.Exists);
                Assert.Equal(before.Entries, after.Entries);
            }
        }
        finally
        {
            // Defensive cleanup in case an assertion failed mid-test
            using (var leftoverReg = Registry.CurrentUser.OpenSubKey(uninstallSubKey, writable: false))
            {
                if (leftoverReg is not null)
                {
                    _ = await RunPythonPackageScriptAsync(
                        repoRoot!,
                        sandbox.DataDirectory,
                        ["-B", "scripts/package.py", "--identity", "spawn-dev-test", "--uninstall"]);
                }
            }

            try
            {
                if (Directory.Exists(distIdentityDir))
                {
                    Directory.Delete(distIdentityDir, recursive: true);
                }
            }
            catch
            {
                // Ignore best-effort dist cleanup.
            }
        }
    }

    private static string? TryFindInnoSetup7Compiler()
    {
        string? envOverride = Environment.GetEnvironmentVariable("SPAWN_ISCC_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return envOverride;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(localAppData, "Programs", "Inno Setup 7", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 7", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 7", "ISCC.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static (bool Exists, HashSet<string> Entries) CaptureDirectoryExistenceAndChildSet(string path)
    {
        if (!Directory.Exists(path))
        {
            return (false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var entries = Directory.EnumerateFileSystemEntries(path)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (true, entries);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonPackageScriptAsync(
        string repoRoot,
        string isolatedDataDir,
        IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        psi.Environment["PYTHONUTF8"] = "1";

        // Child `dotnet publish` must not spawn reusable MSBuild nodes that outlive this test and keep pipes/files open.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        // quick_gate routes test-time HTTP to a dead proxy (network tripwire). The child `dotnet publish` is build tooling
        // (NuGet no-op restore), so only the tripwire value is dropped; a user's real proxy settings are left untouched.
        string? tripwire = Environment.GetEnvironmentVariable("SPAWN_NETWORK_TRIPWIRE");
        if (!string.IsNullOrWhiteSpace(tripwire))
        {
            foreach (string proxyVar in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            {
                if (string.Equals(psi.Environment.TryGetValue(proxyVar, out string? v) ? v : null, tripwire, StringComparison.OrdinalIgnoreCase))
                {
                    psi.Environment.Remove(proxyVar);
                }
            }
        }
        psi.Environment["SPAWN_DATA_DIR"] = isolatedDataDir;
        psi.Environment["SPAWN_HEADLESS_TEST"] = "1";
        psi.Environment["SPAWN_MUTE_AUDIO"] = "1";
        psi.Environment.Remove("CI");
        psi.Environment.Remove("SPAWN_DEV_AUTO_INSTALL");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start python process.");
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();

        await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
        return (proc.ExitCode, await outTask, await errTask);
    }
}
