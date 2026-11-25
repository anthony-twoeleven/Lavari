using Lavari.Contracts.Admin;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Contracts.Security;
using Lavari.Domain.Security;

namespace Lavari.Application.Services;

public interface IAuthService
{
    Task<UserSessionDto> AuthenticateAsync(string email, string password, string editingSessionId, CancellationToken ct = default);
    Task<UserSessionDto?> LoadCurrentSessionAsync(Guid userId, string editingSessionId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, AccessLevel>> LoadEffectivePermissionsAsync(Guid userId, CancellationToken ct = default);
}

public interface IAdminService
{
    Task<PagedResult<UserAdminDto>> ListUsersAsync(PagedRequest request, CancellationToken ct = default);
    Task<UserAdminDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserAdminDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default);
    Task SoftDeleteUserAsync(Guid userId, string concurrencyToken, CancellationToken ct = default);
    Task RestoreUserAsync(Guid userId, string concurrencyToken, CancellationToken ct = default);

    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default);
    Task<RoleDto> CreateRoleAsync(UpsertRoleRequest request, CancellationToken ct = default);
    Task<RoleDto> UpdateRoleAsync(Guid roleId, UpsertRoleRequest request, CancellationToken ct = default);
    Task SoftDeleteRoleAsync(Guid roleId, string concurrencyToken, CancellationToken ct = default);

    Task<IReadOnlyList<ApplicationVariableDto>> ListVariablesAsync(CancellationToken ct = default);
    Task<string> GetVariableValueAsync(string key, string fallback = "", CancellationToken ct = default);
    Task<ApplicationVariableDto> UpsertVariableAsync(UpsertApplicationVariableRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<ErrorCodeDefinitionDto>> ListErrorCodesAsync(CancellationToken ct = default);
    Task<PagedResult<OperationalErrorOccurrenceDto>> ListErrorOccurrencesAsync(PagedRequest request, CancellationToken ct = default);
    Task LogErrorOccurrenceAsync(string errorCode, string correlationId, string? actorId, string? requestPath, string sanitizedDetail, string? stackTrace, CancellationToken ct = default);

    Task<PagedResult<AuditEntryDto>> ListAuditEntriesAsync(string? entityType, string? entityId, PagedRequest request, CancellationToken ct = default);
}

public interface IClientAndJobService
{
    Task<PagedResult<ClientDto>> ListClientsAsync(PagedRequest request, CancellationToken ct = default);
    Task<ClientDto> GetClientAsync(Guid id, CancellationToken ct = default);
    Task<ClientDto> CreateClientAsync(UpsertClientRequest request, CancellationToken ct = default);
    Task<ClientDto> UpdateClientAsync(Guid id, UpsertClientRequest request, CancellationToken ct = default);
    Task SoftDeleteClientAsync(Guid id, string concurrencyToken, CancellationToken ct = default);
    Task RestoreClientAsync(Guid id, string concurrencyToken, CancellationToken ct = default);

    Task<IReadOnlyList<JobGroupDto>> ListJobGroupsAsync(Guid? clientId, bool includeDeleted = false, CancellationToken ct = default);
    Task<JobGroupDto> CreateJobGroupAsync(UpsertJobGroupRequest request, CancellationToken ct = default);
    Task<JobGroupDto> UpdateJobGroupAsync(Guid id, UpsertJobGroupRequest request, CancellationToken ct = default);
    Task SoftDeleteJobGroupAsync(Guid id, string concurrencyToken, CancellationToken ct = default);
    Task RestoreJobGroupAsync(Guid id, string concurrencyToken, CancellationToken ct = default);

    Task<PagedResult<JobDto>> ListJobsAsync(Guid? clientId, Guid? jobGroupId, PagedRequest request, CancellationToken ct = default);
    Task<JobDto> GetJobAsync(Guid id, CancellationToken ct = default);
    Task<JobDto> CreateJobAsync(UpsertJobRequest request, CancellationToken ct = default);
    Task<JobDto> UpdateJobAsync(Guid id, UpsertJobRequest request, CancellationToken ct = default);
    Task SoftDeleteJobAsync(Guid id, string concurrencyToken, CancellationToken ct = default);
    Task RestoreJobAsync(Guid id, string concurrencyToken, CancellationToken ct = default);
}

public interface IJobDateService
{
    Task<IReadOnlyList<JobDateDto>> ListForJobAsync(Guid jobId, CancellationToken ct = default);
    Task<JobDateDto> CreateAsync(Guid jobId, UpsertJobDateRequest request, CancellationToken ct = default);
    Task<JobDateDto> UpdateAsync(Guid id, UpsertJobDateRequest request, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid id, string concurrencyToken, Guid? leaseId, long? fencingVersion, CancellationToken ct = default);
}

