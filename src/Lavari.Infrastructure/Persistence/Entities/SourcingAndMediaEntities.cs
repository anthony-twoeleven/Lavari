using Lavari.Contracts.Common;

namespace Lavari.Infrastructure.Persistence.Entities;

public sealed class ProductEntity : AuditableSoftDeletableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public string? SupplierUrl { get; set; }
    public string? Description { get; set; }
    public string Currency { get; set; } = "AUD";
    public long CostCents { get; set; }
    public long RrpCents { get; set; }
    public DateTimeOffset? PriceObservedAtUtc { get; set; }
    public int AttributesSchemaVersion { get; set; } = 1;
    public string AttributesJson { get; set; } = "[]";
    public string VariantIdentifierKeysJson { get; set; } = "[]";
    public string SourceImageUrlsJson { get; set; } = "[]";
    public Guid? PrimaryImageAssetId { get; set; }
    public List<ProductVariantEntity> Variants { get; set; } = [];
}

public sealed class ProductVariantEntity : AuditableSoftDeletableEntity
{
    public Guid ProductId { get; set; }
    public ProductEntity Product { get; set; } = null!;
    public string VariantName { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string IdentifierValuesJson { get; set; } = "{}";
    public string AttributesJson { get; set; } = "[]";
    public string Currency { get; set; } = "AUD";
    public long CostCents { get; set; }
    public long RrpCents { get; set; }
    public Guid? ImageAssetId { get; set; }
}

public sealed class JobLineItemEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public JobEntity Job { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public Guid? ProductVariantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RoomOrArea { get; set; }
    public string? SourceUrl { get; set; }
    public string SelectedAttributesJson { get; set; } = "[]";
    public string? RequirementsNote { get; set; }
    public long QuantityMilli { get; set; } = 1000;
    public string Unit { get; set; } = "ea";
    public string Currency { get; set; } = "AUD";
    public long CostCents { get; set; }
    public long RrpCents { get; set; }
    public long UnitSellPriceCents { get; set; }
    public Guid? ImageAssetId { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ImageAssetEntity : AuditableSoftDeletableEntity
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string ThumbnailStorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    public long ByteLength { get; set; }
    public int ThumbWidth { get; set; }
    public int ThumbHeight { get; set; }
    public string? SourceProvenance { get; set; }
    public bool RightsConfirmed { get; set; }
    public Guid UploadedByUserId { get; set; }
    public bool IsGlobalLibrary { get; set; } = true;
    public Guid? AiSourceImageAssetId { get; set; }
    public List<ImageAssociationEntity> Associations { get; set; } = [];
}

public sealed class ImageAssociationEntity : AuditableSoftDeletableEntity
{
    public Guid ImageAssetId { get; set; }
    public ImageAssetEntity ImageAsset { get; set; } = null!;
    public Guid? ClientId { get; set; }
    public Guid? JobId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? MoodBoardId { get; set; }
    public Guid? ReceiptId { get; set; }
    public ImageAssociationType AssociationType { get; set; } = ImageAssociationType.Library;
}

public sealed class MoodBoardEntity : AuditableSoftDeletableEntity
{
    public Guid JobId { get; set; }
    public JobEntity Job { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<MoodBoardCanvasEntity> Canvases { get; set; } = [];
}

public sealed class MoodBoardCanvasEntity : AuditableSoftDeletableEntity
{
    public Guid MoodBoardId { get; set; }
    public MoodBoardEntity MoodBoard { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int WidthPx { get; set; } = 1600;
    public int HeightPx { get; set; } = 900;
    public int BrandingVersion { get; set; } = 1;
    public string BrandingSnapshotJson { get; set; } = "{}";
    public string BackgroundColorHex { get; set; } = "#F9F7F4";
    public List<MoodBoardElementEntity> Elements { get; set; } = [];
}

public sealed class MoodBoardElementEntity : AuditableSoftDeletableEntity
{
    public Guid CanvasId { get; set; }
    public MoodBoardCanvasEntity Canvas { get; set; } = null!;
    public MoodBoardElementType ElementType { get; set; } = MoodBoardElementType.Image;
    public Guid? ImageAssetId { get; set; }
    public string? TextContent { get; set; }
    public int FontSizePx { get; set; } = 24;
    public string TextColorHex { get; set; } = "#1F1B16";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 220;
    public int ZIndex { get; set; }
    public string? Caption { get; set; }
    public Guid? SourceJobLineItemId { get; set; }
}
