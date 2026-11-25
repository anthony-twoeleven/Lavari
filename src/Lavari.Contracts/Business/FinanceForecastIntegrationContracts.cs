using Lavari.Contracts.Common;

namespace Lavari.Contracts.Business;

public sealed record FinancialDocumentLineDto
{
    public required Guid Id { get; init; }
    public required FinancialLineSourceType LineSourceType { get; init; }
    public Guid? SourceEntityId { get; init; }
    public string? SourceEntityVersion { get; init; }
    public required string Title { get; init; }
    public string? Details { get; init; }
    public required long QuantityMilli { get; init; }
    public required string UnitLabel { get; init; }
    public required long UnitPriceCents { get; init; }
    public long? InternalUnitCostCents { get; init; }
    public required long LineTotalCents { get; init; }
    public required bool IsTaxable { get; init; }
    public required int SortOrder { get; init; }
}

public sealed record FinancialDocumentAdjustmentDto
{
    public required Guid Id { get; init; }
    public required FinancialAdjustmentType AdjustmentType { get; init; }
    public required string Description { get; init; }
    public required long AmountCents { get; init; }
    public int? BasisPoints { get; init; }
    public required bool IsPreTax { get; init; }
    public required int SortOrder { get; init; }
}

public sealed record FinancialDocumentDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string JobName { get; init; }
    public required string ClientName { get; init; }
    public required FinancialDocumentType DocumentType { get; init; }
    public required string DocumentNumber { get; init; }
    public required string Status { get; init; } // Draft, Issued, Void
    public required string CalculationMode { get; init; } // LineCalculated, ManualOverride, TotalOnly
    public required string Currency { get; init; }
    public required int TaxBasisPoints { get; init; }
    public required string TaxLabel { get; init; }
    public required int DepositBasisPoints { get; init; }
    public required string DepositLabel { get; init; }
    public required long SubtotalCents { get; init; }
    public required long DiscountTotalCents { get; init; }
    public required long CreditTotalCents { get; init; }
    public required long TaxAmountCents { get; init; }
    public required long CalculatedTotalCents { get; init; }
    public required long AppliedTotalCents { get; init; }
    public required long DepositAmountCents { get; init; }
    public long? ManualTotalCents { get; init; }
    public string? ManualOverrideReason { get; init; }
    public Guid? SourceQuoteId { get; init; }
    public DateOnly? IssueDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public DateTimeOffset? IssuedAtUtc { get; init; }
    public string? IssuedBy { get; init; }
    public string? PoNumber { get; init; }
    public string? Notes { get; init; }
    public Guid? ApprovedWordArtifactId { get; init; }
    public Guid? ApprovedPdfArtifactId { get; init; }
    public required int VersionNumber { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required IReadOnlyList<FinancialDocumentLineDto> Lines { get; init; }
    public required IReadOnlyList<FinancialDocumentAdjustmentDto> Adjustments { get; init; }
}

public sealed record SaveFinancialDocumentLineInput
{
    public FinancialLineSourceType LineSourceType { get; init; } = FinancialLineSourceType.Manual;
    public Guid? SourceEntityId { get; init; }
    public string? SourceEntityVersion { get; init; }
    public required string Title { get; init; }
    public string? Details { get; init; }
    public long QuantityMilli { get; init; } = 1000;
    public string UnitLabel { get; init; } = "ea";
    public long UnitPriceCents { get; init; }
    public long? InternalUnitCostCents { get; init; }
    public bool IsTaxable { get; init; } = true;
    public int SortOrder { get; init; }
}

public sealed record SaveFinancialDocumentAdjustmentInput
{
    public required FinancialAdjustmentType AdjustmentType { get; init; }
    public required string Description { get; init; }
    public long AmountCents { get; init; }
    public int? BasisPoints { get; init; }
    public bool IsPreTax { get; init; } = true;
    public int SortOrder { get; init; }
}

public sealed record CreateFinancialDocumentRequest
{
    public required Guid JobId { get; init; }
    public required FinancialDocumentType DocumentType { get; init; }
    public string CalculationMode { get; init; } = "LineCalculated";
    public string? Currency { get; init; }
    public int? TaxBasisPoints { get; init; }
    public string? TaxLabel { get; init; }
    public int? DepositBasisPoints { get; init; }
    public string? DepositLabel { get; init; }
    public DateOnly? IssueDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? PoNumber { get; init; }
    public string? Notes { get; init; }
    public long? ManualTotalCents { get; init; }
    public string? ManualOverrideReason { get; init; }
    public IReadOnlyList<SaveFinancialDocumentLineInput> Lines { get; init; } = [];
    public IReadOnlyList<SaveFinancialDocumentAdjustmentInput> Adjustments { get; init; } = [];
}

