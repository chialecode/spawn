using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using Spawn.Contracts;
using Spawn.Host.Helpers;
using Spawn.Host.Services;
using Spawn.OverlayHost.Audio;
using Spawn.OverlayHost.Views;
using Xunit;

namespace Spawn.Architecture.GateTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfStaHeadlessCollection
{
    public const string Name = "WpfStaHeadless";
}

/// <summary>
/// Verifies unified UI/monospace font stacks (including CJK monospace fallbacks), tabular numerals,
/// About-page canonical <c>DEV</c> badge colors, source-build PE <c>ApplicationIcon</c> embedding,
/// <c>scripts/generate_icons.py --check</c>, DPI-aware tray <c>HICON</c> loading, and Win11 DWM frame theme sync.
/// </summary>
[Collection(WpfStaHeadlessCollection.Name)]
public sealed partial class IconFontAndShellStyleGateTests
{
    private const string CanonicalUiFontStack =
        "Inter, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei";

    private const string CanonicalMonoFontStack =
        "JetBrains Mono, Cascadia Mono, Consolas, Microsoft YaHei UI, Microsoft YaHei";

    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint RtIconTypeId = 3;
    private const uint RtGroupIconTypeId = 14;

    [Fact]
    public void TypographyAndBadgeXamlGate_EnforcesUiAndMonoFontStacksTabularNumeralsSemiBoldVersionAndCanonicalDevPill()
    {
        string repoRoot = LocateRepositoryRoot();
        XNamespace xamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        // 1. Controls.xaml font stacks
        XDocument controlsDoc = XDocument.Load(Path.Combine(repoRoot, "src", "Spawn.Host", "Themes", "Controls.xaml"));
        var fontFamilies = controlsDoc.Descendants(xamlNs + "FontFamily")
            .ToDictionary(
                el => (string?)el.Attribute(xNs + "Key") ?? string.Empty,
                el => el.Value.Trim(),
                StringComparer.Ordinal);

        Assert.Equal(CanonicalUiFontStack, fontFamilies["SpawnUiFont"]);
        Assert.Equal(CanonicalMonoFontStack, fontFamilies["SpawnMonoFont"]);

        // 2. OverlayToastWindow.xaml explicit FontFamily, aligned mono stack, and Tabular numerals
        XDocument overlayDoc = XDocument.Load(Path.Combine(repoRoot, "src", "Spawn.OverlayHost", "Views", "OverlayToastWindow.xaml"));
        XElement overlayRoot = overlayDoc.Root!;
        Assert.Equal(CanonicalUiFontStack, (string?)overlayRoot.Attribute("FontFamily"));
        Assert.Equal("Tabular", (string?)overlayRoot.Attribute("Typography.NumeralAlignment"));

        XElement xboxPercent = overlayDoc.Descendants(xamlNs + "TextBlock")
            .Single(el => string.Equals((string?)el.Attribute(xNs + "Name"), "XboxPercentText", StringComparison.Ordinal));
        Assert.Equal(CanonicalMonoFontStack, (string?)xboxPercent.Attribute("FontFamily"));

        // 3. MainWindow.xaml Tabular numerals, SemiBold Update.CurrentVersionText, and canonical About DEV badge (#F3A115 / #202124)
        XDocument mainDoc = XDocument.Load(Path.Combine(repoRoot, "src", "Spawn.Host", "Views", "MainWindow.xaml"));
        XElement mainRoot = mainDoc.Root!;
        Assert.Equal("Tabular", (string?)mainRoot.Attribute("Typography.NumeralAlignment"));

        XElement updateVersionText = mainDoc.Descendants(xamlNs + "TextBlock")
            .Single(el => string.Equals((string?)el.Attribute("Text"), "{Binding Update.CurrentVersionText}", StringComparison.Ordinal));
        Assert.Equal("SemiBold", (string?)updateVersionText.Attribute("FontWeight"));

        XElement aboutBadge = mainDoc.Descendants(xamlNs + "Border")
            .Single(el => string.Equals((string?)el.Attribute(xNs + "Name"), "AboutBuildChannelBadge", StringComparison.Ordinal));
        Assert.Equal("#F3A115", ((string?)aboutBadge.Attribute("Background"))?.ToUpperInvariant());

        XElement aboutBadgeText = aboutBadge.Elements(xamlNs + "TextBlock").Single();
        Assert.Equal("{Binding BuildChannelBadgeText}", (string?)aboutBadgeText.Attribute("Text"));
        Assert.Equal("#202124", ((string?)aboutBadgeText.Attribute("Foreground"))?.ToUpperInvariant());

        // 4. Headless STA runtime verification on OverlayToastWindow (never shown)
        Exception? staException = null;
        var staThread = new Thread(() =>
        {
            try
            {
                using var silentPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: true);
                var window = new OverlayToastWindow(silentPlayer);
                try
                {
                    Assert.False(window.IsVisible);
                    Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(window));
                    Assert.Equal(CanonicalUiFontStack, window.FontFamily.Source);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(60)), "STA font/numeral verification timed out.");
        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    [Theory]
    [InlineData("Spawn.Host")]
    [InlineData("Spawn.OverlayHost")]
    [InlineData("Spawn.TrainerWorker")]
    public void DirectoryBuildPropsAndCompiledExecutables_DefaultChannelIconAndEmbedWin32PeIconResources(string projectName)
    {
        string repoRoot = LocateRepositoryRoot();
        string propsXml = File.ReadAllText(Path.Combine(repoRoot, "Directory.Build.props"), Encoding.UTF8);
        Assert.Contains(@"$(MSBuildThisFileDirectory)assets\icon.ico", propsXml, StringComparison.Ordinal);
        Assert.Contains(@"$(MSBuildThisFileDirectory)assets\icon-dev.ico", propsXml, StringComparison.Ordinal);

        string exePath = LocateCompiledExePath(repoRoot, projectName);
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(fs);

        DirectoryEntry resDir = peReader.PEHeaders.PEHeader!.ResourceTableDirectory;
        Assert.True(resDir.RelativeVirtualAddress > 0 && resDir.Size >= 16, $"{projectName}.exe is missing a Win32 PE .rsrc table.");

        ReadOnlySpan<byte> rsrc = peReader.GetSectionData(resDir.RelativeVirtualAddress).GetContent().AsSpan();
        int namedCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(12, 2));
        int idCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(14, 2));
        int totalEntries = namedCount + idCount;
        Assert.True(totalEntries > 0, $"{projectName}.exe .rsrc directory contains zero entries.");

