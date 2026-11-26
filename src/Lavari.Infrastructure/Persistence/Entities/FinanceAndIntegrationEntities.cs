using Lavari.Contracts.Common;

namespace Lavari.Infrastructure.Persistence.Entities;

public sealed class FinancialDocumentEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public JobEntity Job { get; set; } = null!;
    public FinancialDocumentType DocumentType { get; set; } = FinancialDocumentType.Quote;
    public string DocumentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft"; // Draft, Issued, Void
    public string CalculationMode { get; set; } = "LineCalculated"; // LineCalculated, ManualOverride, TotalOnly
    public string Currency { get; set; } = "AUD";
    public int TaxBasisPoints { get; set; } = 1000; // 10% GST
    public string TaxLabel { get; set; } = "10% GST";
    public int DepositBasisPoints { get; set; } = 7000; // 70% deposit
    public string DepositLabel { get; set; } = "70% DEPOSIT REQUIRED";
    public long SubtotalCents { get; set; }
    public long DiscountTotalCents { get; set; }
    public long CreditTotalCents { get; set; }
    public long TaxAmountCents { get; set; }
    public long CalculatedTotalCents { get; set; }
    public long AppliedTotalCents { get; set; }
    public long DepositAmountCents { get; set; }
    public long? ManualTotalCents { get; set; }
    public string? ManualOverrideReason { get; set; }
    public Guid? SourceQuoteId { get; set; }
    public Guid? TemplateId { get; set; }
    public int TemplateVersion { get; set; } = 1;
    public DateTimeOffset? IssuedAtUtc { get; set; }
    public string? IssuedBy { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? PoNumber { get; set; }
    public string? Notes { get; set; }
    public string? PaymentDetailsSnapshot { get; set; }
    public string? PaymentTermsSnapshot { get; set; }
    public Guid? ApprovedWordArtifactId { get; set; }
    public Guid? ApprovedPdfArtifactId { get; set; }
    public int VersionNumber { get; set; } = 1;

    public List<FinancialDocumentLineEntity> Lines { get; set; } = [];
    public List<FinancialDocumentAdjustmentEntity> Adjustments { get; set; } = [];
    public List<FinancialDocumentSnapshotEntity> Snapshots { get; set; } = [];
}

