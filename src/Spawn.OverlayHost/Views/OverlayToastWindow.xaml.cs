using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.OverlayHost.Audio;
using Spawn.OverlayHost.Interop;

namespace Spawn.OverlayHost.Views;

/// <summary>
/// Non-activating, click-through topmost window presenting Steam, PS5 Trophy, and Xbox Rare Diamond achievement toasts.
/// Calls WPF <see cref="Window.Show"/> (with <c>ShowActivated="False"</c>) so WPF's layered window renderer
/// commits real pixels via <c>UpdateLayeredWindow</c>, then <see cref="Window.Hide"/> when idle for 0% GPU usage.
/// </summary>
public partial class OverlayToastWindow : Window
{
    private readonly SynthesizedUnlockSoundPlayer _soundPlayer;
    private nint _hwnd;

    public nint WindowHandle => _hwnd;

    /// <summary>
    /// Exposes the inner visual stage for headless STA <see cref="RenderTargetBitmap"/> verification.
    /// </summary>
    public FrameworkElement VisualStage => RootStage;

    public OverlayToastWindow(SynthesizedUnlockSoundPlayer soundPlayer)
    {
        ArgumentNullException.ThrowIfNull(soundPlayer);
        _soundPlayer = soundPlayer;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void EnsureHandleCreatedHidden()
    {
        if (_hwnd != nint.Zero)
        {
            return;
        }

        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();
        OverlayNativeMethods.ApplyClickThroughNoActivateStyles(_hwnd);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;

        if (HwndSource.FromHwnd(_hwnd) is HwndSource source)
        {
            source.AddHook(WndProc);
        }

        OverlayNativeMethods.ApplyClickThroughNoActivateStyles(_hwnd);
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == OverlayNativeMethods.WmMouseActivate)
        {
            handled = true;
            return OverlayNativeMethods.MaNoActivate;
        }

        return nint.Zero;
    }

    /// <summary>
    /// Binds <paramref name="request"/> into the active theme card and sets an opaque neutral transform
    /// so headless STA tests can verify non-transparent rendered pixels without calling <see cref="Window.Show"/>.
    /// </summary>
    public void PrepareForHeadlessRender(OverlayToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        BitmapSource? icon = LoadBitmapOffUiThread(request.IconPath);
        BindToastVisualsCore(request, icon, keepHiddenForEntrance: false, slideStartOffsetY: 0.0);
    }

