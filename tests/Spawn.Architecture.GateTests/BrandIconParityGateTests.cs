using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Spawn.Host.Services;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Keeps the brand mark in one shape and one palette everywhere it is produced: the SVG sources, the PNG/ICO assets
/// rasterized by <c>scripts/generate_icons.py</c>, and the SkiaSharp fallback in <see cref="TrayIconService"/>.
/// </summary>
public sealed partial class BrandIconParityGateTests
{
    private static readonly int[] ExpectedIcoSizes = [16, 24, 32, 48, 64, 128, 256];

    // Probe points in 1024-unit space, chosen well inside a single flat region (no anti-aliased edges).
    private static readonly (float X, float Y) OutsidePoint = (16, 16);
    private static readonly (float X, float Y) BackgroundPoint = (150, 512);
    private static readonly (float X, float Y) LetterTopBarPoint = (592, 305);
    private static readonly (float X, float Y) LetterMiddleBarPoint = (512, 515);
    private static readonly (float X, float Y) LetterBottomBarPoint = (400, 725);
    private static readonly (float X, float Y) BadgeFillPoint = (600, 880);
    private static readonly (float X, float Y) BadgeGlyphPoint = (735, 880);

    [Theory]
    [InlineData("icon.svg", false)]
    [InlineData("icon-dev.svg", true)]
    public void SvgSources_UseCanonicalGeometryAndPalette(string fileName, bool isDev)
    {
        XDocument svg = XDocument.Load(Path.Combine(LocateRepositoryRoot(), "assets", fileName));
        XNamespace ns = "http://www.w3.org/2000/svg";
        XElement root = svg.Root!;
        Assert.Equal("0 0 1024 1024", (string?)root.Attribute("viewBox"));

        var rects = root.Elements(ns + "rect").ToList();
        var paths = root.Elements(ns + "path").ToList();
        Assert.Equal(isDev ? 2 : 1, rects.Count);
        Assert.Equal(isDev ? 2 : 1, paths.Count);

        AssertRect(rects[0], BrandIconGeometry.BackgroundRect, BrandIconGeometry.BackgroundRadius, BrandIconGeometry.BackgroundColor);
        Assert.Equal(BrandIconGeometry.LetterPathData, NormalizePath((string?)paths[0].Attribute("d")));
        Assert.Equal(ToHex(BrandIconGeometry.LetterColor), ((string?)paths[0].Attribute("fill"))?.ToUpperInvariant());

        if (isDev)
        {
            AssertRect(rects[1], BrandIconGeometry.BadgeRect, BrandIconGeometry.BadgeRadius, BrandIconGeometry.BadgeColor);
            Assert.Equal(BrandIconGeometry.DevGlyphPathData, NormalizePath((string?)paths[1].Attribute("d")));
            Assert.Equal(ToHex(BrandIconGeometry.BadgeTextColor), ((string?)paths[1].Attribute("fill"))?.ToUpperInvariant());
            Assert.Equal("evenodd", (string?)paths[1].Attribute("fill-rule"));
        }
    }

    [Fact]
    public void GeneratorScript_DeclaresCanonicalPalette()
    {
        string script = File.ReadAllText(Path.Combine(LocateRepositoryRoot(), "scripts", "generate_icons.py"));
        AssertPythonColor(script, "BACKGROUND", BrandIconGeometry.BackgroundColor);
        AssertPythonColor(script, "LETTER", BrandIconGeometry.LetterColor);
        AssertPythonColor(script, "BADGE", BrandIconGeometry.BadgeColor);
        AssertPythonColor(script, "BADGE_TEXT", BrandIconGeometry.BadgeTextColor);
    }

    [Theory]
    [InlineData("icon.png", false)]
    [InlineData("icon-dev.png", true)]
    public void PngAssets_MatchCanonicalPalette(string fileName, bool isDev)
    {
        using SKBitmap bitmap = SKBitmap.Decode(Path.Combine(LocateRepositoryRoot(), "assets", fileName));
        Assert.NotNull(bitmap);
        Assert.Equal(bitmap.Width, bitmap.Height);
        Assert.True(bitmap.Width >= 256, $"{fileName} should be at least 256 px, was {bitmap.Width}.");
        AssertBrandPixels(bitmap, isDev);
    }