public sealed class FinancialDocumentLineEntity : AuditableSoftDeletableEntity
{
    public Guid FinancialDocumentId { get; set; }
    public FinancialDocumentEntity FinancialDocument { get; set; } = null!;
    public FinancialLineSourceType LineSourceType { get; set; } = FinancialLineSourceType.Manual;
    public Guid? SourceEntityId { get; set; }
    public string? SourceEntityVersion { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Details { get; set; }
    public long QuantityMilli { get; set; } = 1000; // 1000 = 1.000
    public string UnitLabel { get; set; } = "ea";
    public long UnitPriceCents { get; set; }
    public long? InternalUnitCostCents { get; set; }
    public long LineTotalCents { get; set; }
    public bool IsTaxable { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class FinancialDocumentAdjustmentEntity : AuditableSoftDeletableEntity
{
    public Guid FinancialDocumentId { get; set; }
    public FinancialDocumentEntity FinancialDocument { get; set; } = null!;
    public FinancialAdjustmentType AdjustmentType { get; set; } = FinancialAdjustmentType.Discount;
    public string Description { get; set; } = string.Empty;
    public long AmountCents { get; set; }
    public int? BasisPoints { get; set; }
    public bool IsPreTax { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class FinancialDocumentSnapshotEntity : AuditableSoftDeletableEntity
{
    public Guid FinancialDocumentId { get; set; }
    public FinancialDocumentEntity FinancialDocument { get; set; } = null!;
    public int VersionNumber { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public string IssuedBy { get; set; } = string.Empty;
}

public sealed class TimeBillingAllocationEntity : AuditableSoftDeletableEntity
{
    public Guid TimeEntryId { get; set; }
    public TimeEntryEntity TimeEntry { get; set; } = null!;
    public Guid FinancialDocumentId { get; set; }
    public FinancialDocumentEntity FinancialDocument { get; set; } = null!;
    public long AllocatedSeconds { get; set; }
    public long BilledAmountCents { get; set; }
    public string Currency { get; set; } = "AUD";
    public bool IsActive { get; set; } = true;
}

public sealed class ProcurementItemEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public JobEntity Job { get; set; } = null!;
    public Guid? JobLineItemId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string? OrderReference { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Ordered, Shipped, Delivered, Installed, Cancelled
    public long OrderedQuantityMilli { get; set; } = 1000;
    public long ReceivedQuantityMilli { get; set; }
    public string Currency { get; set; } = "AUD";
    public long TotalCostCents { get; set; }
    public DateOnly? ExpectedDeliveryDate { get; set; }
    public DateOnly? ActualDeliveryDate { get; set; }
    public string? ShippingDetails { get; set; }
    public string? InstallationDetails { get; set; }
    public string? Notes { get; set; }
    public List<ProcurementPaymentEntity> Payments { get; set; } = [];
}

public sealed class ProcurementPaymentEntity : AuditableSoftDeletableEntity
{
    public Guid ProcurementItemId { get; set; }
    public ProcurementItemEntity ProcurementItem { get; set; } = null!;
    public string Description { get; set; } = string.Empty;
    public string Currency { get; set; } = "AUD";
    public long AmountCents { get; set; }
    public DateTimeOffset? PaidAtUtc { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Paid
    public Guid? ReceiptId { get; set; }
    public string? Reference { get; set; }
}

public sealed class ReceiptEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public JobEntity Job { get; set; } = null!;
    public Guid? JobLineItemId { get; set; }
    public Guid? ProcurementItemId { get; set; }
    public string MerchantName { get; set; } = string.Empty;
    public DateOnly ReceiptDate { get; set; }
    public string Currency { get; set; } = "AUD";
    public long AmountCents { get; set; }
    public string? Notes { get; set; }
    public Guid ImageAssetId { get; set; }
    public Guid UploadedByUserId { get; set; }
}

public sealed class DocumentTemplateEntity : AuditableSoftDeletableEntity
{
    public string Name { get; set; } = string.Empty;
    public FinancialDocumentType DocumentType { get; set; } = FinancialDocumentType.Invoice;
    public int Version { get; set; } = 1;
    public string StorageKey { get; set; } = string.Empty;
    public string PlaceholdersJson { get; set; } = "[]";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class GeneratedArtifactEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public ArtifactSourceType SourceType { get; set; }
    public Guid SourceId { get; set; }
    public string SourceVersion { get; set; } = string.Empty;
    public string Format { get; set; } = "Pdf"; // Docx, Pdf
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public long ByteLength { get; set; }
    public bool IsDraft { get; set; }
    public string RendererVersion { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
}

public sealed class EmailConnectionEntity : AuditableSoftDeletableEntity
{
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "Gmail";
    public string AccountEmail { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public DateTimeOffset? ConsentGrantedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class JobEmailLabelEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public Guid EmailConnectionId { get; set; }
    public string GmailLabelId { get; set; } = string.Empty;
    public string GmailLabelName { get; set; } = string.Empty;
}

public sealed class ForecastSnapshotEntity : AuditableSoftDeletableEntity
{
    public string Name { get; set; } = string.Empty;
    public Guid? JobId { get; set; }
    public string ReportingTimeZone { get; set; } = "Australia/Sydney";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Currency { get; set; } = "AUD";
    public long PlannedRevenueCents { get; set; }
    public long ActualRevenueCents { get; set; }
    public long PlannedCostCents { get; set; }
    public long ActualCostCents { get; set; }
    public string AssumptionsJson { get; set; } = "{}";
    public string CalculationVersion { get; set; } = "1.0";
    public string SourceBreakdownJson { get; set; } = "[]";
}

public sealed class LlmExtractionDraftEntity : AuditableSoftDeletableEntity
{
    public Guid? JobId { get; set; }
    public Guid? AcceptedProductId { get; set; }
    [ExcludeFromAudit]
    public string SourceUrl { get; set; } = string.Empty;
    public string CapturedContentSanitized { get; set; } = string.Empty;
    [ExcludeFromAudit]
    public string ExtractedJson { get; set; } = "{}";
    public string Status { get; set; } = "PendingReview"; // Processing, PendingReview, Approved, Rejected, Failed
    public string ProviderName { get; set; } = "Gemini";
    public string ModelName { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public DateTimeOffset? ProviderConsentGrantedAtUtc { get; set; }
}

public sealed class SupplierExtractionWorkEntity : AuditableSoftDeletableEntity
{
    public Guid DraftId { get; set; }
    public Guid InitiatingUserId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? JobId { get; set; }
    public Guid ReservationId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    [ExcludeFromAudit]
    public string SourceUrl { get; set; } = string.Empty;
    [ExcludeFromAudit]
    public string SelectedPageText { get; set; } = string.Empty;
    [ExcludeFromAudit]
    public string Prompt { get; set; } = string.Empty;
    public string State { get; set; } = "Queued";
}

public sealed class AiSpendReservationEntity : AuditableSoftDeletableEntity
{
    public Guid InitiatingUserId { get; set; }
    public Guid ServicePrincipalId { get; set; }
    public Guid ClientRequestId { get; set; }
    [ExcludeFromAudit]
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid? ClientId { get; set; }
    public Guid? JobId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateOnly BudgetDateUtc { get; set; }
    public string Currency { get; set; } = string.Empty;
    public long ReservedCents { get; set; }
    public string State { get; set; } = "Reserved";
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public int? TotalTokens { get; set; }
}