public interface ITimeTrackingService
{
    Task<IReadOnlyList<TimeEntryDto>> ListForJobAsync(Guid jobId, bool includeDeleted = false, CancellationToken ct = default);
    Task<IReadOnlyList<TimeEntryDto>> ListActiveTimersForCurrentUserAsync(CancellationToken ct = default);
    Task<StartTimerResultDto> StartTimerAsync(StartTimerRequest request, CancellationToken ct = default);
    Task<TimeEntryDto> PauseTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default);
    Task<TimeEntryDto> ResumeTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default);
    Task<TimeEntryDto> StopTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default);
    Task<TimeEntryDto> CancelTimerAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default);
    Task<TimerAdjustmentPreviewDto> PreviewStopAtAsync(Guid timeEntryId, DateTimeOffset effectiveTimeUtc, CancellationToken ct = default);
    Task<TimeEntryDto> StopAtAsync(Guid timeEntryId, TimerAtTimeRequest request, CancellationToken ct = default);
    Task<TimerAdjustmentPreviewDto> PreviewResumeAtAsync(Guid timeEntryId, DateTimeOffset effectiveTimeUtc, CancellationToken ct = default);
    Task<TimeEntryDto> ResumeAtAsync(Guid timeEntryId, TimerAtTimeRequest request, CancellationToken ct = default);
    Task<TimeEntryDto> CreateFlatEntryAsync(CreateFlatTimeEntryRequest request, CancellationToken ct = default);
    Task<TimeEntryDto> CreateRangeEntryAsync(CreateRangeTimeEntryRequest request, CancellationToken ct = default);
    Task<TimeEntryDto> UpdateEntryAsync(Guid timeEntryId, UpdateTimeEntryRequest request, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default);
    Task RestoreAsync(Guid timeEntryId, string concurrencyToken, CancellationToken ct = default);
}

