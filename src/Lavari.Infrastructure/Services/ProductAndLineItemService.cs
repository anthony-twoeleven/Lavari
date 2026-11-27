using System.Text.Json;
using System.Globalization;
using Lavari.Application.Abstractions;
using Lavari.Application.Locking;
using Lavari.Application.Services;
using Lavari.Contracts.Admin;
using Lavari.Contracts.Business;
using Lavari.Contracts.Caching;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Domain.Errors;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Services;

public sealed class ProductAndLineItemService : IProductAndLineItemService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly IEditLockService _editLocks;
    private readonly IAppCacheService _cache;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    public ProductAndLineItemService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IEditLockService editLocks,
        IAppCacheService cache,
        TimeProvider timeProvider,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _editLocks = editLocks;
        _cache = cache;
        _timeProvider = timeProvider;
        _ids = ids;
    }

    public async Task<PagedResult<ProductDto>> ListProductsAsync(PagedRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ProductCatalog, request.IncludeDeleted ? AccessLevel.Delete : AccessLevel.Read);

        IQueryable<ProductEntity> query = request.IncludeDeleted ? _db.Products.IgnoreQueryFilters() : _db.Products;
        query = query.AsNoTracking().Include(p => p.Variants);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(term) ||
                                     (p.SupplierName != null && p.SupplierName.ToLower().Contains(term)) ||
                                     (p.Description != null && p.Description.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var page = request.NormalizedPage;
        var pageSize = request.NormalizedPageSize;
        var rows = await query.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return new PagedResult<ProductDto>
        {
            Items = rows.Select(p => MapProduct(p, null)).ToArray(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<ProductDto> GetProductAsync(Guid id, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ProductCatalog, AccessLevel.Read);
        var p = await _db.Products.AsNoTracking().Include(x => x.Variants).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundServiceException("Product not found.");
        return MapProduct(p, null);
    }

    public async Task<ProductDto> CreateProductAsync(UpsertProductRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ProductCatalog, AccessLevel.Alter);
        ValidateProductRequest(request);

        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        var name = request.Name.Trim();

        // Warn on possible duplicates without silently merging
        var possibleDup = await _db.Products.AsNoTracking()
            .Where(p => p.Name.ToLower() == name.ToLower() ||
                        (!string.IsNullOrWhiteSpace(request.SupplierUrl) && p.SupplierUrl == request.SupplierUrl.Trim()))
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

        var entity = new ProductEntity
        {
            Id = _ids.NewId(),
            Name = name,
            SupplierName = request.SupplierName?.Trim(),
            SupplierUrl = request.SupplierUrl?.Trim(),
            Description = request.Description?.Trim(),
            Currency = currency,
            CostCents = request.CostCents,
            RrpCents = request.RrpCents,
            PriceObservedAtUtc = _timeProvider.GetUtcNow(),
            AttributesSchemaVersion = 2,
            AttributesJson = JsonSerializer.Serialize(request.Attributes, JsonOpts),
            VariantIdentifierKeysJson = JsonSerializer.Serialize(request.VariantIdentifierKeys, JsonOpts),
            SourceImageUrlsJson = JsonSerializer.Serialize(request.SourceImageUrls, JsonOpts),
            PrimaryImageAssetId = request.PrimaryImageAssetId
        };
        _db.Products.Add(entity);
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.ProductCatalog));

        var warning = possibleDup is not null ? $"A similar product '{possibleDup}' already exists in the catalog." : null;
        return MapProduct(entity, warning);
    }

    public async Task<ProductDto> UpdateProductAsync(Guid id, UpsertProductRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ProductCatalog, AccessLevel.Alter);
        ValidateProductRequest(request);

        var entity = await _db.Products.Include(x => x.Variants).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundServiceException("Product not found.");

        if (!string.Equals(entity.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Product was modified by another user.");
        }

        entity.Name = request.Name.Trim();
        entity.SupplierName = request.SupplierName?.Trim();
        entity.SupplierUrl = request.SupplierUrl?.Trim();
        entity.Description = request.Description?.Trim();
        entity.Currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        entity.CostCents = request.CostCents;
        entity.RrpCents = request.RrpCents;
        entity.PriceObservedAtUtc = _timeProvider.GetUtcNow();
        entity.AttributesSchemaVersion = 2;
        entity.AttributesJson = JsonSerializer.Serialize(request.Attributes, JsonOpts);
        entity.VariantIdentifierKeysJson = JsonSerializer.Serialize(request.VariantIdentifierKeys, JsonOpts);
        entity.SourceImageUrlsJson = JsonSerializer.Serialize(request.SourceImageUrls, JsonOpts);
        entity.PrimaryImageAssetId = request.PrimaryImageAssetId;

        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.ProductCatalog));
        return MapProduct(entity, null);
    }

    public async Task SoftDeleteProductAsync(Guid id, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ProductCatalog, AccessLevel.Delete);
        var entity = await _db.Products.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundServiceException("Product not found.");

        if (!string.Equals(entity.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Product was modified by another user.");
        }

        entity.DeletedAtUtc = _timeProvider.GetUtcNow();
        entity.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
        _cache.InvalidateScope(CacheScope.ForModule(ModuleKeys.ProductCatalog));
    }

    public async Task<ProductVariantDto> AddVariantAsync(Guid productId, UpsertProductVariantRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ProductCatalog, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.VariantName))
        {
            throw new ValidationServiceException("Variant name is required.");
        }

        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        _ = new MoneyCents(request.CostCents, currency);
        _ = new MoneyCents(request.RrpCents, currency);
        ValidateAttributes(request.Attributes);

        if (!await _db.Products.AnyAsync(p => p.Id == productId, ct))
        {
            throw new NotFoundServiceException("Product not found.");
        }

        var variant = new ProductVariantEntity
        {
            Id = _ids.NewId(),
            ProductId = productId,
            VariantName = request.VariantName.Trim(),
            Sku = request.Sku?.Trim(),
            IdentifierValuesJson = JsonSerializer.Serialize(request.IdentifierValues, JsonOpts),
            AttributesJson = JsonSerializer.Serialize(request.Attributes, JsonOpts),
            Currency = currency,
            CostCents = request.CostCents,
            RrpCents = request.RrpCents,
            ImageAssetId = request.ImageAssetId
        };
        _db.ProductVariants.Add(variant);
        await _db.SaveChangesAsync(ct);
        return MapVariant(variant);
    }

    public async Task<IReadOnlyList<JobLineItemDto>> ListJobLineItemsAsync(Guid jobId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.JobLineItems, AccessLevel.Read);

        var rows = await _db.JobLineItems.AsNoTracking()
            .Where(x => x.JobId == jobId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return rows.Select(MapLineItem).ToArray();
    }

    public async Task<JobLineItemDto> AddJobLineItemAsync(Guid jobId, UpsertJobLineItemRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.JobLineItems, AccessLevel.Alter);
        if (request.ClientId.HasValue)
        {
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
        }
        if (!request.LeaseId.HasValue || !request.FencingVersion.HasValue)
        {
            throw new ConflictServiceException("An active editing lock is required.", ErrorCodes.EditLockRequired);
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, jobId, request.LeaseId, request.FencingVersion, ct);

        ValidateLineItemRequest(request);
        var clientId = await _db.Jobs.Where(j => j.Id == jobId).Select(j => (Guid?)j.ClientId).FirstOrDefaultAsync(ct);
        if (!clientId.HasValue)
        {
            throw new NotFoundServiceException("Job not found.");
        }
        if (request.ClientId.HasValue && request.ClientId != clientId)
        {
            throw new ValidationServiceException("The selected job does not belong to the selected client.");
        }

        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        var entity = new JobLineItemEntity
        {
            Id = _ids.NewId(),
            JobId = jobId,
            ProductId = request.ProductId,
            ProductVariantId = request.ProductVariantId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            RoomOrArea = request.RoomOrArea?.Trim(),
            SourceUrl = request.SourceUrl?.Trim(),
            SelectedAttributesJson = JsonSerializer.Serialize(request.SelectedAttributes, JsonOpts),
            RequirementsNote = request.RequirementsNote?.Trim(),
            QuantityMilli = request.QuantityMilli,
            Unit = string.IsNullOrWhiteSpace(request.Unit) ? "ea" : request.Unit.Trim(),
            Currency = currency,
            CostCents = request.CostCents,
            RrpCents = request.RrpCents,
            UnitSellPriceCents = request.UnitSellPriceCents,
            ImageAssetId = request.ImageAssetId,
            Notes = request.Notes?.Trim(),
            SortOrder = request.SortOrder
        };
        _db.JobLineItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, jobId, request.LeaseId, request.FencingVersion, ct);
        await transaction.CommitAsync(ct);
        return MapLineItem(entity);
    }

    public async Task<JobLineItemDto> UpdateJobLineItemAsync(Guid id, UpsertJobLineItemRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobLineItems, AccessLevel.Alter);
        var entity = await _db.JobLineItems.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundServiceException("Job line item not found.");

        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, entity.JobId, request.LeaseId, request.FencingVersion, ct);
        ValidateLineItemRequest(request);

        if (!string.Equals(entity.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Line item was modified by another user.");
        }

        entity.ProductId = request.ProductId;
        entity.ProductVariantId = request.ProductVariantId;
        entity.Title = request.Title.Trim();
        entity.Description = request.Description?.Trim();
        entity.RoomOrArea = request.RoomOrArea?.Trim();
        entity.SourceUrl = request.SourceUrl?.Trim();
        entity.SelectedAttributesJson = JsonSerializer.Serialize(request.SelectedAttributes, JsonOpts);
        entity.RequirementsNote = request.RequirementsNote?.Trim();
        entity.QuantityMilli = request.QuantityMilli;
        entity.Unit = string.IsNullOrWhiteSpace(request.Unit) ? "ea" : request.Unit.Trim();
        entity.Currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        entity.CostCents = request.CostCents;
        entity.RrpCents = request.RrpCents;
        entity.UnitSellPriceCents = request.UnitSellPriceCents;
        entity.ImageAssetId = request.ImageAssetId;
        entity.Notes = request.Notes?.Trim();
        entity.SortOrder = request.SortOrder;

        await _db.SaveChangesAsync(ct);
        return MapLineItem(entity);
    }

    public async Task SoftDeleteJobLineItemAsync(Guid id, string concurrencyToken, Guid? leaseId, long? fencingVersion, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobLineItems, AccessLevel.Delete);
        var entity = await _db.JobLineItems.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundServiceException("Job line item not found.");

        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, entity.JobId, leaseId, fencingVersion, ct);
        if (!string.Equals(entity.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Line item was modified by another user.");
        }

        entity.DeletedAtUtc = _timeProvider.GetUtcNow();
        entity.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntryDto>> GetLineItemTimelineAsync(Guid lineItemId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.JobLineItems, AccessLevel.Read);
        var idStr = lineItemId.ToString("D");

        return await _db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityType == nameof(JobLineItemEntity) && a.EntityId == idStr)
            .OrderByDescending(a => a.OccurredAtUtc)
            .Select(a => new AuditEntryDto
            {
                Id = a.Id,
                EntityType = a.EntityType,
                EntityId = a.EntityId,
                Operation = a.Operation,
                OccurredAtUtc = a.OccurredAtUtc,
                ActorId = a.ActorId,
                ActorDisplayName = a.ActorDisplayName,
                CorrelationId = a.CorrelationId,
                TransactionId = a.TransactionId,
                SchemaVersion = a.SchemaVersion,
                BeforeJson = a.BeforeJson,
                AfterJson = a.AfterJson
            })
            .ToListAsync(ct);
    }

    private static void ValidateProductRequest(UpsertProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Product name is required.");
        }
        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        _ = new MoneyCents(request.CostCents, currency);
        _ = new MoneyCents(request.RrpCents, currency);

        ValidateAttributes(request.Attributes);

        if (request.SourceImageUrls is null || request.SourceImageUrls.Count > 10 || request.SourceImageUrls.Any(url =>
                string.IsNullOrWhiteSpace(url) || url.Length > 2000 ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo)))
        {
            throw new ValidationServiceException("Source image references must be up to 10 absolute HTTP(S) URLs without credentials.");
        }
    }

    private static void ValidateLineItemRequest(UpsertJobLineItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ValidationServiceException("Line item title is required.");
        }
        if (request.QuantityMilli <= 0)
        {
            throw new ValidationServiceException("Quantity must be greater than zero.");
        }
        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        _ = new MoneyCents(request.CostCents, currency);
        _ = new MoneyCents(request.RrpCents, currency);
        _ = new MoneyCents(request.UnitSellPriceCents, currency);
        ValidateAttributes(request.SelectedAttributes);
    }

    private static void ValidateAttributes(IReadOnlyList<ProductAttributeDto>? attributes)
    {
        if (attributes is null || attributes.Count > 25)
        {
            throw new ValidationServiceException("A record may contain at most 25 product attributes.");
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
        {
            if (attribute is null ||
                !ValidAttributeKey(attribute.Key) ||
                !seenKeys.Add(attribute.Key) ||
                string.IsNullOrWhiteSpace(attribute.Label) || attribute.Label.Length > 100 ||
                attribute.Label.Contains('\n') || attribute.Label.Contains('\r') ||
                string.IsNullOrWhiteSpace(attribute.Value) || attribute.Value.Length > 500 ||
                attribute.Value.Contains('\n') || attribute.Value.Contains('\r') ||
                attribute.Unit?.Length > 40 ||
                !ProductAttributeValueTypes.IsSupported(attribute.ValueType) ||
                !ValidAttributeValue(attribute.ValueType, attribute.Value))
            {
                throw new ValidationServiceException(
                    "Product attributes must have unique keys and valid labels, values, units, and types.");
            }
        }
    }

    private static bool ValidAttributeKey(string key)
        => !string.IsNullOrWhiteSpace(key) && key.Length <= 80 &&
           char.IsAsciiLetterOrDigit(key[0]) &&
           key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool ValidAttributeValue(string valueType, string value)
        => valueType switch
        {
            ProductAttributeValueTypes.Text => true,
            ProductAttributeValueTypes.Number => decimal.TryParse(
                value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            ProductAttributeValueTypes.Url => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo),
            ProductAttributeValueTypes.Date => DateOnly.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            _ => false
        };

    private static ProductDto MapProduct(ProductEntity p, string? dupWarning) => new()
    {
        Id = p.Id,
        Name = p.Name,
        SupplierName = p.SupplierName,
        SupplierUrl = p.SupplierUrl,
        Description = p.Description,
        Currency = p.Currency,
        CostCents = p.CostCents,
        RrpCents = p.RrpCents,
        PriceObservedAtUtc = p.PriceObservedAtUtc,
        Attributes = DeserializeList<ProductAttributeDto>(p.AttributesJson),
        VariantIdentifierKeys = DeserializeList<string>(p.VariantIdentifierKeysJson),
        SourceImageUrls = DeserializeList<string>(p.SourceImageUrlsJson),
        PrimaryImageAssetId = p.PrimaryImageAssetId,
        Variants = p.Variants.Where(v => !v.IsDeleted).Select(MapVariant).ToArray(),
        ConcurrencyToken = p.ConcurrencyToken,
        IsDeleted = p.IsDeleted,
        PossibleDuplicateWarning = dupWarning
    };

    private static ProductVariantDto MapVariant(ProductVariantEntity v) => new()
    {
        Id = v.Id,
        ProductId = v.ProductId,
        VariantName = v.VariantName,
        Sku = v.Sku,
        IdentifierValues = JsonSerializer.Deserialize<Dictionary<string, string>>(v.IdentifierValuesJson, JsonOpts) ?? new Dictionary<string, string>(),
        Attributes = DeserializeList<ProductAttributeDto>(v.AttributesJson),
        Currency = v.Currency,
        CostCents = v.CostCents,
        RrpCents = v.RrpCents,
        ImageAssetId = v.ImageAssetId,
        ConcurrencyToken = v.ConcurrencyToken
    };

    private static JobLineItemDto MapLineItem(JobLineItemEntity x) => new()
    {
        Id = x.Id,
        JobId = x.JobId,
        ProductId = x.ProductId,
        ProductVariantId = x.ProductVariantId,
        Title = x.Title,
        Description = x.Description,
        RoomOrArea = x.RoomOrArea,
        SourceUrl = x.SourceUrl,
        SelectedAttributes = DeserializeList<ProductAttributeDto>(x.SelectedAttributesJson),
        RequirementsNote = x.RequirementsNote,
        QuantityMilli = x.QuantityMilli,
        Unit = x.Unit,
        Currency = x.Currency,
        CostCents = x.CostCents,
        RrpCents = x.RrpCents,
        UnitSellPriceCents = x.UnitSellPriceCents,
        ImageAssetId = x.ImageAssetId,
        Notes = x.Notes,
        SortOrder = x.SortOrder,
        ConcurrencyToken = x.ConcurrencyToken
    };

    private static IReadOnlyList<T> DeserializeList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? [];
    }
}