public sealed record UpdateFinancialDocumentRequest
{
    public required string CalculationMode { get; init; }
    public int TaxBasisPoints { get; init; }
    public required string TaxLabel { get; init; }
    public int DepositBasisPoints { get; init; }
    public required string DepositLabel { get; init; }
    public DateOnly? IssueDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? PoNumber { get; init; }
    public string? Notes { get; init; }
    public long? ManualTotalCents { get; init; }
    public string? ManualOverrideReason { get; init; }
    public IReadOnlyList<SaveFinancialDocumentLineInput> Lines { get; init; } = [];
    public IReadOnlyList<SaveFinancialDocumentAdjustmentInput> Adjustments { get; init; } = [];
    public required string ConcurrencyToken { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
    public bool ConfirmTotalChange { get; init; }
    public long? ExpectedPreviousCalculatedCents { get; init; }
    public long? ExpectedPreviousAppliedCents { get; init; }
    public long? ExpectedProposedCalculatedCents { get; init; }
    public long? ExpectedProposedAppliedCents { get; init; }
}

public sealed record TotalChangeWarningDto
{
    public required bool RequiresConfirmation { get; init; }
    public required long PreviousCalculatedTotalCents { get; init; }
    public required long PreviousAppliedTotalCents { get; init; }
    public required long ProposedCalculatedTotalCents { get; init; }
    public required long ProposedAppliedTotalCents { get; init; }
    public required string Currency { get; init; }
    public required string Message { get; init; }
}

public sealed record UpdateFinancialDocumentResultDto
{
    public required bool RequiresTotalChangeConfirmation { get; init; }
    public TotalChangeWarningDto? Warning { get; init; }
    public FinancialDocumentDto? Document { get; init; }
}

public sealed record GeneratedArtifactDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required ArtifactSourceType SourceType { get; init; }
    public required Guid SourceId { get; init; }
    public required string Format { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long ByteLength { get; init; }
    public required bool IsDraft { get; init; }
    public required string RendererVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record ProcurementPaymentDto
{
    public required Guid Id { get; init; }
    public required string Description { get; init; }
    public required string Currency { get; init; }
    public required long AmountCents { get; init; }
    public DateTimeOffset? PaidAtUtc { get; init; }
    public DateOnly? DueDate { get; init; }
    public required string Status { get; init; }
    public Guid? ReceiptId { get; init; }
    public string? Reference { get; init; }
}

public sealed record ProcurementItemDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public Guid? JobLineItemId { get; init; }
    public required string SupplierName { get; init; }
    public string? OrderReference { get; init; }
    public required string Status { get; init; }
    public required long OrderedQuantityMilli { get; init; }
    public required long ReceivedQuantityMilli { get; init; }
    public required string Currency { get; init; }
    public required long TotalCostCents { get; init; }
    public required long PaidCents { get; init; }
    public required long OutstandingCents { get; init; }
    public DateOnly? ExpectedDeliveryDate { get; init; }
    public DateOnly? ActualDeliveryDate { get; init; }
    public string? ShippingDetails { get; init; }
    public string? InstallationDetails { get; init; }
    public string? Notes { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required IReadOnlyList<ProcurementPaymentDto> Payments { get; init; }
}

public sealed record UpsertProcurementItemRequest
{
    public Guid? JobLineItemId { get; init; }
    public required string SupplierName { get; init; }
    public string? OrderReference { get; init; }
    public required string Status { get; init; }
    public long OrderedQuantityMilli { get; init; } = 1000;
    public long ReceivedQuantityMilli { get; init; }
    public required string Currency { get; init; }
    public long TotalCostCents { get; init; }
    public DateOnly? ExpectedDeliveryDate { get; init; }
    public DateOnly? ActualDeliveryDate { get; init; }
    public string? ShippingDetails { get; init; }
    public string? InstallationDetails { get; init; }
    public string? Notes { get; init; }
    public string? ConcurrencyToken { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
}

public sealed record AddProcurementPaymentRequest
{
    public required string Description { get; init; }
    public required string Currency { get; init; }
    public long AmountCents { get; init; }
    public DateTimeOffset? PaidAtUtc { get; init; }
    public DateOnly? DueDate { get; init; }
    public string Status { get; init; } = "Paid";
    public Guid? ReceiptId { get; init; }
    public string? Reference { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
}

public sealed record ReceiptDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public Guid? JobLineItemId { get; init; }
    public Guid? ProcurementItemId { get; init; }
    public required string MerchantName { get; init; }
    public required DateOnly ReceiptDate { get; init; }
    public required string Currency { get; init; }
    public required long AmountCents { get; init; }
    public string? Notes { get; init; }
    public required Guid ImageAssetId { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record ForecastRequest
{
    public Guid? JobId { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public string ReportingTimeZone { get; init; } = "Australia/Sydney";
    public string Currency { get; init; } = "AUD";
}

public sealed record ForecastSourceItemDto
{
    public required Guid JobId { get; init; }
    public required string JobName { get; init; }
    public required ForecastSourceType SourceType { get; init; }
    public required Guid SourceId { get; init; }
    public required string Label { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public required bool IsActual { get; init; }
    public required string Direction { get; init; } // Revenue, Cost
    public required long AmountCents { get; init; }
    public required string Currency { get; init; }
}

public sealed record ForecastReportDto
{
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required string ReportingTimeZone { get; init; }
    public required string Currency { get; init; }
    public required long PlannedRevenueCents { get; init; }
    public required long ActualRevenueCents { get; init; }
    public required long PlannedCostCents { get; init; }
    public required long ActualCostCents { get; init; }
    public required long PlannedProfitCents { get; init; }
    public required long ActualProfitCents { get; init; }
    public required int IncompleteRecordCount { get; init; }
    public required string Assumptions { get; init; }
    public required IReadOnlyList<ForecastSourceItemDto> Sources { get; init; }
}

public sealed record IntegrationStatusDto
{
    public required bool GmailConfigured { get; init; }
    public required bool GmailConnectedForCurrentUser { get; init; }
    public string? GmailConnectedAccount { get; init; }
    public required bool GeminiConfigured { get; init; }
    public required string GeminiModel { get; init; }
    public required string StatusSummary { get; init; }
}

public sealed record SupplierCaptureRequest
{
    public Guid? JobId { get; init; }
    public required string SourceUrl { get; init; }
    public required string PageTextContent { get; init; }
    public bool UserConfirmedConsent { get; init; }
}

public sealed record LlmExtractionDraftDto
{
    public required Guid Id { get; init; }
    public Guid? JobId { get; init; }
    public required string SourceUrl { get; init; }
    public required string Status { get; init; }
    public required string ProviderName { get; init; }
    public required string ModelName { get; init; }
    public required string ExtractedJson { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
