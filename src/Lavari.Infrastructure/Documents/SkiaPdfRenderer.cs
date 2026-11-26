using Lavari.Application.Abstractions;
using Lavari.Application.Services;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Domain.Money;
using SkiaSharp;

namespace Lavari.Infrastructure.Documents;

/// <summary>
/// Local open-source MIT-licensed PDF renderer using SkiaSharp SKDocument.CreatePdf.
/// Produces clean branded multi-page PDFs for Quotes, Invoices and Mood Boards with zero paid services or external processes.
/// </summary>
public sealed class SkiaPdfRenderer : IPdfRenderer
{
    private static readonly SKSamplingOptions HighQualitySampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private readonly IFormattingService _formatting;

    public string RendererVersion => "SkiaSharp-PDF-1.0";

    public SkiaPdfRenderer(IFormattingService formatting)
    {
        _formatting = formatting;
    }

    public byte[] RenderFinancialDocumentPdf(
        FinancialDocumentDto document,
        string companyName,
        string companyAbn,
        string paymentDetails,
        string paymentTerms,
        byte[]? logoPngBytes,
        string? locale = null)
    {
        using var stream = new SKDynamicMemoryWStream();
        using var pdf = SKDocument.CreatePdf(stream);

        // A4 Portrait in points (595 x 842)
        const float pageWidth = 595f;
        const float pageHeight = 842f;
        const float margin = 42f;
        const float contentWidth = pageWidth - (margin * 2);

        using var titleFont = new SKFont(Typefaces.Bold, 20);
        using var headingFont = new SKFont(Typefaces.Bold, 11);
        using var bodyFont = new SKFont(Typefaces.Normal, 9.5f);
        using var smallFont = new SKFont(Typefaces.Normal, 8.5f);

        using var darkPaint = new SKPaint { Color = new SKColor(0x1F, 0x1B, 0x16), IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = new SKColor(0x5E, 0x57, 0x4E), IsAntialias = true };
        using var accentPaint = new SKPaint { Color = new SKColor(0x67, 0x50, 0xA4), IsAntialias = true };
        using var linePaint = new SKPaint { Color = new SKColor(0xD8, 0xD2, 0xCA), StrokeWidth = 1, IsAntialias = true };
        using var headerBgPaint = new SKPaint { Color = new SKColor(0xF3, 0xEF, 0xEB), Style = SKPaintStyle.Fill };

        var canvas = pdf.BeginPage(pageWidth, pageHeight);
        float y = margin;

        // Company header & optional logo
        if (logoPngBytes is { Length: > 0 })
        {
            using var logoImg = SKImage.FromEncodedData(logoPngBytes);
            if (logoImg is not null)
            {
                var logoH = 40f;
                var logoW = logoImg.Width * (logoH / logoImg.Height);
                canvas.DrawImage(logoImg, new SKRect(margin, y, margin + logoW, y + logoH), HighQualitySampling);
            }
        }

        var docHeading = document.DocumentType == FinancialDocumentType.Quote ? "Q U O T E" : "I N V O I C E";
        if (document.Status == "Draft")
        {
            docHeading += "  (DRAFT)";
        }

        DrawLeft(canvas, companyName, margin, y + 54, headingFont, darkPaint);
        DrawLeft(canvas, $"ABN: {companyAbn}", margin, y + 68, smallFont, mutedPaint);
        DrawLeft(canvas, docHeading, pageWidth - margin - 200, y + 26, titleFont, accentPaint);
        DrawLeft(canvas, $"No: {document.DocumentNumber}", pageWidth - margin - 200, y + 46, bodyFont, darkPaint);
        DrawLeft(canvas, $"Date: {(document.IssueDate.HasValue ? _formatting.FormatCalendarDate(document.IssueDate.Value, locale) : "Draft")}", pageWidth - margin - 200, y + 60, bodyFont, mutedPaint);

        y += 84;
        canvas.DrawLine(margin, y, pageWidth - margin, y, linePaint);
        y += 18;

        // Client & Job block
        DrawLeft(canvas, $"CLIENT:  {document.ClientName}", margin, y, bodyFont, darkPaint);
        DrawLeft(canvas, $"PROJECT: {document.JobName}", margin, y + 16, bodyFont, darkPaint);
        if (!string.IsNullOrWhiteSpace(document.PoNumber))
        {
            DrawLeft(canvas, $"PO:      {document.PoNumber}", pageWidth - margin - 200, y, bodyFont, darkPaint);
        }
        if (document.DueDate.HasValue)
        {
            DrawLeft(canvas, $"DUE:     {_formatting.FormatCalendarDate(document.DueDate.Value, locale)}", pageWidth - margin - 200, y + 16, bodyFont, darkPaint);
        }
        y += 38;

        // Table Header
        void DrawTableHeader()
        {
            canvas.DrawRect(margin, y, contentWidth, 22, headerBgPaint);
            DrawLeft(canvas, "DESCRIPTION", margin + 6, y + 15, headingFont, darkPaint);
            DrawLeft(canvas, "RATE", margin + 300, y + 15, headingFont, darkPaint);
            DrawLeft(canvas, "QTY", margin + 385, y + 15, headingFont, darkPaint);
            DrawLeft(canvas, "TOTAL", margin + 435, y + 15, headingFont, darkPaint);
            y += 28;
        }

        DrawTableHeader();

        if (document.Lines.Count == 0)
        {
            DrawLeft(canvas, "Manual total-only document (no itemized lines).", margin + 6, y + 12, bodyFont, mutedPaint);
            y += 26;
        }
        else
        {
            foreach (var line in document.Lines.OrderBy(l => l.SortOrder))
            {
                if (y > pageHeight - 150)
                {
                    pdf.EndPage();
                    canvas = pdf.BeginPage(pageWidth, pageHeight);
                    y = margin;
                    DrawTableHeader();
                }

                var qtyText = (line.QuantityMilli / 1000m).ToString("0.###") + " " + line.UnitLabel;
                var rateText = _formatting.FormatMoney(new MoneyCents(line.UnitPriceCents, document.Currency), locale);
                var totalText = _formatting.FormatMoney(new MoneyCents(line.LineTotalCents, document.Currency), locale);

                DrawLeft(canvas, Truncate(line.Title, 48), margin + 6, y + 12, bodyFont, darkPaint);
                DrawLeft(canvas, rateText, margin + 300, y + 12, bodyFont, darkPaint);
                DrawLeft(canvas, qtyText, margin + 385, y + 12, bodyFont, darkPaint);
                DrawLeft(canvas, totalText, margin + 435, y + 12, bodyFont, darkPaint);
                y += 18;

                if (!string.IsNullOrWhiteSpace(line.Details))
                {
                    DrawLeft(canvas, Truncate(line.Details, 78), margin + 10, y + 10, smallFont, mutedPaint);
                    y += 14;
                }

                canvas.DrawLine(margin, y + 2, pageWidth - margin, y + 2, linePaint);
                y += 8;
            }
        }

        // Totals block
        if (y > pageHeight - 170)
        {
            pdf.EndPage();
            canvas = pdf.BeginPage(pageWidth, pageHeight);
            y = margin;
        }

        y += 12;
        float rightLabelX = margin + 280;
        float rightValX = margin + 435;

        void DrawSummaryRow(string label, string val, SKFont font, SKPaint paint)
        {
            DrawLeft(canvas, label, rightLabelX, y, font, paint);
            DrawLeft(canvas, val, rightValX, y, font, paint);
            y += 17;
        }

        if (document.CalculationMode != "TotalOnly")
        {
            DrawSummaryRow("SUBTOTAL", _formatting.FormatMoney(new MoneyCents(document.SubtotalCents, document.Currency), locale), bodyFont, darkPaint);
            if (document.DiscountTotalCents != 0)
            {
                DrawSummaryRow("DISCOUNTS", _formatting.FormatMoney(new MoneyCents(-document.DiscountTotalCents, document.Currency), locale), bodyFont, mutedPaint);
            }
            if (document.CreditTotalCents != 0)
            {
                DrawSummaryRow("CREDITS", _formatting.FormatMoney(new MoneyCents(-document.CreditTotalCents, document.Currency), locale), bodyFont, mutedPaint);
            }
            DrawSummaryRow(document.TaxLabel, _formatting.FormatMoney(new MoneyCents(document.TaxAmountCents, document.Currency), locale), bodyFont, darkPaint);
        }

        if (document.CalculationMode == "ManualOverride")
        {
            DrawSummaryRow("CALCULATED TOTAL", _formatting.FormatMoney(new MoneyCents(document.CalculatedTotalCents, document.Currency), locale), smallFont, mutedPaint);
            DrawSummaryRow($"APPLIED TOTAL ({document.Currency}, OVERRIDE)", _formatting.FormatMoney(new MoneyCents(document.AppliedTotalCents, document.Currency), locale), headingFont, accentPaint);
        }
        else
        {
            var label = document.CalculationMode == "TotalOnly"
                ? $"MANUAL TOTAL ({document.Currency})"
                : $"TOTAL ({document.Currency})";
            DrawSummaryRow(label, _formatting.FormatMoney(new MoneyCents(document.AppliedTotalCents, document.Currency), locale), headingFont, accentPaint);
        }

        if (document.DepositAmountCents > 0)
        {
            DrawSummaryRow(document.DepositLabel, _formatting.FormatMoney(new MoneyCents(document.DepositAmountCents, document.Currency), locale), bodyFont, darkPaint);
        }

        // Payment Details footer
        y += 12;
        canvas.DrawLine(margin, y, pageWidth - margin, y, linePaint);
        y += 16;
        DrawLeft(canvas, $"Payment Details: {Truncate(paymentDetails, 95)}", margin, y, smallFont, darkPaint);
        DrawLeft(canvas, $"Terms: {Truncate(paymentTerms, 95)}", margin, y + 14, smallFont, mutedPaint);
        if (!string.IsNullOrWhiteSpace(document.Notes))
        {
            DrawLeft(canvas, $"Notes: {Truncate(document.Notes, 95)}", margin, y + 28, smallFont, mutedPaint);
        }

        pdf.EndPage();
        pdf.Close();
        using var data = stream.DetachAsData();
        return data.ToArray();
    }

