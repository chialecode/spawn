using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class SingleInstanceAndPackageInstallerTests
{
    private const string SpawnDevTestAppId = "io.github.chialecode.spawn-dev-test";

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

            // 1b. Secondary launch with --background stays completely silent and NEVER fires ActivateRequested
            await using (var bgSecondary = new SingleInstanceCoordinator(testIdentity))
            {
                var bgResult = await bgSecondary.InitializeAsync(["--background"]);
                Assert.Equal(SingleInstanceStartupAction.ExitSecondaryInstance, bgResult.Action);
                Assert.True(bgResult.WasPrimaryRunning);
                Assert.True(bgResult.HandshakeDelivered);
            }

            await Task.Delay(120);
            Assert.False(activatedArgsTcs.Task.IsCompleted);

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

            // 3. Installer/updater launch with --quit-for-update signals primary (with reason) and waits for mutex release
            await using (var updater = new SingleInstanceCoordinator(testIdentity))
            {
                var quitResult = await updater.InitializeAsync(
                    ["--quit-for-update", "--restart-reason=update"],
                    quitWaitTimeout: TimeSpan.FromSeconds(5));

                Assert.Equal(SingleInstanceStartupAction.ExitAfterQuitForUpdate, quitResult.Action);
                Assert.True(quitResult.WasPrimaryRunning);
                Assert.True(quitResult.HandshakeDelivered);
                Assert.Equal("update", quitResult.Options.RestartReason);
            }

            Assert.True(await quitRequestedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(UpdateRestartReason.Update, primary.LastQuitForUpdateReason);
            Assert.False(SingleInstanceCoordinator.IsInstanceRunning(testIdentity));
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task PackageScript_ExplorerVerificationJobReportingAndEnvSanitization_AvoidFalseInJobPositives()
    {
        string? repoRoot = TestSandbox.TryLocateRepositoryRoot();
        Assert.False(string.IsNullOrWhiteSpace(repoRoot));

        const string pyProbe = """
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts"))
import package as pkg

# 1. is_explorer_image checks %SystemRoot%\explorer.exe case-insensitively
assert pkg.is_explorer_image(r"C:\Windows\explorer.exe", system_root=r"C:\WINDOWS")
assert not pkg.is_explorer_image(r"C:\Windows\System32\cmd.exe", system_root=r"C:\WINDOWS")
assert not pkg.is_explorer_image(r"C:\Temp\explorer.exe", system_root=r"C:\WINDOWS")

# 2. verify_parent_is_explorer checks live PPID, rejects self/mismatch/non-explorer
assert pkg.verify_parent_is_explorer(
    4200,
    reported_ppid=800,
    self_pid=9999,
    system_root=r"C:\Windows",
    lookup_parent=lambda pid: 800 if pid == 4200 else None,
    lookup_image=lambda ppid: r"C:\WINDOWS\Explorer.EXE" if ppid == 800 else None,
)
assert not pkg.verify_parent_is_explorer(
    4200,
    reported_ppid=801,
    self_pid=9999,
    system_root=r"C:\Windows",
    lookup_parent=lambda pid: 800,
    lookup_image=lambda ppid: r"C:\WINDOWS\Explorer.EXE",
)
assert not pkg.verify_parent_is_explorer(
    4200,
    reported_ppid=9999,
    self_pid=9999,
    system_root=r"C:\Windows",
    lookup_parent=lambda pid: 9999,
    lookup_image=lambda ppid: r"C:\WINDOWS\Explorer.EXE",
)

# 3. resolve_shell_launch_job_state reports in_job=False, via_shell=True for verified Explorer child
#    EVEN WHEN raw IsProcessInJob(pid, NULL) returns True (Windows PCA/shell or app-owned job).
pid_ok, in_job_ok, via_shell_ok = pkg.resolve_shell_launch_job_state(
    4200,
    800,
    verify_explorer=lambda p, pp: True,
    pid_in_job=lambda p: True,
)
assert (pid_ok, in_job_ok, via_shell_ok) == (4200, False, True)

# When Explorer parent is NOT confirmed, conservatively report raw pid_in_job and via_shell=False
pid_fb, in_job_fb, via_shell_fb = pkg.resolve_shell_launch_job_state(
    4201,
    123,
    verify_explorer=lambda p, pp: False,
    pid_in_job=lambda p: True,
)
assert (pid_fb, in_job_fb, via_shell_fb) == (4201, True, False)

# 4. build_clean_app_env strips SPAWN_* overrides for real spawn-dev but preserves them for spawn-dev-test
dirty = {
    "PATH": "C:\\Windows\\System32",
    "SPAWN_DATA_DIR": "C:\\Temp\\isolated",
    "SPAWN_CHANNEL": "test",
    "SPAWN_IDENTITY": "spawn-test",
    "SPAWN_HEADLESS_TEST": "1",
    "SPAWN_TEST_HEADLESS": "1",
    "SPAWN_MUTE_AUDIO": "1",
}
clean_dev = pkg.build_clean_app_env(pkg.IDENTITY_PROFILES["spawn-dev"], source_env=dirty)
assert "PATH" in clean_dev
for forbidden in ("SPAWN_DATA_DIR", "SPAWN_CHANNEL", "SPAWN_IDENTITY", "SPAWN_HEADLESS_TEST", "SPAWN_TEST_HEADLESS", "SPAWN_MUTE_AUDIO"):
    assert forbidden not in clean_dev

kept_test = pkg.build_clean_app_env(pkg.IDENTITY_PROFILES["spawn-dev-test"], source_env=dirty)
assert kept_test["SPAWN_DATA_DIR"] == dirty["SPAWN_DATA_DIR"]
assert kept_test["SPAWN_HEADLESS_TEST"] == "1"

# 5. 64-bit HANDLE ctypes signatures on kernel32: IsProcessInJob(GetCurrentProcess(), None) must succeed
#    with last_error == 0 (never ERROR_INVALID_HANDLE=6) and agree with is_process_in_any_job(os.getpid()).
import ctypes
from ctypes import wintypes
import os

k32 = pkg._get_kernel32()
assert k32.GetCurrentProcess.restype == wintypes.HANDLE
assert k32.CloseHandle.argtypes == [wintypes.HANDLE]
assert k32.IsProcessInJob.argtypes[0] == wintypes.HANDLE
assert k32.IsProcessInJob.argtypes[1] == wintypes.HANDLE
assert k32.SetPriorityClass.argtypes[0] == wintypes.HANDLE

in_job_out = wintypes.BOOL(False)
ctypes.set_last_error(0)
ok_self = k32.IsProcessInJob(k32.GetCurrentProcess(), None, ctypes.byref(in_job_out))
last_err = ctypes.get_last_error()
assert ok_self != 0, f"IsProcessInJob(GetCurrentProcess(), None) failed with last_error={last_err}"
assert last_err == 0, f"Unexpected GetLastError={last_err}"
assert bool(in_job_out.value) == pkg.is_current_process_in_any_job()
assert pkg.is_current_process_in_any_job() == pkg.is_process_in_any_job(os.getpid())

# 6. D-32 dev auto-install skip guards
assert pkg.should_skip_dev_auto_install({"SPAWN_DEV_AUTO_INSTALL": "0"}) is not None
assert pkg.should_skip_dev_auto_install({"CI": "true"}) is not None
assert pkg.should_skip_dev_auto_install({"CI": "TRUE"}) is not None
assert pkg.should_skip_dev_auto_install({}) is None

print(json.dumps({"ok": True, "selfInJob": bool(in_job_out.value)}))
""";

        await using var sandbox = new TestSandbox(repoRoot);
        var proc = await RunPythonPackageScriptAsync(
            repoRoot!,
            sandbox.DataDirectory,
            ["-B", "-c", pyProbe]);
        Assert.True(
            proc.ExitCode == 0,
            $"package.py helper self-test failed (exit {proc.ExitCode}):\n{proc.Stdout}\n{proc.Stderr}");
        Assert.Contains("\"ok\": true", proc.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryCompliance_ThirdPartyNoticesInstallerAndQuitChain_EnforceM3IsolationInvariants()
    {
        string? repoRoot = TestSandbox.TryLocateRepositoryRoot();
        Assert.False(string.IsNullOrWhiteSpace(repoRoot));

        // 1. Repository root THIRD-PARTY-NOTICES.txt must cover M3 MIT packages, embedded AWACY dataset, M2 packages & LGPL-3.0 gbe_fork
        string noticesPath = Path.Combine(repoRoot!, "THIRD-PARTY-NOTICES.txt");
        Assert.True(File.Exists(noticesPath), $"Missing repository root compliance file: {noticesPath}");
        string notices = File.ReadAllText(noticesPath);
        string[] requiredTokens =
        [
            "SharpCompress 0.50.9",
            "AngleSharp 1.10.3",
            "Microsoft.Windows.CsWin32 0.3.371",
            "NAudio.Wasapi 2.4.0",
            "AreWeAntiCheatYet",
            "WPF-UI",
            "SkiaSharp",
            "CommunityToolkit.Mvvm",
            "Google.Protobuf",
            "Microsoft.Data.Sqlite",
            "Detanup01/gbe_fork",
            "LGPL-3.0",
        ];
        foreach (string token in requiredTokens)
        {
            Assert.Contains(token, notices, StringComparison.Ordinal);
        }

        // 2. installer/spawn.nsi installs the staged app folder as is (Workers\Trainer\ and
        //    THIRD-PARTY-NOTICES.txt included), relaunches only on /RELAUNCH, writes the
        //    io.github.chialecode.<identity> uninstall entry, and never deletes recursively from the
        //    install folder (only the generated file list, the .retired folder and an empty folder).
        Assert.False(File.Exists(Path.Combine(repoRoot!, "installer", "spawn.iss")), "Inno Setup script must be gone.");
        string nsiPath = Path.Combine(repoRoot!, "installer", "spawn.nsi");
        string[] activeNsiLines = File.ReadAllLines(nsiPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith(';'))
            .ToArray();
        string activeNsiText = string.Join('\n', activeNsiLines);
        Assert.Contains("File /r \"${SPAWN_SOURCE_DIR}\\*\"", activeNsiText, StringComparison.Ordinal);
        Assert.Contains("${GetOptions} $0 \"/RELAUNCH\" $1", activeNsiText, StringComparison.Ordinal);
        Assert.Contains("--after-update", activeNsiText, StringComparison.Ordinal);
        Assert.Contains("Uninstall\\${SPAWN_APP_ID}", activeNsiText, StringComparison.Ordinal);
        Assert.Contains("!insertmacro SPAWN_REMOVE_INSTALLED_FILES", activeNsiText, StringComparison.Ordinal);
        Assert.Contains("--quit-for-update", activeNsiText, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminateProcess", activeNsiText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", activeNsiText, StringComparison.OrdinalIgnoreCase);
        string[] recursiveRemovals = activeNsiLines
            .Where(line => line.StartsWith("RMDir /r", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(recursiveRemovals);
        Assert.All(
            recursiveRemovals,
            line => Assert.Equal("RMDir /r \"$INSTDIR\\.retired\"", line));

        // 2b. W10 release tooling and ECDSA P-256 update-signing public key hygiene
        string releasePyPath = Path.Combine(repoRoot!, "scripts", "release.py");
        string signCsPath = Path.Combine(repoRoot!, "scripts", "sign_update.cs");
        string pubKeyPath = Path.Combine(repoRoot!, "release-keys", $"{Spawn.Core.Update.UpdateSigningPublicKey.KeyId}.pub.pem");
        Assert.True(File.Exists(releasePyPath), $"Missing release script: {releasePyPath}");
        Assert.True(File.Exists(signCsPath), $"Missing signing tool: {signCsPath}");
        Assert.True(File.Exists(pubKeyPath), $"Missing update signing public key: {pubKeyPath}");

        byte[] pubKeyBytes = File.ReadAllBytes(pubKeyPath);
        Assert.False(pubKeyBytes.Length >= 3 && pubKeyBytes[0] == 0xEF && pubKeyBytes[1] == 0xBB && pubKeyBytes[2] == 0xBF);
        Assert.DoesNotContain((byte)'\r', pubKeyBytes);
        string pubKeyPem = Encoding.UTF8.GetString(pubKeyBytes);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----\n", pubKeyPem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", pubKeyPem, StringComparison.OrdinalIgnoreCase);
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(pubKeyPem);
            Assert.Equal(256, ecdsa.KeySize);
            ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: false);
            Assert.Equal("1.2.840.10045.3.1.7", ecParams.Curve.Oid?.Value);
        }

        // 3. --quit-for-update graceful shutdown chain in App.xaml.cs stops active trainer sessions & worker (never force-kills games)
        string appCsPath = Path.Combine(repoRoot!, "src", "Spawn.Host", "App.xaml.cs");
        string appCsText = File.ReadAllText(appCsPath);
        Assert.Contains("QuitForUpdateRequested", appCsText, StringComparison.Ordinal);
        Assert.Contains("ITrainerRuntimeService", appCsText, StringComparison.Ordinal);
        Assert.Contains("StopAllAsync(TrainerShutdownReason.HostShutdownOrUpdate)", appCsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageAndInstaller_SpawnDevTestIdentity_BuildsSilentlyInstallsAndUninstallsWithZeroChannelPollution()
    {
        string? repoRoot = TestSandbox.TryLocateRepositoryRoot();
        Assert.False(string.IsNullOrWhiteSpace(repoRoot));

        string? makensisPath = TryFindPinnedMakensis(repoRoot!);
        if (string.IsNullOrWhiteSpace(makensisPath))
        {
            // Without the pinned NSIS fetched yet (cloud CI runners: tests stay offline) still verify
            // SpawnCommandLineOptions; scripts/package.py fetches NSIS on its first local run.
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

        string uninstallSubKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{SpawnDevTestAppId}";
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

            // 1. Verify receipt, stage_app & installed executables, Workers\Trainer\ isolation, compliance files, and dev-workspace.json bridge
            Assert.True(File.Exists(receiptPath));
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath)))
            {
                Assert.True(doc.RootElement.GetProperty("installed").GetBoolean());
                Assert.Equal("spawn-dev-test", doc.RootElement.GetProperty("identity").GetString());
                Assert.Equal(testVersion, doc.RootElement.GetProperty("newVersion").GetString());
                Assert.True(doc.RootElement.TryGetProperty("installedTrainerWorker", out _));
                Assert.True(doc.RootElement.TryGetProperty("installedNotices", out _));
            }

            // 1a. Verify staged dist\spawn-dev-test\app preserves strict Clean Host root isolation
            string stagedAppDir = Path.Combine(distIdentityDir, "app");
            string stagedTrainerExe = Path.Combine(stagedAppDir, "Workers", "Trainer", "Spawn.TrainerWorker.exe");
            string stagedTrainerDll = Path.Combine(stagedAppDir, "Workers", "Trainer", "Spawn.TrainerWorker.dll");
            string stagedNotices = Path.Combine(stagedAppDir, "THIRD-PARTY-NOTICES.txt");
            Assert.True(File.Exists(stagedTrainerExe), $"Missing staged worker executable: {stagedTrainerExe}");
            Assert.True(File.Exists(stagedTrainerDll), $"Missing staged worker assembly: {stagedTrainerDll}");
            Assert.True(File.Exists(stagedNotices), $"Missing staged THIRD-PARTY-NOTICES.txt: {stagedNotices}");
            Assert.Empty(Directory.GetFiles(stagedAppDir, "Spawn.TrainerWorker*", SearchOption.TopDirectoryOnly));
            AssertCleanHostPackagedRootIsolation(stagedAppDir);
            // W10: Dev channel strips native *.pdb (e.g., libSkiaSharp.pdb) while preserving managed Spawn.*.pdb
            Assert.Empty(Directory.GetFiles(stagedAppDir, "libSkiaSharp.pdb", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(stagedAppDir, "Spawn.Core.pdb")));
            Assert.True(File.Exists(Path.Combine(stagedAppDir, "Spawn.OverlayHost.pdb")));
            Assert.True(File.Exists(Path.Combine(stagedAppDir, "Workers", "Trainer", "Spawn.TrainerWorker.pdb")));

            string shaSumsPath = Path.Combine(distIdentityDir, "SHA256SUMS.txt");
            Assert.True(File.Exists(shaSumsPath));
            string shaSums = await File.ReadAllTextAsync(shaSumsPath);
            Assert.Contains("app/Workers/Trainer/Spawn.TrainerWorker.exe", shaSums, StringComparison.Ordinal);
            Assert.Contains("THIRD-PARTY-NOTICES.txt", shaSums, StringComparison.Ordinal);

            // 1b. Verify isolated installed layout preserves Workers\Trainer\ & THIRD-PARTY-NOTICES.txt with zero root leakage
            string installedExe = Path.Combine(isolatedInstallDir, "spawn-dev-test.exe");
            string installedOverlayExe = Path.Combine(isolatedInstallDir, "Spawn.OverlayHost.exe");
            string installedTrainerExe = Path.Combine(isolatedInstallDir, "Workers", "Trainer", "Spawn.TrainerWorker.exe");
            string installedTrainerDll = Path.Combine(isolatedInstallDir, "Workers", "Trainer", "Spawn.TrainerWorker.dll");
            string installedNoticesPath = Path.Combine(isolatedInstallDir, "THIRD-PARTY-NOTICES.txt");
            Assert.True(File.Exists(installedExe));
            Assert.True(File.Exists(installedOverlayExe));
            Assert.True(File.Exists(installedTrainerExe), $"Missing installed worker executable: {installedTrainerExe}");
            Assert.True(File.Exists(installedTrainerDll), $"Missing installed worker assembly: {installedTrainerDll}");
            Assert.True(File.Exists(Path.Combine(isolatedInstallDir, "VERSION.txt")));
            Assert.True(File.Exists(installedNoticesPath));
            Assert.Empty(Directory.GetFiles(isolatedInstallDir, "Spawn.TrainerWorker*", SearchOption.TopDirectoryOnly));
            AssertCleanHostPackagedRootIsolation(isolatedInstallDir);
            Assert.Empty(Directory.GetFiles(isolatedInstallDir, "libSkiaSharp.pdb", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(isolatedInstallDir, "Spawn.Core.pdb")));
            Assert.True(File.Exists(Path.Combine(isolatedInstallDir, "Workers", "Trainer", "Spawn.TrainerWorker.pdb")));
            string installedNotices = await File.ReadAllTextAsync(installedNoticesPath);
            Assert.Contains("SharpCompress 0.50.9", installedNotices, StringComparison.Ordinal);
            Assert.Contains("AngleSharp 1.10.3", installedNotices, StringComparison.Ordinal);
            Assert.Contains("AreWeAntiCheatYet", installedNotices, StringComparison.Ordinal);
            Assert.Contains("Detanup01/gbe_fork", installedNotices, StringComparison.Ordinal);
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

    private static void AssertCleanHostPackagedRootIsolation(string rootDir)
    {
        string[] forbiddenRootPrefixes =
        [
            "Spawn.TrainerWorker",
            "Spawn.BlueprintWorker",
            "Spawn.WorkflowWorker",
            "Reloaded",
            "SevenZipExtractor",
            "SevenZipSharp",
            "OpenCvSharp",
            "Iced",
            "Maa.Framework",
        ];

        foreach (string file in Directory.EnumerateFiles(rootDir, "*", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(file);
            foreach (string prefix in forbiddenRootPrefixes)
            {
                Assert.False(
                    fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                    $"Clean Host packaged root '{rootDir}' must not contain isolated/forbidden file '{fileName}'.");
            }
        }
    }

    private static string? TryFindPinnedMakensis(string repoRoot)
    {
        string? envOverride = Environment.GetEnvironmentVariable("SPAWN_MAKENSIS_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return envOverride;
        }

        string cached = Path.Combine(repoRoot, ".tools", "nsis-3.13", "makensis.exe");
        return File.Exists(cached) ? cached : null;
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
