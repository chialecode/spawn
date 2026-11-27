using System.Windows;
using System.Windows.Media;
using Spawn.Contracts;

namespace Spawn.Host.Controls;

/// <summary>
/// Pure native WPF vector graph cell rendering one row's vertical lane rails, cubic Bezier fork/merge curves,
/// snapshot node dot, HEAD status ring, and pinned halo inside the virtualized Branching Save Tree.
/// Zero WebView2, zero external graphics packages, crisp across PerMonitorV2 DPI and Light/Dark theme switches.
/// </summary>
public sealed class SaveGraphCellControl : FrameworkElement
{
    public static readonly DependencyProperty RowLayoutProperty = DependencyProperty.Register(
        nameof(RowLayout),
        typeof(SaveGraphRowLayout),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LaneSpacingProperty = DependencyProperty.Register(
        nameof(LaneSpacing),
        typeof(double),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(SaveTreeGraphLayoutEngine.DefaultLaneSpacing, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LeftPaddingProperty = DependencyProperty.Register(
        nameof(LeftPadding),
        typeof(double),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(SaveTreeGraphLayoutEngine.DefaultLeftPadding, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsRowSelectedProperty = DependencyProperty.Register(
        nameof(IsRowSelected),
        typeof(bool),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceBackgroundBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBackgroundBrush),
        typeof(Brush),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedSurfaceBrushProperty = DependencyProperty.Register(
        nameof(SelectedSurfaceBrush),
        typeof(Brush),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HeadRingBrushProperty = DependencyProperty.Register(
        nameof(HeadRingBrush),
        typeof(Brush),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PinAccentBrushProperty = DependencyProperty.Register(
        nameof(PinAccentBrush),
        typeof(Brush),
        typeof(SaveGraphCellControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush[] FallbackLaneBrushes = BuildFallbackLaneBrushes();
    private static readonly Brush FallbackSurfaceBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush FallbackHeadRingBrush = CreateFrozenBrush(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly Brush FallbackPinBrush = CreateFrozenBrush(Color.FromRgb(0xD9, 0x77, 0x06));

    public SaveGraphCellControl()
    {
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
        IsHitTestVisible = false;
    }

    public SaveGraphRowLayout? RowLayout
    {
        get => (SaveGraphRowLayout?)GetValue(RowLayoutProperty);
        set => SetValue(RowLayoutProperty, value);
    }

    public double LaneSpacing
    {
        get => (double)GetValue(LaneSpacingProperty);
        set => SetValue(LaneSpacingProperty, value);
    }

    public double LeftPadding
    {
        get => (double)GetValue(LeftPaddingProperty);
        set => SetValue(LeftPaddingProperty, value);
    }

    public bool IsRowSelected
    {
        get => (bool)GetValue(IsRowSelectedProperty);
        set => SetValue(IsRowSelectedProperty, value);
    }

    public Brush? SurfaceBackgroundBrush
    {
        get => (Brush?)GetValue(SurfaceBackgroundBrushProperty);
        set => SetValue(SurfaceBackgroundBrushProperty, value);
    }

    public Brush? SelectedSurfaceBrush
    {
        get => (Brush?)GetValue(SelectedSurfaceBrushProperty);
        set => SetValue(SelectedSurfaceBrushProperty, value);
    }

    public Brush? HeadRingBrush
    {
        get => (Brush?)GetValue(HeadRingBrushProperty);
        set => SetValue(HeadRingBrushProperty, value);
    }

    public Brush? PinAccentBrush
    {
        get => (Brush?)GetValue(PinAccentBrushProperty);
        set => SetValue(PinAccentBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        SaveGraphRowLayout? layout = RowLayout;
        int lanes = Math.Max(1, layout?.TotalLaneColumns ?? 1);
        double desiredWidth = Math.Clamp(
            (LeftPadding * 2.0) + Math.Max(0, lanes - 1) * LaneSpacing + 8.0,
            SaveTreeGraphLayoutEngine.MinGraphWidth,
            SaveTreeGraphLayoutEngine.MaxGraphWidth);

        double desiredHeight = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0
            ? SaveTreeGraphLayoutEngine.DefaultRowHeight
            : availableSize.Height;

        return new Size(
            double.IsInfinity(availableSize.Width) ? desiredWidth : Math.Min(desiredWidth, availableSize.Width),
            desiredHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        SaveGraphRowLayout? layout = RowLayout;
        if (layout is null)
        {
            return;
        }

        double height = ActualHeight > 0 ? ActualHeight : SaveTreeGraphLayoutEngine.DefaultRowHeight;
        double midY = height * 0.5;
        double leftPad = LeftPadding;
        double spacing = LaneSpacing;

        double LaneX(int lane) => leftPad + (lane * spacing);

        // 1. Pass-through straight vertical rails (y: 0 -> height)
        foreach (SaveGraphRailSegment rail in layout.PassThroughRails)
        {
            double x = LaneX(rail.Lane);
            Brush brush = ResolveLaneBrush(rail.ColorIndex, rail.ColorHex);
            Pen pen = CreateLinePen(brush, thickness: 2.1, dashed: false);
            dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }

        // 2. Incoming curves / lines from top edge (y = 0) into node center (y = midY)
        foreach (SaveGraphCurveSegment incoming in layout.IncomingCurves)
        {
            double x1 = LaneX(incoming.FromLane);
            double x2 = LaneX(incoming.ToLane);
            Brush brush = ResolveLaneBrush(incoming.ColorIndex, incoming.ColorHex);
            Pen pen = CreateLinePen(brush, thickness: 2.1, dashed: incoming.IsMerge);

            if (incoming.FromLane == incoming.ToLane)
            {
                dc.DrawLine(pen, new Point(x1, 0), new Point(x2, midY));
            }
            else
            {
                dc.DrawGeometry(null, pen, BuildVerticalCubicBezier(x1, 0, x2, midY));
            }
        }

        // 3. Outgoing curves / lines from node center (y = midY) to bottom edge (y = height)
        foreach (SaveGraphCurveSegment outgoing in layout.OutgoingCurves)
        {
            double x1 = LaneX(outgoing.FromLane);
            double x2 = LaneX(outgoing.ToLane);
            Brush brush = ResolveLaneBrush(outgoing.ColorIndex, outgoing.ColorHex);
            Pen pen = CreateLinePen(brush, thickness: 2.1, dashed: outgoing.IsMerge);

            if (outgoing.FromLane == outgoing.ToLane)
            {
                dc.DrawLine(pen, new Point(x1, midY), new Point(x2, height));
            }
            else
            {
                dc.DrawGeometry(null, pen, BuildVerticalCubicBezier(x1, midY, x2, height));
            }
        }

        // 4. Node dot, cutout halo, HEAD ring, and Pin indicator
        Point center = new(LaneX(layout.NodeLane), midY);
        Brush nodeBrush = ResolveLaneBrush(layout.ColorIndex, layout.BranchColorHex);
        Brush cutoutBrush = (IsRowSelected ? SelectedSurfaceBrush : SurfaceBackgroundBrush)
            ?? SurfaceBackgroundBrush
            ?? TryFindResource("PageBackgroundBrush") as Brush
            ?? FallbackSurfaceBrush;

        double coreRadius = layout.IsHead
            ? 5.6
            : (layout.IsBranchTip ? 5.1 : 4.4);

        // Background halo cutout keeps intersections razor-clean
        dc.DrawEllipse(cutoutBrush, null, center, coreRadius + 2.2, coreRadius + 2.2);

        if (layout.IsHead)
        {
            Brush headBrush = HeadRingBrush
                ?? TryFindResource("SaveHeadRingBrush") as Brush
                ?? FallbackHeadRingBrush;

            // Soft translucent pulse ring around HEAD
            Brush softHalo = CreateTranslucentBrush(headBrush, opacity: 0.22);
            dc.DrawEllipse(softHalo, null, center, coreRadius + 5.2, coreRadius + 5.2);

            Pen ringPen = CreateLinePen(headBrush, thickness: 1.9, dashed: layout.IsDetachedHead);
            dc.DrawEllipse(null, ringPen, center, coreRadius + 2.5, coreRadius + 2.5);
        }
        else if (layout.IsPinned)
        {
            Brush pinBrush = PinAccentBrush
                ?? TryFindResource("SavePinStarBrush") as Brush
                ?? FallbackPinBrush;
            Pen pinPen = CreateLinePen(pinBrush, thickness: 1.55, dashed: false);
            dc.DrawEllipse(null, pinPen, center, coreRadius + 2.3, coreRadius + 2.3);
        }

        if (layout.IsSafetyNode || layout.IsDetachedHead)
        {
            // Hollow ring style distinguishes automatic safety anchors & detached HEAD
            Pen hollowPen = CreateLinePen(nodeBrush, thickness: 2.2, dashed: false);
            dc.DrawEllipse(cutoutBrush, hollowPen, center, coreRadius - 0.4, coreRadius - 0.4);
        }
        else
        {
            dc.DrawEllipse(nodeBrush, null, center, coreRadius, coreRadius);

            if (layout.IsBranchTip)
            {
                // Tiny crisp inner center highlight on branch tip nodes
                dc.DrawEllipse(cutoutBrush, null, center, 1.7, 1.7);
            }
        }
    }

    private Brush ResolveLaneBrush(int colorIndex, string? colorHex)
    {
        int idx = Math.Abs(colorIndex) % SaveBranchColors.Palette.Count;
        string resourceKey = $"SaveBranchLane{idx}Brush";
        if (TryFindResource(resourceKey) is Brush themedBrush)
        {
            return themedBrush;
        }

        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(colorHex) is Color parsed)
                {
                    return CreateFrozenBrush(parsed);
                }
            }
            catch
            {
                // Fall back to curated lane palette.
            }
        }

        return FallbackLaneBrushes[idx];
    }

    private static StreamGeometry BuildVerticalCubicBezier(double x1, double y1, double x2, double y2)
    {
        double dy = y2 - y1;
        Point c1 = new(x1, y1 + (dy * 0.56));
        Point c2 = new(x2, y1 + (dy * 0.44));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x1, y1), isFilled: false, isClosed: false);
            ctx.BezierTo(c1, c2, new Point(x2, y2), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Pen CreateLinePen(Brush brush, double thickness, bool dashed)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        if (dashed)
        {
            pen.DashStyle = new DashStyle([3.0, 2.5], 0);
        }

        pen.Freeze();
        return pen;
    }

    private static Brush CreateTranslucentBrush(Brush source, double opacity)
    {
        if (source is SolidColorBrush scb)
        {
            Color c = scb.Color;
            byte alpha = (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255);
            return CreateFrozenBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }

        Brush clone = source.Clone();
        clone.Opacity = opacity;
        if (clone.CanFreeze)
        {
            clone.Freeze();
        }

        return clone;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush[] BuildFallbackLaneBrushes()
    {
        var list = new Brush[SaveBranchColors.Palette.Count];
        for (int i = 0; i < SaveBranchColors.Palette.Count; i++)
        {
            Color color = (Color)ColorConverter.ConvertFromString(SaveBranchColors.Palette[i]);
            list[i] = CreateFrozenBrush(color);
        }

        return list;
    }
}