    public async Task PlayToastAsync(OverlayToastRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        int requestedMs = request.DisplayDurationMs ?? request.DurationMs;
        int holdMs = Math.Clamp(requestedMs, 1000, 12000);
        double slideStartOffsetY = request.Position is OverlayPositionKind.TopLeft or OverlayPositionKind.TopRight or OverlayPositionKind.TopCenter
            ? -22.0
            : 22.0;

        // Decode bitmap off the UI thread so large PNG/WebP icons never stall WPF composition or trigger extra PNG re-encodes.
        BitmapSource? preloadedIcon = await Task.Run(
            () => LoadBitmapOffUiThread(request.IconPath),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Start audio playback immediately so the 160ms DisplayPort/DAC sub-audible wake primer runs concurrently
        // with window Show() and the 240ms slide-in animation for zero-latency audio-visual sync.
        _soundPlayer.PlayForToast(request);

        await Dispatcher.InvokeAsync(() =>
        {
            // Keep RootStage.Opacity == 0 before Show() and SetWindowPos so the first layered frame never flashes opaque.
            BindToastVisualsCore(request, preloadedIcon, keepHiddenForEntrance: true, slideStartOffsetY);

            // Prime initial DIP position before Show() so the window enters on the target monitor.
            var (initialXPx, initialYPx) = ComputeTargetPhysicalPosition(request.Position);
            ApplyDipFallbackCoordinates(initialXPx, initialYPx);

            // CRITICAL: Must call WPF Window.Show() (with ShowActivated="False") so WPF runs Measure/Arrange/Render
            // and commits layered pixels via UpdateLayeredWindow!
            if (!IsVisible)
            {
                Show();
            }

            UpdateLayout();

            if (_hwnd == nint.Zero)
            {
                _hwnd = new WindowInteropHelper(this).Handle;
            }

            OverlayNativeMethods.ApplyClickThroughNoActivateStyles(_hwnd);

            // Recompute physical coordinates using actual post-DPI window pixel bounds so 125%/150%/200% 4K monitors
            // never clip or push the toast outside the visible work area (or fullscreen monitor bounds).
            var (finalXPx, finalYPx) = ComputeTargetPhysicalPosition(request.Position);
            OverlayNativeMethods.PositionAndBringTopmostNoActivate(_hwnd, finalXPx, finalYPx);

            // Second pass in case moving across mixed-DPI monitors triggered a PerMonitorV2 WM_DPICHANGED resize.
            var (verifiedXPx, verifiedYPx) = ComputeTargetPhysicalPosition(request.Position);
            if (verifiedXPx != finalXPx || verifiedYPx != finalYPx)
            {
                OverlayNativeMethods.PositionAndBringTopmostNoActivate(_hwnd, verifiedXPx, verifiedYPx);
            }

            RunEntranceAndShimmerAnimations(slideStartOffsetY, holdMs);
        });

        try
        {
            await Task.Delay(holdMs, cancellationToken).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() => RunExitAnimation(slideStartOffsetY));
            await Task.Delay(270, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Dispatcher.InvokeAsync(HideStageImmediately);
            throw;
        }

        await Dispatcher.InvokeAsync(HideStageImmediately);
    }

    public void BindToastVisuals(OverlayToastRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        BitmapSource? icon = LoadBitmapOffUiThread(req.IconPath);
        BindToastVisualsCore(req, icon, keepHiddenForEntrance: false, slideStartOffsetY: 0.0);
    }

    /// <summary>
    /// Prepares visual bindings while keeping <see cref="VisualStage"/> hidden at <c>Opacity = 0</c>
    /// for flicker-free entrance animation setup (also directly testable in headless STA tests).
    /// </summary>
    public void PrepareForEntranceBeforeAnimation(OverlayToastRequest req, BitmapSource? preloadedIcon = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        double slideStartOffsetY = req.Position is OverlayPositionKind.TopLeft or OverlayPositionKind.TopRight or OverlayPositionKind.TopCenter
            ? -22.0
            : 22.0;
        BindToastVisualsCore(req, preloadedIcon ?? LoadBitmapOffUiThread(req.IconPath), keepHiddenForEntrance: true, slideStartOffsetY);
    }

    private void BindToastVisualsCore(
        OverlayToastRequest req,
        BitmapSource? icon,
        bool keepHiddenForEntrance,
        double slideStartOffsetY)
    {
        SteamCard.Visibility = req.Theme == OverlayThemeKind.Steam ? Visibility.Visible : Visibility.Collapsed;
        Ps5Card.Visibility = req.Theme == OverlayThemeKind.Ps5Trophy ? Visibility.Visible : Visibility.Collapsed;
        XboxCard.Visibility = req.Theme == OverlayThemeKind.XboxRare ? Visibility.Visible : Visibility.Collapsed;

        string desc = string.IsNullOrWhiteSpace(req.Description)
            ? $"来自《{req.GameTitle}》"
            : req.Description;

        switch (req.Theme)
        {
            case OverlayThemeKind.Ps5Trophy:
                ConfigurePs5Visuals(req, icon, desc);
                break;

            case OverlayThemeKind.XboxRare:
                ConfigureXboxVisuals(req, icon, desc);
                break;

            default:
                ConfigureSteamVisuals(req, icon, desc);
                break;
        }

        RootStage.BeginAnimation(OpacityProperty, null);
        StageTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (keepHiddenForEntrance)
        {
            RootStage.Opacity = 0.0;
            StageTranslate.Y = slideStartOffsetY;
            StageScale.ScaleX = 0.96;
            StageScale.ScaleY = 0.96;
        }
        else
        {
            // Headless static renders require opaque neutral transforms.
            RootStage.Opacity = 1.0;
            StageTranslate.Y = 0.0;
            StageScale.ScaleX = 1.0;
            StageScale.ScaleY = 1.0;
        }
    }

    private void HideStageImmediately()
    {
        RootStage.BeginAnimation(OpacityProperty, null);
        StageTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ShimmerBand.BeginAnimation(OpacityProperty, null);
        ShimmerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        SteamProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootStage.Opacity = 0.0;
        if (IsVisible)
        {
            Hide();
        }
    }

    private void ConfigureSteamVisuals(OverlayToastRequest req, BitmapSource? icon, string desc)
    {
        SteamHeaderText.Text = string.IsNullOrWhiteSpace(req.GameTitle)
            ? "成就已解锁"
            : $"成就已解锁 · {req.GameTitle}";
        SteamTitleText.Text = req.DisplayName;
        SteamDescText.Text = desc;

        bool isRare = req.IsRare || req.GlobalPercent is < 10.0;
        SteamRareBadge.Visibility = isRare ? Visibility.Visible : Visibility.Collapsed;
        if (isRare)
        {
            SteamRareText.Text = req.GlobalPercent.HasValue
                ? $"稀有 {req.GlobalPercent.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
                : "稀有成就";
        }

        SteamIconImage.Source = icon;
        SteamIconImage.Visibility = icon is not null ? Visibility.Visible : Visibility.Collapsed;
        SteamFallbackGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigurePs5Visuals(OverlayToastRequest req, BitmapSource? icon, string desc)
    {
        Color accentColor;
        string gradeText;

        if (req.IsPlatinumCompletion)
        {
            accentColor = Color.FromRgb(0x93, 0xC5, 0xFD);
            gradeText = "白金奖杯 · 100% 全收集达成";
            Ps5BgStop1.Color = Color.FromArgb(0xF6, 0x11, 0x18, 0x2B);
            Ps5BgStop2.Color = Color.FromArgb(0xF8, 0x1E, 0x29, 0x4B);
            Ps5BgStop3.Color = Color.FromArgb(0xF6, 0x31, 0x2E, 0x61);
            Ps5Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x93, 0xC5, 0xFD));
        }
        else if (req.IsRare || req.GlobalPercent is < 10.0)
        {
            accentColor = Color.FromRgb(0xFB, 0xBF, 0x24);
            gradeText = $"已获得奖杯 · 金杯 ({req.GameTitle})";
            Ps5BgStop1.Color = Color.FromArgb(0xF4, 0x14, 0x15, 0x1E);
            Ps5BgStop2.Color = Color.FromArgb(0xF6, 0x22, 0x1E, 0x28);
            Ps5BgStop3.Color = Color.FromArgb(0xF4, 0x2D, 0x25, 0x1C);
            Ps5Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFB, 0xBF, 0x24));
        }
        else if (req.GlobalPercent is < 30.0)
        {
            accentColor = Color.FromRgb(0xE2, 0xE8, 0xF0);
            gradeText = $"已获得奖杯 · 银杯 ({req.GameTitle})";
            Ps5BgStop1.Color = Color.FromArgb(0xF4, 0x12, 0x15, 0x1E);
            Ps5BgStop2.Color = Color.FromArgb(0xF6, 0x1B, 0x20, 0x2E);
            Ps5BgStop3.Color = Color.FromArgb(0xF4, 0x23, 0x29, 0x3A);
            Ps5Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xE2, 0xE8, 0xF0));
        }
        else
        {
            accentColor = Color.FromRgb(0xEA, 0x9A, 0x56);
            gradeText = $"已获得奖杯 · 铜杯 ({req.GameTitle})";
            Ps5BgStop1.Color = Color.FromArgb(0xF4, 0x12, 0x15, 0x1E);
            Ps5BgStop2.Color = Color.FromArgb(0xF6, 0x1E, 0x1B, 0x24);
            Ps5BgStop3.Color = Color.FromArgb(0xF4, 0x28, 0x20, 0x24);
            Ps5Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xEA, 0x9A, 0x56));
        }

        var solidAccent = new SolidColorBrush(accentColor);
        Ps5GradeLabel.Text = gradeText;
        Ps5GradeLabel.Foreground = solidAccent;
        Ps5TitleText.Text = req.DisplayName;
        Ps5DescText.Text = desc;

        Ps5CrestPath.Fill = solidAccent;
        Ps5FallbackGlyph.Fill = solidAccent;
        Ps5CrestBadge.Background = new SolidColorBrush(Color.FromArgb(0x28, accentColor.R, accentColor.G, accentColor.B));
        Ps5CrestBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x78, accentColor.R, accentColor.G, accentColor.B));

        bool showCustomIcon = icon is not null && !req.IsPlatinumCompletion;
        Ps5IconImage.Source = showCustomIcon ? icon : null;
        Ps5IconImage.Visibility = showCustomIcon ? Visibility.Visible : Visibility.Collapsed;
        Ps5FallbackGlyph.Visibility = showCustomIcon ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConfigureXboxVisuals(OverlayToastRequest req, BitmapSource? icon, string desc)
    {
        bool isRare = req.IsRare || req.GlobalPercent is < 10.0;
        if (isRare)
        {
            XboxHeaderText.Text = string.IsNullOrWhiteSpace(req.GameTitle)
                ? "稀有成就已解锁"
                : $"稀有成就已解锁 · {req.GameTitle}";
            XboxHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
            XboxDescText.Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xD5, 0xFF));
            XboxCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xA8, 0x55, 0xF7));
            XboxIconBorder.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0x8B, 0x5C, 0xF6));
            XboxIconBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xA8, 0x55, 0xF7));
            XboxFallbackGlyph.Fill = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
            XboxBgStop1.Color = Color.FromArgb(0xF5, 0x13, 0x11, 0x1E);
            XboxBgStop2.Color = Color.FromArgb(0xF6, 0x22, 0x15, 0x3B);
            XboxBgStop3.Color = Color.FromArgb(0xF5, 0x18, 0x1E, 0x34);

            XboxDiamondBadge.Visibility = Visibility.Visible;
            XboxPercentText.Text = req.GlobalPercent.HasValue
                ? $"{req.GlobalPercent.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
                : "稀有";
        }
        else
        {
            XboxHeaderText.Text = string.IsNullOrWhiteSpace(req.GameTitle)
                ? "成就已解锁"
                : $"成就已解锁 · {req.GameTitle}";
            XboxHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            XboxDescText.Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xF7, 0xD0));
            XboxCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x78, 0x10, 0xB9, 0x81));
            XboxIconBorder.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x10, 0xB9, 0x81));
            XboxIconBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0x34, 0xD3, 0x99));
            XboxFallbackGlyph.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            XboxBgStop1.Color = Color.FromArgb(0xF5, 0x0F, 0x19, 0x16);
            XboxBgStop2.Color = Color.FromArgb(0xF6, 0x11, 0x29, 0x21);
            XboxBgStop3.Color = Color.FromArgb(0xF5, 0x12, 0x21, 0x26);

            // Standard Xbox achievements stay strictly in the green palette with no purple diamond pill.
            XboxDiamondBadge.Visibility = Visibility.Collapsed;
        }

        XboxTitleText.Text = req.DisplayName;
        XboxDescText.Text = desc;

        XboxIconImage.Source = icon;
        XboxIconImage.Visibility = icon is not null ? Visibility.Visible : Visibility.Collapsed;
        XboxFallbackGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private (int TargetXPx, int TargetYPx) ComputeTargetPhysicalPosition(OverlayPositionKind position)
    {
        var workArea = OverlayNativeMethods.GetActiveMonitorWorkArea(_hwnd);
        double monitorScaleX = workArea.DpiX >= 72 ? workArea.DpiX / 96.0 : 1.0;
        double monitorScaleY = workArea.DpiY >= 72 ? workArea.DpiY / 96.0 : 1.0;

        int winWidthPx;
        int winHeightPx;

        var actualRect = OverlayNativeMethods.TryGetPhysicalWindowRect(_hwnd);
        if (actualRect is { WidthPx: >= 100, HeightPx: >= 40 } rect)
        {
            winWidthPx = rect.WidthPx;
            winHeightPx = rect.HeightPx;
        }
        else
        {
            double dipW = ActualWidth > 10 ? ActualWidth : Width;
            double dipH = ActualHeight > 10 ? ActualHeight : Height;
            winWidthPx = (int)Math.Round(dipW * monitorScaleX);
            winHeightPx = (int)Math.Round(dipH * monitorScaleY);
        }

        int marginXPx = (int)Math.Round(18.0 * monitorScaleX);
        int marginYPx = (int)Math.Round(18.0 * monitorScaleY);

        int rawX = position switch
        {
            OverlayPositionKind.BottomLeft or OverlayPositionKind.TopLeft => workArea.LeftPx + marginXPx,
            OverlayPositionKind.TopCenter or OverlayPositionKind.BottomCenter => workArea.LeftPx + (workArea.WidthPx - winWidthPx) / 2,
            _ => workArea.LeftPx + workArea.WidthPx - winWidthPx - marginXPx,
        };

        int rawY = position switch
        {
            OverlayPositionKind.TopRight or OverlayPositionKind.TopLeft or OverlayPositionKind.TopCenter => workArea.TopPx + marginYPx,
            _ => workArea.TopPx + workArea.HeightPx - winHeightPx - marginYPx,
        };

        int minX = workArea.LeftPx + 4;
        int maxX = Math.Max(minX, workArea.LeftPx + workArea.WidthPx - winWidthPx - 4);
        int minY = workArea.TopPx + 4;
        int maxY = Math.Max(minY, workArea.TopPx + workArea.HeightPx - winHeightPx - 4);

        return (Math.Clamp(rawX, minX, maxX), Math.Clamp(rawY, minY, maxY));
    }

    private void ApplyDipFallbackCoordinates(int physicalX, int physicalY)
    {
        uint windowDpi = OverlayNativeMethods.GetEffectiveWindowDpi(_hwnd);
        DpiScale wpfDpi = VisualTreeHelper.GetDpi(this);
        double scaleX = windowDpi >= 72 ? windowDpi / 96.0 : (wpfDpi.DpiScaleX > 0 ? wpfDpi.DpiScaleX : 1.0);
        double scaleY = windowDpi >= 72 ? windowDpi / 96.0 : (wpfDpi.DpiScaleY > 0 ? wpfDpi.DpiScaleY : 1.0);

        Left = physicalX / scaleX;
        Top = physicalY / scaleY;
    }

    private void RunEntranceAndShimmerAnimations(double slideStartOffsetY, int holdMs)
    {
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        RootStage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = easeOut,
        });

        StageTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slideStartOffsetY, 0.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easeOut,
        });

        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easeOut,
        });
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easeOut,
        });

        SteamProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(holdMs)));

        ShimmerBand.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))),
                new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(850))),
            },
        });
        ShimmerTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, 560.0, TimeSpan.FromMilliseconds(850))
        {
            EasingFunction = easeOut,
        });
    }

    private void RunExitAnimation(double slideEndOffsetY)
    {
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        RootStage.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = easeIn,
        });
        StageTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, slideEndOffsetY * 0.75, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = easeIn,
        });
    }

    /// <summary>
    /// Decodes a local achievement icon into a frozen thread-safe <see cref="BitmapSource"/> off the UI thread.
    /// Uses direct WIC decoding first (zero Skia PNG re-encoding); falls back to raw <see cref="SKBitmap"/> pixel transfer
    /// only for formats WIC cannot decode (such as WebP on machines without the WebP WIC codec).
    /// </summary>
    public static BitmapSource? LoadBitmapOffUiThread(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        // Primary: direct WIC decode directly into frozen BitmapImage (zero redundant Skia PNG encode/decode pass).
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int nativeWidth = 0;
            try
            {
                var probeDecoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (probeDecoder.Frames.Count > 0)
                {
                    nativeWidth = probeDecoder.Frames[0].PixelWidth;
                }
            }
            catch
            {
                nativeWidth = 0;
            }

            fs.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Only downscale oversized images; never upscale crisp 64x64 Steam achievement icons.
            if (nativeWidth > 128)
            {
                bmp.DecodePixelWidth = 128;
            }
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Fall back to direct SkiaSharp pixel copy for WebP / non-WIC formats without re-encoding to PNG.
        }

        try
        {
            byte[] raw = File.ReadAllBytes(filePath);
            using var decoded = SKBitmap.Decode(raw);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
            {
                return null;
            }

            var targetInfo = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var normalized = new SKBitmap(targetInfo);
            using (var canvas = new SKCanvas(normalized))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(decoded, 0, 0);
            }

            int stride = normalized.RowBytes;
            byte[] pixelBuffer = new byte[stride * normalized.Height];
            System.Runtime.InteropServices.Marshal.Copy(normalized.GetPixels(), pixelBuffer, 0, pixelBuffer.Length);

            var source = BitmapSource.Create(
                normalized.Width,
                normalized.Height,
                96.0,
                96.0,
                PixelFormats.Pbgra32,
                null,
                pixelBuffer,
                stride);
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