public interface IProductAndLineItemService
{
    Task<PagedResult<ProductDto>> ListProductsAsync(PagedRequest request, CancellationToken ct = default);
    Task<ProductDto> GetProductAsync(Guid id, CancellationToken ct = default);
    Task<ProductDto> CreateProductAsync(UpsertProductRequest request, CancellationToken ct = default);
    Task<ProductDto> UpdateProductAsync(Guid id, UpsertProductRequest request, CancellationToken ct = default);
    Task SoftDeleteProductAsync(Guid id, string concurrencyToken, CancellationToken ct = default);
    Task<ProductVariantDto> AddVariantAsync(Guid productId, UpsertProductVariantRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<JobLineItemDto>> ListJobLineItemsAsync(Guid jobId, CancellationToken ct = default);
    Task<JobLineItemDto> AddJobLineItemAsync(Guid jobId, UpsertJobLineItemRequest request, CancellationToken ct = default);
    Task<JobLineItemDto> UpdateJobLineItemAsync(Guid id, UpsertJobLineItemRequest request, CancellationToken ct = default);
    Task SoftDeleteJobLineItemAsync(Guid id, string concurrencyToken, Guid? leaseId, long? fencingVersion, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntryDto>> GetLineItemTimelineAsync(Guid lineItemId, CancellationToken ct = default);
}

public sealed record ProcessedImageResult(
    byte[] ProcessedBytes,
    string ContentType,
    string Extension,
    int Width,
    int Height,
    byte[] ThumbnailBytes,
    int ThumbWidth,
    int ThumbHeight,
    bool WasDownscaled);

public interface IImageProcessingService
{
    ProcessedImageResult ProcessForStorage(ReadOnlySpan<byte> rawBytes, string? declaredContentType = null);
}

public interface IImageLibraryService
{
    Task<PagedResult<ImageAssetDto>> ListAsync(ImageLibraryFilterRequest filter, CancellationToken ct = default);
    Task<ImageAssetDto> UploadAsync(
        string title,
        string? description,
        string? sourceProvenance,
        bool rightsConfirmed,
        bool isGlobalLibrary,
        Guid? clientId,
        Guid? jobId,
        Guid? productId,
        Guid? moodBoardId,
        ReadOnlyMemory<byte> rawBytes,
        string? declaredContentType,
        CancellationToken ct = default);
    Task<ImageAssetDto> AddAssociationAsync(Guid imageAssetId, Guid? clientId, Guid? jobId, Guid? productId, ImageAssociationType associationType, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)> ReadImageBytesAsync(Guid imageAssetId, bool thumbnail, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid imageAssetId, string concurrencyToken, CancellationToken ct = default);
    Task RestoreAsync(Guid imageAssetId, string concurrencyToken, CancellationToken ct = default);
}

public interface IMoodBoardService
{
    Task<IReadOnlyList<MoodBoardDto>> ListForJobAsync(Guid jobId, CancellationToken ct = default);
    Task<MoodBoardDto> GetAsync(Guid boardId, CancellationToken ct = default);
    Task<MoodBoardDto> CreateBoardAsync(Guid jobId, UpsertMoodBoardRequest request, CancellationToken ct = default);
    Task<MoodBoardCanvasDto> AddCanvasAsync(Guid boardId, UpsertCanvasRequest request, CancellationToken ct = default);
    Task<MoodBoardCanvasDto> UpdateCanvasMetaAsync(Guid canvasId, UpsertCanvasRequest request, CancellationToken ct = default);
    Task<MoodBoardCanvasDto> SaveCanvasElementsAsync(Guid canvasId, SaveCanvasElementsRequest request, CancellationToken ct = default);
    Task<MoodBoardCanvasDto> ReapplyCurrentBrandingAsync(Guid canvasId, string concurrencyToken, CancellationToken ct = default);
    Task SoftDeleteCanvasAsync(Guid canvasId, string concurrencyToken, CancellationToken ct = default);
    Task<GeneratedArtifactDto> SaveAsPdfAsync(Guid boardId, Guid? singleCanvasId, CancellationToken ct = default);
}

public interface IFinancialDocumentService
{
    Task<IReadOnlyList<FinancialDocumentDto>> ListForJobAsync(Guid jobId, FinancialDocumentType? documentType = null, CancellationToken ct = default);
    Task<FinancialDocumentDto> GetAsync(Guid documentId, CancellationToken ct = default);
    Task<FinancialDocumentDto> CreateAsync(CreateFinancialDocumentRequest request, CancellationToken ct = default);
    Task<UpdateFinancialDocumentResultDto> UpdateAsync(Guid documentId, UpdateFinancialDocumentRequest request, CancellationToken ct = default);
    Task<FinancialDocumentDto> ConvertQuoteToInvoiceAsync(Guid quoteId, CancellationToken ct = default);
    Task<GeneratedArtifactDto> GenerateDocxAsync(Guid documentId, bool isDraft, CancellationToken ct = default);
    Task<GeneratedArtifactDto> SaveAsPdfAsync(Guid documentId, bool isDraft, CancellationToken ct = default);
    Task<FinancialDocumentDto> IssueAsync(Guid documentId, string concurrencyToken, Guid? leaseId, long? fencingVersion, CancellationToken ct = default);
    Task<IReadOnlyList<GeneratedArtifactDto>> ListArtifactsAsync(Guid jobId, ArtifactSourceType? sourceType = null, Guid? sourceId = null, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType, string FileName)> DownloadArtifactAsync(Guid artifactId, CancellationToken ct = default);

    Task<IReadOnlyList<ProcurementItemDto>> ListProcurementAsync(Guid jobId, CancellationToken ct = default);
    Task<ProcurementItemDto> CreateProcurementAsync(Guid jobId, UpsertProcurementItemRequest request, CancellationToken ct = default);
    Task<ProcurementItemDto> UpdateProcurementAsync(Guid id, UpsertProcurementItemRequest request, CancellationToken ct = default);
    Task<ProcurementItemDto> AddProcurementPaymentAsync(Guid procurementId, AddProcurementPaymentRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<ReceiptDto>> ListReceiptsAsync(Guid jobId, CancellationToken ct = default);
    Task<ReceiptDto> UploadReceiptAsync(
        Guid jobId,
        Guid? jobLineItemId,
        Guid? procurementItemId,
        string merchantName,
        DateOnly receiptDate,
        string currency,
        long amountCents,
        string? notes,
        ReadOnlyMemory<byte> imageBytes,
        string? contentType,
        CancellationToken ct = default);
}

public interface IWordTemplateService
{
    byte[] RenderDocument(byte[] templateDocxBytes, IReadOnlyDictionary<string, string> scalarValues, IReadOnlyList<IReadOnlyDictionary<string, string>> lineItems);
    IReadOnlyList<string> ExtractPlaceholders(byte[] templateDocxBytes);
}

public interface IPdfRenderer
{
    string RendererVersion { get; }
    byte[] RenderFinancialDocumentPdf(
        FinancialDocumentDto document,
        string companyName,
        string companyAbn,
        string paymentDetails,
        string paymentTerms,
        byte[]? logoPngBytes,
        string? locale = null);

    byte[] RenderMoodBoardPdf(
        MoodBoardDto board,
        IReadOnlyList<MoodBoardCanvasDto> canvases,
        IReadOnlyDictionary<Guid, byte[]> imageBytesByAssetId,
        byte[]? logoPngBytes);
}

public interface IForecastingService
{
    Task<ForecastReportDto> CalculateForecastAsync(ForecastRequest request, CancellationToken ct = default);
}

public interface IIntegrationService
{
    Task<IntegrationStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<LlmExtractionDraftDto> ExtractSupplierPageDraftAsync(SupplierCaptureRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<LlmExtractionDraftDto>> ListExtractionDraftsAsync(Guid? jobId, CancellationToken ct = default);
}
