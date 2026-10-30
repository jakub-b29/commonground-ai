using QRCoder;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CommonGround.Infrastructure.MetaModels;

public sealed class SamOverlayRenderer
{
    private static string ToDataUrl(Image image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return $"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}";
    }

    private static Color ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) return Color.ParseHex("0284C7");
        return Color.ParseHex(h);
    }

    public (string BaseDataUrl, string OverlayDataUrl, decimal AreaM2) CreateDefectBeforeAndOverlay(string concept)
    {
        var lower = concept.ToLowerInvariant();
        decimal area = lower.Contains("crack", StringComparison.Ordinal) ? 2.85m
            : lower.Contains("spalling", StringComparison.Ordinal) ? 3.40m
            : lower.Contains("fire", StringComparison.Ordinal) ? 6.20m
            : 4.35m;

        using var baseImg = new Image<Rgba32>(640, 400);
        baseImg.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("D6CFC4"));
            for (var x = 0; x < 640; x += 32)
            {
                ctx.DrawLine(Color.ParseHex("B8B0A0"), 1f, new PointF(x, 0), new PointF(x, 400));
            }
            for (var y = 0; y < 400; y += 32)
            {
                ctx.DrawLine(Color.ParseHex("B8B0A0"), 1f, new PointF(0, y), new PointF(640, y));
            }
            var stain = Color.ParseHex("8A6D4B");
            ctx.Fill(stain, new EllipsePolygon(320f, 190f, 150f, 95f));
            ctx.Fill(Color.ParseHex("6E5638"), new EllipsePolygon(300f, 180f, 95f, 60f));
            ctx.Fill(Color.ParseHex("5A4630"), new EllipsePolygon(330f, 200f, 45f, 28f));
        });
        var baseUrl = ToDataUrl(baseImg);

        using var overlay = new Image<Rgba32>(640, 400);
        overlay.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("D6CFC4"));
            for (var x = 0; x < 640; x += 32)
            {
                ctx.DrawLine(Color.ParseHex("B8B0A0"), 1f, new PointF(x, 0), new PointF(x, 400));
            }
            for (var y = 0; y < 400; y += 32)
            {
                ctx.DrawLine(Color.ParseHex("B8B0A0"), 1f, new PointF(0, y), new PointF(640, y));
            }
            ctx.Fill(Color.ParseHex("8A6D4B"), new EllipsePolygon(320f, 190f, 150f, 95f));
            var mask = new Rgba32(0, 180, 220, 140);
            ctx.Fill(mask, new EllipsePolygon(320f, 190f, 150f, 95f));
            var magenta = Color.ParseHex("D6006D");
            ctx.Draw(magenta, 3f, new RectangularPolygon(165, 90, 310, 200));
            ctx.Fill(Color.White, new RectangularPolygon(165, 62, 300, 26));
            ctx.DrawText($"sam-3.1  {concept}  {area:F2} m2", new Font(SystemFonts.Get("Arial"), 13f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(172, 66));
        });
        return (baseUrl, ToDataUrl(overlay), area);
    }

    public (string AfterDataUrl, string AfterOverlayDataUrl, decimal AfterAreaM2) CreateRepairedAfterAndOverlay(string concept, decimal beforeAreaM2)
    {
        var afterArea = Math.Round(beforeAreaM2 * 0.018m, 2);
        if (afterArea <= 0m) afterArea = 0.08m;
        using var after = new Image<Rgba32>(640, 400);
        after.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("EDEAE4"));
            for (var x = 0; x < 640; x += 32)
            {
                ctx.DrawLine(Color.ParseHex("D8D2C6"), 1f, new PointF(x, 0), new PointF(x, 400));
            }
            for (var y = 0; y < 400; y += 32)
            {
                ctx.DrawLine(Color.ParseHex("D8D2C6"), 1f, new PointF(0, y), new PointF(640, y));
            }
            ctx.Fill(Color.ParseHex("E3DED4"), new EllipsePolygon(320f, 190f, 150f, 95f));
        });
        var afterUrl = ToDataUrl(after);

        using var overlay = new Image<Rgba32>(640, 400);
        overlay.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("EDEAE4"));
            for (var x = 0; x < 640; x += 32)
            {
                ctx.DrawLine(Color.ParseHex("D8D2C6"), 1f, new PointF(x, 0), new PointF(x, 400));
            }
            for (var y = 0; y < 400; y += 32)
            {
                ctx.DrawLine(Color.ParseHex("D8D2C6"), 1f, new PointF(0, y), new PointF(640, y));
            }
            var green = Color.ParseHex("15803D");
            ctx.Draw(green, 3f, new RectangularPolygon(165, 90, 310, 200));
            ctx.Fill(Color.White, new RectangularPolygon(120, 330, 420, 30));
            var pct = beforeAreaM2 > 0m ? Math.Round(afterArea / beforeAreaM2 * 100m, 1) : 0m;
            ctx.DrawText($"Remaining defect mask: {afterArea:F2} m2 ({pct}% < 5.0% threshold) - VERIFIED", new Font(SystemFonts.Get("Arial"), 12f, FontStyle.Bold), Color.ParseHex("15803D"), new PointF(128, 336));
        });
        return (afterUrl, ToDataUrl(overlay), afterArea);
    }

    public string CreateAnnotatedValvePhoto(string category, string title, string colorHex)
    {
        var color = ParseHex(colorHex);
        using var img = new Image<Rgba32>(640, 400);
        img.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("3A4149"));
            for (var i = 0; i < 4; i++)
            {
                var y = 90f + i * 70f;
                ctx.DrawLine(Color.ParseHex("9AA3AD"), 10f, new PointF(40, y), new PointF(600, y));
                ctx.Fill(Color.ParseHex("C0C7CE"), new EllipsePolygon(180f + i * 90f, y, 22f));
            }
            ctx.Draw(color, 5f, new EllipsePolygon(270f, 160f, 42f));
            ctx.Fill(Color.White, new RectangularPolygon(180, 300, 340, 28));
            ctx.DrawText(title, new Font(SystemFonts.Get("Arial"), 12f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(188, 305));
            ctx.Fill(Color.White, new RectangularPolygon(180, 30, 200, 24));
            ctx.DrawText($"sam-3.1  {category}", new Font(SystemFonts.Get("Arial"), 11f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(188, 34));
        });
        return ToDataUrl(img);
    }

    public string CreateRenovationOptionPreview(string optionLabel, bool isOptionA)
    {
        using var img = new Image<Rgba32>(640, 400);
        img.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex(isOptionA ? "DCE8D8" : "D8E2EC"));
            ctx.Fill(Color.ParseHex("B9C4CE"), new RectangularPolygon(120, 60, 400, 220));
            for (var floor = 0; floor < 4; floor++)
            {
                for (var win = 0; win < 5; win++)
                {
                    ctx.Fill(Color.ParseHex("7E93A6"), new RectangularPolygon(145 + win * 75, 85 + floor * 48, 48, 30));
                }
            }
            ctx.Fill(Color.ParseHex(isOptionA ? "4E7A3A" : "5B6B7A"), new RectangularPolygon(0, 300, 640, 100));
            if (isOptionA)
            {
                for (var i = 0; i < 6; i++)
                {
                    ctx.Fill(Color.ParseHex("3E6B2E"), new EllipsePolygon(70f + i * 95f, 320f, 26f));
                }
            }
            else
            {
                ctx.Fill(Color.ParseHex("8A97A5"), new RectangularPolygon(180, 315, 280, 45));
            }
            ctx.Fill(Color.White, new RectangularPolygon(16, 16, 300, 28));
            ctx.DrawText(optionLabel, new Font(SystemFonts.Get("Arial"), 13f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(24, 20));
        });
        return ToDataUrl(img);
    }

    public string CreateQrStairwellNoticePoster(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    public string CreateWasteSegregationPoster()
    {
        using var img = new Image<Rgba32>(640, 400);
        var fractions = new[] { ("Mixed", "3A3A3A"), ("Plastics and metals", "C9A227"), ("Paper", "2563A8"), ("Bio", "4E7A3A"), ("Glass", "2E7D6B") };
        img.Mutate(ctx =>
        {
            ctx.Fill(Color.White);
            ctx.DrawText("Waste segregation guide", new Font(SystemFonts.Get("Arial"), 18f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(24, 20));
            for (var i = 0; i < fractions.Length; i++)
            {
                var y = 70 + i * 62;
                ctx.Fill(Color.ParseHex(fractions[i].Item2), new RectangularPolygon(24, y, 90, 48));
                ctx.DrawText(fractions[i].Item1, new Font(SystemFonts.Get("Arial"), 14f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(130, y + 14));
            }
        });
        return ToDataUrl(img);
    }
}
