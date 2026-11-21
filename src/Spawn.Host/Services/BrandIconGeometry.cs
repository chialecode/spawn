using SkiaSharp;

namespace Spawn.Host.Services;

/// <summary>
/// Canonical SPAWN brand mark in a 1024-unit square, identical to <c>assets/icon.svg</c> / <c>assets/icon-dev.svg</c>
/// and to the rasterizer in <c>scripts/generate_icons.py</c>: a flat dark-gray rounded square, a white geometric
/// <c>S</c> (height 250-780, stroke 110, terminal corner radius 28) and, for the Dev channel, a flat amber badge
/// carrying a <c>DEV</c> glyph. No gradients, borders, shadows or font dependencies.
/// </summary>
public static class BrandIconGeometry
{
    public const float Units = 1024f;

    public static readonly SKRect BackgroundRect = new(48, 48, 976, 976);
    public const float BackgroundRadius = 200f;

    public static readonly SKRect BadgeRect = new(560, 800, 960, 960);
    public const float BadgeRadius = 52f;

    public static readonly SKColor BackgroundColor = new(0x3C, 0x40, 0x43);
    public static readonly SKColor LetterColor = new(0xFF, 0xFF, 0xFF);
    public static readonly SKColor BadgeColor = new(0xF3, 0xA1, 0x15);
    public static readonly SKColor BadgeTextColor = new(0x20, 0x21, 0x24);

    /// <summary>White <c>S</c> path data (SVG syntax, 1024 units).</summary>
    public const string LetterPathData =
        "M724 250A28 28 0 0 1 752 278V332A28 28 0 0 1 724 360H432A50 50 0 0 0 432 460H592A160 160 0 0 1 592 780H300A28 28 0 0 1 272 752V698A28 28 0 0 1 300 670H592A50 50 0 0 0 592 570H432A160 160 0 0 1 432 250Z";

    /// <summary><c>DEV</c> badge glyph path data (SVG syntax, 1024 units, even-odd fill).</summary>
    public const string DevGlyphPathData =
        "M636 839H670A40 40 0 0 1 710 879V885A40 40 0 0 1 670 925H636ZM654 857V907H670A22 22 0 0 0 692 885V879A22 22 0 0 0 670 857ZM726 839H782V857H744V873H776V891H744V907H782V925H726ZM796 839H815L840 903L865 839H884L850 925H830Z";

    /// <summary>Draws the mark into a square of <paramref name="sizePx"/> pixels at the canvas origin.</summary>
    public static void Draw(SKCanvas canvas, float sizePx, bool isDevChannel)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        int saved = canvas.Save();
        try
        {
            canvas.Scale(sizePx / Units);

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            paint.Color = BackgroundColor;
            canvas.DrawRoundRect(BackgroundRect, BackgroundRadius, BackgroundRadius, paint);

            using (SKPath letter = SKPath.ParseSvgPathData(LetterPathData))
            {
                paint.Color = LetterColor;
                canvas.DrawPath(letter, paint);
            }

            if (isDevChannel)
            {
                paint.Color = BadgeColor;
                canvas.DrawRoundRect(BadgeRect, BadgeRadius, BadgeRadius, paint);

                using SKPath glyph = SKPath.ParseSvgPathData(DevGlyphPathData);
                glyph.FillType = SKPathFillType.EvenOdd;
                paint.Color = BadgeTextColor;
                canvas.DrawPath(glyph, paint);
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }
}
