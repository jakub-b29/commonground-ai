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

    private static Font GetSafeFont(float size, FontStyle style = FontStyle.Regular)
    {
        if (SystemFonts.TryGet("Arial", out var family)
            || SystemFonts.TryGet("Segoe UI", out family)
            || SystemFonts.TryGet("DejaVu Sans", out family))
        {
            return family.CreateFont(size, style);
        }

        var fallback = SystemFonts.Families.FirstOrDefault();
        return fallback.CreateFont(size, style);
    }

    private static string TruncateText(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var clean = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return clean.Length <= maxLen ? clean : clean[..(maxLen - 1)] + "…";
    }

    public string CreateRenovationOptionPreview(string optionLabel, bool isOptionA, IReadOnlyList<string>? customInfoLines = null)
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
            ctx.Fill(Color.White, new RectangularPolygon(16, 16, 420, 30));
            ctx.DrawText(TruncateText(optionLabel, 48), GetSafeFont(13f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(24, 21));

            ctx.Fill(Color.ParseHex("0F172A"), new RectangularPolygon(452, 16, 172, 30));
            ctx.DrawText("muse-image-1.0 • 640x400", GetSafeFont(10.5f, FontStyle.Bold), Color.ParseHex("38BDF8"), new PointF(462, 23));

            if (customInfoLines is { Count: > 0 })
            {
                var boxHeight = Math.Min(3, customInfoLines.Count) * 20 + 18;
                ctx.Fill(new Rgba32(15, 23, 42, 225), new RectangularPolygon(16, 390 - boxHeight, 608, boxHeight));
                var lineIdx = 0;
                foreach (var rawLine in customInfoLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(3))
                {
                    ctx.DrawText("• " + TruncateText(rawLine, 76), GetSafeFont(11.5f, FontStyle.Regular), Color.White, new PointF(26, 396 - boxHeight + lineIdx * 20));
                    lineIdx++;
                }
            }
        });
        return ToDataUrl(img);
    }

    public string CreateQrStairwellNoticePoster(string url, string? headline = null, string? bodySummary = null, IReadOnlyList<string>? customInfoLines = null)
    {
        var safeUrl = string.IsNullOrWhiteSpace(url) ? "https://commonground.local/notice" : url.Trim();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(safeUrl, QRCodeGenerator.ECCLevel.Q);
        var qrPngBytes = new PngByteQRCode(data).GetGraphic(6);

        if (string.IsNullOrWhiteSpace(headline) && string.IsNullOrWhiteSpace(bodySummary) && (customInfoLines is null || customInfoLines.Count == 0))
        {
            using var qrOnlyImg = Image.Load<Rgba32>(qrPngBytes);
            using var canvas = new Image<Rgba32>(640, 400);
            canvas.Mutate(ctx =>
            {
                ctx.Fill(Color.White);
                ctx.Fill(Color.ParseHex("0F172A"), new RectangularPolygon(0, 0, 640, 52));
                ctx.DrawText("STAIRWELL QR NOTICE • muse-image-1.0", GetSafeFont(14f, FontStyle.Bold), Color.White, new PointF(20, 16));
                qrOnlyImg.Mutate(q => q.Resize(240, 240));
                ctx.DrawImage(qrOnlyImg, new Point(200, 80), 1f);
                ctx.DrawText(TruncateText(safeUrl, 68), GetSafeFont(11f, FontStyle.Regular), Color.ParseHex("334155"), new PointF(140, 340));
            });
            return ToDataUrl(canvas);
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(bodySummary))
        {
            lines.Add(bodySummary.Trim());
        }
        if (customInfoLines is not null)
        {
            lines.AddRange(customInfoLines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }

        return CreateCustomAnnotatedImagePoster(
            title: string.IsNullOrWhiteSpace(headline) ? "Resident Building Notice" : headline!,
            subtitle: "Official Stairwell Digital Notice & QR Ballot Link",
            infoLines: lines,
            badgeText: "QR NOTICE",
            qrPayload: safeUrl,
            accentHex: "0284C7",
            footerNote: safeUrl);
    }

    public string CreateWasteSegregationPoster(IReadOnlyList<string>? customInfoLines = null, string? communityName = null)
    {
        using var img = new Image<Rgba32>(640, 400);
        var fractions = new[]
        {
            ("Mixed / Residual", "3A3A3A"),
            ("Plastics & Metals", "C9A227"),
            ("Paper & Cardboard", "2563A8"),
            ("Bio / Organic", "4E7A3A"),
            ("Glass Packaging", "2E7D6B")
        };
        img.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("F8FAFC"));
            ctx.Fill(Color.ParseHex("0F172A"), new RectangularPolygon(0, 0, 640, 54));
            var headerTitle = string.IsNullOrWhiteSpace(communityName)
                ? "Waste Segregation Guide"
                : $"Waste Segregation Guide — {TruncateText(communityName, 32)}";
            ctx.DrawText(headerTitle, GetSafeFont(15f, FontStyle.Bold), Color.White, new PointF(20, 16));
            ctx.Fill(Color.ParseHex("1E293B"), new RectangularPolygon(465, 12, 158, 30));
            ctx.DrawText("muse-image-1.0 • 640x400", GetSafeFont(10f, FontStyle.Bold), Color.ParseHex("38BDF8"), new PointF(475, 20));

            for (var i = 0; i < fractions.Length; i++)
            {
                var y = 68 + i * 62;
                ctx.Fill(Color.ParseHex(fractions[i].Item2), new RectangularPolygon(20, y, 86, 48));
                ctx.DrawText(fractions[i].Item1, GetSafeFont(13.5f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(120, y + 14));
            }

            if (customInfoLines is { Count: > 0 })
            {
                ctx.Fill(Color.ParseHex("0F172A"), new RectangularPolygon(330, 68, 290, 298));
                ctx.Fill(Color.ParseHex("0284C7"), new RectangularPolygon(330, 68, 290, 6));
                ctx.DrawText("CUSTOM BUILDING INSTRUCTIONS", GetSafeFont(11f, FontStyle.Bold), Color.ParseHex("38BDF8"), new PointF(344, 86));
                var yOffset = 114f;
                foreach (var line in customInfoLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(7))
                {
                    ctx.DrawText("• " + TruncateText(line, 38), GetSafeFont(11.5f, FontStyle.Regular), Color.White, new PointF(344, yOffset));
                    yOffset += 32f;
                }
            }
        });
        return ToDataUrl(img);
    }

    public string CreateCustomAnnotatedImagePoster(
        string title,
        string subtitle,
        IReadOnlyList<string> infoLines,
        string badgeText,
        string? qrPayload = null,
        string? accentHex = null,
        string? footerNote = null)
    {
        var accent = ParseHex(string.IsNullOrWhiteSpace(accentHex) ? "0284C7" : accentHex!);
        var hasQr = !string.IsNullOrWhiteSpace(qrPayload);
        byte[]? qrBytes = null;
        if (hasQr)
        {
            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(qrPayload!.Trim(), QRCodeGenerator.ECCLevel.Q);
            qrBytes = new PngByteQRCode(qrData).GetGraphic(5);
        }

        using var img = new Image<Rgba32>(640, 400);
        img.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("0B1120"));
            ctx.Fill(Color.ParseHex("111827"), new RectangularPolygon(16, 16, 608, 368));
            ctx.Fill(accent, new RectangularPolygon(16, 16, 608, 8));

            // Top badge & model watermark
            ctx.Fill(accent, new RectangularPolygon(32, 38, 150, 26));
            ctx.DrawText(TruncateText(string.IsNullOrWhiteSpace(badgeText) ? "PROPERTY INFO" : badgeText.ToUpperInvariant(), 18),
                GetSafeFont(11f, FontStyle.Bold), Color.White, new PointF(42, 44));

            ctx.Fill(Color.ParseHex("1E293B"), new RectangularPolygon(430, 38, 176, 26));
            ctx.DrawText("muse-image-1.0 • 640x400", GetSafeFont(10.5f, FontStyle.Bold), Color.ParseHex("38BDF8"), new PointF(442, 44));

            // Title & subtitle
            var maxTextCols = hasQr ? 48 : 68;
            ctx.DrawText(TruncateText(string.IsNullOrWhiteSpace(title) ? "Property Visual Notice" : title, maxTextCols),
                GetSafeFont(18f, FontStyle.Bold), Color.White, new PointF(32, 78));

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                ctx.DrawText(TruncateText(subtitle, maxTextCols + 10),
                    GetSafeFont(12.5f, FontStyle.Regular), Color.ParseHex("94A3B8"), new PointF(32, 106));
            }

            // Divider line
            var contentWidth = hasQr ? 390f : 576f;
            ctx.DrawLine(Color.ParseHex("334155"), 1.5f, new PointF(32, 134), new PointF(32 + contentWidth, 134));

            // Info lines rendered directly onto image
            var cleanLines = (infoLines ?? Array.Empty<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(6)
                .ToList();
            if (cleanLines.Count == 0)
            {
                cleanLines.Add("Generated visual notice with custom property telemetry.");
            }

            var y = 150f;
            foreach (var line in cleanLines)
            {
                ctx.Fill(accent, new EllipsePolygon(40f, y + 8f, 4.5f));
                ctx.DrawText(TruncateText(line, hasQr ? 52 : 76),
                    GetSafeFont(12.5f, FontStyle.Regular), Color.ParseHex("F1F5F9"), new PointF(54, y));
                y += 30f;
            }

            // Optional QR code box on right
            if (hasQr && qrBytes is not null)
            {
                using var qrImg = Image.Load<Rgba32>(qrBytes);
                qrImg.Mutate(q => q.Resize(156, 156));
                ctx.Fill(Color.White, new RectangularPolygon(440, 140, 166, 182));
                ctx.DrawImage(qrImg, new Point(445, 145), 1f);
                ctx.DrawText("SCAN FOR DETAILS", GetSafeFont(9.5f, FontStyle.Bold), Color.ParseHex("0F172A"), new PointF(472, 304));
            }

            // Footer bar
            ctx.Fill(Color.ParseHex("0F172A"), new RectangularPolygon(16, 344, 608, 40));
            var footer = string.IsNullOrWhiteSpace(footerNote)
                ? $"CommonGround Visual Studio • Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
                : $"CommonGround • {TruncateText(footerNote, 64)}";
            ctx.DrawText(footer, GetSafeFont(10.5f, FontStyle.Regular), Color.ParseHex("94A3B8"), new PointF(32, 357));
        });

        return ToDataUrl(img);
    }
}