    [Theory]
    [InlineData("icon.ico")]
    [InlineData("icon-dev.ico")]
    public void IcoAssets_ContainEveryShellSize(string fileName)
    {
        byte[] ico = File.ReadAllBytes(Path.Combine(LocateRepositoryRoot(), "assets", fileName));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2, 2)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2));
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int width = ico[6 + (i * 16)];
            sizes.Add(width == 0 ? 256 : width);
        }

        Assert.Equal(ExpectedIcoSizes, sizes.Order().ToArray());
    }

    [Theory]
    [InlineData(false, 256)]
    [InlineData(true, 256)]
    [InlineData(true, 64)]
    public void SkiaFallback_RendersCanonicalMark(bool isDev, int sizePx)
    {
        byte[] png = TrayIconService.RenderBrandIconPngBytes(isDev, sizePx);
        using SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.Equal(sizePx, bitmap.Width);
        Assert.Equal(sizePx, bitmap.Height);
        AssertBrandPixels(bitmap, isDev);
    }

    private static void AssertBrandPixels(SKBitmap bitmap, bool isDev)
    {
        Assert.Equal(0, Sample(bitmap, OutsidePoint).Alpha);
        AssertColor(BrandIconGeometry.BackgroundColor, Sample(bitmap, BackgroundPoint), "background");
        AssertColor(BrandIconGeometry.LetterColor, Sample(bitmap, LetterTopBarPoint), "letter top bar");
        AssertColor(BrandIconGeometry.LetterColor, Sample(bitmap, LetterMiddleBarPoint), "letter middle bar");
        AssertColor(BrandIconGeometry.LetterColor, Sample(bitmap, LetterBottomBarPoint), "letter bottom bar");

        if (isDev)
        {
            AssertColor(BrandIconGeometry.BadgeColor, Sample(bitmap, BadgeFillPoint), "DEV badge");
            if (bitmap.Width >= 128)
            {
                // Below 128 px one pixel spans more than the probe's margin inside the 18-unit "E" stem.
                AssertColor(BrandIconGeometry.BadgeTextColor, Sample(bitmap, BadgeGlyphPoint), "DEV glyph");
            }
        }
        else
        {
            AssertColor(BrandIconGeometry.BackgroundColor, Sample(bitmap, BadgeFillPoint), "release corner (no badge)");
        }
    }

    private static SKColor Sample(SKBitmap bitmap, (float X, float Y) unitPoint)
    {
        int x = Math.Clamp((int)(unitPoint.X / BrandIconGeometry.Units * bitmap.Width), 0, bitmap.Width - 1);
        int y = Math.Clamp((int)(unitPoint.Y / BrandIconGeometry.Units * bitmap.Height), 0, bitmap.Height - 1);
        return bitmap.GetPixel(x, y);
    }

    private static void AssertColor(SKColor expected, SKColor actual, string region)
    {
        const int tolerance = 2;
        bool ok = actual.Alpha == 255
                  && Math.Abs(actual.Red - expected.Red) <= tolerance
                  && Math.Abs(actual.Green - expected.Green) <= tolerance
                  && Math.Abs(actual.Blue - expected.Blue) <= tolerance;
        Assert.True(ok, $"{region}: expected {ToHex(expected)} opaque, got {ToHex(actual)} alpha {actual.Alpha}.");
    }

    private static void AssertRect(XElement rect, SKRect expected, float radius, SKColor fill)
    {
        Assert.Equal(expected.Left, float.Parse((string)rect.Attribute("x")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected.Top, float.Parse((string)rect.Attribute("y")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected.Width, float.Parse((string)rect.Attribute("width")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected.Height, float.Parse((string)rect.Attribute("height")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(radius, float.Parse((string)rect.Attribute("rx")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ToHex(fill), ((string?)rect.Attribute("fill"))?.ToUpperInvariant());
    }

    private static void AssertPythonColor(string script, string name, SKColor expected)
    {
        Match match = Regex.Match(script, $@"^{name} = \(0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}})\)", RegexOptions.Multiline);
        Assert.True(match.Success, $"generate_icons.py must declare {name} as an RGB hex tuple.");
        string actual = $"#{match.Groups[1].Value}{match.Groups[2].Value}{match.Groups[3].Value}".ToUpperInvariant();
        Assert.Equal(ToHex(expected), actual);
    }

    private static string NormalizePath(string? d) => WhitespaceRegex().Replace(d ?? string.Empty, " ").Trim();

    private static string ToHex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

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