    public byte[] RenderMoodBoardPdf(
        MoodBoardDto board,
        IReadOnlyList<MoodBoardCanvasDto> canvases,
        IReadOnlyDictionary<Guid, byte[]> imageBytesByAssetId,
        byte[]? logoPngBytes)
    {
        using var stream = new SKDynamicMemoryWStream();
        using var pdf = SKDocument.CreatePdf(stream);

        // Landscape A4 in points (842 x 595) — one canvas per page, aspect-ratio preserved without cropping.
        const float pageWidth = 842f;
        const float pageHeight = 595f;
        const float headerHeight = 46f;
        const float margin = 24f;

        using var brandFont = new SKFont(Typefaces.Bold, 14);
        using var metaFont = new SKFont(Typefaces.Normal, 9.5f);

        foreach (var c in canvases)
        {
            var pdfCanvas = pdf.BeginPage(pageWidth, pageHeight);

            var bgColor = ParseColor(c.BackgroundColorHex, new SKColor(0xF9, 0xF7, 0xF4));
            var brandPrimary = ParseColor(c.Branding.PrimaryColorHex, new SKColor(0x67, 0x50, 0xA4));
            var brandText = ParseColor(c.Branding.TextColorHex, new SKColor(0x1F, 0x1B, 0x16));

            pdfCanvas.Clear(bgColor);

            // Company branding header bar
            using (var barPaint = new SKPaint { Color = brandPrimary, Style = SKPaintStyle.Fill })
            {
                pdfCanvas.DrawRect(0, 0, pageWidth, headerHeight, barPaint);
            }
            using (var headerTextPaint = new SKPaint { Color = SKColors.White, IsAntialias = true })
            {
                DrawLeft(pdfCanvas, c.Branding.CompanyName, margin, 29, brandFont, headerTextPaint);
                DrawLeft(pdfCanvas, $"{board.Name}  •  {c.Name}", pageWidth - margin - 260, 28, metaFont, headerTextPaint);
            }

            // Compute scale to fit canvas coordinate space into available page area while preserving exact aspect ratio
            var availW = pageWidth - (margin * 2);
            var availH = pageHeight - headerHeight - (margin * 2);
            var scale = Math.Min(availW / c.WidthPx, availH / c.HeightPx);
            var drawW = c.WidthPx * scale;
            var drawH = c.HeightPx * scale;
            var offsetX = margin + (availW - drawW) / 2f;
            var offsetY = headerHeight + margin + (availH - drawH) / 2f;

            // Subtle canvas frame
            using (var borderPaint = new SKPaint { Color = new SKColor(0xD8, 0xD2, 0xCA), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            {
                pdfCanvas.DrawRect(offsetX, offsetY, drawW, drawH, borderPaint);
            }

            foreach (var el in c.Elements.OrderBy(e => e.ZIndex))
            {
                var rect = new SKRect(
                    offsetX + (float)(el.X * scale),
                    offsetY + (float)(el.Y * scale),
                    offsetX + (float)((el.X + el.Width) * scale),
                    offsetY + (float)((el.Y + el.Height) * scale));

                if (el.ElementType == MoodBoardElementType.Image &&
                    el.ImageAssetId.HasValue &&
                    imageBytesByAssetId.TryGetValue(el.ImageAssetId.Value, out var imgBytes))
                {
                    using var img = SKImage.FromEncodedData(imgBytes);
                    if (img is not null)
                    {
                        var imgScale = Math.Min(rect.Width / img.Width, rect.Height / img.Height);
                        var iw = img.Width * imgScale;
                        var ih = img.Height * imgScale;
                        var dest = new SKRect(
                            rect.Left + (rect.Width - iw) / 2f,
                            rect.Top + (rect.Height - ih) / 2f,
                            rect.Left + (rect.Width + iw) / 2f,
                            rect.Top + (rect.Height + ih) / 2f);
                        pdfCanvas.DrawImage(img, dest, HighQualitySampling);
                    }
                }
                else if (el.ElementType == MoodBoardElementType.Text &&
                         !string.IsNullOrWhiteSpace(el.TextContent))
                {
                    using var textFont = new SKFont(Typefaces.Normal, Math.Max(7f, (float)(el.FontSizePx * scale)));
                    using var textPaint = new SKPaint { Color = ParseColor(el.TextColorHex, brandText), IsAntialias = true };
                    DrawLeft(pdfCanvas, el.TextContent, rect.Left + 4, rect.Top + textFont.Size + 4, textFont, textPaint);
                }
            }

            pdf.EndPage();
        }

        pdf.Close();
        using var data = stream.DetachAsData();
        return data.ToArray();
    }

    private static class Typefaces
    {
        public static readonly SKTypeface Bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        public static readonly SKTypeface Normal = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    }

    private static void DrawLeft(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint)
        => canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);

    private static SKColor ParseColor(string? hex, SKColor fallback)
        => !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var parsed) ? parsed : fallback;

    private static string Truncate(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";
}
