using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Application.Services;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Services;

public sealed class ImageLibraryService : IImageLibraryService
{
    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly IImageProcessingService _processor;
    private readonly IPrivateFileStore _fileStore;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    public ImageLibraryService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IImageProcessingService processor,
        IPrivateFileStore fileStore,
        TimeProvider timeProvider,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _processor = processor;
        _fileStore = fileStore;
        _timeProvider = timeProvider;
        _ids = ids;
    }

    public async Task<PagedResult<ImageAssetDto>> ListAsync(ImageLibraryFilterRequest filter, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ImageLibrary, AccessLevel.Read);

        if (filter.ClientId.HasValue)
        {
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
        }
        if (filter.JobId.HasValue)
        {
            _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        }

        IQueryable<ImageAssetEntity> query = VisibleImages().Include(x => x.Associations);

        if (filter.GlobalOnly)
        {
            query = query.Where(x => x.IsGlobalLibrary);
        }
        if (filter.ClientId.HasValue)
        {
            query = query.Where(x => x.Associations.Any(a => a.DeletedAtUtc == null && a.ClientId == filter.ClientId.Value));
        }
        if (filter.JobId.HasValue)
        {
            query = query.Where(x => x.Associations.Any(a => a.DeletedAtUtc == null && a.JobId == filter.JobId.Value));
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Title.ToLower().Contains(term) ||
                                     (x.Description != null && x.Description.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var page = filter.Page < 1 ? 1 : filter.Page;
        var pageSize = filter.PageSize < 1 ? 24 : Math.Min(filter.PageSize, 100);

        IOrderedQueryable<ImageAssetEntity> ordered = (filter.SortBy?.ToLowerInvariant(), filter.SortDescending) switch
        {
            ("title", false) => query.OrderBy(x => x.Title),
            ("title", true) => query.OrderByDescending(x => x.Title),
            (_, false) => query.OrderBy(x => x.CreatedAtUtc),
            _ => query.OrderByDescending(x => x.CreatedAtUtc)
        };

        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var canReadClients = _permissions.Has(ModuleKeys.Clients, AccessLevel.Read);
        var canReadJobs = _permissions.Has(ModuleKeys.Jobs, AccessLevel.Read);
        var clientIds = rows.SelectMany(r => r.Associations).Where(a => a.ClientId.HasValue).Select(a => a.ClientId!.Value).Distinct().ToArray();
        var jobIds = rows.SelectMany(r => r.Associations).Where(a => a.JobId.HasValue).Select(a => a.JobId!.Value).Distinct().ToArray();
        var clientNames = canReadClients
            ? await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : null;
        var jobNames = canReadJobs
            ? await _db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.Name, ct)
            : null;

        var items = rows.Select(r => MapAsset(r, false, clientNames, jobNames)).ToArray();
        return new PagedResult<ImageAssetDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<ImageAssetDto> UploadAsync(
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
        CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ImageLibrary, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ValidationServiceException("Image title is required.");
        }

        var (resolvedClientId, resolvedJobId) = await ValidateLinksAsync(clientId, jobId, productId, moodBoardId, ct);

        // Process and enforce 1080p aspect-preserving bound BEFORE writing to disk. Oversized original is never retained!
        var processed = _processor.ProcessForStorage(rawBytes.Span, declaredContentType);

        var mainKey = await _fileStore.SaveAsync("images", processed.Extension, processed.ProcessedBytes, ct);
        string? thumbKey = null;
        ImageAssetEntity asset;
        ImageAssociationEntity? association = null;
        try
        {
            thumbKey = await _fileStore.SaveAsync("thumbs", ".jpg", processed.ThumbnailBytes, ct);

            var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
            asset = new ImageAssetEntity
            {
                Id = _ids.NewId(),
                Title = title.Trim(),
                Description = description?.Trim(),
                StorageKey = mainKey,
                ThumbnailStorageKey = thumbKey,
                ContentType = processed.ContentType,
                Width = processed.Width,
                Height = processed.Height,
                ByteLength = processed.ProcessedBytes.LongLength,
                ThumbWidth = processed.ThumbWidth,
                ThumbHeight = processed.ThumbHeight,
                SourceProvenance = sourceProvenance?.Trim(),
                RightsConfirmed = rightsConfirmed,
                UploadedByUserId = actorId,
                IsGlobalLibrary = isGlobalLibrary
            };
            if (resolvedClientId.HasValue || resolvedJobId.HasValue || productId.HasValue || moodBoardId.HasValue)
            {
                association = new ImageAssociationEntity
                {
                    Id = _ids.NewId(),
                    ImageAssetId = asset.Id,
                    ClientId = resolvedClientId,
                    JobId = resolvedJobId,
                    ProductId = productId,
                    MoodBoardId = moodBoardId,
                    AssociationType = moodBoardId.HasValue ? ImageAssociationType.MoodBoard : productId.HasValue ? ImageAssociationType.Product : resolvedJobId.HasValue ? ImageAssociationType.Job : ImageAssociationType.Client
                };
            }
        }
        catch
        {
            await _fileStore.DeleteTemporaryAsync(mainKey, CancellationToken.None);
            if (thumbKey is not null)
            {
                await _fileStore.DeleteTemporaryAsync(thumbKey, CancellationToken.None);
            }
            throw;
        }

        // Once persistence is attempted, its outcome may be uncertain; never delete files that may be referenced.
        _db.ImageAssets.Add(asset);
        if (association is not null) _db.ImageAssociations.Add(association);
        await _db.SaveChangesAsync(ct);

        var loaded = await _db.ImageAssets.AsNoTracking().Include(x => x.Associations).FirstAsync(x => x.Id == asset.Id, ct);
        return MapAsset(loaded, processed.WasDownscaled, null, null);
    }

    public async Task<ImageAssetDto> AddAssociationAsync(Guid imageAssetId, Guid? clientId, Guid? jobId, Guid? productId, ImageAssociationType associationType, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ImageLibrary, AccessLevel.Alter);
        var asset = await VisibleImages().FirstOrDefaultAsync(x => x.Id == imageAssetId, ct)
            ?? throw new NotFoundServiceException("Image asset not found.");

        if (associationType is not (ImageAssociationType.Library or ImageAssociationType.Client or ImageAssociationType.Job or ImageAssociationType.Product) ||
            (associationType == ImageAssociationType.Client && clientId is null && jobId is null) ||
            (associationType == ImageAssociationType.Job && jobId is null) ||
            (associationType == ImageAssociationType.Product && productId is null))
            throw new ValidationServiceException("Select a valid image association and its related record.");
        var (resolvedClientId, resolvedJobId) = await ValidateLinksAsync(clientId, jobId, productId, null, ct);

        _db.ImageAssociations.Add(new ImageAssociationEntity
        {
            Id = _ids.NewId(),
            ImageAssetId = asset.Id,
            ClientId = resolvedClientId,
            JobId = resolvedJobId,
            ProductId = productId,
            AssociationType = associationType
        });
        await _db.SaveChangesAsync(ct);

        var loaded = await _db.ImageAssets.AsNoTracking().Include(x => x.Associations).FirstAsync(x => x.Id == imageAssetId, ct);
        return MapAsset(loaded, false, null, null);
    }

    private async Task<(Guid? ClientId, Guid? JobId)> ValidateLinksAsync(
        Guid? clientId, Guid? jobId, Guid? productId, Guid? moodBoardId, CancellationToken ct)
    {
        if (productId.HasValue)
        {
            _permissions.Require(ModuleKeys.ProductCatalog, AccessLevel.Read);
            if (!await _db.Products.AnyAsync(p => p.Id == productId.Value, ct))
                throw new NotFoundServiceException("Associated product not found.");
        }

        if (moodBoardId.HasValue)
        {
            _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Read);
            var board = await _db.MoodBoards.AsNoTracking()
                .Where(b => b.Id == moodBoardId.Value).Select(b => new { b.JobId }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundServiceException("Associated mood board not found.");
            if (jobId.HasValue && jobId.Value != board.JobId)
                throw new ValidationServiceException("The selected mood board does not belong to the selected job.");
            jobId = board.JobId;
        }

        if (jobId.HasValue)
        {
            _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
            var job = await _db.Jobs.AsNoTracking()
                .Where(j => j.Id == jobId.Value && _db.Clients.Any(c => c.Id == j.ClientId) &&
                    _db.JobGroups.Any(g => g.Id == j.JobGroupId && g.ClientId == j.ClientId))
                .Select(j => new { j.ClientId }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundServiceException("Associated job not found.");
            if (clientId.HasValue && clientId.Value != job.ClientId)
                throw new ValidationServiceException("The selected job does not belong to the selected client.");
            clientId = job.ClientId;
        }
        else if (clientId.HasValue)
        {
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
            if (!await _db.Clients.AnyAsync(c => c.Id == clientId.Value, ct))
                throw new NotFoundServiceException("Associated client not found.");
        }
        return (clientId, jobId);
    }

    public async Task<(byte[] Bytes, string ContentType)> ReadImageBytesAsync(Guid imageAssetId, bool thumbnail, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ImageLibrary, AccessLevel.Read);
        var asset = await VisibleImages().FirstOrDefaultAsync(x => x.Id == imageAssetId, ct)
            ?? throw new NotFoundServiceException("Image asset not found.");

        var key = thumbnail ? asset.ThumbnailStorageKey : asset.StorageKey;
        var bytes = await _fileStore.ReadAsync(key, ct);
        return (bytes, thumbnail ? "image/jpeg" : asset.ContentType);
    }

    private IQueryable<ImageAssetEntity> VisibleImages(bool includeDeleted = false)
    {
        var clients = _db.Clients.Where(c => c.DeletedAtUtc == null);
        var groups = _db.JobGroups.Where(g => g.DeletedAtUtc == null);
        var jobs = _db.Jobs.Where(j => j.DeletedAtUtc == null && clients.Any(c => c.Id == j.ClientId) &&
            groups.Any(g => g.Id == j.JobGroupId && g.ClientId == j.ClientId));
        var products = _db.Products.Where(p => p.DeletedAtUtc == null);
        var variants = _db.ProductVariants.Where(v => v.DeletedAtUtc == null);
        var lines = _db.JobLineItems.Where(l => l.DeletedAtUtc == null);
        var associations = _db.ImageAssociations.Where(a => a.DeletedAtUtc == null);
        var boards = _db.MoodBoards.Where(b => b.DeletedAtUtc == null && jobs.Any(j => j.Id == b.JobId));
        var canvases = _db.MoodBoardCanvases.Where(c => c.DeletedAtUtc == null);
        var elements = _db.MoodBoardElements.Where(e => e.DeletedAtUtc == null);
        var allReceipts = _db.Receipts.Where(r => r.DeletedAtUtc == null);
        var receipts = allReceipts.Where(r => jobs.Any(j => j.Id == r.JobId));
        IQueryable<ImageAssetEntity> assets = _db.ImageAssets.AsNoTracking();
        if (includeDeleted) assets = assets.IgnoreQueryFilters();
        var query = assets
            .Where(image => image.AiSourceImageAssetId == null)
            .Where(image => image.IsGlobalLibrary || image.UploadedByUserId == _actor.ActorId ||
                associations.Any(a => a.ImageAssetId == image.Id) ||
                products.Any(p => p.PrimaryImageAssetId == image.Id) ||
                variants.Any(v => v.ImageAssetId == image.Id) ||
                lines.Any(l => l.ImageAssetId == image.Id) ||
                allReceipts.Any(r => r.ImageAssetId == image.Id) ||
                elements.Any(e => e.ImageAssetId == image.Id))
            .Where(image => !associations.Any(a => a.ImageAssetId == image.Id && (
                (a.AssociationType != ImageAssociationType.Library && a.AssociationType != ImageAssociationType.Client &&
                 a.AssociationType != ImageAssociationType.Job && a.AssociationType != ImageAssociationType.Product &&
                 a.AssociationType != ImageAssociationType.MoodBoard && a.AssociationType != ImageAssociationType.Receipt) ||
                (a.AssociationType == ImageAssociationType.Client && a.ClientId == null) ||
                (a.AssociationType == ImageAssociationType.Job && a.JobId == null) ||
                (a.AssociationType == ImageAssociationType.Product && a.ProductId == null) ||
                (a.AssociationType == ImageAssociationType.MoodBoard && a.MoodBoardId == null) ||
                (a.AssociationType == ImageAssociationType.Receipt && a.ReceiptId == null) ||
                (a.ClientId != null && !clients.Any(c => c.Id == a.ClientId)) ||
                (a.JobId != null && !jobs.Any(j => j.Id == a.JobId && (a.ClientId == null || j.ClientId == a.ClientId))) ||
                (a.ProductId != null && !products.Any(p => p.Id == a.ProductId)) ||
                (a.MoodBoardId != null && !boards.Any(b => b.Id == a.MoodBoardId && (a.JobId == null || b.JobId == a.JobId) &&
                    jobs.Any(j => j.Id == b.JobId && (a.ClientId == null || j.ClientId == a.ClientId)))) ||
                (a.ReceiptId != null && !receipts.Any(r => r.Id == a.ReceiptId && (a.JobId == null || r.JobId == a.JobId) &&
                    jobs.Any(j => j.Id == r.JobId && (a.ClientId == null || j.ClientId == a.ClientId)))))))
            .Where(image => !variants.Any(v => v.ImageAssetId == image.Id && !products.Any(p => p.Id == v.ProductId)))
            .Where(image => !lines.Any(l => l.ImageAssetId == image.Id && !jobs.Any(j => j.Id == l.JobId)))
            .Where(image => !allReceipts.Any(r => r.ImageAssetId == image.Id && !jobs.Any(j => j.Id == r.JobId)))
            .Where(image => !elements.Any(e => e.ImageAssetId == image.Id &&
                !canvases.Any(c => c.Id == e.CanvasId && boards.Any(b => b.Id == c.MoodBoardId))));

        if (!_permissions.Has(ModuleKeys.Clients, AccessLevel.Read))
            query = query.Where(image => !associations.Any(a => a.ImageAssetId == image.Id && a.ClientId != null));
        if (!_permissions.Has(ModuleKeys.Clients, AccessLevel.Read) || !_permissions.Has(ModuleKeys.Jobs, AccessLevel.Read))
            query = query.Where(image =>
                !associations.Any(a => a.ImageAssetId == image.Id && (a.JobId != null || a.MoodBoardId != null || a.ReceiptId != null)) &&
                !lines.Any(l => l.ImageAssetId == image.Id) &&
                !allReceipts.Any(r => r.ImageAssetId == image.Id) &&
                !elements.Any(e => e.ImageAssetId == image.Id));
        if (!_permissions.Has(ModuleKeys.ProductCatalog, AccessLevel.Read))
            query = query.Where(image => !associations.Any(a => a.ImageAssetId == image.Id && a.ProductId != null) &&
                !products.Any(p => p.PrimaryImageAssetId == image.Id) &&
                !variants.Any(v => v.ImageAssetId == image.Id));
        if (!_permissions.Has(ModuleKeys.JobLineItems, AccessLevel.Read))
            query = query.Where(image => !lines.Any(l => l.ImageAssetId == image.Id));
        if (!_permissions.Has(ModuleKeys.Receipts, AccessLevel.Read))
            query = query.Where(image => !associations.Any(a => a.ImageAssetId == image.Id && a.ReceiptId != null) &&
                !allReceipts.Any(r => r.ImageAssetId == image.Id));
        if (!_permissions.Has(ModuleKeys.MoodBoards, AccessLevel.Read))
            query = query.Where(image => !associations.Any(a => a.ImageAssetId == image.Id && a.MoodBoardId != null) &&
                !elements.Any(e => e.ImageAssetId == image.Id));
        return query;
    }

    public async Task SoftDeleteAsync(Guid imageAssetId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ImageLibrary, AccessLevel.Delete);
        var visibleIds = VisibleImages().Select(x => x.Id);
        var asset = await _db.ImageAssets.FirstOrDefaultAsync(x => x.Id == imageAssetId && visibleIds.Contains(x.Id), ct)
            ?? throw new NotFoundServiceException("Image asset not found.");

        if (!string.Equals(asset.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Image asset was modified by another user.");
        }

        // Spec: Block deletion while an active reference requires the image.
        var inUse = await _db.MoodBoardElements.AnyAsync(e => e.ImageAssetId == imageAssetId, ct)
            || await _db.Receipts.AnyAsync(r => r.ImageAssetId == imageAssetId, ct)
            || await _db.JobLineItems.AnyAsync(l => l.ImageAssetId == imageAssetId, ct)
            || await _db.Products.AnyAsync(p => p.PrimaryImageAssetId == imageAssetId, ct)
            || await _db.ProductVariants.AnyAsync(v => v.ImageAssetId == imageAssetId, ct)
            || await _db.ImageAssets.AnyAsync(i => i.AiSourceImageAssetId == imageAssetId, ct);

        if (inUse)
        {
            throw new ConflictServiceException(
                "This image cannot be deleted while it is actively referenced.",
                ErrorCodes.ActiveReferencesPreventDelete);
        }

        asset.DeletedAtUtc = _timeProvider.GetUtcNow();
        asset.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
    }

    public async Task RestoreAsync(Guid imageAssetId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.ImageLibrary, AccessLevel.Delete);
        var visibleIds = VisibleImages(includeDeleted: true).Select(x => x.Id);
        var asset = await _db.ImageAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == imageAssetId && visibleIds.Contains(x.Id), ct)
            ?? throw new NotFoundServiceException("Image asset not found.");

        if (!string.Equals(asset.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Image asset was modified by another user.");
        }

        asset.DeletedAtUtc = null;
        asset.DeletedBy = null;
        await _db.SaveChangesAsync(ct);
    }

    private static ImageAssetDto MapAsset(
        ImageAssetEntity a,
        bool wasDownscaled,
        Dictionary<Guid, string>? clientNames,
        Dictionary<Guid, string>? jobNames) => new()
    {
        Id = a.Id,
        Title = a.Title,
        Description = a.Description,
        ContentType = a.ContentType,
        Width = a.Width,
        Height = a.Height,
        ByteLength = a.ByteLength,
        ThumbWidth = a.ThumbWidth,
        ThumbHeight = a.ThumbHeight,
        SourceProvenance = a.SourceProvenance,
        RightsConfirmed = a.RightsConfirmed,
        IsGlobalLibrary = a.IsGlobalLibrary,
        WasDownscaledFromLargerOriginal = wasDownscaled,
        CreatedAtUtc = a.CreatedAtUtc,
        ConcurrencyToken = a.ConcurrencyToken,
        Associations = a.Associations.Where(x => !x.IsDeleted).Select(x => new ImageAssociationDto
        {
            Id = x.Id,
            ClientId = clientNames is not null ? x.ClientId : null,
            ClientName = x.ClientId.HasValue && clientNames is not null && clientNames.TryGetValue(x.ClientId.Value, out var cn) ? cn : null,
            JobId = jobNames is not null ? x.JobId : null,
            JobName = x.JobId.HasValue && jobNames is not null && jobNames.TryGetValue(x.JobId.Value, out var jn) ? jn : null,
            ProductId = x.ProductId,
            MoodBoardId = x.MoodBoardId,
            ReceiptId = x.ReceiptId,
            AssociationType = x.AssociationType
        }).ToArray()
    };
}

