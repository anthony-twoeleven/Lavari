using Lavari.Contracts.Common;

namespace Lavari.Contracts.Business;

public sealed record ProductAttributeDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Unit { get; init; }
}

public sealed record ProductVariantDto
{
    public required Guid Id { get; init; }
    public required Guid ProductId { get; init; }
    public required string VariantName { get; init; }
    public string? Sku { get; init; }
    public required IReadOnlyDictionary<string, string> IdentifierValues { get; init; }
    public required IReadOnlyList<ProductAttributeDto> Attributes { get; init; }
    public required string Currency { get; init; }
    public required long CostCents { get; init; }
    public required long RrpCents { get; init; }
    public Guid? ImageAssetId { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record ProductDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? SupplierName { get; init; }
    public string? SupplierUrl { get; init; }
    public string? Description { get; init; }
    public required string Currency { get; init; }
    public required long CostCents { get; init; }
    public required long RrpCents { get; init; }
    public DateTimeOffset? PriceObservedAtUtc { get; init; }
    public required IReadOnlyList<ProductAttributeDto> Attributes { get; init; }
    public required IReadOnlyList<string> VariantIdentifierKeys { get; init; }
    public Guid? PrimaryImageAssetId { get; init; }
    public required IReadOnlyList<ProductVariantDto> Variants { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required bool IsDeleted { get; init; }
    public string? PossibleDuplicateWarning { get; init; }
}

public sealed record UpsertProductRequest
{
    public required string Name { get; init; }
    public string? SupplierName { get; init; }
    public string? SupplierUrl { get; init; }
    public string? Description { get; init; }
    public required string Currency { get; init; }
    public long CostCents { get; init; }
    public long RrpCents { get; init; }
    public IReadOnlyList<ProductAttributeDto> Attributes { get; init; } = [];
    public IReadOnlyList<string> VariantIdentifierKeys { get; init; } = [];
    public Guid? PrimaryImageAssetId { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record UpsertProductVariantRequest
{
    public required string VariantName { get; init; }
    public string? Sku { get; init; }
    public IReadOnlyDictionary<string, string> IdentifierValues { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ProductAttributeDto> Attributes { get; init; } = [];
    public required string Currency { get; init; }
    public long CostCents { get; init; }
    public long RrpCents { get; init; }
    public Guid? ImageAssetId { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record JobLineItemDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? ProductVariantId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? RoomOrArea { get; init; }
    public string? SourceUrl { get; init; }
    public required IReadOnlyList<ProductAttributeDto> SelectedAttributes { get; init; }
    public string? RequirementsNote { get; init; }
    public required long QuantityMilli { get; init; }
    public required string Unit { get; init; }
    public required string Currency { get; init; }
    public required long CostCents { get; init; }
    public required long RrpCents { get; init; }
    public required long UnitSellPriceCents { get; init; }
    public Guid? ImageAssetId { get; init; }
    public string? Notes { get; init; }
    public required int SortOrder { get; init; }
    public required string ConcurrencyToken { get; init; }
}

public sealed record UpsertJobLineItemRequest
{
    public Guid? ProductId { get; init; }
    public Guid? ProductVariantId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? RoomOrArea { get; init; }
    public string? SourceUrl { get; init; }
    public IReadOnlyList<ProductAttributeDto> SelectedAttributes { get; init; } = [];
    public string? RequirementsNote { get; init; }
    public long QuantityMilli { get; init; } = 1000;
    public string Unit { get; init; } = "ea";
    public required string Currency { get; init; }
    public long CostCents { get; init; }
    public long RrpCents { get; init; }
    public long UnitSellPriceCents { get; init; }
    public Guid? ImageAssetId { get; init; }
    public string? Notes { get; init; }
    public int SortOrder { get; init; }
    public string? ConcurrencyToken { get; init; }
    public Guid? LeaseId { get; init; }
    public long? FencingVersion { get; init; }
}

public sealed record ImageAssociationDto
{
    public required Guid Id { get; init; }
    public Guid? ClientId { get; init; }
    public string? ClientName { get; init; }
    public Guid? JobId { get; init; }
    public string? JobName { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? MoodBoardId { get; init; }
    public Guid? ReceiptId { get; init; }
    public required ImageAssociationType AssociationType { get; init; }
}

public sealed record ImageAssetDto
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string ContentType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long ByteLength { get; init; }
    public required int ThumbWidth { get; init; }
    public required int ThumbHeight { get; init; }
    public string? SourceProvenance { get; init; }
    public required bool RightsConfirmed { get; init; }
    public required bool IsGlobalLibrary { get; init; }
    public required bool WasDownscaledFromLargerOriginal { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required IReadOnlyList<ImageAssociationDto> Associations { get; init; }
}

public sealed record ImageLibraryFilterRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 24;
    public string? Search { get; init; }
    public Guid? ClientId { get; init; }
    public Guid? JobId { get; init; }
    public bool GlobalOnly { get; init; }
    public string? SortBy { get; init; } // created, title, client, job
    public bool SortDescending { get; init; } = true;
}

public sealed record MoodBoardElementDto
{
    public required Guid Id { get; init; }
    public required MoodBoardElementType ElementType { get; init; }
    public Guid? ImageAssetId { get; init; }
    public string? TextContent { get; init; }
    public required int FontSizePx { get; init; }
    public required string TextColorHex { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required int ZIndex { get; init; }
    public string? Caption { get; init; }
    public Guid? SourceJobLineItemId { get; init; }
}

public sealed record MoodBoardBrandingDto
{
    public required string CompanyName { get; init; }
    public required string PrimaryColorHex { get; init; }
    public required string SurfaceColorHex { get; init; }
    public required string TextColorHex { get; init; }
    public required int Version { get; init; }
}

public sealed record MoodBoardCanvasDto
{
    public required Guid Id { get; init; }
    public required Guid MoodBoardId { get; init; }
    public required string Name { get; init; }
    public required int SortOrder { get; init; }
    public required int WidthPx { get; init; }
    public required int HeightPx { get; init; }
    public required MoodBoardBrandingDto Branding { get; init; }
    public required string BackgroundColorHex { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required IReadOnlyList<MoodBoardElementDto> Elements { get; init; }
}

public sealed record MoodBoardDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ConcurrencyToken { get; init; }
    public required IReadOnlyList<MoodBoardCanvasDto> Canvases { get; init; }
}

public sealed record UpsertMoodBoardRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record UpsertCanvasRequest
{
    public required string Name { get; init; }
    public int SortOrder { get; init; }
    public int WidthPx { get; init; } = 1600;
    public int HeightPx { get; init; } = 900;
    public string? BackgroundColorHex { get; init; }
    public string? ConcurrencyToken { get; init; }
}

public sealed record SaveCanvasElementsRequest
{
    public required string ConcurrencyToken { get; init; }
    public required IReadOnlyList<MoodBoardElementDto> Elements { get; init; }
}
