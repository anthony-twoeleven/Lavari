using BenchmarkDotNet.Attributes;
using Lavari.Application.Formatting;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Infrastructure.Documents;
using Lavari.Infrastructure.Media;
using SkiaSharp;

namespace Lavari.Benchmarks;

[MemoryDiagnoser]
public class MediaAndDocumentBenchmarks
{
    private readonly SkiaImageProcessingService _imageProcessor = new();
    private readonly SkiaPdfRenderer _pdfRenderer = new(new FormattingService());
    private readonly byte[] _sampleImageBytes;
    private readonly FinancialDocumentDto _sampleInvoice;

    public MediaAndDocumentBenchmarks()
    {
        using var bmp = new SKBitmap(2400, 1800);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Beige);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        _sampleImageBytes = data.ToArray();

        _sampleInvoice = new FinancialDocumentDto
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            JobName = "Benchmark Display Home",
            ClientName = "Benchmark Client Pty Ltd",
            DocumentType = FinancialDocumentType.Invoice,
            DocumentNumber = "INV-2026-0001",
            Status = "Draft",
            CalculationMode = "LineCalculated",
            Currency = "AUD",
            TaxBasisPoints = 1000,
            TaxLabel = "10% GST",
            DepositBasisPoints = 7000,
            DepositLabel = "70% DEPOSIT REQUIRED",
            SubtotalCents = 400_000,
            DiscountTotalCents = 0,
            CreditTotalCents = 0,
            TaxAmountCents = 40_000,
            CalculatedTotalCents = 440_000,
            AppliedTotalCents = 440_000,
            DepositAmountCents = 308_000,
            IssueDate = new DateOnly(2026, 11, 25),
            VersionNumber = 1,
            ConcurrencyToken = "tok",
            Lines =
            [
                new FinancialDocumentLineDto { Id = Guid.NewGuid(), LineSourceType = FinancialLineSourceType.Manual, Title = "Console Table", QuantityMilli = 1000, UnitLabel = "ea", UnitPriceCents = 150_000, LineTotalCents = 150_000, IsTaxable = true, SortOrder = 0 },
                new FinancialDocumentLineDto { Id = Guid.NewGuid(), LineSourceType = FinancialLineSourceType.Manual, Title = "Lounge Sofa", QuantityMilli = 1000, UnitLabel = "ea", UnitPriceCents = 250_000, LineTotalCents = 250_000, IsTaxable = true, SortOrder = 1 }
            ],
            Adjustments = []
        };
    }

    [Benchmark]
    public int ResizeImageTo1080pAndThumbnail()
        => _imageProcessor.ProcessForStorage(_sampleImageBytes, "image/jpeg").ProcessedBytes.Length;

    [Benchmark]
    public int RenderInvoicePdf()
        => _pdfRenderer.RenderFinancialDocumentPdf(_sampleInvoice, "Lavari Interior Design", "00 000 000 000", "BSB/Acc", "7 days", null).Length;
}