public sealed class MoodBoardService : IMoodBoardService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly IAdminService _admin;
    private readonly IPrivateFileStore _fileStore;
    private readonly IImageLibraryService _images;
    private readonly IPdfRenderer _pdfRenderer;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    public MoodBoardService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IAdminService admin,
        IPrivateFileStore fileStore,
        IImageLibraryService images,
        IPdfRenderer pdfRenderer,
        TimeProvider timeProvider,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _admin = admin;
        _fileStore = fileStore;
        _images = images;
        _pdfRenderer = pdfRenderer;
        _timeProvider = timeProvider;
        _ids = ids;
    }

    public async Task<IReadOnlyList<MoodBoardDto>> ListForJobAsync(Guid jobId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Read);

        var boards = await _db.MoodBoards.AsNoTracking()
            .Include(b => b.Canvases).ThenInclude(c => c.Elements)
            .Where(b => b.JobId == jobId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);

        return boards.Select(MapBoard).ToArray();
    }

    public async Task<MoodBoardDto> GetAsync(Guid boardId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Read);
        var board = await _db.MoodBoards.AsNoTracking()
            .Include(b => b.Canvases).ThenInclude(c => c.Elements)
            .FirstOrDefaultAsync(b => b.Id == boardId, ct)
            ?? throw new NotFoundServiceException("Mood board not found.");
        return MapBoard(board);
    }

    public async Task<MoodBoardDto> CreateBoardAsync(Guid jobId, UpsertMoodBoardRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Mood board name is required.");
        }

        if (!await _db.Jobs.AnyAsync(j => j.Id == jobId, ct))
        {
            throw new NotFoundServiceException("Job not found.");
        }

        var branding = await GetCurrentBrandingAsync(ct);
        var board = new MoodBoardEntity
        {
            Id = _ids.NewId(),
            JobId = jobId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim()
        };
        var firstCanvas = new MoodBoardCanvasEntity
        {
            Id = _ids.NewId(),
            MoodBoardId = board.Id,
            Name = "Canvas 1",
            SortOrder = 0,
            WidthPx = 1600,
            HeightPx = 900,
            BrandingVersion = branding.Version,
            BrandingSnapshotJson = JsonSerializer.Serialize(branding, JsonOpts),
            BackgroundColorHex = branding.SurfaceColorHex
        };
        board.Canvases.Add(firstCanvas);
        _db.MoodBoards.Add(board);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(board.Id, ct);
    }

    public async Task<MoodBoardCanvasDto> AddCanvasAsync(Guid boardId, UpsertCanvasRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Alter);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationServiceException("Canvas name is required.");
        }

        if (!await _db.MoodBoards.AnyAsync(b => b.Id == boardId, ct))
        {
            throw new NotFoundServiceException("Mood board not found.");
        }

        var branding = await GetCurrentBrandingAsync(ct);
        var nextOrder = (await _db.MoodBoardCanvases.Where(c => c.MoodBoardId == boardId)
            .Select(c => (int?)c.SortOrder).MaxAsync(ct) ?? -1) + 1;

        var canvas = new MoodBoardCanvasEntity
        {
            Id = _ids.NewId(),
            MoodBoardId = boardId,
            Name = request.Name.Trim(),
            SortOrder = request.SortOrder > 0 ? request.SortOrder : nextOrder,
            WidthPx = Math.Clamp(request.WidthPx, 400, 3840),
            HeightPx = Math.Clamp(request.HeightPx, 300, 2160),
            BrandingVersion = branding.Version,
            BrandingSnapshotJson = JsonSerializer.Serialize(branding, JsonOpts),
            BackgroundColorHex = string.IsNullOrWhiteSpace(request.BackgroundColorHex) ? branding.SurfaceColorHex : request.BackgroundColorHex.Trim()
        };
        _db.MoodBoardCanvases.Add(canvas);
        await _db.SaveChangesAsync(ct);
        return MapCanvas(canvas);
    }

    public async Task<MoodBoardCanvasDto> UpdateCanvasMetaAsync(Guid canvasId, UpsertCanvasRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Alter);
        var canvas = await _db.MoodBoardCanvases.Include(c => c.Elements).FirstOrDefaultAsync(c => c.Id == canvasId, ct)
            ?? throw new NotFoundServiceException("Canvas not found.");

        if (!string.Equals(canvas.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Canvas was modified by another user.");
        }

        canvas.Name = string.IsNullOrWhiteSpace(request.Name) ? canvas.Name : request.Name.Trim();
        canvas.SortOrder = request.SortOrder;
        canvas.WidthPx = Math.Clamp(request.WidthPx, 400, 3840);
        canvas.HeightPx = Math.Clamp(request.HeightPx, 300, 2160);
        if (!string.IsNullOrWhiteSpace(request.BackgroundColorHex))
        {
            canvas.BackgroundColorHex = request.BackgroundColorHex.Trim();
        }

        await _db.SaveChangesAsync(ct);
        return MapCanvas(canvas);
    }

    public async Task<MoodBoardCanvasDto> SaveCanvasElementsAsync(Guid canvasId, SaveCanvasElementsRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Alter);
        var canvas = await _db.MoodBoardCanvases.Include(c => c.Elements).FirstOrDefaultAsync(c => c.Id == canvasId, ct)
            ?? throw new NotFoundServiceException("Canvas not found.");

        if (!string.Equals(canvas.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException(
                "This canvas was updated by another session. Review the latest version before saving your layout.");
        }

        var now = _timeProvider.GetUtcNow();
        var actorId = _actor.ActorId?.ToString("D") ?? "system";
        var incomingIds = request.Elements.Where(e => e.Id != Guid.Empty).Select(e => e.Id).ToHashSet();

        // Soft-delete elements removed from the canvas (never deletes source products/images!)
        foreach (var existing in canvas.Elements.Where(e => !e.IsDeleted))
        {
            if (!incomingIds.Contains(existing.Id))
            {
                existing.DeletedAtUtc = now;
                existing.DeletedBy = actorId;
            }
        }

        foreach (var dto in request.Elements)
        {
            var match = canvas.Elements.FirstOrDefault(e => e.Id == dto.Id && !e.IsDeleted);
            if (match is null)
            {
                _db.MoodBoardElements.Add(new MoodBoardElementEntity
                {
                    Id = dto.Id == Guid.Empty ? _ids.NewId() : dto.Id,
                    CanvasId = canvas.Id,
                    ElementType = dto.ElementType,
                    ImageAssetId = dto.ImageAssetId,
                    TextContent = dto.TextContent,
                    FontSizePx = Math.Clamp(dto.FontSizePx, 8, 160),
                    TextColorHex = string.IsNullOrWhiteSpace(dto.TextColorHex) ? "#1F1B16" : dto.TextColorHex,
                    X = dto.X,
                    Y = dto.Y,
                    Width = Math.Max(20, dto.Width),
                    Height = Math.Max(20, dto.Height),
                    ZIndex = dto.ZIndex,
                    Caption = dto.Caption,
                    SourceJobLineItemId = dto.SourceJobLineItemId
                });
            }
            else
            {
                match.ElementType = dto.ElementType;
                match.ImageAssetId = dto.ImageAssetId;
                match.TextContent = dto.TextContent;
                match.FontSizePx = Math.Clamp(dto.FontSizePx, 8, 160);
                match.TextColorHex = string.IsNullOrWhiteSpace(dto.TextColorHex) ? "#1F1B16" : dto.TextColorHex;
                match.X = dto.X;
                match.Y = dto.Y;
                match.Width = Math.Max(20, dto.Width);
                match.Height = Math.Max(20, dto.Height);
                match.ZIndex = dto.ZIndex;
                match.Caption = dto.Caption;
                match.SourceJobLineItemId = dto.SourceJobLineItemId;
            }
        }

        // Touch parent canvas concurrency token so concurrent layout edits conflict cleanly.
        canvas.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);

        var reloaded = await _db.MoodBoardCanvases.AsNoTracking().Include(c => c.Elements).FirstAsync(c => c.Id == canvasId, ct);
        return MapCanvas(reloaded);
    }

    public async Task<MoodBoardCanvasDto> ReapplyCurrentBrandingAsync(Guid canvasId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Alter);
        var canvas = await _db.MoodBoardCanvases.Include(c => c.Elements).FirstOrDefaultAsync(c => c.Id == canvasId, ct)
            ?? throw new NotFoundServiceException("Canvas not found.");

        if (!string.Equals(canvas.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Canvas was modified by another user.");
        }

        var branding = await GetCurrentBrandingAsync(ct);
        var updatedBranding = branding with { Version = canvas.BrandingVersion + 1 };
        canvas.BrandingVersion = updatedBranding.Version;
        canvas.BrandingSnapshotJson = JsonSerializer.Serialize(updatedBranding, JsonOpts);
        canvas.BackgroundColorHex = updatedBranding.SurfaceColorHex;

        await _db.SaveChangesAsync(ct);
        return MapCanvas(canvas);
    }

    public async Task SoftDeleteCanvasAsync(Guid canvasId, string concurrencyToken, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Delete);
        var canvas = await _db.MoodBoardCanvases.FirstOrDefaultAsync(c => c.Id == canvasId, ct)
            ?? throw new NotFoundServiceException("Canvas not found.");

        if (!string.Equals(canvas.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Canvas was modified by another user.");
        }

        canvas.DeletedAtUtc = _timeProvider.GetUtcNow();
        canvas.DeletedBy = _actor.ActorId?.ToString("D") ?? "system";
        await _db.SaveChangesAsync(ct);
    }

    public async Task<GeneratedArtifactDto> SaveAsPdfAsync(Guid boardId, Guid? singleCanvasId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.MoodBoards, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);

        var boardDto = await GetAsync(boardId, ct);
        var selectedCanvases = singleCanvasId.HasValue
            ? boardDto.Canvases.Where(c => c.Id == singleCanvasId.Value).ToList()
            : boardDto.Canvases.OrderBy(c => c.SortOrder).ToList();

        if (selectedCanvases.Count == 0)
        {
            throw new NotFoundServiceException("No canvas found to export.");
        }

        var imageIds = selectedCanvases
            .SelectMany(c => c.Elements)
            .Where(e => e.ImageAssetId.HasValue)
            .Select(e => e.ImageAssetId!.Value)
            .Distinct()
            .ToList();

        var imageBytesMap = new Dictionary<Guid, byte[]>();
        if (imageIds.Count > 0)
        {
            foreach (var imageId in imageIds)
            {
                var (bytes, _) = await _images.ReadImageBytesAsync(imageId, thumbnail: false, ct);
                imageBytesMap[imageId] = bytes;
            }
        }

        var pdfBytes = _pdfRenderer.RenderMoodBoardPdf(boardDto, selectedCanvases, imageBytesMap, null);
        var storageKey = await _fileStore.SaveAsync("artifacts", ".pdf", pdfBytes, ct);
        var safeName = string.Join("_", boardDto.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var suffix = singleCanvasId.HasValue ? $"-{selectedCanvases[0].Name}" : "-FullBoard";
        var fileName = $"{safeName}{suffix}.pdf";

        var artifact = new GeneratedArtifactEntity
        {
            Id = _ids.NewId(),
            JobId = boardDto.JobId,
            SourceType = ArtifactSourceType.MoodBoard,
            SourceId = boardDto.Id,
            SourceVersion = string.Join(",", selectedCanvases.Select(c => $"{c.Id:N}:{c.ConcurrencyToken}")),
            Format = "Pdf",
            StorageKey = storageKey,
            FileName = fileName,
            ContentType = "application/pdf",
            ByteLength = pdfBytes.LongLength,
            IsDraft = false,
            RendererVersion = _pdfRenderer.RendererVersion,
            MetadataJson = JsonSerializer.Serialize(new { singleCanvasId, canvasCount = selectedCanvases.Count }, JsonOpts)
        };
        _db.GeneratedArtifacts.Add(artifact);
        await _db.SaveChangesAsync(ct);

        return new GeneratedArtifactDto
        {
            Id = artifact.Id,
            JobId = artifact.JobId,
            SourceType = artifact.SourceType,
            SourceId = artifact.SourceId,
            Format = artifact.Format,
            FileName = artifact.FileName,
            ContentType = artifact.ContentType,
            ByteLength = artifact.ByteLength,
            IsDraft = artifact.IsDraft,
            RendererVersion = artifact.RendererVersion,
            CreatedAtUtc = artifact.CreatedAtUtc
        };
    }

    private async Task<MoodBoardBrandingDto> GetCurrentBrandingAsync(CancellationToken ct)
    {
        var name = await _admin.GetVariableValueAsync("Company.Name", "Lavari Interior Design", ct);
        var primary = await _admin.GetVariableValueAsync("Company.BrandPrimaryHex", "#6750A4", ct);
        var surface = await _admin.GetVariableValueAsync("Company.BrandSurfaceHex", "#F9F7F4", ct);
        var text = await _admin.GetVariableValueAsync("Company.BrandTextHex", "#1F1B16", ct);
        return new MoodBoardBrandingDto
        {
            CompanyName = name,
            PrimaryColorHex = primary,
            SurfaceColorHex = surface,
            TextColorHex = text,
            Version = 1
        };
    }

    private static MoodBoardDto MapBoard(MoodBoardEntity b) => new()
    {
        Id = b.Id,
        JobId = b.JobId,
        Name = b.Name,
        Description = b.Description,
        ConcurrencyToken = b.ConcurrencyToken,
        Canvases = b.Canvases.Where(c => !c.IsDeleted).OrderBy(c => c.SortOrder).Select(MapCanvas).ToArray()
    };

    private static MoodBoardCanvasDto MapCanvas(MoodBoardCanvasEntity c)
    {
        var branding = JsonSerializer.Deserialize<MoodBoardBrandingDto>(c.BrandingSnapshotJson, JsonOpts) ?? new MoodBoardBrandingDto
        {
            CompanyName = "Lavari Interior Design",
            PrimaryColorHex = "#6750A4",
            SurfaceColorHex = c.BackgroundColorHex,
            TextColorHex = "#1F1B16",
            Version = c.BrandingVersion
        };

        return new MoodBoardCanvasDto
        {
            Id = c.Id,
            MoodBoardId = c.MoodBoardId,
            Name = c.Name,
            SortOrder = c.SortOrder,
            WidthPx = c.WidthPx,
            HeightPx = c.HeightPx,
            Branding = branding,
            BackgroundColorHex = c.BackgroundColorHex,
            ConcurrencyToken = c.ConcurrencyToken,
            Elements = c.Elements.Where(e => !e.IsDeleted).OrderBy(e => e.ZIndex).Select(e => new MoodBoardElementDto
            {
                Id = e.Id,
                ElementType = e.ElementType,
                ImageAssetId = e.ImageAssetId,
                TextContent = e.TextContent,
                FontSizePx = e.FontSizePx,
                TextColorHex = e.TextColorHex,
                X = e.X,
                Y = e.Y,
                Width = e.Width,
                Height = e.Height,
                ZIndex = e.ZIndex,
                Caption = e.Caption,
                SourceJobLineItemId = e.SourceJobLineItemId
            }).ToArray()
        };
    }
}
