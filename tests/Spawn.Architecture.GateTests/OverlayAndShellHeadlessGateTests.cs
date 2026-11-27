using System.Buffers.Binary;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shape = System.Windows.Shapes.Shape;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.Update;
using Spawn.Host;
using Spawn.Host.Helpers;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Spawn.Host.Views;
using Spawn.Modules.Achievement;
using Spawn.Modules.Launcher;
using Spawn.Modules.Progress;
using Spawn.Modules.Progress.Security;
using Spawn.OverlayHost.Audio;
using Spawn.OverlayHost.Services;
using Spawn.OverlayHost.Views;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Headless zero-window, zero-speaker verification of <c>Spawn.OverlayHost</c> queue coordination,
/// STA <see cref="RenderTargetBitmap"/> non-transparent pixel rendering across all 3 themes,
/// XAML <c>&lt;Popup&gt;</c> cross-tree binding hygiene, and in-memory RIFF PCM WAV synthesis.
/// </summary>
[Collection(WpfStaHeadlessCollection.Name)]
public sealed class OverlayAndShellHeadlessGateTests
{
    [Fact]
    public async Task OverlayToastQueueCoordinator_Ps5FinalCompletion_AppendsSyntheticPlatinumCelebrationCard()
    {
        var delivered = new List<OverlayToastRequest>();
        var twoDeliveredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new OverlayToastQueueCoordinator((toast, _) =>
        {
            lock (delivered)
            {
                delivered.Add(toast);
                if (delivered.Count >= 2)
                {
                    twoDeliveredTcs.TrySetResult(true);
                }
            }

            return Task.CompletedTask;
        });

        coordinator.Enqueue(new OverlayToastRequest
        {
            GameId = "limbo-id",
            GameTitle = "LIMBO",
            AchievementApiName = "ACH_13",
            DisplayName = "叮当声！",
            Description = "逃跑是没用的",
            Theme = OverlayThemeKind.Ps5Trophy,
            IsPlatinumCompletion = true,
            MuteSound = true,
            SoundVolume = 0.0,
            DurationMs = 100,
        });

        await twoDeliveredTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        lock (delivered)
        {
            Assert.Equal(2, delivered.Count);
            Assert.Equal("ACH_13", delivered[0].AchievementApiName);
            Assert.False(delivered[0].IsPlatinumCompletion);

            Assert.Equal(OverlayToastQueueCoordinator.SyntheticPlatinumApiName, delivered[1].AchievementApiName);
            Assert.True(delivered[1].IsPlatinumCompletion);
            Assert.Contains("白金奖杯", delivered[1].DisplayName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OverlayToastWindow_StaHeadlessRenderAndShowLifecycle_RendersNonTransparentPixelsAcrossAllThemes()
    {
        // 1. Static source guard: PlayToastAsync must call WPF Window.Show() and Hide() so UpdateLayeredWindow commits pixels!
        string repoRoot = LocateRepositoryRoot();
        string windowCsPath = Path.Combine(repoRoot, "src", "Spawn.OverlayHost", "Views", "OverlayToastWindow.xaml.cs");
        Assert.True(File.Exists(windowCsPath));
        string windowSource = File.ReadAllText(windowCsPath, Encoding.UTF8);
        Assert.Matches(new Regex(@"\bShow\s*\(\s*\)"), windowSource);
        Assert.Matches(new Regex(@"\bHide\s*\(\s*\)"), windowSource);

        // 2. Headless STA visual tree & RenderTargetBitmap pixel verification (zero desktop window shown!)
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

                    (OverlayThemeKind Theme, bool IsPlatinum, bool IsRare)[] variants =
                    [
                        (OverlayThemeKind.Steam, false, true),
                        (OverlayThemeKind.Ps5Trophy, false, false),
                        (OverlayThemeKind.Ps5Trophy, true, true),
                        (OverlayThemeKind.XboxRare, false, true),
                    ];

                    const int renderW = 388;
                    const int renderH = 96;

                    foreach (var (theme, isPlatinum, isRare) in variants)
                    {
                        window.PrepareForHeadlessRender(new OverlayToastRequest
                        {
                            GameId = "headless-render-check",
                            GameTitle = "LIMBO",
                            AchievementApiName = "ACH_RENDER_CHECK",
                            DisplayName = "全渠道实时跳杯测试",
                            Description = "验证分层窗口卡片非全透明像素渲染",
                            Theme = theme,
                            Position = OverlayPositionKind.BottomRight,
                            IsPlatinumCompletion = isPlatinum,
                            IsRare = isRare,
                            GlobalPercent = isRare ? 3.5 : 42.0,
                            MuteSound = true,
                            SoundVolume = 0.0,
                        });

                        FrameworkElement stage = window.VisualStage;
                        stage.Measure(new Size(renderW, renderH));
                        stage.Arrange(new Rect(0, 0, renderW, renderH));
                        stage.UpdateLayout();

                        Assert.True(stage.ActualWidth >= 320, $"[{theme}] Expected ActualWidth >= 320, got {stage.ActualWidth}");
                        Assert.True(stage.ActualHeight >= 70, $"[{theme}] Expected ActualHeight >= 70, got {stage.ActualHeight}");

                        var rtb = new RenderTargetBitmap(renderW, renderH, 96.0, 96.0, PixelFormats.Pbgra32);
                        rtb.Render(stage);

                        byte[] pixels = new byte[renderW * renderH * 4];
                        rtb.CopyPixels(pixels, renderW * 4, 0);

                        int nonTransparentPixels = 0;
                        for (int idx = 3; idx < pixels.Length; idx += 4)
                        {
                            if (pixels[idx] > 0)
                            {
                                nonTransparentPixels++;
                            }
                        }

                        Assert.True(
                            nonTransparentPixels > 1000,
                            $"[{theme}/Platinum={isPlatinum}] Expected > 1000 non-transparent pixels (Alpha > 0), actual: {nonTransparentPixels}");
                    }
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
        Assert.True(staThread.Join(TimeSpan.FromSeconds(60)), "STA headless render test timed out.");

        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    [Fact]
    public void XamlPopupDataContextAudit_AllHostAndOverlayXamlFiles_NeverUseBrokenAncestorTypeWindowInsidePopup()
    {
        string repoRoot = LocateRepositoryRoot();
        string[] xamlRoots =
        [
            Path.Combine(repoRoot, "src", "Spawn.Host"),
            Path.Combine(repoRoot, "src", "Spawn.OverlayHost"),
        ];

        var brokenAncestorPattern = new Regex(
            @"RelativeSource[\s\S]*?AncestorType\s*=\s*(?:\{\s*x:Type\s+)?Window\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        int popupCount = 0;
        var violations = new List<string>();

        foreach (string root in xamlRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string xamlFile in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            {
                string norm = xamlFile.Replace('\\', '/');
                if (norm.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    || norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(repoRoot, xamlFile).Replace('\\', '/');
                XDocument doc = XDocument.Load(xamlFile, LoadOptions.SetLineInfo);

                foreach (XElement popup in doc.Descendants().Where(e => e.Name.LocalName is "Popup" or "ContextMenu"))
                {
                    popupCount++;
                    string? dataContextAttr = popup.Attribute("DataContext")?.Value;
                    if (string.IsNullOrWhiteSpace(dataContextAttr)
                        || !dataContextAttr.Contains("PlacementTarget.Tag", StringComparison.Ordinal))
                    {
                        int line = (popup as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;
                        violations.Add(
                            $"{relPath}:{line} <{popup.Name.LocalName}> must bridge DataContext via PlacementTarget.Tag (actual DataContext=\"{dataContextAttr ?? "<missing>"}\").");
                    }

                    foreach (XElement node in popup.DescendantsAndSelf())
                    {
                        foreach (XAttribute attr in node.Attributes())
                        {
                            if (brokenAncestorPattern.IsMatch(attr.Value))
                            {
                                int line = (attr as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;
                                violations.Add(
                                    $"{relPath}:{line} <{popup.Name.LocalName}> subtree uses broken AncestorType=Window binding ({node.Name.LocalName}.{attr.Name.LocalName}=\"{attr.Value}\"). Use PlacementTarget.Tag or x:Reference instead.");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(popupCount >= 4, $"Expected at least 4 <Popup>/<ContextMenu> elements in MainWindow.xaml (RowMore, GameHeaderMore, TrainerMore, ManagedTrainerRowMore), found {popupCount}.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void PackUriAssemblyNeutralityAudit_AllHostAndOverlaySources_NeverHardcodeAssemblyComponentPackUri()
    {
        // Packaging compiles Spawn.Host with -p:SpawnHostAssemblyName=spawn / spawn-dev.
        // Any hardcoded "/Spawn.Host;component/...", "/Spawn.OverlayHost;component/...", or
        // "pack://application:,,,/Spawn...." URI passes under dotnet test but throws FileNotFoundException
        // inside ResourceDictionary.set_Source on startup of installed spawn-dev.exe / spawn.exe!
        string repoRoot = LocateRepositoryRoot();
        string[] sourceRoots =
        [
            Path.Combine(repoRoot, "src", "Spawn.Host"),
            Path.Combine(repoRoot, "src", "Spawn.OverlayHost"),
        ];

        string[] forbiddenTokens =
        [
            "Spawn.Host;component",
            "Spawn.OverlayHost;component",
            "pack://application:,,,/Spawn.",
        ];

        int scannedXamlCount = 0;
        int scannedCsCount = 0;
        var violations = new List<string>();

        foreach (string root in sourceRoots)
        {
            Assert.True(Directory.Exists(root), $"Expected source root to exist: {root}");

            foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string norm = file.Replace('\\', '/');
                if (norm.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    || norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string ext = Path.GetExtension(file);
                bool isXaml = string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase);
                bool isCs = string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase);
                if (!isXaml && !isCs)
                {
                    continue;
                }

                if (isXaml)
                {
                    scannedXamlCount++;
                }
                else
                {
                    scannedCsCount++;
                }

                string relPath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    foreach (string token in forbiddenTokens)
                    {
                        if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add(
                                $"{relPath}:{i + 1} contains hardcoded assembly pack URI token '{token}'. Use an assembly-neutral relative URI in XAML (e.g. '../../Themes/Controls.xaml') or typeof(...).Assembly.GetName().Name in C#.");
                        }
                    }
                }

                if (isXaml)
                {
                    XDocument doc = XDocument.Load(file, LoadOptions.SetLineInfo);
                    foreach (XElement rd in doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary"))
                    {
                        string? source = rd.Attribute("Source")?.Value;
                        if (string.IsNullOrWhiteSpace(source))
                        {
                            continue;
                        }

                        if (source.Contains(";component", StringComparison.OrdinalIgnoreCase)
                            || source.StartsWith("/Spawn.", StringComparison.OrdinalIgnoreCase))
                        {
                            int lineNo = (rd as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;
                            violations.Add(
                                $"{relPath}:{lineNo} <ResourceDictionary Source=\"{source}\"> must not hardcode an assembly name; use an assembly-neutral relative URI instead.");
                        }
                    }
                }
            }
        }

        Assert.True(scannedXamlCount >= 6, $"Expected at least 6 XAML files under Spawn.Host / Spawn.OverlayHost, scanned {scannedXamlCount}.");
        Assert.True(scannedCsCount >= 20, $"Expected at least 20 C# files under Spawn.Host / Spawn.OverlayHost, scanned {scannedCsCount}.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DisabledButtonXamlHygieneAudit_HostXamlFiles_NeverSetLocalDangerOrChromaticBrushOnButton()
    {
        string repoRoot = LocateRepositoryRoot();
        string hostRoot = Path.Combine(repoRoot, "src", "Spawn.Host");
        Assert.True(Directory.Exists(hostRoot));

        string[] forbiddenBrushTokens =
        [
            "Danger",
            "Accent",
            "Warning",
            "RunningGreen",
            "#DC2626",
            "#F87171",
            "#EF4444",
        ];

        int scannedButtons = 0;
        var violations = new List<string>();

        foreach (string xamlFile in Directory.EnumerateFiles(hostRoot, "*.xaml", SearchOption.AllDirectories))
        {
            string norm = xamlFile.Replace('\\', '/');
            if (norm.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relPath = Path.GetRelativePath(repoRoot, xamlFile).Replace('\\', '/');
            XDocument doc = XDocument.Load(xamlFile, LoadOptions.SetLineInfo);

            foreach (XElement button in doc.Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                scannedButtons++;
                int line = (button as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;

                foreach (string attrName in new[] { "Foreground", "Background", "BorderBrush" })
                {
                    string? val = button.Attribute(attrName)?.Value;
                    if (string.IsNullOrWhiteSpace(val))
                    {
                        continue;
                    }

                    if (forbiddenBrushTokens.Any(tok => val.Contains(tok, StringComparison.OrdinalIgnoreCase)))
                    {
                        violations.Add(
                            $"{relPath}:{line} <Button {attrName}=\"{val}\"> sets a local chromatic/danger brush that overrides IsEnabled=\"False\" Style triggers. Use a dedicated Style in Themes/Controls.xaml instead.");
                    }
                }

                // Ensure nested <Path> glyphs inside styled Buttons do not hardcode DangerTextBrush (must bind to Button.Foreground).
                if (button.Attribute("Style") is not null)
                {
                    foreach (XElement path in button.Descendants().Where(e => e.Name.LocalName == "Path"))
                    {
                        foreach (string brushAttr in new[] { "Stroke", "Fill" })
                        {
                            string? val = path.Attribute(brushAttr)?.Value;
                            if (!string.IsNullOrWhiteSpace(val)
                                && val.Contains("Danger", StringComparison.OrdinalIgnoreCase))
                            {
                                int pathLine = (path as System.Xml.IXmlLineInfo)?.LineNumber ?? line;
                                violations.Add(
                                    $"{relPath}:{pathLine} <Path {brushAttr}=\"{val}\"> inside styled <Button> hardcodes a Danger brush instead of binding to Button.Foreground.");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(scannedButtons >= 40, $"Expected at least 40 <Button> elements under src/Spawn.Host, scanned {scannedButtons}.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("LightTheme.xaml")]
    [InlineData("DarkTheme.xaml")]
    public void DisabledButtonStyles_LightAndDarkThemes_GrayOutDangerPrimarySecondaryAndSwitchControls(string themeFile)
    {
        Exception? staException = null;
        var staThread = new Thread(() =>
        {
            try
            {
                RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);

                string hostAsm = typeof(MainWindow).Assembly.GetName().Name ?? "Spawn.Host";
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{hostAsm};component/Themes/{themeFile}", UriKind.Absolute),
                });
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{hostAsm};component/Themes/Controls.xaml", UriKind.Absolute),
                });

                Color expectedDisabledText = ((SolidColorBrush)resources["DisabledTextBrush"]).Color;
                Color expectedDisabledSurface = ((SolidColorBrush)resources["DisabledSurfaceBrush"]).Color;
                Color expectedDisabledBorder = ((SolidColorBrush)resources["DisabledBorderBrush"]).Color;
                AssertNeutralGray(expectedDisabledText, $"{themeFile} DisabledTextBrush");
                AssertNeutralGray(expectedDisabledSurface, $"{themeFile} DisabledSurfaceBrush");
                AssertNeutralGray(expectedDisabledBorder, $"{themeFile} DisabledBorderBrush");

                var host = new StackPanel
                {
                    Width = 320,
                    Resources = resources,
                };
                host.SetResourceReference(Panel.BackgroundProperty, "FloatingMenuBackgroundBrush");

                // 1. DangerMenuItemButtonStyle (e.g., "切回主分支并删除当前分支" / "删除此快照")
                var dangerMenuBtn = new Button
                {
                    Style = (Style)resources["DangerMenuItemButtonStyle"],
                    Content = "切回主分支并删除当前分支",
                    Width = 220,
                    IsEnabled = true,
                };
                host.Children.Add(dangerMenuBtn);

                // 2. DangerSecondaryCapsuleButtonStyle (e.g., cloud endpoint "删除")
                var dangerSecondaryBtn = new Button
                {
                    Style = (Style)resources["DangerSecondaryCapsuleButtonStyle"],
                    Content = "删除",
                    Width = 100,
                    IsEnabled = true,
                };
                host.Children.Add(dangerSecondaryBtn);

                // 3. DangerSubtleIconButtonStyle with bound <Path Stroke>
                var dangerIconPath = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                    StrokeThickness = 1.5,
                };
                dangerIconPath.SetBinding(Shape.StrokeProperty, new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
                });
                var dangerIconBtn = new Button
                {
                    Style = (Style)resources["DangerSubtleIconButtonStyle"],
                    Content = dangerIconPath,
                    IsEnabled = true,
                };
                host.Children.Add(dangerIconBtn);

                // 4. Primary / Running / Warning / Secondary / Floating / Subtle / Link buttons
                var primaryBtn = new Button
                {
                    Style = (Style)resources["PrimaryCapsuleButtonStyle"],
                    Content = "创建快照",
                    Width = 140,
                    IsEnabled = true,
                };
                var runningPrimaryBtn = new Button
                {
                    Style = (Style)resources["RunningPrimaryCapsuleButtonStyle"],
                    Content = "停止修改器",
                    Width = 140,
                    IsEnabled = true,
                };
                var warningPrimaryBtn = new Button
                {
                    Style = (Style)resources["WarningPrimaryCapsuleButtonStyle"],
                    Content = "忽略 Mod 差异强制回滚",
                    Width = 180,
                    IsEnabled = true,
                };
                var secondaryBtn = new Button
                {
                    Style = (Style)resources["SecondaryCapsuleButtonStyle"],
                    Content = "新建分叉",
                    Width = 120,
                    IsEnabled = true,
                };
                var floatingMenuBtn = new Button
                {
                    Style = (Style)resources["FloatingMenuItemButtonStyle"],
                    Content = "重命名 / 修改分支颜色",
                    Width = 220,
                    IsEnabled = true,
                };
                var linkBtn = new Button
                {
                    Style = (Style)resources["AccentLinkButtonStyle"],
                    Content = "↳ 跳转至父节点快照",
                    Width = 180,
                    IsEnabled = true,
                };
                var switchBox = new CheckBox
                {
                    Style = (Style)resources["ModernSwitchCheckBoxStyle"],
                    Content = "分叉后立即切换并检出",
                    IsChecked = true,
                    IsEnabled = true,
                };

                host.Children.Add(primaryBtn);
                host.Children.Add(runningPrimaryBtn);
                host.Children.Add(warningPrimaryBtn);
                host.Children.Add(secondaryBtn);
                host.Children.Add(floatingMenuBtn);
                host.Children.Add(linkBtn);
                host.Children.Add(switchBox);

                LayoutElement(host, 320, 480);

                // Verify ENABLED states: danger buttons are distinctly red, primary buttons are chromatic
                AssertRedDominant(GetSolidColor(dangerMenuBtn.Foreground), $"{themeFile} DangerMenuItem enabled foreground");
                AssertRedDominant(GetSolidColor(dangerSecondaryBtn.Foreground), $"{themeFile} DangerSecondary enabled foreground");
                AssertRedDominant(GetSolidColor(dangerIconBtn.Foreground), $"{themeFile} DangerSubtleIcon enabled foreground");
                AssertRedDominant(GetSolidColor(dangerIconPath.Stroke), $"{themeFile} DangerSubtleIcon Path enabled stroke");
                AssertChromatic(GetSolidColor(GetTemplateBorder(primaryBtn).Background), $"{themeFile} Primary enabled background");
                AssertChromatic(GetSolidColor(GetTemplateBorder(runningPrimaryBtn).Background), $"{themeFile} RunningPrimary enabled background");
                AssertChromatic(GetSolidColor(GetTemplateBorder(warningPrimaryBtn).Background), $"{themeFile} WarningPrimary enabled background");
                AssertChromatic(GetSolidColor(linkBtn.Foreground), $"{themeFile} AccentLink enabled foreground");

                int enabledRedPixels = CountRedDominantPixels(dangerMenuBtn, 220, 34);
                Assert.True(enabledRedPixels > 20, $"[{themeFile}] Expected enabled DangerMenuItem to render > 20 red pixels, got {enabledRedPixels}.");

                // Switch ALL controls to DISABLED
                dangerMenuBtn.IsEnabled = false;
                dangerSecondaryBtn.IsEnabled = false;
                dangerIconBtn.IsEnabled = false;
                primaryBtn.IsEnabled = false;
                runningPrimaryBtn.IsEnabled = false;
                warningPrimaryBtn.IsEnabled = false;
                secondaryBtn.IsEnabled = false;
                floatingMenuBtn.IsEnabled = false;
                linkBtn.IsEnabled = false;
                switchBox.IsEnabled = false;

                LayoutElement(host, 320, 480);

                // Danger buttons MUST become neutral muted gray (ZERO red bleed-through!)
                foreach (var (name, btn) in new (string, Button)[]
                {
                    ("DangerMenuItem", dangerMenuBtn),
                    ("DangerSecondary", dangerSecondaryBtn),
                    ("DangerSubtleIcon", dangerIconBtn),
                    ("PrimaryCapsule", primaryBtn),
                    ("RunningPrimaryCapsule", runningPrimaryBtn),
                    ("WarningPrimaryCapsule", warningPrimaryBtn),
                    ("SecondaryCapsule", secondaryBtn),
                    ("FloatingMenuItem", floatingMenuBtn),
                    ("AccentLink", linkBtn),
                })
                {
                    Color fg = GetSolidColor(btn.Foreground);
                    Assert.Equal(expectedDisabledText, fg);
                    AssertNeutralGray(fg, $"{themeFile} {name} disabled Foreground");

                    Border bd = GetTemplateBorder(btn);
                    Color inheritedFg = GetSolidColor(TextElement.GetForeground(bd));
                    Assert.Equal(expectedDisabledText, inheritedFg);
                    AssertNeutralGray(inheritedFg, $"{themeFile} {name} disabled Bd TextElement.Foreground");
                }

                // Bound Path inside disabled danger icon button MUST also turn neutral gray
                Color disabledPathStroke = GetSolidColor(dangerIconPath.Stroke);
                Assert.Equal(expectedDisabledText, disabledPathStroke);
                AssertNeutralGray(disabledPathStroke, $"{themeFile} DangerSubtleIcon Path disabled stroke");

                // Filled/outlined capsule buttons MUST switch background & border to neutral disabled gray
                foreach (var (name, btn) in new (string, Button)[]
                {
                    ("PrimaryCapsule", primaryBtn),
                    ("RunningPrimaryCapsule", runningPrimaryBtn),
                    ("WarningPrimaryCapsule", warningPrimaryBtn),
                    ("SecondaryCapsule", secondaryBtn),
                    ("DangerSecondaryCapsule", dangerSecondaryBtn),
                })
                {
                    Border bd = GetTemplateBorder(btn);
                    Color bg = GetSolidColor(bd.Background);
                    Color border = GetSolidColor(bd.BorderBrush);
                    Assert.Equal(expectedDisabledSurface, bg);
                    Assert.Equal(expectedDisabledBorder, border);
                    AssertNeutralGray(bg, $"{themeFile} {name} disabled Background");
                    AssertNeutralGray(border, $"{themeFile} {name} disabled BorderBrush");
                }

                // Rendered pixels of disabled DangerMenuItem MUST have ZERO red-dominant pixels!
                int disabledRedPixels = CountRedDominantPixels(dangerMenuBtn, 220, 34);
                Assert.Equal(0, disabledRedPixels);

                // Disabled switch track & label MUST turn neutral gray even when IsChecked == true
                Assert.Equal(expectedDisabledText, GetSolidColor(switchBox.Foreground));
                var track = (Border)switchBox.Template.FindName("Track", switchBox);
                Assert.Equal(expectedDisabledSurface, GetSolidColor(track.Background));
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
        Assert.True(staThread.Join(TimeSpan.FromSeconds(60)), $"STA disabled button test timed out for {themeFile}.");

        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    private static void LayoutElement(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static Border GetTemplateBorder(Button button)
    {
        button.ApplyTemplate();
        var bd = button.Template.FindName("Bd", button) as Border;
        Assert.NotNull(bd);
        return bd!;
    }

    private static Color GetSolidColor(Brush? brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        return solid.Color;
    }

    private static void AssertNeutralGray(Color color, string label)
    {
        int chroma = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        Assert.True(
            chroma <= 16,
            $"Expected neutral gray (chroma <= 16) for {label}, got #{color.R:X2}{color.G:X2}{color.B:X2} (chroma={chroma}).");
    }

    private static void AssertRedDominant(Color color, string label)
    {
        Assert.True(
            color.R > color.G + 40 && color.R > color.B + 40,
            $"Expected red-dominant color for {label}, got #{color.R:X2}{color.G:X2}{color.B:X2}.");
    }

    private static void AssertChromatic(Color color, string label)
    {
        int chroma = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        Assert.True(
            chroma > 40,
            $"Expected chromatic color (chroma > 40) for {label}, got #{color.R:X2}{color.G:X2}{color.B:X2} (chroma={chroma}).");
    }

    private static int CountRedDominantPixels(FrameworkElement element, int width, int height)
    {
        var rtb = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(element);
        byte[] pixels = new byte[width * height * 4];
        rtb.CopyPixels(pixels, width * 4, 0);

        int redPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];
            if (a > 20 && r > g + 25 && r > b + 25)
            {
                redPixels++;
            }
        }

        return redPixels;
    }

    [Fact]
    public async Task MainWindow_HeadlessStaInitializeComponent_SucceedsWithoutShowingWindow()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-mainwindow-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();

            var services = new ServiceCollection();
            App.RegisterHostCoreAndModuleServices(services, paths);
            services.AddSingleton<TrayIconService>();

            await using ServiceProvider sp = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            await sp.GetRequiredService<SpawnDatabase>().InitializeAsync();

            using MainWindowViewModel mainVm = sp.GetRequiredService<MainWindowViewModel>();
            using TrayIconService trayService = sp.GetRequiredService<TrayIconService>();

            Exception? staException = null;
            var staThread = new Thread(() =>
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);

                    var window = new MainWindow(mainVm, trayService, static () => { });
                    try
                    {
                        Assert.False(window.IsVisible);
                        Assert.NotNull(window.FindName("WindowOuterBorder"));
                        Assert.NotNull(window.FindName("SaveSidebarSearchTextBox"));
                        Assert.NotNull(window.FindName("TrainerSidebarSearchTextBox"));
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
            Assert.True(staThread.Join(TimeSpan.FromSeconds(60)), "STA headless MainWindow InitializeComponent test timed out.");

            if (staException is not null)
            {
                ExceptionDispatchInfo.Capture(staException).Throw();
            }
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Theory]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.SteamCrystal)]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.Ps5Trophy)]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.RareDiamond)]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.PlatinumCelebration)]
    public void SynthesizedUnlockSoundPlayer_InMemoryWavSynthesisAndSilentGuard_ProducesValidRiffPcmWithoutAudioOutput(
        SynthesizedUnlockSoundPlayer.ChimePreset preset)
    {
        byte[] wav = SynthesizedUnlockSoundPlayer.SynthesizeWavBytes(preset, masterVolume: 0.75f);
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        short audioFormat = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20, 2));
        short channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4));
        Assert.Equal(1, audioFormat);
        Assert.Equal(2, channels);
        Assert.Equal(48000, sampleRate);

        // Verify forceSilent=true never touches audio hardware
        using var silentPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: true);
        silentPlayer.PlayForToast(new OverlayToastRequest
        {
            GameTitle = "LIMBO",
            AchievementApiName = "ACH_1",
            DisplayName = "方向错误",
            SoundPreset = OverlaySoundPreset.SteamAuthentic,
            MuteSound = true,
            SoundVolume = 0.0,
        });
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_RiffWav44100Hz16BitStereoResampleAndScaleTo48000HzIeeeFloat_PreservesFrameCountAmplitudeAndStereoSeparationInMemory()
    {
        // 1. Construct a standard 44,100 Hz, 16-bit, Stereo RIFF PCM WAV in memory (0.20s = 8,820 frames; L=440Hz@0.80, R=880Hz@0.40)
        const int srcRate = 44100;
        const int srcFrames = 8820;
        const int dstRate = 48000;
        byte[] wav44k = BuildSyntheticStereoPcm16WavWithJunkChunk(srcRate, srcFrames, leftAmp: 0.80, rightAmp: 0.40);

        Assert.True(SynthesizedUnlockSoundPlayer.TryDecodeRiffWav(wav44k, out var decoded));
        Assert.Equal(srcRate, decoded.SampleRate);
        Assert.Equal(srcFrames, decoded.FrameCount);
        Assert.Equal(srcFrames * 2, decoded.InterleavedStereoSamples.Length);

        // 2. Resample 44,100 Hz -> 48,000 Hz IEEE Float with 0.50 volume scale (expected: 9,600 stereo frames = 19,200 floats)
        float[] resampled48k = SynthesizedUnlockSoundPlayer.ResampleAndScaleWavToIeeeFloat(
            wav44k,
            volumeScale: 0.50f,
            targetSampleRate: dstRate,
            targetChannels: 2);

        int expectedTargetFrames = (int)Math.Round((double)srcFrames * dstRate / srcRate);
        Assert.Equal(9600, expectedTargetFrames);
        Assert.Equal(expectedTargetFrames * 2, resampled48k.Length);

        float peakLeft = 0f;
        float peakRight = 0f;
        int nonSilentCount = 0;

        for (int frame = 0; frame < expectedTargetFrames; frame++)
        {
            float l = resampled48k[frame * 2];
            float r = resampled48k[frame * 2 + 1];
            Assert.False(float.IsNaN(l) || float.IsInfinity(l));
            Assert.False(float.IsNaN(r) || float.IsInfinity(r));

            float absL = Math.Abs(l);
            float absR = Math.Abs(r);
            if (absL > peakLeft)
            {
                peakLeft = absL;
            }

            if (absR > peakRight)
            {
                peakRight = absR;
            }

            if (absL > 0.001f || absR > 0.001f)
            {
                nonSilentCount++;
            }
        }

        // Original L=0.80 * 0.50 gain -> ~0.40; Original R=0.40 * 0.50 gain -> ~0.20
        Assert.InRange(peakLeft, 0.385f, 0.415f);
        Assert.InRange(peakRight, 0.188f, 0.212f);
        Assert.True(nonSilentCount > 8500, $"Expected non-silent resampled frames > 8500, actual: {nonSilentCount}");

        // Zero gain produces pure silence of exact resampled length
        float[] muted48k = SynthesizedUnlockSoundPlayer.ResampleStereoFrames(decoded, volumeScale: 0.0f, targetSampleRate: dstRate);
        Assert.Equal(expectedTargetFrames * 2, muted48k.Length);
        Assert.All(muted48k, s => Assert.Equal(0f, s));

        // 3. Verify 16-bit PCM fallback scaler (`PlaySoundW` memory buffer) preserves RIFF header and scaled amplitude
        byte[] scaledPcm16 = SynthesizedUnlockSoundPlayer.ScaleDecodedWavToPcm16Bytes(decoded, volumeScale: 0.50f);
        Assert.True(SynthesizedUnlockSoundPlayer.TryDecodeRiffWav(scaledPcm16, out var reDecoded));
        Assert.Equal(srcRate, reDecoded.SampleRate);
        Assert.Equal(srcFrames, reDecoded.FrameCount);
        Assert.InRange(reDecoded.InterleavedStereoSamples.Max(Math.Abs), 0.39f, 0.41f);

        // 4. Read-only in-memory verification against local Steam client WAV if installed on this machine (0 audio output!)
        var probe = Spawn.Core.SteamClientSoundLocator.Probe();
        if (!string.IsNullOrWhiteSpace(probe.EffectiveSoundPath) && File.Exists(probe.EffectiveSoundPath))
        {
            byte[] realSteamWav = File.ReadAllBytes(probe.EffectiveSoundPath);
            Assert.True(SynthesizedUnlockSoundPlayer.TryDecodeRiffWav(realSteamWav, out var realDecoded));
            Assert.True(realDecoded.FrameCount > 1000);
            float[] realResampled = SynthesizedUnlockSoundPlayer.ResampleStereoFrames(realDecoded, volumeScale: 0.65f, targetSampleRate: 48000);
            Assert.True(realResampled.Length > 2000);
            Assert.Contains(realResampled, s => Math.Abs(s) > 0.01f);
        }
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_SteamAuthenticClip_PlaysAtOriginalSteamLevelWithFullDefaultVolume()
    {
        // Steam authentic toast is never peak-boosted; user custom WAV keeps the gentle boost.
        Assert.False(SynthesizedUnlockSoundPlayer.ShouldNormalizeExternalClip(OverlaySoundPreset.SteamAuthentic));
        Assert.True(SynthesizedUnlockSoundPlayer.ShouldNormalizeExternalClip(OverlaySoundPreset.CustomWav));

        // Default volume is 100%, i.e. identical to the Steam client's own playback level.
        Assert.Equal(1.0, new OverlaySettingsDto().SoundVolume);
        Assert.Equal(1.0, new OverlayToastRequest { GameTitle = "g", AchievementApiName = "a", DisplayName = "d" }.SoundVolume);

        // At 100% the un-normalized clip keeps its source peak (Steam desktop_toast_default.wav is ~-14 dBFS).
        const float desktopToastPeak = 0.199f;
        var clip = BuildDecodedStereoSine(44100, 8820, desktopToastPeak, desktopToastPeak);
        float[] rendered = SynthesizedUnlockSoundPlayer.ResampleStereoFrames(clip, volumeScale: 1.0f, targetSampleRate: 44100);
        Assert.InRange(rendered.Max(Math.Abs), desktopToastPeak - 0.005f, desktopToastPeak + 0.005f);
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_GentlePeakNormalizationAndGuardSilencePadding_BoostsMinus10DbfsClipAndWrapsLeadInTailZerosInMemory()
    {
        const int srcRate = 44100;
        const int srcFrames = 8820; // 0.20s
        const int dstRate = 48000;
        const float steamLikePeak = 0.313f; // ~ -10 dBFS (Steam deck_ui_achievement_toast.wav raw peak)

        var quietClip = BuildDecodedStereoSine(srcRate, srcFrames, steamLikePeak, steamLikePeak);

        // 1. NormalizeGentlePeak: peak inside [0.05, 0.65] is boosted linearly to 0.85 without clipping, frame count preserved
        var boosted = SynthesizedUnlockSoundPlayer.NormalizeGentlePeak(quietClip);
        Assert.Equal(srcRate, boosted.SampleRate);
        Assert.Equal(srcFrames, boosted.FrameCount);
        float boostedPeak = boosted.InterleavedStereoSamples.Max(MathF.Abs);
        Assert.InRange(boostedPeak, 0.845f, 0.851f);
        Assert.All(boosted.InterleavedStereoSamples, s => Assert.InRange(s, -1f, 1f));

        // Ratio between boosted and original samples is uniform (pure linear gain, no waveshaping)
        float expectedGain = boostedPeak / quietClip.InterleavedStereoSamples.Max(MathF.Abs);
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(quietClip.InterleavedStereoSamples[i] * expectedGain, boosted.InterleavedStereoSamples[i], 4);
        }

        // Too-quiet (< 0.05) and already-loud (> 0.65) clips must stay untouched
        var nearSilentClip = BuildDecodedStereoSine(srcRate, 2000, 0.02f, 0.02f);
        var loudClip = BuildDecodedStereoSine(srcRate, 2000, 0.90f, 0.90f);
        Assert.Equal(
            nearSilentClip.InterleavedStereoSamples.Max(MathF.Abs),
            SynthesizedUnlockSoundPlayer.NormalizeGentlePeak(nearSilentClip).InterleavedStereoSamples.Max(MathF.Abs));
        Assert.Equal(
            loudClip.InterleavedStereoSamples.Max(MathF.Abs),
            SynthesizedUnlockSoundPlayer.NormalizeGentlePeak(loudClip).InterleavedStereoSamples.Max(MathF.Abs));

        // 2. PadWithGuardSilence: 45ms lead-in + 160ms tail of exact zero frames, payload copied byte-for-byte in the middle
        float[] payload = [0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f];
        float[] padded = SynthesizedUnlockSoundPlayer.PadWithGuardSilence(payload, dstRate, channels: 2);
        int leadSamples = (int)Math.Round(dstRate * 0.045) * 2; // 2160 frames * 2 = 4320
        int tailSamples = (int)Math.Round(dstRate * 0.160) * 2; // 7680 frames * 2 = 15360
        Assert.Equal(4320, leadSamples);
        Assert.Equal(15360, tailSamples);
        Assert.Equal(leadSamples + payload.Length + tailSamples, padded.Length);
        Assert.All(padded.AsSpan(0, leadSamples).ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(payload, padded.AsSpan(leadSamples, payload.Length).ToArray());
        Assert.All(padded.AsSpan(leadSamples + payload.Length).ToArray(), s => Assert.Equal(0f, s));

        // Zero padding requested returns an identical copy
        Assert.Equal(payload, SynthesizedUnlockSoundPlayer.PadWithGuardSilence(payload, dstRate, 2, leadInSilenceMs: 0, tailSilenceMs: 0));

        // 3. Full PrepareExternalClipForWasapiPlayback pipeline: normalize -> resample 44.1k->48k -> x0.50 volume -> guard silence
        float[] prepared = SynthesizedUnlockSoundPlayer.PrepareExternalClipForWasapiPlayback(
            quietClip,
            volumeScale: 0.50f,
            targetSampleRate: dstRate,
            targetChannels: 2);

        int resampledSamples = 9600 * 2;
        Assert.Equal(leadSamples + resampledSamples + tailSamples, prepared.Length);
        Assert.All(prepared.AsSpan(0, leadSamples).ToArray(), s => Assert.Equal(0f, s));
        Assert.All(prepared.AsSpan(leadSamples + resampledSamples).ToArray(), s => Assert.Equal(0f, s));

        float[] body = prepared.AsSpan(leadSamples, resampledSamples).ToArray();
        Assert.All(body, s => Assert.False(float.IsNaN(s) || float.IsInfinity(s)));
        float bodyPeak = body.Max(MathF.Abs);
        // 0.85 normalized target x 0.50 user volume ~= 0.425 (Catmull-Rom tolerance)
        Assert.InRange(bodyPeak, 0.41f, 0.44f);
        Assert.True(body.Count(s => MathF.Abs(s) > 0.001f) > 15000);

        // Normalization disabled keeps original quiet level (0.313 x 0.50 ~= 0.157)
        float[] unnormalized = SynthesizedUnlockSoundPlayer.PrepareExternalClipForWasapiPlayback(
            quietClip,
            volumeScale: 0.50f,
            targetSampleRate: dstRate,
            targetChannels: 2,
            normalizeGentlePeak: false);
        Assert.InRange(unnormalized.AsSpan(leadSamples, resampledSamples).ToArray().Max(MathF.Abs), 0.150f, 0.165f);
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_SmartEndpointClassification_BypassesSpeakerlessGpuDisplayAndPrefersGameHeadset()
    {
        const string gpuMonitor = "XG27UCS (NVIDIA High Definition Audio)";
        const string gameHeadset = "耳机 (INZONE H9 / INZONE H7 - Game)";
        const string chatHeadset = "耳机 (INZONE H9 / INZONE H7 - Chat)";
        const string realtekSpeakers = "扬声器 (Realtek(R) Audio)";
        const string spdifOut = "数字输出 (Realtek(R) Audio)";

        // 1. GPU display audio channels are detected (NVIDIA / AMD / Intel)
        Assert.True(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint(gpuMonitor));
        Assert.True(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint("DELL U2723QE (AMD High Definition Audio Device)"));
        Assert.True(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint("LG HDR 4K (Intel(R) Display Audio)"));
        Assert.False(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint(gameHeadset));
        Assert.False(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint(realtekSpeakers));

        // 2. Narrowband chat sub-endpoints and S/PDIF optical outputs are excluded from auto selection
        Assert.True(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint(chatHeadset));
        Assert.True(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint(spdifOut));
        Assert.True(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint("Headset (Hands-Free AG Audio)"));
        Assert.False(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint(gameHeadset));

        // 3. Scoring ranks game headset > speakers > generic, and disqualifies GPU / chat / optical endpoints
        int headsetScore = SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(gameHeadset);
        int speakerScore = SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(realtekSpeakers);
        int genericScore = SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint("Virtual Audio Cable");

        Assert.True(headsetScore > speakerScore, $"Game headset ({headsetScore}) must outrank speakers ({speakerScore}).");
        Assert.True(speakerScore > genericScore, $"Speakers ({speakerScore}) must outrank generic ({genericScore}).");
        Assert.True(genericScore >= 0);
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(gpuMonitor));
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(chatHeadset));
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(spdifOut));
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(null));

        // 4. Simulated reproduction of the user's machine: default = speakerless NVIDIA monitor -> best candidate is the Game headset
        string[] activeEndpoints = [gpuMonitor, chatHeadset, spdifOut, gameHeadset, realtekSpeakers];
        string best = activeEndpoints
            .Select(name => (Name: name, Score: SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(name)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .First().Name;
        Assert.Equal(gameHeadset, best);
    }

    [Fact]
    public async Task WasapiExtensibleMixFormatWakePrimerAndSystemDefaultRouting_PreservesChannelMaskZeroCrossingPrimerAndDefaultMode()
    {
        // 1. Is32BitIeeeFloatWaveFormat recognizes both plain 0x0003 IeeeFloat and 0xFFFE WaveFormatExtensible (KSDATAFORMAT_SUBTYPE_IEEE_FLOAT),
        //    while rejecting 16-bit / 32-bit integer PCM WaveFormat / WaveFormatExtensible.
        var plainFloat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var extensibleFloat = new WaveFormatExtensible(48000, 32, 2);
        var pcm16 = new WaveFormat(48000, 16, 2);
        var pcm24Extensible = new WaveFormatExtensible(48000, 24, 2);

        Assert.True(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(plainFloat));
        Assert.True(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(extensibleFloat));
        Assert.False(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(pcm16));
        Assert.False(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(pcm24Extensible));

        // 2. ExactMixFormatFloatWaveProvider preserves exact WaveFormatExtensible instance reference (retaining 0xFFFE + dwChannelMask)
        //    and reads strictly in BlockAlign (8-byte stereo float frame) multiples until EOF returns 0.
        float[] sourceSamples = [0.125f, -0.125f, 0.25f, -0.25f, 0.5f, -0.5f]; // 3 stereo frames = 24 bytes
        var provider = new SynthesizedUnlockSoundPlayer.ExactMixFormatFloatWaveProvider(extensibleFloat, sourceSamples);
        Assert.Same(extensibleFloat, provider.WaveFormat);
        Assert.Equal(WaveFormatEncoding.Extensible, provider.WaveFormat.Encoding);
        Assert.Equal(8, provider.WaveFormat.BlockAlign);

        byte[] readAccum = new byte[24];
        // Request unaligned 11 bytes -> must return exact BlockAlign multiple (8 bytes = 1 stereo frame)
        int n1 = provider.Read(readAccum, offset: 0, count: 11);
        Assert.Equal(8, n1);
        // Request remaining -> returns remaining 16 bytes
        int n2 = provider.Read(readAccum, offset: 8, count: 64);
        Assert.Equal(16, n2);
        // Subsequent read at EOF -> returns 0
        int n3 = provider.Read(readAccum, offset: 0, count: 16);
        Assert.Equal(0, n3);

        float[] roundTripped = MemoryMarshal.Cast<byte, float>(readAccum.AsSpan()).ToArray();
        Assert.Equal(sourceSamples, roundTripped);

        // 3. PrependSubAudibleWakePrimerAndTailSilence generates 160ms sub-audible Hann-windowed 35Hz zero-crossing wake primer
        //    (0 < maxAbs <= 0.001f, smooth start/end without DC jump), preserves exact middle payload, and appends 240ms zero tail.
        const int rate = 48000;
        const int channels = 2;
        float[] bodyPayload = [0.42f, -0.42f, 0.84f, -0.84f, 0.21f, -0.21f, -0.63f, 0.63f];
        float[] wakePrimed = SynthesizedUnlockSoundPlayer.PrependSubAudibleWakePrimerAndTailSilence(
            bodyPayload,
            sampleRate: rate,
            channels: channels,
            wakePrimerMs: 160,
            tailSilenceMs: 240,
            primerAmplitude: 0.00045f);

        int primerSamples = (int)Math.Round(rate * 0.160) * channels; // 7680 * 2 = 15360
        int tailSamples = (int)Math.Round(rate * 0.240) * channels;   // 11520 * 2 = 23040
        Assert.Equal(15360, primerSamples);
        Assert.Equal(23040, tailSamples);
        Assert.Equal(primerSamples + bodyPayload.Length + tailSamples, wakePrimed.Length);

        ReadOnlySpan<float> primerSpan = wakePrimed.AsSpan(0, primerSamples);
        float maxPrimerAbs = 0f;
        for (int i = 0; i < primerSpan.Length; i++)
        {
            float a = MathF.Abs(primerSpan[i]);
            if (a > maxPrimerAbs)
            {
                maxPrimerAbs = a;
            }
        }

        Assert.True(maxPrimerAbs > 0f && maxPrimerAbs <= 0.001f, $"Expected 0 < maxPrimerAbs <= 0.001f, got {maxPrimerAbs}");
        Assert.Equal(0f, primerSpan[0], precision: 6);
        Assert.Equal(0f, primerSpan[1], precision: 6);
        Assert.True(MathF.Abs(primerSpan[^2]) < 1e-5f, $"Expected smooth zero-crossing end on primer L, got {primerSpan[^2]}");
        Assert.True(MathF.Abs(primerSpan[^1]) < 1e-5f, $"Expected smooth zero-crossing end on primer R, got {primerSpan[^1]}");

        Assert.Equal(bodyPayload, wakePrimed.AsSpan(primerSamples, bodyPayload.Length).ToArray());
        Assert.All(wakePrimed.AsSpan(primerSamples + bodyPayload.Length).ToArray(), s => Assert.Equal(0f, s));

        // 4. SettingsViewModel defaults to OverlayAudioOutputModes.SystemDefault and normalizes legacy "auto-smart" DB rows to SystemDefault!
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            // Persist a legacy "auto-smart" row to verify smooth migration back to SystemDefault on InitializeAsync
            await db.SaveOverlaySettingsAsync(new OverlaySettingsDto
            {
                AudioOutputDeviceId = OverlayAudioOutputModes.AutoSmart,
                MuteSound = true,
                SoundVolume = 0.0,
            });

            var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
            await using var ipcClient = new OverlayIpcClient(paths);
            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            var themeMgr = new ThemeManagerService();

            var vm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.SelectedAudioOutputDeviceId);
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.AudioOutputDeviceOptions[0].Value);
            Assert.DoesNotContain(vm.AudioOutputDeviceOptions, o => o.Label.Contains("绕开", StringComparison.Ordinal));

            await vm.InitializeAsync();
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.SelectedAudioOutputDeviceId);
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.AudioOutputDeviceOptions[0].Value);
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public void M3A16_MainWindowXaml_RemovesTopBarQuickImportCapsule_ConsolidatesLibraryScanToSettings_AndEnforcesDedicatedTrainersRailAndThreeSubTabsSixSettingsCategories()
    {
        // 1. Enum invariants: 5 PrimaryNavSection items (Library, Achievements, Saves, Trainers, Settings) + 4 GameDetail sub-tabs + 8 Settings categories.
        Assert.Equal(
            [PrimaryNavSection.Library, PrimaryNavSection.Achievements, PrimaryNavSection.Saves, PrimaryNavSection.Trainers, PrimaryNavSection.Settings],
            Enum.GetValues<PrimaryNavSection>());
        Assert.Equal(
            [GameDetailSubTab.Achievements, GameDetailSubTab.Saves, GameDetailSubTab.GameData, GameDetailSubTab.Trainers],
            Enum.GetValues<GameDetailSubTab>());
        Assert.Contains(SettingsCategory.SavesAndCloudSync, Enum.GetValues<SettingsCategory>());
        Assert.Contains(SettingsCategory.TrainerAndSandbox, Enum.GetValues<SettingsCategory>());
        Assert.Contains(SettingsCategory.Updates, Enum.GetValues<SettingsCategory>());
        Assert.Equal(8, Enum.GetValues<SettingsCategory>().Length);

        string repoRoot = LocateRepositoryRoot();
        string mainWindowXamlPath = Path.Combine(repoRoot, "src", "Spawn.Host", "Views", "MainWindow.xaml");
        string mainWindowXaml = File.ReadAllText(mainWindowXamlPath, Encoding.UTF8);

        // 2. User #16: Top-bar capsule quick-import/scan bar MUST be completely removed!
        Assert.DoesNotContain("QuickImportTextBox", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowQuickImportCapsule", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("输入游戏目录路径，或扫描 .local\\games", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"添加 / 扫描\"", mainWindowXaml, StringComparison.Ordinal);

        // 3. User #16: Scan directories & manual single-game import MUST live under SettingsCategory.GeneralAndLibrary + empty-state shortcut!
        Assert.Contains("ConverterParameter=GeneralAndLibrary", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("SettingsManualImportTextBox", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ImportManualGamePathCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ScanAllConfiguredDirectoriesCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("立即扫描游戏库", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OpenLibrarySettingsCommand", mainWindowXaml, StringComparison.Ordinal);

        // 4. M3-A-16: Triple detail sub-tabs + Header "打开修改器" + 6th Settings category + Trainer ⋯ Popup + Virtualized Hotkey ListBox!
        Assert.Contains("GameDetailSubTab.Trainers", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OpenGameTrainersSubTabCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("SettingsCategory.TrainerAndSandbox", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("修改器与安全沙箱", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainerMoreToggle", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("在 VirusTotal 查看此哈希", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Hotkeys}\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizationMode=\"Recycling\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("GuardLockBannerText", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowProtectedFenceCard", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowHybridOfflinePanel", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowVanguardIdleWarning", mainWindowXaml, StringComparison.Ordinal);

        // 5. Dedicated Trainers Sidebar Rail, Overview Mode, IME-guarded search, and shared Workbench Template checks!
        Assert.Contains("PrimaryNavSection.Trainers", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("HasAnyRunningTrainer", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainersRailToolTip", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("全部修改器总览", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainerSidebarSearchTextBox", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OnTrainerSidebarSearchPreviewKeyDown", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowTrainersOverviewPanel", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowTrainersSingleGamePanel", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ManagedTrainers}\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ManagedTrainerRowMoreBtn", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("TrainerWorkbenchTemplate", mainWindowXaml, StringComparison.Ordinal);

        // 5b. Dedicated Saves Time Machine & Branching Tree Sidebar Rail, Overview Mode, IME-guarded search, and shared SaveWorkbenchControl checks!
        Assert.Contains("PrimaryNavSection.Saves", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("GameDetailSubTab.Saves", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OpenGameSavesSubTabCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("SettingsCategory.SavesAndCloudSync", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("存档与云同步", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("全部存档总览", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("SaveSidebarSearchTextBox", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("OnSaveSidebarSearchPreviewKeyDown", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowSavesOverviewPanel", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowSavesSingleGamePanel", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("SaveWorkbenchControl", mainWindowXaml, StringComparison.Ordinal);

        // 6. Column 1 XAML check: 5 RadioButtons in PrimaryRailGroup (Library, Achievements, Saves, Trainers, Settings).
        XDocument doc = XDocument.Load(mainWindowXamlPath);
        int railButtons = doc.Descendants()
            .Count(e => e.Name.LocalName == "RadioButton"
                        && string.Equals(e.Attribute("GroupName")?.Value, "PrimaryRailGroup", StringComparison.Ordinal));
        Assert.Equal(5, railButtons);
    }

    [Fact]
    public void M3A16_AntiCheatBadgeColoringAndThemeBrushes_EnforceTierColorsAndVerifiedOfflineBypassBadge()
    {
        var tier0 = new GameListItemViewModel(new GameRecord
        {
            GameId = "g0",
            Title = "Hollow Knight",
            InstallDir = @"C:\Games\HK",
            ExecutableRelPath = "hk.exe",
            AntiCheatTier = AntiCheatTier.Tier0,
        });
        Assert.Equal("#10B981", tier0.AntiCheatBadgeHexColor);
        Assert.False(tier0.HasVerifiedOfflineBypassBadge);

        var tier1 = new GameListItemViewModel(new GameRecord
        {
            GameId = "g1",
            Title = "WeakAC",
            InstallDir = @"C:\Games\WeakAC",
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier1,
        });
        Assert.Equal("#F59E0B", tier1.AntiCheatBadgeHexColor);
        Assert.False(tier1.HasVerifiedOfflineBypassBadge);

        var tier2Unconfirmed = new GameListItemViewModel(new GameRecord
        {
            GameId = "g2",
            Title = "Elden Ring",
            InstallDir = @"C:\Games\ER",
            ExecutableRelPath = "eldenring.exe",
            AntiCheatTier = AntiCheatTier.Tier2,
            VerifiedOfflineBypass = false,
        });
        Assert.Equal("#DC2626", tier2Unconfirmed.AntiCheatBadgeHexColor);
        Assert.False(tier2Unconfirmed.HasVerifiedOfflineBypassBadge);

        var tier2Confirmed = new GameListItemViewModel(tier2Unconfirmed.Record with { VerifiedOfflineBypass = true });
        Assert.Equal("#DC2626", tier2Confirmed.AntiCheatBadgeHexColor);
        Assert.True(tier2Confirmed.HasVerifiedOfflineBypassBadge);
        Assert.Equal("离线无 AC 就绪", tier2Confirmed.VerifiedOfflineBypassBadgeText);

        var tier3 = new GameListItemViewModel(new GameRecord
        {
            GameId = "g3",
            Title = "KernelAC",
            InstallDir = @"C:\Games\KAC",
            ExecutableRelPath = "game.exe",
            AntiCheatTier = AntiCheatTier.Tier3,
            VerifiedOfflineBypass = true, // Tier 3 must NEVER show offline bypass badge
        });
        Assert.Equal("#DC2626", tier3.AntiCheatBadgeHexColor);
        Assert.False(tier3.HasVerifiedOfflineBypassBadge);

        // Both LightTheme.xaml and DarkTheme.xaml must declare exact Tier hex colors.
        string repoRoot = LocateRepositoryRoot();
        foreach (string themeName in new[] { "LightTheme.xaml", "DarkTheme.xaml" })
        {
            string xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "Spawn.Host", "Themes", themeName), Encoding.UTF8);
            Assert.Contains("x:Key=\"Tier0BadgeForegroundBrush\" Color=\"#10B981\"", xaml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("x:Key=\"Tier1BadgeForegroundBrush\" Color=\"#F59E0B\"", xaml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("x:Key=\"Tier23BadgeForegroundBrush\" Color=\"#DC2626\"", xaml, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task M3A16_TrainerWorkbenchViewModel_CoversTier0HybridTier2FenceCooldownSnapshotRollbackAndHotkeyProxy()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-w08-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            var tier0Game = new GameRecord
            {
                GameId = "vm-tier0",
                Title = "Hollow Knight",
                InstallDir = Path.Combine(tempDir, "HK"),
                ExecutableRelPath = "hk.exe",
                AntiCheatTier = AntiCheatTier.Tier0,
            };
            var hybridTier2Game = new GameRecord
            {
                GameId = "vm-hybrid",
                Title = "ELDEN RING",
                InstallDir = Path.Combine(tempDir, "ER"),
                ExecutableRelPath = "eldenring.exe",
                AntiCheatTier = AntiCheatTier.Tier2,
                SteamAppId = 1245620,
            };
            var protectedTier2Game = new GameRecord
            {
                GameId = "vm-online-t2",
                Title = "Apex Legends",
                InstallDir = Path.Combine(tempDir, "Apex"),
                ExecutableRelPath = "r5apex.exe",
                AntiCheatTier = AntiCheatTier.Tier2,
                SteamAppId = 1172470,
            };
            await db.UpsertGameAsync(tier0Game);
            await db.UpsertGameAsync(hybridTier2Game);
            await db.UpsertGameAsync(protectedTier2Game);

            var guardClock = new HeadlessManualTimeProvider();
            using var guard = new AntiCheatGuardService(
                timeProvider: guardClock,
                cooldownDuration: TimeSpan.FromSeconds(15),
                residualProcessProbe: static () => [],
                pollInterval: TimeSpan.FromMilliseconds(250),
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));

            var fakeGate = new FakeTrainerSafetyGate();
            var fakeCatalog = new FakeTrainerCatalogService();
            var fakeRuntime = new FakeTrainerRuntimeService();
            var fakeSnapshots = new FakePreTrainerSnapshotService();
            var fakeLibrary = new FakeGameLibraryService(db);
            var fakeLaunch = new FakeGameLaunchService();
            var openedUrls = new List<string>();

            using var vm = new TrainerWorkbenchViewModel(
                paths,
                db,
                fakeCatalog,
                fakeRuntime,
                fakeSnapshots,
                fakeGate,
                guard,
                fakeLibrary,
                fakeLaunch,
                url => openedUrls.Add(url));

            // 1. Tier 0 Unbound -> Import -> Ready -> VirusTotal Hash URL -> Launch -> Hotkey Proxy -> Stop
            var tier0Item = new GameListItemViewModel(tier0Game);
            await vm.LoadForGameAsync(tier0Item);
            Assert.False(vm.IsProtectedOnlineGameBlocked);
            Assert.True(vm.ShowTrainerWorkspaceControls);
            Assert.Equal(TrainerRuntimeState.Unbound, vm.RuntimeState);
            Assert.Equal("匹配修改器", vm.PrimaryActionButtonText);
            Assert.True(vm.ShowUnboundSecondaryImportButton);

            await vm.ImportLocalTrainerPathAsync(Path.Combine(tempDir, "fake_trainer.exe"));
            Assert.Equal(TrainerRuntimeState.Ready, vm.RuntimeState);
            Assert.True(vm.HasBoundTrainer);
            Assert.Equal("启动修改器", vm.PrimaryActionButtonText);
            Assert.True(vm.ShowReadyMoreMenuButton);
            Assert.Single(vm.Hotkeys);

            // VirusTotal hash link opens ONLY on explicit click and contains exact SHA-256 URL (never uploads file)
            Assert.Empty(openedUrls);
            vm.OpenVirusTotalHashCommand.Execute(null);
            Assert.Single(openedUrls);
            Assert.Equal(UiFormatters.BuildVirusTotalFileUrl(vm.ActiveTrainer!.FileSha256), openedUrls[0]);

            // F-02: OpenTrainerSandboxFolder resolution must honor configured CustomSandboxRoot (not hardcode default root)
            string customSandboxRoot = Path.Combine(tempDir, "CustomSandboxAscii");
            string customTrainerSubDir = Path.Combine(customSandboxRoot, Path.GetDirectoryName(vm.ActiveTrainer!.SandboxRelativePath)!);
            Directory.CreateDirectory(customTrainerSubDir);
            await db.SaveTrainerSettingsAsync(new TrainerSettingsDto { CustomSandboxRoot = customSandboxRoot });
            string resolvedCustomDir = await vm.ResolveActiveTrainerDirectoryAsync();
            Assert.Equal(Path.GetFullPath(customTrainerSubDir), Path.GetFullPath(resolvedCustomDir));
            await db.SaveTrainerSettingsAsync(new TrainerSettingsDto { CustomSandboxRoot = null });

            // Launch -> Running (emerald primary button + Show/Hide secondary) -> Hotkey Proxy toggle + reset -> Stop
            await vm.LaunchTrainerCommand.ExecuteAsync(null);
            Assert.Equal(TrainerRuntimeState.Running, vm.RuntimeState);
            Assert.True(vm.IsRunningForCurrentGame);
            Assert.True(vm.IsPrimaryButtonRunningStyle);
            Assert.Equal("运行中 · 停止修改器", vm.PrimaryActionButtonText);
            Assert.True(vm.ShowRunningWindowToggleButton);
            Assert.True(vm.CanTriggerHotkeys);

            await vm.TriggerHotkeyRowAsync(vm.Hotkeys[0]);
            Assert.True(vm.Hotkeys[0].ProxyState);
            Assert.Equal(1, fakeRuntime.TriggerCount);

            await vm.ResetHotkeyProxyStatesCommand.ExecuteAsync(null);
            Assert.False(vm.Hotkeys[0].ProxyState);

            await vm.StopTrainerCommand.ExecuteAsync(null);
            Assert.Equal(TrainerRuntimeState.Ready, vm.RuntimeState);
            Assert.False(vm.IsRunningForCurrentGame);

            // 2. Pre-Trainer Snapshot list + rollback disabled while game running, enabled when stopped
            Assert.Single(vm.PreTrainerSnapshots);
            Assert.True(vm.PreTrainerSnapshots[0].CanRestore);
            vm.UpdateSelectedGameRunningState(true);
            Assert.False(vm.PreTrainerSnapshots[0].CanRestore);
            vm.UpdateSelectedGameRunningState(false);
            Assert.True(vm.PreTrainerSnapshots[0].CanRestore);
            await vm.RestoreSnapshotAsync(vm.PreTrainerSnapshots[0]);
            Assert.Equal(1, fakeSnapshots.RestoreCallCount);

            // 3. 15s Cooldown Countdown Banner locks primary action & hotkeys
            guard.NotifyProtectedGameStarted("other-ac-game", AntiCheatTier.Tier3);
            Task cooldownTask = guard.NotifyProtectedGameExitedAsync("other-ac-game", AntiCheatTier.Tier3);
            guardClock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(25);
            vm.UpdateGuardSnapshot(guard.CurrentSnapshot);
            Assert.True(vm.IsGuardLocked);
            Assert.Contains("余温冷却中", vm.GuardLockBannerText, StringComparison.Ordinal);
            Assert.False(vm.IsPrimaryActionButtonEnabled);
            Assert.False(vm.CanTriggerHotkeys);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!cooldownTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                guardClock.Advance(TimeSpan.FromMilliseconds(250));
                await Task.Delay(5);
            }

            await cooldownTask.WaitAsync(TimeSpan.FromSeconds(2));
            vm.UpdateGuardSnapshot(guard.CurrentSnapshot);
            Assert.False(vm.IsGuardLocked);

            // 4. Hybrid Single-Player (Tier 2 Elden Ring): blocked until explicit confirmation, static Tier stays Tier2!
            var hybridItem = new GameListItemViewModel(hybridTier2Game);
            await vm.LoadForGameAsync(hybridItem);
            Assert.False(vm.IsProtectedOnlineGameBlocked);
            Assert.True(vm.IsHybridSinglePlayerGame);
            Assert.True(vm.ShowHybridOfflinePanel);
            Assert.False(vm.CanLaunchOrRunTrainer);

            await vm.ConfirmOfflineBypassCommand.ExecuteAsync(null);
            Assert.Equal(AntiCheatTier.Tier2, hybridItem.AntiCheatTier);
            Assert.True(hybridItem.HasVerifiedOfflineBypassBadge);
            Assert.True(vm.IsOfflineBypassConfirmed);
            Assert.True(vm.CanLaunchOrRunTrainer);

            // Live AC indicator appears -> CanLaunchOrRunTrainer immediately flips false!
            fakeGate.LiveProbe = new LiveAntiCheatProbeReport
            {
                ActiveAntiCheatProcesses = ["EasyAntiCheat_EOS.exe"],
            };
            await vm.RefreshLiveAntiCheatProbeCommand.ExecuteAsync(null);
            Assert.False(vm.IsLiveAntiCheatClear);
            Assert.False(vm.CanLaunchOrRunTrainer);
            fakeGate.LiveProbe = new LiveAntiCheatProbeReport();

            // 5. Protected Online Tier 2 (no single-player): physically hides all trainer controls, renders Security Fence Card!
            var onlineItem = new GameListItemViewModel(protectedTier2Game);
            await vm.LoadForGameAsync(onlineItem);
            Assert.True(vm.IsProtectedOnlineGameBlocked);
            Assert.True(vm.ShowProtectedFenceCard);
            Assert.False(vm.ShowTrainerWorkspaceControls);
            Assert.False(vm.IsPrimaryActionButtonEnabled);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task M3A16_SettingsViewModel_TrainerAndSandboxPersistenceAndDefenderStatusTransitions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-w08-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
            await using var ipcClient = new OverlayIpcClient(paths);
            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            var themeMgr = new ThemeManagerService();
            var fakeDefender = new FakeDefenderExclusionAssistant();
            var fakeAudio = new FakeTrainerAudioController();
            var fakeRuntime = new FakeTrainerRuntimeService();
            var openedUris = new List<string>();
            var feedbackMessages = new List<string>();

            var vm = new SettingsViewModel(
                paths,
                db,
                keyProvider,
                ipcClient,
                guard,
                themeMgr,
                fakeDefender,
                fakeAudio,
                uri => openedUris.Add(uri),
                fakeRuntime);
            vm.StatusFeedbackRequested += msg => feedbackMessages.Add(msg);

            await vm.InitializeAsync();

            // Defaults match D-28 (FullMute default, 75ms hold, PreSnapshot enabled, Retention=5, MaxSize=256MB)
            Assert.Equal(TrainerAudioMode.FullMute, vm.SelectedTrainerAudioMode);
            Assert.Equal(75, vm.TrainerHotkeyHoldMs);
            Assert.True(vm.TrainerCreatePreSnapshot);
            Assert.Equal(5, vm.SelectedTrainerSnapshotRetentionCount);
            Assert.Equal(256, vm.SelectedTrainerSnapshotMaxSizeMb);
            Assert.False(vm.TrainerIncludeFlingArchive);
            Assert.False(vm.TrainerStrictVanguardMode);

            // Switch to Category 6 & verify title
            vm.SelectedCategory = SettingsCategory.TrainerAndSandbox;
            Assert.Equal("修改器与安全沙箱", vm.CurrentCategoryTitle);

            // Defender state transitions: TamperProtectionBlocked -> SmartAppControlNotice -> AddedAndVerified
            fakeDefender.NextInspectReport = new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.TamperProtectionBlocked,
                SandboxPath = vm.EffectiveTrainerSandboxRoot,
                IsTamperProtectionEnabled = true,
                Message = "Tamper protection blocked",
                GuidanceUrl = DefenderExclusionAssistant.DefenderExclusionsSettingsUri,
            };
            await vm.InspectDefenderExclusionCommand.ExecuteAsync(null);
            Assert.True(vm.ShowDefenderManualGuidance);
            Assert.False(vm.IsDefenderExcluded);

            vm.OpenDefenderSettingsCommand.Execute(null);
            Assert.Contains(DefenderExclusionAssistant.DefenderExclusionsSettingsUri, openedUris);

            fakeDefender.NextInspectReport = new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.SmartAppControlNotice,
                SandboxPath = vm.EffectiveTrainerSandboxRoot,
                IsSmartAppControlEnforced = true,
                Message = "SAC Enforced",
                GuidanceUrl = DefenderExclusionAssistant.SmartAppControlLearnMoreUrl,
            };
            await vm.InspectDefenderExclusionCommand.ExecuteAsync(null);
            Assert.True(vm.ShowSmartAppControlCard);

            fakeDefender.NextRequestReport = new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.AddedAndVerified,
                SandboxPath = vm.EffectiveTrainerSandboxRoot,
                IsExcluded = true,
                Message = "Added and verified",
            };
            await vm.RequestDefenderExclusionCommand.ExecuteAsync(null);
            Assert.True(vm.IsDefenderExcluded);
            Assert.False(vm.ShowDefenderManualGuidance);

            // Restore A0 audio backups invokes ITrainerAudioController
            await vm.RestoreTrainerA0BackupsCommand.ExecuteAsync(null);
            Assert.Equal(1, fakeAudio.RestoreCount);

            // F-02: Custom sandbox root migration MUST refuse while a trainer session is running,
            // and upon idle migration/reset MUST clear stale DefenderStatusReport.
            string customAsciiSandbox = Path.Combine(tempDir, "AsciiSandboxRoot");
            string gameTrainerDir = Path.Combine(paths.TrainerSandboxDirectory, "vm-tier0", "tr-1");
            Directory.CreateDirectory(gameTrainerDir);
            string markerFile = Path.Combine(gameTrainerDir, "marker.txt");
            File.WriteAllText(markerFile, "ok", Encoding.UTF8);

            // While trainer is running -> refuse both ApplyCustomSandboxRoot and ResetDefaultSandboxRoot!
            await fakeRuntime.LaunchAsync("tr-vm-tier0", new TrainerLaunchOptions());
            vm.TrainerCustomSandboxRootInput = customAsciiSandbox;
            await vm.ApplyCustomSandboxRootCommand.ExecuteAsync(null);
            Assert.Null(vm.AppliedCustomTrainerSandboxRoot);
            Assert.True(vm.IsDefenderExcluded);
            Assert.Contains(feedbackMessages, m => m.Contains("正在运行", StringComparison.Ordinal));

            feedbackMessages.Clear();
            await vm.ResetDefaultSandboxRootCommand.ExecuteAsync(null);
            Assert.True(vm.IsDefenderExcluded);
            Assert.Contains(feedbackMessages, m => m.Contains("正在运行", StringComparison.Ordinal));

            // Stop active session -> migration succeeds AND resets stale DefenderStatusReport to null!
            await fakeRuntime.StopAsync("sess-1");
            await vm.ApplyCustomSandboxRootCommand.ExecuteAsync(null);
            Assert.Equal(Path.GetFullPath(customAsciiSandbox), vm.AppliedCustomTrainerSandboxRoot);
            Assert.Null(vm.DefenderStatusReport);
            Assert.False(vm.IsDefenderExcluded);
            Assert.Equal("未检测", vm.DefenderStatusBadgeText);
            Assert.True(File.Exists(Path.Combine(customAsciiSandbox, "vm-tier0", "tr-1", "marker.txt")));

            // Re-establish Defender excluded state for custom root, then reset back to default -> resets DefenderStatusReport!
            await vm.RequestDefenderExclusionCommand.ExecuteAsync(null);
            Assert.True(vm.IsDefenderExcluded);
            await vm.ResetDefaultSandboxRootCommand.ExecuteAsync(null);
            Assert.Null(vm.AppliedCustomTrainerSandboxRoot);
            Assert.Null(vm.DefenderStatusReport);
            Assert.False(vm.IsDefenderExcluded);
            Assert.True(File.Exists(Path.Combine(paths.TrainerSandboxDirectory, "vm-tier0", "tr-1", "marker.txt")));

            // Library scan directory persistence under GeneralAndLibrary (#16)
            string extraGamesDir = Path.Combine(tempDir, "ExtraGames");
            Directory.CreateDirectory(extraGamesDir);
            await vm.AddScanDirectoryPathAsync(extraGamesDir);
            Assert.Contains(vm.ScanDirectories, e => string.Equals(e.Path, extraGamesDir, StringComparison.OrdinalIgnoreCase) && e.IsRemovable);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task M3A16_HostDependencyInjection_WiresFullProgressAndLauncherGraphWithZeroCircularDependencies()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-w08-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(paths);
            services.AddSingleton(new SingleInstanceCoordinator(paths));
            services.AddSpawnCore(paths);
            services.AddSpawnLauncher();
            services.AddSpawnAchievement();
            services.AddSpawnProgress();
            services.AddSingleton<ThemeManagerService>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<TrainerWorkbenchViewModel>();
            services.AddSingleton<MainWindowViewModel>();

            await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            var launch = sp.GetRequiredService<IGameLaunchService>();
            var runtime = sp.GetRequiredService<ITrainerRuntimeService>();
            var catalog = sp.GetRequiredService<ITrainerCatalogService>();
            var snapshots = sp.GetRequiredService<IPreTrainerSaveSnapshotService>();
            var defender = sp.GetRequiredService<IDefenderExclusionAssistant>();
            var hooks = sp.GetServices<ISensitiveWorkerShutdownHook>().ToList();
            var mainVm = sp.GetRequiredService<MainWindowViewModel>();

            Assert.NotNull(launch);
            Assert.NotNull(runtime);
            Assert.NotNull(catalog);
            Assert.NotNull(snapshots);
            Assert.NotNull(defender);
            Assert.NotEmpty(hooks);
            Assert.NotNull(mainVm.Trainer);
            Assert.NotNull(mainVm.Settings);
            mainVm.Dispose();
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task DedicatedTrainersSidebar_RailIndicatorPrioritizationOverviewAndLibrarySync_WorksEndToEndHeadlessly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-trainer-sidebar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            var hollowKnight = new GameRecord
            {
                GameId = "game-hk",
                Title = "Hollow Knight",
                InstallDir = Path.Combine(tempDir, "HK"),
                ExecutableRelPath = "hk.exe",
                AntiCheatTier = AntiCheatTier.Tier0,
            };
            var celeste = new GameRecord
            {
                GameId = "game-celeste",
                Title = "Celeste",
                InstallDir = Path.Combine(tempDir, "Celeste"),
                ExecutableRelPath = "celeste.exe",
                AntiCheatTier = AntiCheatTier.Tier0,
            };
            var hybridElden = new GameRecord
            {
                GameId = "vm-hybrid",
                Title = "ELDEN RING",
                InstallDir = Path.Combine(tempDir, "ER"),
                ExecutableRelPath = "eldenring.exe",
                AntiCheatTier = AntiCheatTier.Tier2,
                SteamAppId = 1245620,
                VerifiedOfflineBypass = true,
                VerifiedOfflineBypassAtUtc = DateTimeOffset.UtcNow,
            };
            var protectedApex = new GameRecord
            {
                GameId = "vm-online-t2",
                Title = "Apex Legends",
                InstallDir = Path.Combine(tempDir, "Apex"),
                ExecutableRelPath = "r5apex.exe",
                AntiCheatTier = AntiCheatTier.Tier2,
                SteamAppId = 1172470,
            };

            await db.UpsertGameAsync(hollowKnight);
            await db.UpsertGameAsync(celeste);
            await db.UpsertGameAsync(hybridElden);
            await db.UpsertGameAsync(protectedApex);

            var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
            await using var ipcClient = new OverlayIpcClient(paths);
            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            var themeMgr = new ThemeManagerService();
            var eventBus = new InMemorySpawnEventBus();

            var fakeLibrary = new FakeGameLibraryService(db);
            var fakeLaunch = new FakeGameLaunchService();
            var fakeAchievements = new FakeAchievementService();
            var fakeGameData = new FakeGameDataManagementService();
            var fakeGate = new FakeTrainerSafetyGate();
            var fakeCatalog = new FakeTrainerCatalogService();
            var fakeRuntime = new FakeTrainerRuntimeService();
            var fakeSnapshots = new FakePreTrainerSnapshotService();
            var openedUrls = new List<string>();

            var settingsVm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
            var trainerVm = new TrainerWorkbenchViewModel(
                paths,
                db,
                fakeCatalog,
                fakeRuntime,
                fakeSnapshots,
                fakeGate,
                guard,
                fakeLibrary,
                fakeLaunch,
                url => openedUrls.Add(url));

            using var mainVm = new MainWindowViewModel(
                paths,
                db,
                fakeLibrary,
                fakeLaunch,
                fakeAchievements,
                fakeGameData,
                guard,
                ipcClient,
                eventBus,
                fakeCatalog,
                fakeRuntime,
                fakeGate,
                settingsVm,
                trainerVm,
                url => openedUrls.Add(url));

            await mainVm.InitializeAsync();

            // 1. Navigate to top-level Trainers section -> Overview mode by default, empty state, prioritized sidebar
            mainVm.ActiveSection = PrimaryNavSection.Trainers;
            await mainVm.RefreshTrainerHubStateAsync();

            Assert.True(mainVm.ShowTrainersOverviewPanel);
            Assert.False(mainVm.ShowTrainersSingleGamePanel);
            Assert.Equal("修改器管理", mainVm.TopBarTitle);
            Assert.False(mainVm.HasAnyRunningTrainer);
            Assert.Equal("修改器管理", mainVm.TrainersRailToolTip);
            Assert.False(mainVm.HasManagedTrainers);
            Assert.Equal(4, mainVm.TrainerSidebarGames.Count);
            // Safety-locked game (Apex Legends) MUST be sorted to the bottom and clearly marked as safety-locked
            Assert.Equal("vm-online-t2", mainVm.TrainerSidebarGames[^1].GameId);
            Assert.True(mainVm.TrainerSidebarGames[^1].IsSafetyLocked);
            Assert.Equal("安全锁定", mainVm.TrainerSidebarGames[^1].StatusPillText);

            // 2. Import trainer from Library -> Game Detail -> Trainers sub-tab -> IMMEDIATELY reflects in Trainers section!
            mainVm.ActiveSection = PrimaryNavSection.Library;
            mainVm.SelectedGame = mainVm.AllGames.Single(g => g.GameId == "game-hk");
            mainVm.SelectedGameSubTab = GameDetailSubTab.Trainers;
            await trainerVm.LoadForGameAsync(mainVm.SelectedGame);

            string stubExe1 = Path.Combine(tempDir, "hk_trainer.exe");
            File.WriteAllBytes(stubExe1, [0x4D, 0x5A]);
            await trainerVm.ImportLocalTrainerPathAsync(stubExe1);

            Assert.Equal(1, mainVm.TotalManagedTrainersCount);
            Assert.Equal(1, mainVm.BoundTrainerGamesCount);
            Assert.Single(mainVm.ManagedTrainers);
            Assert.Equal("game-hk", mainVm.ManagedTrainers[0].GameId);
            // Hollow Knight (bound trainer) is now prioritized at the top of Column 2 Trainers sidebar!
            Assert.Equal("game-hk", mainVm.TrainerSidebarGames[0].GameId);
            Assert.Equal(TrainerSidebarGameCategory.HasBoundTrainer, mainVm.TrainerSidebarGames[0].Category);
            Assert.Equal("已绑定", mainVm.TrainerSidebarGames[0].StatusPillText);

            // 3. Launch trainer from Trainers Overview -> rail green dot lights up + sidebar pins as Running + Library workbench syncs!
            mainVm.ActiveSection = PrimaryNavSection.Trainers;
            await mainVm.ManagedTrainers[0].PrimaryLaunchOrStopCommand.ExecuteAsync(null);

            Assert.True(mainVm.HasAnyRunningTrainer);
            Assert.Contains("运行", mainVm.TrainersRailToolTip, StringComparison.Ordinal);
            Assert.Equal("Hollow Knight", mainVm.ActiveRunningTrainerGameTitle);
            Assert.Equal("game-hk", mainVm.TrainerSidebarGames[0].GameId);
            Assert.True(mainVm.TrainerSidebarGames[0].HasRunningTrainer);
            Assert.Equal("运行中", mainVm.TrainerSidebarGames[0].StatusPillText);
            Assert.True(mainVm.ManagedTrainers[0].IsRunning);
            Assert.Equal("停止", mainVm.ManagedTrainers[0].PrimaryActionText);
            Assert.True(trainerVm.IsRunningForCurrentGame);
            Assert.Equal("运行中 · 停止修改器", trainerVm.PrimaryActionButtonText);

            // 4. Overview ⋯ VirusTotal hash lookup & SHA-256 verification
            mainVm.ManagedTrainers[0].OpenVirusTotalCommand.Execute(null);
            Assert.Single(openedUrls);
            Assert.Equal(UiFormatters.BuildVirusTotalFileUrl(mainVm.ManagedTrainers[0].FullSha256), openedUrls[0]);
            await mainVm.ManagedTrainers[0].VerifyIntegrityCommand.ExecuteAsync(null);
            Assert.Contains("SHA-256", mainVm.StatusBannerMessage, StringComparison.Ordinal);

            // 5. Stop running trainer from Overview banner -> rail dot turns off & workbench syncs back to Ready
            await mainVm.StopActiveRunningTrainerCommand.ExecuteAsync(null);
            Assert.False(mainVm.HasAnyRunningTrainer);
            Assert.False(mainVm.ManagedTrainers[0].IsRunning);
            Assert.False(trainerVm.IsRunningForCurrentGame);
            Assert.Equal("启动修改器", trainerVm.PrimaryActionButtonText);

            // 6. Sidebar search filtering (by game title & trainer display name) + clear
            mainVm.TrainerSidebarSearchText = "Synthetic Stub";
            Assert.Single(mainVm.TrainerSidebarGames);
            Assert.Equal("game-hk", mainVm.TrainerSidebarGames[0].GameId);
            mainVm.ClearTrainerSidebarSearchCommand.Execute(null);
            Assert.Equal(4, mainVm.TrainerSidebarGames.Count);

            // 7. Compact < 1000 DIP narrow window drawer auto-collapse + Per-Game workbench + Drag-and-Drop import in Trainers section!
            mainVm.UpdateViewportWidth(860);
            Assert.True(mainVm.IsNarrowWindow);
            mainVm.ToggleNarrowSidebarCommand.Execute(null);
            Assert.True(mainVm.IsNarrowSidebarOpen);

            var celesteSidebarRow = mainVm.TrainerSidebarGames.Single(g => g.GameId == "game-celeste");
            mainVm.SelectTrainerSidebarGameCommand.Execute(celesteSidebarRow);
            await trainerVm.LoadForGameAsync(celesteSidebarRow.Game);

            Assert.False(mainVm.IsNarrowSidebarOpen);
            Assert.True(mainVm.ShowTrainersSingleGamePanel);
            Assert.False(mainVm.ShowTrainersOverviewPanel);
            Assert.Equal("修改器 · Celeste", mainVm.TopBarTitle);

            string stubExe2 = Path.Combine(tempDir, "celeste_trainer.exe");
            File.WriteAllBytes(stubExe2, [0x4D, 0x5A]);
            await mainVm.ImportDroppedPathsAsync([stubExe2]);

            Assert.Equal(2, mainVm.TotalManagedTrainersCount);
            Assert.Equal(2, mainVm.ManagedTrainers.Count);
            Assert.True(trainerVm.HasBoundTrainer);

            // 8. Remove Celeste's trainer from Overview -> IMMEDIATELY resets TrainerWorkbenchViewModel to Unbound & updates Overview!
            var celesteManagedRow = mainVm.ManagedTrainers.Single(m => m.GameId == "game-celeste");
            await celesteManagedRow.RemoveCommand.ExecuteAsync(null);

            Assert.Equal(1, mainVm.TotalManagedTrainersCount);
            Assert.Single(mainVm.ManagedTrainers);
            Assert.False(trainerVm.HasBoundTrainer);
            Assert.Equal(TrainerRuntimeState.Unbound, trainerVm.RuntimeState);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static void CleanupTempDir(string tempDir)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private sealed class HeadlessManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];
        private readonly Lock _gate = new();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> ready = [];
            lock (_gate)
            {
                _now += delta;
                for (int i = _timers.Count - 1; i >= 0; i--)
                {
                    if (_timers[i].DueAt <= _now)
                    {
                        ready.Add(_timers[i]);
                        _timers.RemoveAt(i);
                    }
                }
            }

            foreach (var timer in ready)
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, GetUtcNow() + dueTime, this);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset dueAt, HeadlessManualTimeProvider owner) : ITimer
        {
            public DateTimeOffset DueAt { get; private set; } = dueAt;
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeTrainerSafetyGate : ITrainerSafetyGate
    {
        public bool IsWatchdogActive => false;
        public LiveAntiCheatProbeReport LiveProbe { get; set; } = new();
        public event EventHandler<TrainerEmergencyStopEvent>? EmergencyStopTriggered
        {
            add { }
            remove { }
        }

        public Task<FourLayerAntiCheatReport> InspectGameFourLayerAsync(GameRecord game, bool allowOnlineLookup = true, CancellationToken ct = default) =>
            Task.FromResult(new FourLayerAntiCheatReport
            {
                EffectiveTier = game.AntiCheatTier,
                HasSinglePlayerCategory = game.GameId != "vm-online-t2",
                IsMultiplayerOnly = game.GameId == "vm-online-t2",
            });

        public Task<LiveAntiCheatProbeReport> ProbeLiveAntiCheatAsync(CancellationToken ct = default) =>
            Task.FromResult(LiveProbe);

        public Task<VerifiedOfflineBypass> EvaluateOfflineBypassAsync(GameRecord game, CancellationToken ct = default)
        {
            bool eligible = game.AntiCheatTier == AntiCheatTier.Tier2 && game.GameId == "vm-hybrid";
            return Task.FromResult(new VerifiedOfflineBypass
            {
                GameId = game.GameId,
                IsConfirmed = eligible && game.VerifiedOfflineBypass,
                ConfirmedAtUtc = game.VerifiedOfflineBypassAtUtc,
                IsEligible = eligible,
                StaticAntiCheatTier = game.AntiCheatTier,
                HasSinglePlayerMode = game.GameId != "vm-online-t2",
                IsLiveAntiCheatClear = LiveProbe.IsClearForTrainer,
                ActiveAntiCheatIndicators = LiveProbe.ActiveAntiCheatProcesses,
            });
        }

        public Task<TrainerAllowanceDecision> EvaluateTrainerAllowanceAsync(GameRecord game, bool strictVanguardMode = false, CancellationToken ct = default)
        {
            if (game.GameId == "vm-online-t2" || game.AntiCheatTier is AntiCheatTier.Tier1 or AntiCheatTier.Tier3)
            {
                return Task.FromResult(new TrainerAllowanceDecision
                {
                    Outcome = TrainerAllowanceOutcome.BlockedByTier,
                    IsAllowed = false,
                    Message = "受保护联网游戏禁止使用修改器。",
                    StaticGameTier = game.AntiCheatTier,
                });
            }

            if (game.AntiCheatTier == AntiCheatTier.Tier2)
            {
                if (!game.VerifiedOfflineBypass)
                {
                    return Task.FromResult(new TrainerAllowanceDecision
                    {
                        Outcome = TrainerAllowanceOutcome.RequiresOfflineConfirmation,
                        IsAllowed = false,
                        Message = "需确认混合单机离线无 AC 模式。",
                        StaticGameTier = game.AntiCheatTier,
                        CanConfirmOfflineBypass = true,
                    });
                }

                if (!LiveProbe.IsClearForTrainer)
                {
                    return Task.FromResult(new TrainerAllowanceDecision
                    {
                        Outcome = TrainerAllowanceOutcome.BlockedByLiveAntiCheat,
                        IsAllowed = false,
                        Message = "检测到活跃反作弊进程。",
                        StaticGameTier = game.AntiCheatTier,
                        CanConfirmOfflineBypass = true,
                        DetectedAntiCheatProcesses = LiveProbe.ActiveAntiCheatProcesses,
                    });
                }

                return Task.FromResult(new TrainerAllowanceDecision
                {
                    Outcome = TrainerAllowanceOutcome.Allowed,
                    IsAllowed = true,
                    Message = "已通过离线无 AC 硬验票。",
                    StaticGameTier = game.AntiCheatTier,
                    CanConfirmOfflineBypass = true,
                    UsedVerifiedOfflineBypass = true,
                });
            }

            return Task.FromResult(new TrainerAllowanceDecision
            {
                Outcome = TrainerAllowanceOutcome.Allowed,
                IsAllowed = true,
                Message = "Tier 0 纯离线单机就绪。",
                StaticGameTier = AntiCheatTier.Tier0,
            });
        }

        public void NotifyTrainerSessionStarted(string sessionId, string gameId) { }
        public void NotifyTrainerSessionEnded(string sessionId) { }
    }

    private sealed class FakeTrainerCatalogService : ITrainerCatalogService
    {
        private readonly Dictionary<string, List<TrainerCatalogRecord>> _byGame = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<TrainerHotkeyBindingRecord>> _hotkeys = new(StringComparer.OrdinalIgnoreCase);

        public Task<TrainerMatchResult> MatchAsync(string gameId, string? userSearchOverride = null, CancellationToken ct = default) =>
            Task.FromResult(new TrainerMatchResult { GameId = gameId, EffectiveQuery = userSearchOverride ?? "Hollow Knight" });

        public Task<TrainerImportResult> DownloadAsync(string gameId, TrainerCandidate candidate, IProgress<TrainerDownloadProgress>? progress = null, CancellationToken ct = default) =>
            ImportLocalAsync(gameId, candidate.Title, ct);

        public Task<TrainerImportResult> ImportLocalAsync(string gameId, string userSelectedPath, CancellationToken ct = default)
        {
            string trainerId = $"tr-{gameId}";
            var record = new TrainerCatalogRecord
            {
                TrainerId = trainerId,
                GameId = gameId,
                Provider = TrainerProviderKind.LocalImport,
                DisplayName = "Synthetic Stub Trainer",
                VersionLabel = "v1.0",
                SandboxRelativePath = $"{gameId}\\{trainerId}\\stub.exe",
                FileSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                FileSize = 1024,
                PeMachine = "x64",
                ParseMode = TrainerParseMode.Structured,
            };
            var hk = new TrainerHotkeyBindingRecord
            {
                TrainerId = trainerId,
                EntryId = "hk-1",
                Label = "Infinite Health",
                OriginalChord = "Num 1",
                EntryKind = TrainerHotkeyEntryKind.Toggle,
                SortOrder = 0,
            };

            _byGame[gameId] = [record];
            _hotkeys[trainerId] = [hk];

            return Task.FromResult(new TrainerImportResult
            {
                Succeeded = true,
                Trainer = record,
                Hotkeys = [hk],
            });
        }

        public Task<TrainerIntegrityReport> VerifyAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult(new TrainerIntegrityReport { TrainerId = trainerId, IsValid = true, FileExists = true, HashMatches = true, IsInsideSandbox = true });

        public Task RemoveAsync(string trainerId, CancellationToken ct = default)
        {
            foreach (var list in _byGame.Values)
            {
                list.RemoveAll(t => t.TrainerId == trainerId);
            }

            _hotkeys.Remove(trainerId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TrainerCatalogRecord>> ListForGameAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrainerCatalogRecord>>(_byGame.TryGetValue(gameId, out var list) ? list : []);

        public Task<IReadOnlyList<TrainerHotkeyBindingRecord>> GetHotkeysAsync(string trainerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrainerHotkeyBindingRecord>>(_hotkeys.TryGetValue(trainerId, out var list) ? list : []);
    }

    private sealed class FakeTrainerRuntimeService : ITrainerRuntimeService
    {
        public TrainerRuntimeState State { get; private set; } = TrainerRuntimeState.Ready;
        public TrainerSessionHandle? ActiveSession { get; private set; }
        public int TriggerCount { get; private set; }
        public event EventHandler<TrainerRuntimeStateChanged>? StateChanged;

        public Task<TrainerLaunchOutcome> LaunchAsync(string trainerId, TrainerLaunchOptions options, CancellationToken ct = default)
        {
            string gameId = trainerId.StartsWith("tr-", StringComparison.Ordinal) ? trainerId[3..] : "vm-tier0";
            ActiveSession = new TrainerSessionHandle
            {
                SessionId = "sess-1",
                GameId = gameId,
                TrainerId = trainerId,
                BackendKind = "ExternalTrainer",
                ElevationLevel = TrainerElevationLevel.Level1MediumIl,
                WorkerProcessId = 1001,
                TrainerProcessId = 1002,
            };
            State = TrainerRuntimeState.Running;
            StateChanged?.Invoke(this, new TrainerRuntimeStateChanged
            {
                GameId = gameId,
                TrainerId = trainerId,
                SessionId = "sess-1",
                PreviousState = TrainerRuntimeState.Ready,
                CurrentState = TrainerRuntimeState.Running,
            });

            return Task.FromResult(new TrainerLaunchOutcome
            {
                Succeeded = true,
                Session = ActiveSession,
                PreSnapshot = new PreTrainerSnapshotResult
                {
                    Succeeded = true,
                    Status = PreTrainerSnapshotStatus.Completed,
                    Snapshot = new PreTrainerSnapshotRecord
                    {
                        SnapshotId = "snap-1",
                        GameId = gameId,
                        TrainerId = trainerId,
                        FileCount = 2,
                        TotalBytes = 4096,
                    },
                },
            });
        }

        public Task TriggerHotkeyAsync(string sessionId, string entryId, CancellationToken ct = default)
        {
            TriggerCount++;
            return Task.CompletedTask;
        }

        public Task SetOriginalWindowVisibleAsync(string sessionId, bool visible, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(string sessionId, CancellationToken ct = default)
        {
            string gameId = ActiveSession?.GameId ?? "vm-tier0";
            ActiveSession = null;
            State = TrainerRuntimeState.Ready;
            StateChanged?.Invoke(this, new TrainerRuntimeStateChanged
            {
                GameId = gameId,
                SessionId = sessionId,
                PreviousState = TrainerRuntimeState.Running,
                CurrentState = TrainerRuntimeState.Ready,
            });
            return Task.CompletedTask;
        }
    }

    private sealed class FakePreTrainerSnapshotService : IPreTrainerSaveSnapshotService
    {
        public int RestoreCallCount { get; private set; }

        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default) =>
            Task.FromResult(new PreTrainerSnapshotResult
            {
                Succeeded = true,
                Status = PreTrainerSnapshotStatus.Completed,
                Snapshot = new PreTrainerSnapshotRecord
                {
                    SnapshotId = "snap-1",
                    GameId = gameId,
                    TrainerId = trainerId,
                    FileCount = 2,
                    TotalBytes = 4096,
                },
            });

        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>(
            [
                new PreTrainerSnapshotRecord
                {
                    SnapshotId = "snap-1",
                    GameId = gameId,
                    Status = PreTrainerSnapshotStatus.Completed,
                    FileCount = 2,
                    TotalBytes = 4096,
                },
            ]);

        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default)
        {
            RestoreCallCount++;
            return Task.FromResult(new PreTrainerRestoreResult { Succeeded = true, RestoredFileCount = 2, Message = "Restored" });
        }
    }

    private sealed class FakeGameLibraryService(SpawnDatabase db) : IGameLibraryService
    {
        public Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default) => db.GetAllGamesAsync(cancellationToken);
        public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public Task<GameRecord?> ImportGameAsync(string directoryOrExePath, CancellationToken cancellationToken = default) => Task.FromResult<GameRecord?>(null);
        public Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameRecord>>([]);
        public Task<GameRecord?> RescanGameAsync(string gameId, CancellationToken cancellationToken = default) => db.GetGameByIdAsync(gameId, cancellationToken);
        public async Task<GameRecord?> UpdateGameMetadataAsync(string gameId, GameMetadataUpdate update, CancellationToken cancellationToken = default)
        {
            if (update.VerifiedOfflineBypass.HasValue)
            {
                await db.SetGameVerifiedOfflineBypassAsync(gameId, update.VerifiedOfflineBypass.Value, update.VerifiedOfflineBypassAtUtc, cancellationToken);
            }

            return await db.GetGameByIdAsync(gameId, cancellationToken);
        }

        public Task<bool> RemoveGameAsync(string gameId, CancellationToken cancellationToken = default) => db.DeleteGameAsync(gameId, cancellationToken);
    }

    private sealed class FakeGameLaunchService : IGameLaunchService
    {
        public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions { get; } = new Dictionary<string, ActiveGameSession>();
        public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
        {
            add { }
            remove { }
        }

        public ActiveGameSession? GetActiveSession(string gameId) => null;
        public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(GameLaunchResult.Failure("STUB", "stub"));
        public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RegisterTrustedOverlayWindow(nint windowHandle, int processId) { }
        public void UnregisterTrustedOverlayWindow(nint windowHandle) { }
    }

    private sealed class FakeDefenderExclusionAssistant : IDefenderExclusionAssistant
    {
        public DefenderExclusionStatusReport? NextInspectReport { get; set; }
        public DefenderExclusionStatusReport? NextRequestReport { get; set; }

        public Task<DefenderExclusionStatusReport> InspectStatusAsync(string sandboxPath, CancellationToken ct = default) =>
            Task.FromResult(NextInspectReport ?? new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.Unknown,
                SandboxPath = sandboxPath,
                Message = "Unknown",
            });

        public Task<DefenderExclusionStatusReport> RequestExclusionAsync(string sandboxPath, CancellationToken ct = default) =>
            Task.FromResult(NextRequestReport ?? new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.AddedAndVerified,
                SandboxPath = sandboxPath,
                IsExcluded = true,
                Message = "Verified",
            });
    }

    private sealed class FakeTrainerAudioController : ITrainerAudioController
    {
        public int RestoreCount { get; private set; }
        public event Func<IReadOnlyList<int>, bool, double, CancellationToken, Task>? FallbackRequired
        {
            add { }
            remove { }
        }

        public Task PrepareAsync(TrainerAudioPolicy policy, string sandboxRoot, CancellationToken ct = default) => Task.CompletedTask;
        public void TrackProcess(int pid) { }
        public Task ApplyPolicyAsync(TrainerAudioPolicy policy, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreA0BackupsAsync(CancellationToken ct = default)
        {
            RestoreCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAchievementService : IAchievementService
    {
        public event EventHandler<AchievementUnlockedNotification>? AchievementUnlocked
        {
            add { }
            remove { }
        }

        public Task<GameAchievementSummaryDto> GetGameAchievementsAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameAchievementSummaryDto
            {
                GameId = gameId,
                ChannelStatusText = "就绪",
            });

        public Task<IReadOnlyList<AchievementUnlockDto>> GetRecentUnlocksAsync(int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AchievementUnlockDto>>([]);

        public Task<GameAchievementSummaryDto> SyncGameSchemaAsync(string gameId, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            GetGameAchievementsAsync(gameId, cancellationToken);

        public Task<int> ScanLocalUnlocksAsync(string gameId, bool silentSeed = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<bool> SetAchievementUnlockedAsync(string gameId, string apiName, bool unlocked, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<GbeForkActivationResult> ActivateGbeForkAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GbeForkActivationResult { Succeeded = false, Message = "stub" });

        public Task<GbeForkActivationResult> RestoreOriginalDllAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GbeForkActivationResult { Succeeded = false, Message = "stub" });

        public Task SendTestToastAsync(string? gameId = null, OverlayThemeKind? themeOverride = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGameDataManagementService : IGameDataManagementService
    {
        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(string gameId, bool refreshOnlineRules = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameDataEntryDto>>([]);

        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDataCleanResult { Succeeded = false, Message = "stub" });

        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);

        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDataCleanResult { Succeeded = false, Message = "stub" });
    }

    private static SynthesizedUnlockSoundPlayer.DecodedStereoWav BuildDecodedStereoSine(int sampleRate, int frameCount, float leftAmp, float rightAmp)
    {
        float[] interleaved = new float[frameCount * 2];
        for (int i = 0; i < frameCount; i++)
        {
            double t = (double)i / sampleRate;
            interleaved[i * 2] = (float)(Math.Sin(2.0 * Math.PI * 440.0 * t) * leftAmp);
            interleaved[i * 2 + 1] = (float)(Math.Sin(2.0 * Math.PI * 660.0 * t) * rightAmp);
        }

        return new SynthesizedUnlockSoundPlayer.DecodedStereoWav(sampleRate, frameCount, interleaved);
    }

    private static byte[] BuildSyntheticStereoPcm16WavWithJunkChunk(int sampleRate, int frameCount, double leftAmp, double rightAmp)
    {
        const int channels = 2;
        const int bitsPerSample = 16;
        const int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int dataBytes = frameCount * blockAlign;
        const int junkPayloadBytes = 12;

        // RIFF(12) + fmt (24) + JUNK(8 + 12) + data(8 + dataBytes)
        int totalLen = 12 + 24 + (8 + junkPayloadBytes) + (8 + dataBytes);
        byte[] buf = new byte[totalLen];
        Span<byte> span = buf.AsSpan();

        "RIFF"u8.CopyTo(span[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)(totalLen - 8));
        "WAVE"u8.CopyTo(span.Slice(8, 4));

        "fmt "u8.CopyTo(span.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(34, 2), bitsPerSample);

        "JUNK"u8.CopyTo(span.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(40, 4), junkPayloadBytes);

        int dataHeaderOffset = 44 + junkPayloadBytes;
        "data"u8.CopyTo(span.Slice(dataHeaderOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(dataHeaderOffset + 4, 4), (uint)dataBytes);

        Span<byte> pcm = span.Slice(dataHeaderOffset + 8, dataBytes);
        for (int i = 0; i < frameCount; i++)
        {
            double t = (double)i / sampleRate;
            short sL = (short)Math.Round(Math.Sin(2.0 * Math.PI * 440.0 * t) * leftAmp * 32767.0);
            short sR = (short)Math.Round(Math.Sin(2.0 * Math.PI * 880.0 * t) * rightAmp * 32767.0);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 4, 2), sL);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 4 + 2, 2), sR);
        }

        return buf;
    }

    [Fact]
    public async Task M3A22_SettingsUpdatesCategory_BannerTrayAndUpdateViewModelStateTransitions_WorkHeadlessly()
    {
        // 1. XAML structural gate: Settings '更新' category, update-ready banner, dev notice, toggles, single primary button, history list.
        string repoRoot = LocateRepositoryRoot();
        string mainWindowXaml = File.ReadAllText(
            Path.Combine(repoRoot, "src", "Spawn.Host", "Views", "MainWindow.xaml"),
            Encoding.UTF8);

        Assert.Contains("SettingsCategory.Updates", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Updates", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("UpdateReadyBanner", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Update.ShowReadyBanner", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Update.ReadyBannerText", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Update.RestartAndUpdateCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Update.PrimaryActionCommand", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Update.DevChannelNoticeText", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Update.AutoCheckEnabled", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Update.AutoInstallWhenIdle", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Update.HistoryItems", mainWindowXaml, StringComparison.Ordinal);

        // 2. SettingsViewModel category title & dev-channel notice (zero HTTP).
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-w12-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            var fakeDevSvc = new FakeUpdateService(new UpdateStateSnapshot
            {
                IsUpdateSupported = false,
                CurrentVersion = "0.1.2-dev.202611220000",
                Channel = "dev",
                CommitSha = "1234567890abcdef",
                State = UpdateServiceState.DisabledDevChannel,
                Settings = new UpdateSettingsDto(),
                StatusMessage = UpdateService.DevChannelStatusMessage,
                History = [],
            });

            using var devUpdateVm = new UpdateViewModel(fakeDevSvc, _ => { });
            var settingsVm = new SettingsViewModel(
                paths,
                db,
                new SteamApiKeyProvider(paths, allowLocalDevFallback: false),
                new OverlayIpcClient(paths),
                new AntiCheatGuardService(
                    residualProcessProbe: static () => [],
                    highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], [])),
                new ThemeManagerService(),
                updateViewModel: devUpdateVm);

            settingsVm.SelectedCategory = SettingsCategory.Updates;
            Assert.Equal("更新", settingsVm.CurrentCategoryTitle);
            Assert.True(settingsVm.Update.IsDevOrSourceChannel);
            Assert.Equal("开发版由本机打包自动重装", settingsVm.Update.DevChannelNoticeText);
            Assert.False(settingsVm.Update.CanExecutePrimaryAction);
            Assert.False(settingsVm.Update.ShowReadyBanner);
            Assert.Equal("1234567", settingsVm.Update.CommitShortShaText);

            await settingsVm.Update.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Equal(0, fakeDevSvc.CheckCalls);

            // 3. Release-channel UpdateViewModel transitions: Idle -> ReadyToInstall -> Banner Dismiss -> New Version Re-shows -> Restart action.
            var releaseSvc = new FakeUpdateService(new UpdateStateSnapshot
            {
                IsUpdateSupported = true,
                CurrentVersion = "0.1.1",
                Channel = "release",
                CommitSha = "abcdef1234567890",
                State = UpdateServiceState.Idle,
                Settings = new UpdateSettingsDto { AutoCheckEnabled = true, AutoInstallWhenIdle = true },
                StatusMessage = "等待检查更新",
                History = [],
            });

            using var relVm = new UpdateViewModel(releaseSvc, _ => { });
            Assert.True(relVm.IsUpdateSupported);
            Assert.True(relVm.CanExecutePrimaryAction);
            Assert.Equal("检查更新", relVm.PrimaryActionButtonText);
            Assert.False(relVm.ShowReadyBanner);

            // Transition to ReadyToInstall with history row.
            releaseSvc.Emit(releaseSvc.CurrentSnapshot with
            {
                State = UpdateServiceState.ReadyToInstall,
                StatusMessage = "新版本 v0.1.2 已就绪，重启更新",
                LastCheckUtc = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero),
                NextCheckUtc = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
                ReadyUpdate = new PreparedUpdateDto
                {
                    Version = "0.1.2",
                    InstallerName = "spawn-setup-0.1.2-x64.exe",
                    InstallerPath = Path.Combine(tempDir, "spawn-setup-0.1.2-x64.exe"),
                    InstallerSize = 1024,
                    ExpectedSha256 = new string('a', 64),
                    PublishedUtc = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero),
                    NotesUrl = "https://github.com/chialecode/spawn/releases/tag/v0.1.2",
                    PreparedAtUtc = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero),
                },
                History =
                [
                    new UpdateHistoryEntryDto
                    {
                        TimestampUtc = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero),
                        FromVersion = "0.1.1",
                        ToVersion = "0.1.2",
                        Trigger = UpdateTriggerKind.Auto,
                        Result = UpdateHistoryResult.Downloaded,
                        Detail = "已下载验签",
                    },
                ],
            });

            Assert.True(relVm.HasReadyUpdate);
            Assert.True(relVm.ShowReadyBanner);
            Assert.Contains("新版本已就绪，重启更新", relVm.ReadyBannerText, StringComparison.Ordinal);
            Assert.Equal("立即重启更新", relVm.PrimaryActionButtonText);
            Assert.Single(relVm.HistoryItems);
            Assert.Equal("v0.1.1 → v0.1.2", relVm.HistoryItems[0].VersionTransitionText);

            // Dismiss hides banner while keeping ReadyUpdate button active in Settings.
            relVm.DismissReadyBannerCommand.Execute(null);
            Assert.False(relVm.ShowReadyBanner);
            Assert.True(relVm.HasReadyUpdate);

            // Primary action invokes InstallReadyUpdateAsync when ready.
            await relVm.PrimaryActionCommand.ExecuteAsync(null);
            Assert.Equal(1, releaseSvc.InstallCalls);

            // Toggling AutoInstallWhenIdle persists notify-only mode via SaveSettingsAsync.
            relVm.AutoInstallWhenIdle = false;
            Assert.NotNull(releaseSvc.LastSavedSettings);
            Assert.False(releaseSvc.LastSavedSettings!.AutoInstallWhenIdle);
            Assert.Contains("仅提醒", relVm.InstallPolicyDescriptionText, StringComparison.Ordinal);

            // 4. Headless TrayIconService tracks ready version & notifications with zero Win32 popups.
            using var tray = new TrayIconService();
            tray.SetReadyUpdateVersion("0.1.2");
            Assert.Equal("0.1.2", tray.ReadyUpdateVersion);
            tray.ShowNotification("SPAWN 新版本已就绪", "正式版 v0.1.2 已就绪，重启更新");
            Assert.Equal(1, tray.NotificationCount);
            Assert.Equal("SPAWN 新版本已就绪", tray.LastNotificationTitle);
            Assert.Contains("0.1.2", tray.LastNotificationMessage ?? string.Empty, StringComparison.Ordinal);

            // 5. F-02 lifecycle invariant: PrepareGracefulShutdownForUpdateAsync keeps _singleInstance alive until
            //    CompleteDeferredShutdownCoreAsync() so AbortDeferredShutdownCoreAsync() never loses single-instance ownership.
            string appCs = File.ReadAllText(Path.Combine(repoRoot, "src", "Spawn.Host", "App.xaml.cs"), Encoding.UTF8);
            int prepIdx = appCs.IndexOf("private async Task PrepareGracefulShutdownForUpdateAsync", StringComparison.Ordinal);
            int completeIdx = appCs.IndexOf("private async Task CompleteDeferredShutdownCoreAsync", StringComparison.Ordinal);
            int abortIdx = appCs.IndexOf("private async Task AbortDeferredShutdownCoreAsync", StringComparison.Ordinal);
            Assert.True(prepIdx > 0 && completeIdx > prepIdx && abortIdx > completeIdx);
            string prepBody = appCs[prepIdx..completeIdx];
            string completeBody = appCs[completeIdx..abortIdx];
            Assert.DoesNotContain("_singleInstance.DisposeAsync", prepBody, StringComparison.Ordinal);
            Assert.Contains("_singleInstance.DisposeAsync", completeBody, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public async Task F01_UpdateViewModelCommands_ServiceExceptions_SurfaceAsFeedbackAndNeverEscape()
    {
        var svc = new FakeUpdateService(new UpdateStateSnapshot
        {
            IsUpdateSupported = true,
            CurrentVersion = "0.1.1",
            Channel = "release",
            CommitSha = "abcdef1234567890",
            State = UpdateServiceState.Idle,
            Settings = new UpdateSettingsDto(),
            StatusMessage = "等待检查更新",
            History = [],
        })
        {
            CheckException = new InvalidOperationException("模拟服务异常"),
            InstallException = new IOException("模拟安装异常"),
        };

        using var vm = new UpdateViewModel(svc, _ => { });
        var feedback = new List<string>();
        vm.StatusFeedbackRequested += feedback.Add;

        // Generated AsyncRelayCommand awaits the method: any escaping exception would fault ExecuteAsync.
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        Assert.Equal(2, svc.CheckCalls);
        Assert.Equal(2, feedback.Count);
        Assert.All(feedback, f => Assert.Contains("检查更新失败：模拟服务异常", f, StringComparison.Ordinal));

        svc.CheckException = new OperationCanceledException();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Contains("已取消", feedback[^1], StringComparison.Ordinal);

        // Ready update -> RestartAndUpdate path with a throwing InstallReadyUpdateAsync.
        svc.Emit(svc.CurrentSnapshot with
        {
            State = UpdateServiceState.ReadyToInstall,
            ReadyUpdate = new PreparedUpdateDto
            {
                Version = "0.1.2",
                InstallerName = "spawn-setup-0.1.2-x64.exe",
                InstallerPath = Path.Combine(Path.GetTempPath(), "spawn-setup-0.1.2-x64.exe"),
                InstallerSize = 1024,
                ExpectedSha256 = new string('a', 64),
                PublishedUtc = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero),
                PreparedAtUtc = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero),
            },
        });

        await vm.RestartAndUpdateCommand.ExecuteAsync(null);
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        Assert.Equal(2, svc.InstallCalls);
        Assert.Contains("重启更新失败：模拟安装异常", feedback[^1], StringComparison.Ordinal);
        Assert.Contains("重启更新失败：模拟安装异常", feedback[^2], StringComparison.Ordinal);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateStateSnapshot CurrentSnapshot { get; private set; }
        public int CheckCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public UpdateSettingsDto? LastSavedSettings { get; private set; }

        public event EventHandler<UpdateStateSnapshot>? StateChanged;
        public event EventHandler<UpdateUserNotificationEvent>? UserNotificationRequested
        {
            add { }
            remove { }
        }

        public FakeUpdateService(UpdateStateSnapshot initial) => CurrentSnapshot = initial;

        public void Emit(UpdateStateSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            StateChanged?.Invoke(this, snapshot);
        }

        public Task InitializeAsync(bool launchedAfterUpdate = false, bool consumedValidUpdateMarker = false, string? previousVersionFromMarker = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void StartBackgroundScheduler() { }
        public Task StopBackgroundSchedulerAsync() => Task.CompletedTask;

        public Task<UpdateSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentSnapshot.Settings);

        public Task SaveSettingsAsync(UpdateSettingsDto settings, CancellationToken cancellationToken = default)
        {
            LastSavedSettings = settings;
            CurrentSnapshot = CurrentSnapshot with { Settings = settings };
            return Task.CompletedTask;
        }

        public Exception? CheckException { get; set; }
        public Exception? InstallException { get; set; }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateTriggerKind trigger, CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            await Task.Yield();
            if (CheckException is not null)
            {
                throw CheckException;
            }

            return new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpToDate, Message = "ok" };
        }

        public Task<UpdateInstallResult> TryAutoInstallIfEligibleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UpdateInstallResult { Succeeded = false, Message = "stub" });

        public async Task<UpdateInstallResult> InstallReadyUpdateAsync(UpdateTriggerKind trigger = UpdateTriggerKind.Manual, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            await Task.Yield();
            if (InstallException is not null)
            {
                throw InstallException;
            }

            return new UpdateInstallResult { Succeeded = true, Message = "launching" };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public async Task LibraryScan_EmptyLibraryIncrementalDiscoveryAndMidScanUserSelection_NeverJumpsSelectedGameOrRebuildsDetailRows()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-scan-sel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            string scanRoot = Path.Combine(tempDir, "ScanRoot");
            Directory.CreateDirectory(scanRoot);

            // Discovered in reverse alphabetical order so each newly scanned game inserts ABOVE earlier games in LibraryGroups.
            var plannedGames = new[]
            {
                CreateSyntheticScanGame("game-echo", "Echo Sanctuary", scanRoot, PlatformSource.Steamrip),
                CreateSyntheticScanGame("game-delta", "Delta Horizon", scanRoot, PlatformSource.GogOffline),
                CreateSyntheticScanGame("game-alpha", "Alpha Quest", scanRoot, PlatformSource.Steamrip),
                CreateSyntheticScanGame("game-bravo", "Bravo Odyssey", scanRoot, PlatformSource.GogOffline),
            };

            var eventBus = new InMemorySpawnEventBus();
            var gatedLibrary = new GatedIncrementalGameLibraryService(db, eventBus, scanRoot, plannedGames);
            var gatedAchievements = new ConfigurableFakeAchievementService();
            foreach (var g in plannedGames)
            {
                gatedAchievements.SetSummary(g.GameId,
                [
                    new GameAchievementItemDto
                    {
                        Definition = new AchievementDefinitionDto
                        {
                            ApiName = $"ACH_{g.GameId}_1",
                            DisplayName = $"{g.Title} Secret",
                            Description = $"Hidden condition for {g.Title}",
                            IsHidden = true,
                        },
                    },
                ]);
            }

            var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
            await using var ipcClient = new OverlayIpcClient(paths);
            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            var themeMgr = new ThemeManagerService();
            var fakeLaunch = new FakeGameLaunchService();
            var fakeGameData = new FakeGameDataManagementService();
            var fakeGate = new FakeTrainerSafetyGate();
            var fakeCatalog = new FakeTrainerCatalogService();
            var fakeRuntime = new FakeTrainerRuntimeService();
            var fakeSnapshots = new FakePreTrainerSnapshotService();

            var settingsVm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
            var trainerVm = new TrainerWorkbenchViewModel(
                paths, db, fakeCatalog, fakeRuntime, fakeSnapshots, fakeGate, guard, gatedLibrary, fakeLaunch, static _ => { });
            using var mainVm = new MainWindowViewModel(
                paths, db, gatedLibrary, fakeLaunch, gatedAchievements, fakeGameData, guard, ipcClient,
                eventBus, fakeCatalog, fakeRuntime, fakeGate, settingsVm, trainerVm, static _ => { });

            await mainVm.InitializeAsync();
            await settingsVm.AddScanDirectoryPathAsync(scanRoot, triggerImmediateScan: false);

            Assert.Empty(mainVm.AllGames);
            Assert.Null(mainVm.SelectedGame);
            Assert.True(mainVm.ShowLibraryEmptyState);

            var selectionTransitions = new List<string?>();
            mainVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame))
                {
                    selectionTransitions.Add(mainVm.SelectedGame?.GameId);
                }
            };

            int allGamesResetCount = 0;
            int allGroupResetCount = 0;
            mainVm.AllGames.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    allGamesResetCount++;
                }
            };
            mainVm.LibraryGroups[0].Items.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    allGroupResetCount++;
                }
            };

            int filteredAchievementsResetCount = 0;
            mainVm.FilteredAchievements.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    filteredAchievementsResetCount++;
                }
            };

            // Start scanning the 4-game root directory asynchronously.
            Task scanTask = mainVm.ScanDefaultLibraryCommand.ExecuteAsync(null);

            // Step 1: Release game 1 (Echo Sanctuary) -> library was empty, so it auto-selects Echo Sanctuary once.
            gatedLibrary.AllowNextDiscovery(0);
            await gatedLibrary.WaitDiscoveryPublishedAsync(0).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(mainVm.SelectedGame);
            Assert.Equal("game-echo", mainVm.SelectedGame!.GameId);
            Assert.Equal(["game-echo"], selectionTransitions);
            Assert.Single(mainVm.FilteredAchievements);

            // User reveals hidden spoiler on Echo Sanctuary's achievement row; unrelated scan updates must NOT reset it.
            mainVm.FilteredAchievements[0].RevealRowSpoilerCommand.Execute(null);
            Assert.False(mainVm.FilteredAchievements[0].CanClickToRevealSpoiler);
            int resetsAfterEchoLoaded = filteredAchievementsResetCount;

            // Step 2: Release game 2 (Delta Horizon, sorts BEFORE Echo Sanctuary).
            // SelectedGame MUST stay Echo Sanctuary with zero jump and zero FilteredAchievements reset!
            gatedLibrary.AllowNextDiscovery(1);
            await gatedLibrary.WaitDiscoveryPublishedAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["game-delta", "game-echo"], mainVm.LibraryGroups[0].Items.Select(x => x.GameId).ToArray());
            Assert.Equal("game-echo", mainVm.SelectedGame.GameId);
            Assert.Equal(["game-echo"], selectionTransitions);
            Assert.Equal(resetsAfterEchoLoaded, filteredAchievementsResetCount);
            Assert.False(mainVm.FilteredAchievements[0].CanClickToRevealSpoiler);

            // Step 3: User clicks newly discovered Delta Horizon mid-scan!
            var deltaVm = mainVm.AllGames.Single(g => g.GameId == "game-delta");
            mainVm.SelectLibraryGameCommand.Execute(deltaVm);

            Assert.Same(deltaVm, mainVm.SelectedGame);
            Assert.Equal(["game-echo", "game-delta"], selectionTransitions);
            int resetsAfterDeltaSelected = filteredAchievementsResetCount;

            // Step 4: Release games 3 & 4 (Alpha Quest, Bravo Odyssey — both insert ABOVE Delta Horizon!)
            // plus background AchievementSchemaUpdatedEvent for Alpha & Bravo, then let scan complete.
            gatedLibrary.AllowNextDiscovery(2);
            await gatedLibrary.WaitDiscoveryPublishedAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
            await eventBus.PublishAsync(new AchievementSchemaUpdatedEvent(
                "game-alpha", 0, 1, AchievementCaptureMode.SteamEmulator, "Goldberg · 就绪", DateTimeOffset.UtcNow))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));

            gatedLibrary.AllowNextDiscovery(3);
            await gatedLibrary.WaitDiscoveryPublishedAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
            await scanTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                ["game-alpha", "game-bravo", "game-delta", "game-echo"],
                mainVm.LibraryGroups[0].Items.Select(x => x.GameId).ToArray());
            Assert.Same(deltaVm, mainVm.SelectedGame);
            Assert.True(deltaVm.IsSelected);
            Assert.All(mainVm.AllGames.Where(g => g.GameId != "game-delta"), g => Assert.False(g.IsSelected));

            // Strictly 2 transitions across the entire 4-game scan: initial auto-select (Echo) + user click (Delta).
            Assert.Equal(["game-echo", "game-delta"], selectionTransitions);
            Assert.Equal(resetsAfterDeltaSelected, filteredAchievementsResetCount);
            Assert.Equal(0, allGamesResetCount);
            Assert.Equal(0, allGroupResetCount);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryReload_UserClicksGameDuringInFlightAsyncQuery_NeverRevertsToStaleSelection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-reload-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            string scanRoot = Path.Combine(tempDir, "Games");
            Directory.CreateDirectory(scanRoot);
            var gameA = CreateSyntheticScanGame("game-a", "Alpha Quest", scanRoot, PlatformSource.Steamrip);
            var gameB = CreateSyntheticScanGame("game-b", "Bravo Odyssey", scanRoot, PlatformSource.GogOffline);
            var gameC = CreateSyntheticScanGame("game-c", "Charlie Tactics", scanRoot, PlatformSource.Steamrip);
            var gameD = CreateSyntheticScanGame("game-d", "Delta Horizon", scanRoot, PlatformSource.GogOffline);

            await db.UpsertGameAsync(gameA);
            await db.UpsertGameAsync(gameB);

            var eventBus = new InMemorySpawnEventBus();
            var library = new FakeGameLibraryService(db);
            var gatedAchievements = new ConfigurableFakeAchievementService();
            var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
            await using var ipcClient = new OverlayIpcClient(paths);
            using var guard = new AntiCheatGuardService(
                residualProcessProbe: static () => [],
                highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
            var themeMgr = new ThemeManagerService();
            var fakeLaunch = new FakeGameLaunchService();
            var fakeGameData = new FakeGameDataManagementService();
            var fakeGate = new FakeTrainerSafetyGate();
            var fakeCatalog = new FakeTrainerCatalogService();
            var fakeRuntime = new FakeTrainerRuntimeService();
            var fakeSnapshots = new FakePreTrainerSnapshotService();

            var settingsVm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
            var trainerVm = new TrainerWorkbenchViewModel(
                paths, db, fakeCatalog, fakeRuntime, fakeSnapshots, fakeGate, guard, library, fakeLaunch, static _ => { });
            using var mainVm = new MainWindowViewModel(
                paths, db, library, fakeLaunch, gatedAchievements, fakeGameData, guard, ipcClient,
                eventBus, fakeCatalog, fakeRuntime, fakeGate, settingsVm, trainerVm, static _ => { });

            await mainVm.InitializeAsync();
            var vmA = mainVm.AllGames.Single(g => g.GameId == "game-a");
            var vmB = mainVm.AllGames.Single(g => g.GameId == "game-b");
            mainVm.SelectLibraryGameCommand.Execute(vmA);
            Assert.Same(vmA, mainVm.SelectedGame);

            var selectionTransitions = new List<string?>();
            mainVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame))
                {
                    selectionTransitions.Add(mainVm.SelectedGame?.GameId);
                }
            };

            // Pause inside GetGameAchievementsAsync during the reload triggered by gameC.
            var enteredQueryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseQueryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gatedAchievements.PauseOnGameId("game-c", enteredQueryTcs, releaseQueryTcs.Task);

            await db.UpsertGameAsync(gameC);
            Task publishCTask = eventBus.PublishAsync(new GameImportedOrRescannedEvent(gameC, IsNewImport: true, DateTimeOffset.UtcNow)).AsTask();
            await enteredQueryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Enqueue another scan event (gameD) while Pass #1 is suspended mid-await to exercise single-flight coalescing.
            await db.UpsertGameAsync(gameD);
            Task publishDTask = eventBus.PublishAsync(new GameImportedOrRescannedEvent(gameD, IsNewImport: true, DateTimeOffset.UtcNow)).AsTask();

            // User clicks Game B WHILE the reload for Game C is suspended mid-await (when SelectedGame had been Game A)!
            mainVm.SelectLibraryGameCommand.Execute(vmB);
            Assert.Same(vmB, mainVm.SelectedGame);

            // Release the paused async query and wait for both coalesced passes to complete.
            gatedAchievements.ClearPause();
            releaseQueryTcs.SetResult();
            await Task.WhenAll(publishCTask, publishDTask).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(4, mainVm.AllGames.Count);
            Assert.Same(vmB, mainVm.SelectedGame);
            Assert.Equal(["game-b"], selectionTransitions);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task LibraryImport_SingleGameVsMultiGameBatchAndStaDispatcher_RespectsSelectionPrecedence()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-import-sta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            string batchFolder = Path.Combine(tempDir, "BatchFolder");
            Directory.CreateDirectory(batchFolder);
            string singleDirE = Path.Combine(tempDir, "SingleE");
            Directory.CreateDirectory(singleDirE);
            string singleDirF = Path.Combine(tempDir, "SingleF");
            Directory.CreateDirectory(singleDirF);

            var gameA = CreateSyntheticScanGame("game-a", "Alpha Quest", tempDir, PlatformSource.Steamrip);
            var gameB = CreateSyntheticScanGame("game-b", "Bravo Odyssey", batchFolder, PlatformSource.Steamrip);
            var gameC = CreateSyntheticScanGame("game-c", "Charlie Tactics", batchFolder, PlatformSource.GogOffline);
            var gameE = new GameRecord
            {
                GameId = "game-e",
                Title = "Echo Sanctuary",
                InstallDir = singleDirE,
                ExecutableRelPath = "echo.exe",
                PlatformSource = PlatformSource.Standalone,
                AntiCheatTier = AntiCheatTier.Tier0,
            };
            var gameF = new GameRecord
            {
                GameId = "game-f",
                Title = "Fable Frontier",
                InstallDir = singleDirF,
                ExecutableRelPath = "fable.exe",
                PlatformSource = PlatformSource.Standalone,
                AntiCheatTier = AntiCheatTier.Tier0,
            };

            await db.UpsertGameAsync(gameA);

            // Run on a dedicated STA thread with a real WPF Dispatcher message loop to verify Dispatcher.InvokeAsync marshaling.
            ExceptionDispatchInfo? capturedError = null;
            var staThread = new Thread(() =>
            {
                var staDispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                _ = staDispatcher.InvokeAsync(() => _ = ExecuteOnStaAsync());
                Dispatcher.PushFrame(frame);
                staDispatcher.InvokeShutdown();

                async Task ExecuteOnStaAsync()
                {
                    try
                    {
                        var eventBus = new InMemorySpawnEventBus();
                        var library = new ScriptedImportAndScanLibraryService(
                            db,
                            eventBus,
                            directImportsByPath: new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase)
                            {
                                [Path.GetFullPath(singleDirE)] = gameE,
                                [Path.GetFullPath(singleDirF)] = gameF,
                            },
                            batchScansByRoot: new Dictionary<string, IReadOnlyList<GameRecord>>(StringComparer.OrdinalIgnoreCase)
                            {
                                [Path.GetFullPath(batchFolder)] = [gameB, gameC],
                            });

                        var achievements = new ConfigurableFakeAchievementService();
                        var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
                        await using var ipcClient = new OverlayIpcClient(paths);
                        using var guard = new AntiCheatGuardService(
                            residualProcessProbe: static () => [],
                            highRiskEnvironmentProbe: static () => new HighRiskEnvironmentReport([], []));
                        var themeMgr = new ThemeManagerService();
                        var fakeLaunch = new FakeGameLaunchService();
                        var fakeGameData = new FakeGameDataManagementService();
                        var fakeGate = new FakeTrainerSafetyGate();
                        var fakeCatalog = new FakeTrainerCatalogService();
                        var fakeRuntime = new FakeTrainerRuntimeService();
                        var fakeSnapshots = new FakePreTrainerSnapshotService();

                        var settingsVm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
                        var trainerVm = new TrainerWorkbenchViewModel(
                            paths, db, fakeCatalog, fakeRuntime, fakeSnapshots, fakeGate, guard, library, fakeLaunch, static _ => { });
                        using var mainVm = new MainWindowViewModel(
                            paths, db, library, fakeLaunch, achievements, fakeGameData, guard, ipcClient,
                            eventBus, fakeCatalog, fakeRuntime, fakeGate, settingsVm, trainerVm, static _ => { },
                            uiDispatcher: staDispatcher);

                        await mainVm.InitializeAsync();
                        var vmA = mainVm.AllGames.Single(g => g.GameId == "game-a");
                        mainVm.SelectLibraryGameCommand.Execute(vmA);

                        var transitions = new List<string?>();
                        mainVm.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame))
                            {
                                transitions.Add(mainVm.SelectedGame?.GameId);
                            }
                        };

                        // 1. Dropping a multi-game parent folder while Game A is selected MUST keep Game A selected (0 transitions).
                        await mainVm.ImportDroppedPathsAsync([batchFolder]);
                        await staDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                        Assert.Equal(3, mainVm.AllGames.Count);
                        Assert.Same(vmA, mainVm.SelectedGame);
                        Assert.Empty(transitions);

                        // 2. Manual single-game import of Game E from Settings focuses Game E cleanly in 1 transition.
                        await mainVm.ImportManualGameFromSettingsAsync(singleDirE);
                        await staDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                        Assert.Equal("game-e", mainVm.SelectedGame?.GameId);
                        Assert.Equal(["game-e"], transitions);

                        // 3. Single-game drag-drop of Game F while user clicks Game B mid-import: user click wins!
                        var enteredImportFTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var releaseImportFTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        library.PauseDirectImport(Path.GetFullPath(singleDirF), enteredImportFTcs, releaseImportFTcs.Task);

                        Task dropFTask = mainVm.ImportDroppedPathsAsync([singleDirF]);
                        await enteredImportFTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                        var vmB = mainVm.AllGames.Single(g => g.GameId == "game-b");
                        mainVm.SelectLibraryGameCommand.Execute(vmB);
                        Assert.Same(vmB, mainVm.SelectedGame);

                        releaseImportFTcs.SetResult();
                        await dropFTask.WaitAsync(TimeSpan.FromSeconds(5));
                        await staDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task.WaitAsync(TimeSpan.FromSeconds(5));

                        Assert.Equal(5, mainVm.AllGames.Count);
                        Assert.Same(vmB, mainVm.SelectedGame);
                        Assert.Equal(["game-e", "game-b"], transitions);
                    }
                    catch (Exception ex)
                    {
                        capturedError = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();
            staThread.Join();

            capturedError?.Throw();
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static GameRecord CreateSyntheticScanGame(string gameId, string title, string parentRoot, PlatformSource source)
    {
        string installDir = Path.Combine(parentRoot, gameId);
        Directory.CreateDirectory(installDir);
        return new GameRecord
        {
            GameId = gameId,
            Title = title,
            SortTitle = title,
            InstallDir = installDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = source,
            AntiCheatTier = AntiCheatTier.Tier0,
        };
    }

    private sealed class GatedIncrementalGameLibraryService : IGameLibraryService
    {
        private readonly SpawnDatabase _db;
        private readonly ISpawnEventBus _eventBus;
        private readonly string _expectedRoot;
        private readonly IReadOnlyList<GameRecord> _games;
        private readonly TaskCompletionSource[] _allowStep;
        private readonly TaskCompletionSource[] _publishedStep;

        public GatedIncrementalGameLibraryService(
            SpawnDatabase db,
            ISpawnEventBus eventBus,
            string expectedRoot,
            IReadOnlyList<GameRecord> games)
        {
            _db = db;
            _eventBus = eventBus;
            _expectedRoot = Path.GetFullPath(expectedRoot);
            _games = games;
            _allowStep = Enumerable.Range(0, games.Count)
                .Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
            _publishedStep = Enumerable.Range(0, games.Count)
                .Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
        }

        public void AllowNextDiscovery(int index) => _allowStep[index].TrySetResult();
        public Task WaitDiscoveryPublishedAsync(int index) => _publishedStep[index].Task;

        public Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default) =>
            _db.GetAllGamesAsync(cancellationToken);

        public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) =>
            _db.GetGameByIdAsync(gameId, cancellationToken);

        public Task<GameRecord?> ImportGameAsync(string directoryOrExePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameRecord?>(null);

        public async Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(Path.GetFullPath(rootDirectory), _expectedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var discovered = new List<GameRecord>(_games.Count);
            for (int i = 0; i < _games.Count; i++)
            {
                await _allowStep[i].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                var game = _games[i];
                await _db.UpsertGameAsync(game, cancellationToken).ConfigureAwait(false);
                await _eventBus.PublishAsync(
                    new GameImportedOrRescannedEvent(game, IsNewImport: true, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
                discovered.Add(game);
                _publishedStep[i].TrySetResult();
            }

            return discovered;
        }

        public Task<GameRecord?> RescanGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            _db.GetGameByIdAsync(gameId, cancellationToken);

        public Task<GameRecord?> UpdateGameMetadataAsync(string gameId, GameMetadataUpdate update, CancellationToken cancellationToken = default) =>
            _db.GetGameByIdAsync(gameId, cancellationToken);

        public Task<bool> RemoveGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            _db.DeleteGameAsync(gameId, cancellationToken);
    }

    private sealed class ScriptedImportAndScanLibraryService(
        SpawnDatabase db,
        ISpawnEventBus eventBus,
        Dictionary<string, GameRecord> directImportsByPath,
        Dictionary<string, IReadOnlyList<GameRecord>> batchScansByRoot) : IGameLibraryService
    {
        private string? _pausedDirectPath;
        private TaskCompletionSource? _enteredPause;
        private Task? _releasePause;

        public void PauseDirectImport(string normalizedPath, TaskCompletionSource entered, Task release)
        {
            _pausedDirectPath = normalizedPath;
            _enteredPause = entered;
            _releasePause = release;
        }

        public Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default) =>
            db.GetAllGamesAsync(cancellationToken);

        public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) =>
            db.GetGameByIdAsync(gameId, cancellationToken);

        public async Task<GameRecord?> ImportGameAsync(string directoryOrExePath, CancellationToken cancellationToken = default)
        {
            string full = Path.GetFullPath(directoryOrExePath);
            if (!directImportsByPath.TryGetValue(full, out var record))
            {
                return null;
            }

            if (_pausedDirectPath is not null && string.Equals(_pausedDirectPath, full, StringComparison.OrdinalIgnoreCase))
            {
                _enteredPause?.TrySetResult();
                if (_releasePause is not null)
                {
                    await _releasePause.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await db.UpsertGameAsync(record, cancellationToken).ConfigureAwait(false);
            await eventBus.PublishAsync(
                new GameImportedOrRescannedEvent(record, IsNewImport: true, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return record;
        }

        public async Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(string rootDirectory, CancellationToken cancellationToken = default)
        {
            string full = Path.GetFullPath(rootDirectory);
            if (!batchScansByRoot.TryGetValue(full, out var batch))
            {
                return [];
            }

            var results = new List<GameRecord>(batch.Count);
            foreach (var rec in batch)
            {
                await Task.Run(async () =>
                {
                    await db.UpsertGameAsync(rec, cancellationToken).ConfigureAwait(false);
                    await eventBus.PublishAsync(
                        new GameImportedOrRescannedEvent(rec, IsNewImport: true, DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                results.Add(rec);
            }

            return results;
        }

        public Task<GameRecord?> RescanGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            db.GetGameByIdAsync(gameId, cancellationToken);

        public Task<GameRecord?> UpdateGameMetadataAsync(string gameId, GameMetadataUpdate update, CancellationToken cancellationToken = default) =>
            db.GetGameByIdAsync(gameId, cancellationToken);

        public Task<bool> RemoveGameAsync(string gameId, CancellationToken cancellationToken = default) =>
            db.DeleteGameAsync(gameId, cancellationToken);
    }

    private sealed class ConfigurableFakeAchievementService : IAchievementService
    {
        private readonly Dictionary<string, IReadOnlyList<GameAchievementItemDto>> _itemsByGame = new(StringComparer.OrdinalIgnoreCase);
        private string? _pauseGameId;
        private TaskCompletionSource? _enteredPause;
        private Task? _releasePause;

        public event EventHandler<AchievementUnlockedNotification>? AchievementUnlocked
        {
            add { }
            remove { }
        }

        public void SetSummary(string gameId, IReadOnlyList<GameAchievementItemDto> items) =>
            _itemsByGame[gameId] = items;

        public void PauseOnGameId(string gameId, TaskCompletionSource entered, Task release)
        {
            _pauseGameId = gameId;
            _enteredPause = entered;
            _releasePause = release;
        }

        public void ClearPause()
        {
            _pauseGameId = null;
            _enteredPause = null;
            _releasePause = null;
        }

        public async Task<GameAchievementSummaryDto> GetGameAchievementsAsync(string gameId, CancellationToken cancellationToken = default)
        {
            if (_pauseGameId is not null && string.Equals(_pauseGameId, gameId, StringComparison.OrdinalIgnoreCase))
            {
                _enteredPause?.TrySetResult();
                if (_releasePause is not null)
                {
                    await _releasePause.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _itemsByGame.TryGetValue(gameId, out var items);
            var list = items ?? [];
            return new GameAchievementSummaryDto
            {
                GameId = gameId,
                ChannelStatusText = "就绪",
                UnlockedCount = list.Count(static x => x.IsUnlocked),
                TotalCount = list.Count,
                Items = list,
            };
        }

        public Task<IReadOnlyList<AchievementUnlockDto>> GetRecentUnlocksAsync(int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AchievementUnlockDto>>([]);

        public Task<GameAchievementSummaryDto> SyncGameSchemaAsync(string gameId, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            GetGameAchievementsAsync(gameId, cancellationToken);

        public Task<int> ScanLocalUnlocksAsync(string gameId, bool silentSeed = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<bool> SetAchievementUnlockedAsync(string gameId, string apiName, bool unlocked, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<GbeForkActivationResult> ActivateGbeForkAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GbeForkActivationResult { Succeeded = false, Message = "stub" });

        public Task<GbeForkActivationResult> RestoreOriginalDllAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GbeForkActivationResult { Succeeded = false, Message = "stub" });

        public Task SendTestToastAsync(string? gameId = null, OverlayThemeKind? themeOverride = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
