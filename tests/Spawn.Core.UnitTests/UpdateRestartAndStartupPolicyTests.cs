using System.Text.Json;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

public sealed class UpdateRestartAndStartupPolicyTests
{
    private static readonly DateTimeOffset BaseNow = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartupWindowPolicy_Decide_MatchesFullRestartAndBackgroundTruthTable()
    {
        var plainArgs = SpawnCommandLineOptions.Parse([]);
        var backgroundArgs = SpawnCommandLineOptions.Parse(["--background"]);

        var devMarkerOpen = new UpdateRestartMarker(UpdateRestartReason.DevInstall, WindowOpen: true, "0.1.1-dev.1", BaseNow.AddSeconds(-20));
        var devMarkerTray = new UpdateRestartMarker(UpdateRestartReason.DevInstall, WindowOpen: false, "0.1.1-dev.1", BaseNow.AddSeconds(-20));
        var updateMarkerOpen = new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: true, "0.1.1", BaseNow.AddSeconds(-20));
        var updateMarkerTray = new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: false, "0.1.1", BaseNow.AddSeconds(-20));
        var boundaryMarkerTray = new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: false, "0.1.1", BaseNow - StartupWindowPolicy.RestartMarkerTtl);
        var expiredMarkerTray = new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: false, "0.1.1", BaseNow - StartupWindowPolicy.RestartMarkerTtl - TimeSpan.FromSeconds(1));
        var expiredMarkerOpen = new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: true, "0.1.1", BaseNow.AddHours(-1));
        var futureMarkerTray = new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: false, "0.1.1", BaseNow.AddSeconds(5));

        // 1. No marker: plain launch opens window; --background stays in tray
        Assert.True(StartupWindowPolicy.Decide(null, plainArgs, BaseNow).OpenWindow);
        Assert.False(StartupWindowPolicy.Decide(null, backgroundArgs, BaseNow).OpenWindow);

        // 2. Dev reinstall + --background ALWAYS stays tray-only even if window was open before quit
        StartupWindowDecision devReinstallFromOpen = StartupWindowPolicy.Decide(devMarkerOpen, backgroundArgs, BaseNow);
        Assert.False(devReinstallFromOpen.OpenWindow);
        Assert.True(devReinstallFromOpen.ConsumedValidRestartMarker);
        Assert.Equal(UpdateRestartReason.DevInstall, devReinstallFromOpen.ConsumedRestartReason);
        Assert.True(devReinstallFromOpen.MarkerWindowOpen);

        StartupWindowDecision devReinstallFromTray = StartupWindowPolicy.Decide(devMarkerTray, backgroundArgs, BaseNow);
        Assert.False(devReinstallFromTray.OpenWindow);
        Assert.True(devReinstallFromTray.ConsumedValidRestartMarker);

        // 3. Dev marker without --background restores previous window state
        Assert.True(StartupWindowPolicy.Decide(devMarkerOpen, plainArgs, BaseNow).OpenWindow);
        Assert.False(StartupWindowPolicy.Decide(devMarkerTray, plainArgs, BaseNow).OpenWindow);

        // 4. Release self-update ("update") restores previous window-open vs tray state (even if --background was inherited)
        StartupWindowDecision updateRestoresOpen = StartupWindowPolicy.Decide(updateMarkerOpen, plainArgs, BaseNow);
        Assert.True(updateRestoresOpen.OpenWindow);
        Assert.True(updateRestoresOpen.ConsumedValidRestartMarker);
        Assert.Equal(UpdateRestartReason.Update, updateRestoresOpen.ConsumedRestartReason);
        Assert.Equal("0.1.1", updateRestoresOpen.PreviousVersion);

        StartupWindowDecision updateRestoresOpenEvenWithBg = StartupWindowPolicy.Decide(updateMarkerOpen, backgroundArgs, BaseNow);
        Assert.True(updateRestoresOpenEvenWithBg.OpenWindow);

        StartupWindowDecision updateRestoresTray = StartupWindowPolicy.Decide(updateMarkerTray, plainArgs, BaseNow);
        Assert.False(updateRestoresTray.OpenWindow);
        Assert.True(updateRestoresTray.ConsumedValidRestartMarker);
        Assert.False(updateRestoresTray.MarkerWindowOpen);

        // 5. Exact 5-minute TTL boundary is valid; 5m+1s or future-skewed markers are expired/ignored
        Assert.False(StartupWindowPolicy.Decide(boundaryMarkerTray, plainArgs, BaseNow).OpenWindow);

        StartupWindowDecision expiredNormal = StartupWindowPolicy.Decide(expiredMarkerTray, plainArgs, BaseNow);
        Assert.True(expiredNormal.OpenWindow);
        Assert.False(expiredNormal.ConsumedValidRestartMarker);

        StartupWindowDecision expiredBg = StartupWindowPolicy.Decide(expiredMarkerOpen, backgroundArgs, BaseNow);
        Assert.False(expiredBg.OpenWindow);
        Assert.False(expiredBg.ConsumedValidRestartMarker);

        StartupWindowDecision futureNormal = StartupWindowPolicy.Decide(futureMarkerTray, plainArgs, BaseNow);
        Assert.True(futureNormal.OpenWindow);
        Assert.False(futureNormal.ConsumedValidRestartMarker);
    }

    [Fact]
    public void StartupWindowPolicy_ResolveEffectiveOpenWindow_FallsBackWhenTrayUnavailableAndStaysQuietInHeadlessTests()
    {
        var trayPlanned = new StartupWindowDecision(OpenWindow: false, false, null, null, null);
        var windowPlanned = new StartupWindowDecision(OpenWindow: true, false, null, null, null);

        // Interactive session with working tray -> stays tray-only
        EffectiveStartupWindowResult normalTray = StartupWindowPolicy.ResolveEffectiveOpenWindow(
            trayPlanned,
            trayActive: true,
            isHeadlessTest: false);
        Assert.False(normalTray.OpenWindow);
        Assert.False(normalTray.FellBackBecauseTrayUnavailable);

        // Interactive session where tray icon creation failed -> falls back to showing main window (never strands user)
        EffectiveStartupWindowResult trayFailedFallback = StartupWindowPolicy.ResolveEffectiveOpenWindow(
            trayPlanned,
            trayActive: false,
            isHeadlessTest: false);
        Assert.True(trayFailedFallback.OpenWindow);
        Assert.True(trayFailedFallback.FellBackBecauseTrayUnavailable);

        // Interactive session planning open window -> opens without marking fallback
        EffectiveStartupWindowResult normalOpen = StartupWindowPolicy.ResolveEffectiveOpenWindow(
            windowPlanned,
            trayActive: true,
            isHeadlessTest: false);
        Assert.True(normalOpen.OpenWindow);
        Assert.False(normalOpen.FellBackBecauseTrayUnavailable);

        // Headless test mode -> NEVER pops a window even when tray is inactive or OpenWindow=true
        EffectiveStartupWindowResult headlessTray = StartupWindowPolicy.ResolveEffectiveOpenWindow(
            trayPlanned,
            trayActive: false,
            isHeadlessTest: true);
        Assert.False(headlessTray.OpenWindow);
        Assert.False(headlessTray.FellBackBecauseTrayUnavailable);

        EffectiveStartupWindowResult headlessNormal = StartupWindowPolicy.ResolveEffectiveOpenWindow(
            windowPlanned,
            trayActive: false,
            isHeadlessTest: true);
        Assert.False(headlessNormal.OpenWindow);
        Assert.False(headlessNormal.FellBackBecauseTrayUnavailable);
    }

    [Fact]
    public void StartupWindowPolicy_DecideSecondInstanceAction_KeepsBackgroundSilentAndHonorsQuitForUpdate()
    {
        Assert.Equal(
            SecondInstanceAction.ActivateWindow,
            StartupWindowPolicy.DecideSecondInstanceAction(SpawnCommandLineOptions.Parse([]), isQuitting: false));

        Assert.Equal(
            SecondInstanceAction.Ignore,
            StartupWindowPolicy.DecideSecondInstanceAction(SpawnCommandLineOptions.Parse(["--background"]), isQuitting: false));

        Assert.Equal(
            SecondInstanceAction.Ignore,
            StartupWindowPolicy.DecideSecondInstanceAction(SpawnCommandLineOptions.Parse(["--MINIMIZED"]), isQuitting: false));

        Assert.Equal(
            SecondInstanceAction.ActivateWindow,
            StartupWindowPolicy.DecideSecondInstanceAction(SpawnCommandLineOptions.Parse(["--background", "--activate"]), isQuitting: false));

        Assert.Equal(
            SecondInstanceAction.QuitForUpdate,
            StartupWindowPolicy.DecideSecondInstanceAction(SpawnCommandLineOptions.Parse(["--quit-for-update"]), isQuitting: false));

        Assert.Equal(
            SecondInstanceAction.Ignore,
            StartupWindowPolicy.DecideSecondInstanceAction(SpawnCommandLineOptions.Parse([]), isQuitting: true));
    }

    [Fact]
    public void StartupWindowPolicy_ClassifyVersionChange_DistinguishesDevAutoAndManualInstalls()
    {
        var validAuto = new StartupWindowDecision(true, true, UpdateRestartReason.Update, true, "0.1.1");
        var validDev = new StartupWindowDecision(false, true, UpdateRestartReason.DevInstall, true, "0.1.1-dev.1");
        var noMarker = new StartupWindowDecision(true, false, null, null, null);

        Assert.Equal(UpdateInstallMethod.DevInstall, StartupWindowPolicy.ClassifyVersionChange("dev", "0.1.1-dev.1", "0.1.1-dev.2", validDev));
        Assert.Equal(UpdateInstallMethod.DevInstall, StartupWindowPolicy.ClassifyVersionChange("dev", "0.1.1-dev.1", "0.1.1-dev.2", noMarker));
        Assert.Equal(UpdateInstallMethod.AutoUpdate, StartupWindowPolicy.ClassifyVersionChange("release", "0.1.1", "0.1.2", validAuto));
        Assert.Equal(UpdateInstallMethod.ManualInstall, StartupWindowPolicy.ClassifyVersionChange("release", "0.1.1", "0.1.2", noMarker));
        Assert.Equal(UpdateInstallMethod.ManualInstall, StartupWindowPolicy.ClassifyVersionChange("release", "0.1.1", "0.1.2", validDev));
        Assert.Equal(UpdateInstallMethod.None, StartupWindowPolicy.ClassifyVersionChange("release", null, "0.1.2", noMarker));
        Assert.Equal(UpdateInstallMethod.None, StartupWindowPolicy.ClassifyVersionChange("release", "0.1.2", "0.1.2", noMarker));
        Assert.Equal(UpdateInstallMethod.None, StartupWindowPolicy.ClassifyVersionChange("dev", "0.1.0-source", "0.1.1-dev.1", noMarker));
    }

    [Fact]
    public async Task UpdateRestartStateStore_RoundTripsAtomicallyConsumesOnceAndCleansCorruptFiles()
    {
        await using var sandbox = new TestSandbox();
        string markerPath = sandbox.Paths.UpdateRestartStateFilePath;

        // 1. Save valid release update marker (WindowOpen=false) -> JSON schema matches contract & first read consumes/deletes file
        var releaseMarker = new UpdateRestartMarker(
            Reason: UpdateRestartReason.Update,
            WindowOpen: false,
            FromVersion: "0.1.1",
            AtUtc: BaseNow.AddSeconds(-30));
        UpdateRestartStateStore.SaveMarker(markerPath, releaseMarker);
        Assert.True(File.Exists(markerPath));

        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath)))
        {
            Assert.Equal("update", doc.RootElement.GetProperty("reason").GetString());
            Assert.False(doc.RootElement.GetProperty("windowOpen").GetBoolean());
            Assert.Equal("0.1.1", doc.RootElement.GetProperty("fromVersion").GetString());
            Assert.Equal("2026-11-22T09:59:30Z", doc.RootElement.GetProperty("at").GetString());
        }

        StartupWindowDecision firstLaunch = UpdateRestartStateStore.ConsumeAndDecide(
            markerPath,
            SpawnCommandLineOptions.Parse([]),
            BaseNow);
        Assert.False(firstLaunch.OpenWindow);
        Assert.True(firstLaunch.ConsumedValidRestartMarker);
        Assert.Equal(UpdateRestartReason.Update, firstLaunch.ConsumedRestartReason);
        Assert.False(File.Exists(markerPath));

        // 2. Subsequent normal launch has no marker left -> opens window
        StartupWindowDecision secondLaunch = UpdateRestartStateStore.ConsumeAndDecide(
            markerPath,
            SpawnCommandLineOptions.Parse([]),
            BaseNow.AddSeconds(5));
        Assert.True(secondLaunch.OpenWindow);
        Assert.False(secondLaunch.ConsumedValidRestartMarker);

        // 3. Corrupt JSON file is safely deleted and falls back to normal launch
        await File.WriteAllTextAsync(markerPath, "{not-valid-json");
        StartupWindowDecision afterCorrupt = UpdateRestartStateStore.ConsumeAndDecide(
            markerPath,
            SpawnCommandLineOptions.Parse([]),
            BaseNow);
        Assert.True(afterCorrupt.OpenWindow);
        Assert.False(afterCorrupt.ConsumedValidRestartMarker);
        Assert.False(File.Exists(markerPath));

        // 4. Expired marker on disk is deleted and ignored
        UpdateRestartStateStore.SaveMarker(
            markerPath,
            new UpdateRestartMarker(UpdateRestartReason.DevInstall, WindowOpen: false, "0.1.1-dev.1", BaseNow.AddMinutes(-10)));
        StartupWindowDecision afterExpired = UpdateRestartStateStore.ConsumeAndDecide(
            markerPath,
            SpawnCommandLineOptions.Parse([]),
            BaseNow);
        Assert.True(afterExpired.OpenWindow);
        Assert.False(afterExpired.ConsumedValidRestartMarker);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public void HostLifecycle_AppAndTray_WireRestartStateContractTrayFallbackAndTaskbarCreatedRecovery()
    {
        string? repoRoot = TestSandbox.TryLocateRepositoryRoot();
        Assert.False(string.IsNullOrWhiteSpace(repoRoot));

        string appCs = File.ReadAllText(Path.Combine(repoRoot!, "src", "Spawn.Host", "App.xaml.cs"));
        Assert.Contains("UpdateRestartStateStore.ConsumeAndDecide", appCs, StringComparison.Ordinal);
        Assert.Contains("UpdateRestartStateStore.SaveMarker", appCs, StringComparison.Ordinal);
        Assert.Contains("StartupWindowPolicy.ResolveEffectiveOpenWindow", appCs, StringComparison.Ordinal);
        Assert.Contains("StartupWindowPolicy.DecideSecondInstanceAction", appCs, StringComparison.Ordinal);
        Assert.Contains("_mainWindow is not null && _mainWindow.IsVisible", appCs, StringComparison.Ordinal);

        string trayCs = File.ReadAllText(Path.Combine(repoRoot!, "src", "Spawn.Host", "Services", "TrayIconService.cs"));
        Assert.Contains("RegisterWindowMessageW(\"TaskbarCreated\")", trayCs, StringComparison.Ordinal);
        Assert.Contains("CleanupFailedInitialization", trayCs, StringComparison.Ordinal);
    }
}