        var resourceTypeIds = new HashSet<uint>();
        for (int i = 0; i < totalEntries; i++)
        {
            int entryOffset = 16 + (i * 8);
            uint nameOrId = BinaryPrimitives.ReadUInt32LittleEndian(rsrc.Slice(entryOffset, 4));
            if ((nameOrId & 0x80000000u) == 0)
            {
                resourceTypeIds.Add(nameOrId);
            }
        }

        Assert.Contains(RtIconTypeId, resourceTypeIds);
        Assert.Contains(RtGroupIconTypeId, resourceTypeIds);
    }

    [Fact]
    public void GenerateIconsScript_CheckFlag_SupportsCheckCliAndFailsFastOnMissingAssets()
    {
        string repoRoot = LocateRepositoryRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "generate_icons.py");
        string scriptText = File.ReadAllText(scriptPath, Encoding.UTF8);
        Assert.Contains("\"--check\"", scriptText, StringComparison.Ordinal);
        Assert.Contains("def check_all_icons(", scriptText, StringComparison.Ordinal);

        // Full 1024x1024 canonical & tampered byte parity runs serially in scripts/test_package.py
        // so parallel dotnet test assemblies never contend with Python rasterization CPU.
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-icon-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var (failExit, _, failStderr) = RunPythonScriptQuiet(
                repoRoot,
                scriptPath,
                "--check",
                "--assets-dir",
                tempDir);
            Assert.NotEqual(0, failExit);
            Assert.Contains("missing icon.png", failStderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TrayIconService_SmallIconMetricsAndDpiHIconLoading_LoadsIcoFramesAndSkiaFallbackAtExactDpiSizes(int dpiSizePx)
    {
        Assert.Equal((16, 16), TrayIconService.NormalizeSmallIconSize(0, -1));
        Assert.Equal((dpiSizePx, dpiSizePx), TrayIconService.NormalizeSmallIconSize(dpiSizePx, dpiSizePx));

        var (sysW, sysH) = TrayIconService.GetSystemSmallIconSize();
        Assert.InRange(sysW, 16, 256);
        Assert.InRange(sysH, 16, 256);

        Assert.NotNull(TrayIconService.ResolveBrandIconIcoPath(isDevChannel: false));
        Assert.NotNull(TrayIconService.ResolveBrandIconIcoPath(isDevChannel: true));

        foreach (bool isDev in new[] { false, true })
        {
            // 1. Multi-size .ico LoadImageW path
            nint icoHandle = TrayIconService.LoadOrRenderTrayHIcon(isDev, dpiSizePx, dpiSizePx);
            Assert.NotEqual(nint.Zero, icoHandle);
            try
            {
                AssertHIconDimensions(icoHandle, dpiSizePx, dpiSizePx);
            }
            finally
            {
                Assert.True(TrayIconService.DestroyTrayHIcon(icoHandle));
            }

            // 2. BrandIconGeometry SkiaSharp fallback path at exact DPI size
            nint fallbackHandle = TrayIconService.LoadOrRenderTrayHIcon(
                isDev,
                dpiSizePx,
                dpiSizePx,
                icoPathOverride: string.Empty,
                allowEmbeddedModuleIcon: false);
            Assert.NotEqual(nint.Zero, fallbackHandle);
            try
            {
                AssertHIconDimensions(fallbackHandle, dpiSizePx, dpiSizePx);
            }
            finally
            {
                Assert.True(TrayIconService.DestroyTrayHIcon(fallbackHandle));
            }
        }
    }

    [Fact]
    public void DwmWindowFrameHelperAndMainWindowSync_MapsLightDarkAndAppliesImmersiveDarkModeOnHiddenToolHwnd()
    {
        Assert.Equal(20, DwmWindowFrameHelper.DwmwaUseImmersiveDarkMode);
        Assert.Equal(0, DwmWindowFrameHelper.GetImmersiveDarkModeAttributeValue(SpawnUiThemeKind.Light));
        Assert.Equal(1, DwmWindowFrameHelper.GetImmersiveDarkModeAttributeValue(SpawnUiThemeKind.Dark));
        Assert.False(DwmWindowFrameHelper.TrySyncImmersiveDarkMode(nint.Zero, SpawnUiThemeKind.Dark));

        string repoRoot = LocateRepositoryRoot();
        string mainWindowCs = File.ReadAllText(
            Path.Combine(repoRoot, "src", "Spawn.Host", "Views", "MainWindow.xaml.cs"),
            Encoding.UTF8);
        Assert.Contains("SourceInitialized += OnWindowSourceInitialized", mainWindowCs, StringComparison.Ordinal);
        Assert.Contains("nameof(SettingsViewModel.SelectedUiTheme)", mainWindowCs, StringComparison.Ordinal);
        Assert.Contains("DwmWindowFrameHelper.TrySyncImmersiveDarkMode", mainWindowCs, StringComparison.Ordinal);

        var themeManager = new ThemeManagerService();
        var observed = new List<SpawnUiThemeKind>();
        themeManager.ThemeChanged += observed.Add;
        themeManager.ApplyTheme(SpawnUiThemeKind.Dark);
        themeManager.ApplyTheme(SpawnUiThemeKind.Light);
        Assert.Equal([SpawnUiThemeKind.Dark, SpawnUiThemeKind.Light], observed);

        Exception? staException = null;
        var staThread = new Thread(() =>
        {
            try
            {
                var parameters = new HwndSourceParameters("SpawnDwmTestSink")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0,
                    ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
                    ParentWindow = nint.Zero,
                };

                using var source = new HwndSource(parameters);
                Assert.NotEqual(nint.Zero, source.Handle);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    Assert.True(DwmWindowFrameHelper.TrySyncImmersiveDarkMode(source.Handle, SpawnUiThemeKind.Dark));
                    Assert.True(DwmWindowFrameHelper.TrySyncImmersiveDarkMode(source.Handle, SpawnUiThemeKind.Light));
                }
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(60)), "STA hidden HwndSource DWM test timed out.");
        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    private static void AssertHIconDimensions(nint hIcon, int expectedWidth, int expectedHeight)
    {
        Assert.True(GetIconInfo(hIcon, out NativeIconInfo info));
        try
        {
            nint targetBmp = info.HbmColor != nint.Zero ? info.HbmColor : info.HbmMask;
            Assert.NotEqual(nint.Zero, targetBmp);
            int written = GetObjectW(targetBmp, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap bmp);
            Assert.True(written > 0);
            Assert.Equal(expectedWidth, bmp.BmWidth);
            int expectedBmpHeight = info.HbmColor != nint.Zero ? expectedHeight : expectedHeight * 2;
            Assert.Equal(expectedBmpHeight, bmp.BmHeight);
        }
        finally
        {
            if (info.HbmColor != nint.Zero)
            {
                _ = DeleteObject(info.HbmColor);
            }

            if (info.HbmMask != nint.Zero)
            {
                _ = DeleteObject(info.HbmMask);
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPythonScriptQuiet(
        string workingDirectory,
        string scriptPath,
        params string[] args)
    {
        var psi = new ProcessStartInfo("python")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-B");
        psi.ArgumentList.Add(scriptPath);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var proc = Process.Start(psi)!;
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(120_000), "generate_icons.py timed out.");
        return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string LocateCompiledExePath(string repoRoot, string projectName)
    {
        string activeConfig = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        string[] preferredConfigs = activeConfig == "Release"
            ? ["Release", "Debug"]
            : ["Debug", "Release"];

        foreach (string cfg in preferredConfigs)
        {
            string binConfigDir = Path.Combine(repoRoot, "src", projectName, "bin", cfg);
            if (!Directory.Exists(binConfigDir))
            {
                continue;
            }

            foreach (string tfmDir in Directory.GetDirectories(binConfigDir))
            {
                string exe = Path.Combine(tfmDir, $"{projectName}.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        throw new FileNotFoundException($"Compiled executable for {projectName} was not found.");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint HbmMask;
        public nint HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int BmType;
        public int BmWidth;
        public int BmHeight;
        public int BmWidthBytes;
        public ushort BmPlanes;
        public ushort BmBitsPixel;
        public nint BmBits;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint hIcon, out NativeIconInfo piconinfo);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true, SetLastError = true)]
    private static extern int GetObjectW(nint h, int c, out NativeBitmap pv);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint ho);
}
