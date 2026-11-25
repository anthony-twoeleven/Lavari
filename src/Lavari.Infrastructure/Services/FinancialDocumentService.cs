using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Application.Locking;
using Lavari.Application.Services;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Domain.Errors;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Services;

public sealed class FinancialDocumentService : IFinancialDocumentService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim DocumentNumberGate = new(1, 1);

    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly IEditLockService _editLocks;
    private readonly IAdminService _admin;
    private readonly IFormattingService _formatting;
    private readonly IWordTemplateService _wordRenderer;
    private readonly IPdfRenderer _pdfRenderer;
    private readonly IPrivateFileStore _fileStore;
    private readonly IImageLibraryService _images;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    public FinancialDocumentService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IEditLockService editLocks,
        IAdminService admin,
        IFormattingService formatting,
        IWordTemplateService wordRenderer,
        IPdfRenderer pdfRenderer,
        IPrivateFileStore fileStore,
        IImageLibraryService images,
        TimeProvider timeProvider,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _editLocks = editLocks;
        _admin = admin;
        _formatting = formatting;
        _wordRenderer = wordRenderer;
        _pdfRenderer = pdfRenderer;
        _fileStore = fileStore;
        _images = images;
        _timeProvider = timeProvider;
        _ids = ids;
    }

    public async Task<IReadOnlyList<FinancialDocumentDto>> ListForJobAsync(Guid jobId, FinancialDocumentType? documentType = null, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        if (documentType == FinancialDocumentType.Quote)
        {
            _permissions.Require(ModuleKeys.Quotes, AccessLevel.Read);
        }
        else if (documentType == FinancialDocumentType.Invoice)
        {
            _permissions.Require(ModuleKeys.Invoices, AccessLevel.Read);
        }
        else
        {
            if (!_permissions.Has(ModuleKeys.Quotes, AccessLevel.Read) && !_permissions.Has(ModuleKeys.Invoices, AccessLevel.Read))
            {
                throw new ForbiddenServiceException("You do not have permission to view quotes or invoices.");
            }
        }

        var query = _db.FinancialDocuments.AsNoTracking()
            .Include(d => d.Job).ThenInclude(j => j.Client)
            .Include(d => d.Lines)
            .Include(d => d.Adjustments)
            .Where(d => d.JobId == jobId);

        if (documentType.HasValue)
        {
            query = query.Where(d => d.DocumentType == documentType.Value);
        }
        else
        {
            var canQ = _permissions.Has(ModuleKeys.Quotes, AccessLevel.Read);
            var canI = _permissions.Has(ModuleKeys.Invoices, AccessLevel.Read);
            if (!canQ) query = query.Where(d => d.DocumentType != FinancialDocumentType.Quote);
            if (!canI) query = query.Where(d => d.DocumentType != FinancialDocumentType.Invoice);
        }

        var docs = await query.OrderByDescending(d => d.CreatedAtUtc).ToListAsync(ct);
        return docs.Select(MapDocument).ToArray();
    }

    public async Task<FinancialDocumentDto> GetAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.FinancialDocuments.AsNoTracking()
            .Include(d => d.Job).ThenInclude(j => j.Client)
            .Include(d => d.Lines)
            .Include(d => d.Adjustments)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundServiceException("Financial document not found.");

        _permissions.Require(ModuleForDocType(doc.DocumentType), AccessLevel.Read);
        return MapDocument(doc);
    }

    public async Task<FinancialDocumentDto> CreateAsync(CreateFinancialDocumentRequest request, CancellationToken ct = default)
    {
        var docType = request.DocumentType;
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleForDocType(docType), AccessLevel.Alter);

        var job = await _db.Jobs.Include(j => j.Client).FirstOrDefaultAsync(j => j.Id == request.JobId, ct)
            ?? throw new NotFoundServiceException("Job not found.");

        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency ?? job.DefaultCurrency);
        var defaultTaxBps = int.TryParse(await _admin.GetVariableValueAsync("Finance.DefaultTaxBasisPoints", "1000", ct), out var tb) ? tb : 1000;
        var defaultTaxLabel = await _admin.GetVariableValueAsync("Finance.DefaultTaxLabel", "10% GST", ct);
        var defaultDepositBps = int.TryParse(await _admin.GetVariableValueAsync("Finance.DefaultDepositBasisPoints", "7000", ct), out var dbp) ? dbp : 7000;
        var defaultDepositLabel = await _admin.GetVariableValueAsync("Finance.DefaultDepositLabel", "70% DEPOSIT REQUIRED", ct);

        var mode = NormalizeCalculationMode(request.CalculationMode);
        var taxBps = request.TaxBasisPoints ?? defaultTaxBps;
        var depositBps = request.DepositBasisPoints ?? defaultDepositBps;

        var totals = ComputeTotals(mode, currency, taxBps, depositBps, request.ManualTotalCents, request.Lines, request.Adjustments);
        var number = await GenerateNextDocumentNumberAsync(docType, ct);

        var defaultTemplate = await _db.DocumentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.DocumentType == docType && t.IsDefault && t.IsActive, ct);

        var entity = new FinancialDocumentEntity
        {
            Id = _ids.NewId(),
            JobId = job.Id,
            DocumentType = docType,
            DocumentNumber = number,
            Status = "Draft",
            CalculationMode = mode,
            Currency = currency,
            TaxBasisPoints = taxBps,
            TaxLabel = string.IsNullOrWhiteSpace(request.TaxLabel) ? defaultTaxLabel : request.TaxLabel.Trim(),
            DepositBasisPoints = depositBps,
            DepositLabel = string.IsNullOrWhiteSpace(request.DepositLabel) ? defaultDepositLabel : request.DepositLabel.Trim(),
            SubtotalCents = totals.SubtotalCents,
            DiscountTotalCents = totals.DiscountCents,
            CreditTotalCents = totals.CreditCents,
            TaxAmountCents = totals.TaxCents,
            CalculatedTotalCents = totals.CalculatedTotalCents,
            AppliedTotalCents = totals.AppliedTotalCents,
            DepositAmountCents = totals.DepositCents,
            ManualTotalCents = mode == "LineCalculated" ? null : totals.AppliedTotalCents,
            ManualOverrideReason = request.ManualOverrideReason?.Trim(),
            TemplateId = defaultTemplate?.Id,
            TemplateVersion = defaultTemplate?.Version ?? 1,
            IssueDate = request.IssueDate ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime),
            DueDate = request.DueDate,
            PoNumber = request.PoNumber?.Trim() ?? job.PoNumber,
            Notes = request.Notes?.Trim(),
            PaymentDetailsSnapshot = await _admin.GetVariableValueAsync("Finance.PaymentDetails", string.Empty, ct),
            PaymentTermsSnapshot = await _admin.GetVariableValueAsync("Finance.PaymentTerms", "7 day payment terms", ct),
            VersionNumber = 1
        };

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var lineTotal = CalculateLineTotalCents(line.UnitPriceCents, line.QuantityMilli, currency);
            entity.Lines.Add(new FinancialDocumentLineEntity
            {
                Id = _ids.NewId(),
                FinancialDocumentId = entity.Id,
                LineSourceType = line.LineSourceType,
                SourceEntityId = line.SourceEntityId,
                SourceEntityVersion = line.SourceEntityVersion,
                Title = line.Title.Trim(),
                Details = line.Details?.Trim(),
                QuantityMilli = line.QuantityMilli,
                UnitLabel = string.IsNullOrWhiteSpace(line.UnitLabel) ? "ea" : line.UnitLabel.Trim(),
                UnitPriceCents = line.UnitPriceCents,
                InternalUnitCostCents = line.InternalUnitCostCents,
                LineTotalCents = lineTotal,
                IsTaxable = line.IsTaxable,
                SortOrder = line.SortOrder > 0 ? line.SortOrder : i
            });
        }

        for (var i = 0; i < request.Adjustments.Count; i++)
        {
            var adj = request.Adjustments[i];
            entity.Adjustments.Add(new FinancialDocumentAdjustmentEntity
            {
                Id = _ids.NewId(),
                FinancialDocumentId = entity.Id,
                AdjustmentType = adj.AdjustmentType,
                Description = adj.Description.Trim(),
                AmountCents = Math.Abs(adj.AmountCents),
                BasisPoints = adj.BasisPoints,
                IsPreTax = adj.IsPreTax,
                SortOrder = adj.SortOrder > 0 ? adj.SortOrder : i
            });
        }

        _db.FinancialDocuments.Add(entity);
        await _db.SaveChangesAsync(ct);

        // If this is an Invoice with TimeEntry lines, allocate time transactionally to prevent double billing!
        if (docType == FinancialDocumentType.Invoice)
        {
            await SyncInvoiceTimeAllocationsAsync(entity, ct);
        }

        return await GetAsync(entity.Id, ct);
    }

    public async Task<UpdateFinancialDocumentResultDto> UpdateAsync(Guid documentId, UpdateFinancialDocumentRequest request, CancellationToken ct = default)
    {
        var doc = await _db.FinancialDocuments
            .Include(d => d.Job).ThenInclude(j => j.Client)
            .Include(d => d.Lines)
            .Include(d => d.Adjustments)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundServiceException("Financial document not found.");

        _permissions.Require(ModuleForDocType(doc.DocumentType), AccessLevel.Alter);
        if (doc.Status != "Draft")
        {
            throw new ConflictServiceException(
                "Issued or void financial documents are immutable and cannot be edited.",
                ErrorCodes.RecordImmutable);
        }

        await _editLocks.EnsureActiveLeaseHeldAsync(ToLockRecordType(doc.DocumentType), doc.Id, request.LeaseId, request.FencingVersion, ct);

        if (!string.Equals(doc.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("This document was modified by another request. Reload and review before saving.");
        }

        var mode = NormalizeCalculationMode(request.CalculationMode);
        var totals = ComputeTotals(mode, doc.Currency, request.TaxBasisPoints, request.DepositBasisPoints, request.ManualTotalCents, request.Lines, request.Adjustments);

        // Spec: Warn and require confirmation before changing an existing calculated total or applied total!
        var hadPreviousTotal = doc.CalculatedTotalCents != 0 || doc.AppliedTotalCents != 0;
        var totalChanged = doc.CalculatedTotalCents != totals.CalculatedTotalCents
            || doc.AppliedTotalCents != totals.AppliedTotalCents
            || !string.Equals(doc.CalculationMode, mode, StringComparison.Ordinal);

        if (hadPreviousTotal && totalChanged && doc.CalculationMode != "TotalOnly")
        {
            var confirmationMatches = request.ConfirmTotalChange
                && request.ExpectedPreviousCalculatedCents == doc.CalculatedTotalCents
                && request.ExpectedPreviousAppliedCents == doc.AppliedTotalCents
                && request.ExpectedProposedCalculatedCents == totals.CalculatedTotalCents
                && request.ExpectedProposedAppliedCents == totals.AppliedTotalCents;

            if (!confirmationMatches)
            {
                if (request.ConfirmTotalChange)
                {
                    throw new ConflictServiceException(
                        "The confirmed totals no longer match the current server calculation. Review the updated totals and confirm again.",
                        ErrorCodes.TotalChangeConfirmationRequired);
                }

                return new UpdateFinancialDocumentResultDto
                {
                    RequiresTotalChangeConfirmation = true,
                    Warning = new TotalChangeWarningDto
                    {
                        RequiresConfirmation = true,
                        PreviousCalculatedTotalCents = doc.CalculatedTotalCents,
                        PreviousAppliedTotalCents = doc.AppliedTotalCents,
                        ProposedCalculatedTotalCents = totals.CalculatedTotalCents,
                        ProposedAppliedTotalCents = totals.AppliedTotalCents,
                        Currency = doc.Currency,
                        Message = $"This change updates the calculated total from {_formatting.FormatMoney(new MoneyCents(doc.CalculatedTotalCents, doc.Currency))} to {_formatting.FormatMoney(new MoneyCents(totals.CalculatedTotalCents, doc.Currency))} and the applied total from {_formatting.FormatMoney(new MoneyCents(doc.AppliedTotalCents, doc.Currency))} to {_formatting.FormatMoney(new MoneyCents(totals.AppliedTotalCents, doc.Currency))}. Confirm to apply."
                    }
                };
            }
        }

        var now = _timeProvider.GetUtcNow();
        var actorId = _actor.ActorId?.ToString("D") ?? "system";

        // Soft-delete previous lines and adjustments, then append updated versions (preserving full audit trail!)
        foreach (var oldLine in doc.Lines.Where(l => !l.IsDeleted))
        {
            oldLine.DeletedAtUtc = now;
            oldLine.DeletedBy = actorId;
        }
        foreach (var oldAdj in doc.Adjustments.Where(a => !a.IsDeleted))
        {
            oldAdj.DeletedAtUtc = now;
            oldAdj.DeletedBy = actorId;
        }

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var lineTotal = CalculateLineTotalCents(line.UnitPriceCents, line.QuantityMilli, doc.Currency);
            _db.FinancialDocumentLines.Add(new FinancialDocumentLineEntity
            {
                Id = _ids.NewId(),
                FinancialDocumentId = doc.Id,
                LineSourceType = line.LineSourceType,
                SourceEntityId = line.SourceEntityId,
                SourceEntityVersion = line.SourceEntityVersion,
                Title = line.Title.Trim(),
                Details = line.Details?.Trim(),
                QuantityMilli = line.QuantityMilli,
                UnitLabel = string.IsNullOrWhiteSpace(line.UnitLabel) ? "ea" : line.UnitLabel.Trim(),
                UnitPriceCents = line.UnitPriceCents,
                InternalUnitCostCents = line.InternalUnitCostCents,
                LineTotalCents = lineTotal,
                IsTaxable = line.IsTaxable,
                SortOrder = line.SortOrder > 0 ? line.SortOrder : i
            });
        }

        for (var i = 0; i < request.Adjustments.Count; i++)
        {
            var adj = request.Adjustments[i];
            _db.FinancialDocumentAdjustments.Add(new FinancialDocumentAdjustmentEntity
            {
                Id = _ids.NewId(),
                FinancialDocumentId = doc.Id,
                AdjustmentType = adj.AdjustmentType,
                Description = adj.Description.Trim(),
                AmountCents = Math.Abs(adj.AmountCents),
                BasisPoints = adj.BasisPoints,
                IsPreTax = adj.IsPreTax,
                SortOrder = adj.SortOrder > 0 ? adj.SortOrder : i
            });
        }

        doc.CalculationMode = mode;
        doc.TaxBasisPoints = request.TaxBasisPoints;
        doc.TaxLabel = request.TaxLabel.Trim();
        doc.DepositBasisPoints = request.DepositBasisPoints;
        doc.DepositLabel = request.DepositLabel.Trim();
        doc.SubtotalCents = totals.SubtotalCents;
        doc.DiscountTotalCents = totals.DiscountCents;
        doc.CreditTotalCents = totals.CreditCents;
        doc.TaxAmountCents = totals.TaxCents;
        doc.CalculatedTotalCents = totals.CalculatedTotalCents;
        doc.AppliedTotalCents = totals.AppliedTotalCents;
        doc.DepositAmountCents = totals.DepositCents;
        doc.ManualTotalCents = mode == "LineCalculated" ? null : totals.AppliedTotalCents;
        doc.ManualOverrideReason = request.ManualOverrideReason?.Trim();
        doc.IssueDate = request.IssueDate;
        doc.DueDate = request.DueDate;
        doc.PoNumber = request.PoNumber?.Trim();
        doc.Notes = request.Notes?.Trim();
        doc.VersionNumber++;

        await _db.SaveChangesAsync(ct);

        if (doc.DocumentType == FinancialDocumentType.Invoice)
        {
            await SyncInvoiceTimeAllocationsAsync(doc, ct);
        }

        return new UpdateFinancialDocumentResultDto
        {
            RequiresTotalChangeConfirmation = false,
            Document = await GetAsync(doc.Id, ct)
        };
    }

    public async Task<FinancialDocumentDto> ConvertQuoteToInvoiceAsync(Guid quoteId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Quotes, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Invoices, AccessLevel.Alter);

        var quote = await GetAsync(quoteId, ct);
        if (quote.DocumentType != FinancialDocumentType.Quote)
        {
            throw new ValidationServiceException("Only quotes can be converted into invoices.");
        }

        var createReq = new CreateFinancialDocumentRequest
        {
            JobId = quote.JobId,
            DocumentType = FinancialDocumentType.Invoice,
            CalculationMode = quote.CalculationMode,
            Currency = quote.Currency,
            TaxBasisPoints = quote.TaxBasisPoints,
            TaxLabel = quote.TaxLabel,
            DepositBasisPoints = quote.DepositBasisPoints,
            DepositLabel = quote.DepositLabel,
            IssueDate = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime),
            DueDate = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime.AddDays(7)),
            PoNumber = quote.PoNumber,
            Notes = quote.Notes,
            ManualTotalCents = quote.ManualTotalCents,
            ManualOverrideReason = quote.ManualOverrideReason,
            Lines = quote.Lines.Select(l => new SaveFinancialDocumentLineInput
            {
                LineSourceType = l.LineSourceType,
                SourceEntityId = l.SourceEntityId,
                SourceEntityVersion = l.SourceEntityVersion,
                Title = l.Title,
                Details = l.Details,
                QuantityMilli = l.QuantityMilli,
                UnitLabel = l.UnitLabel,
                UnitPriceCents = l.UnitPriceCents,
                InternalUnitCostCents = l.InternalUnitCostCents,
                IsTaxable = l.IsTaxable,
                SortOrder = l.SortOrder
            }).ToArray(),
            Adjustments = quote.Adjustments.Select(a => new SaveFinancialDocumentAdjustmentInput
            {
                AdjustmentType = a.AdjustmentType,
                Description = a.Description,
                AmountCents = a.AmountCents,
                BasisPoints = a.BasisPoints,
                IsPreTax = a.IsPreTax,
                SortOrder = a.SortOrder
            }).ToArray()
        };

        var created = await CreateAsync(createReq, ct);
        var invoiceEntity = await _db.FinancialDocuments.FirstAsync(d => d.Id == created.Id, ct);
        invoiceEntity.SourceQuoteId = quote.Id;
        await _db.SaveChangesAsync(ct);

        return await GetAsync(created.Id, ct);
    }

    public async Task<GeneratedArtifactDto> GenerateDocxAsync(Guid documentId, bool isDraft, CancellationToken ct = default)
    {
        var doc = await GetAsync(documentId, ct);
        _permissions.Require(ModuleForDocType(doc.DocumentType), AccessLevel.Read);

        // If already issued and approved artifact exists, return that exact immutable artifact!
        if (doc.Status == "Issued" && doc.ApprovedWordArtifactId.HasValue)
        {
            var approved = await _db.GeneratedArtifacts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == doc.ApprovedWordArtifactId.Value, ct);
            if (approved is not null)
            {
                return MapArtifact(approved);
            }
        }

        var template = await _db.DocumentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.DocumentType == doc.DocumentType && t.IsDefault && t.IsActive, ct)
            ?? throw new ValidationServiceException(
                $"No active default Word template was found for {doc.DocumentType}.",
                ErrorCodes.TemplateValidationFailed);

        var templateBytes = await _fileStore.ReadAsync(template.StorageKey, ct);
        var job = await _db.Jobs.AsNoTracking().Include(j => j.JobGroup).Include(j => j.Client).FirstAsync(j => j.Id == doc.JobId, ct);
        var abn = await _admin.GetVariableValueAsync("Company.Abn", "00 000 000 000", ct);
        var paymentDetails = await _admin.GetVariableValueAsync("Finance.PaymentDetails", string.Empty, ct);
        var paymentTerms = await _admin.GetVariableValueAsync("Finance.PaymentTerms", "7 day payment terms", ct);

        var title = doc.DocumentType == FinancialDocumentType.Quote ? "Q U O T E" : "I N V O I C E";
        if (isDraft || doc.Status == "Draft") title += " (DRAFT)";

        var adjParts = new List<string>();
        if (doc.DiscountTotalCents > 0) adjParts.Add($"Discounts: -{_formatting.FormatMoney(new MoneyCents(doc.DiscountTotalCents, doc.Currency))}");
        if (doc.CreditTotalCents > 0) adjParts.Add($"Credits: -{_formatting.FormatMoney(new MoneyCents(doc.CreditTotalCents, doc.Currency))}");
        if (doc.CalculationMode == "ManualOverride") adjParts.Add($"Manual Override (Calculated: {_formatting.FormatMoney(new MoneyCents(doc.CalculatedTotalCents, doc.Currency))})");

        var scalars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{Document.Title}}"] = title,
            ["{{Document.Number}}"] = doc.DocumentNumber,
            ["{{Document.Date}}"] = doc.IssueDate.HasValue ? _formatting.FormatCalendarDate(doc.IssueDate.Value) : "Draft",
            ["{{Document.Currency}}"] = doc.Currency,
            ["{{Document.Subtotal}}"] = _formatting.FormatMoney(new MoneyCents(doc.SubtotalCents, doc.Currency)),
            ["{{Document.TaxLabel}}"] = doc.TaxLabel,
            ["{{Document.TaxAmount}}"] = _formatting.FormatMoney(new MoneyCents(doc.TaxAmountCents, doc.Currency)),
            ["{{Document.Total}}"] = _formatting.FormatMoney(new MoneyCents(doc.AppliedTotalCents, doc.Currency)),
            ["{{Document.DepositLabel}}"] = doc.DepositLabel,
            ["{{Document.DepositAmount}}"] = _formatting.FormatMoney(new MoneyCents(doc.DepositAmountCents, doc.Currency)),
            ["{{Document.AdjustmentsSummary}}"] = string.Join(" | ", adjParts),
            ["{{Document.DueDateLabel}}"] = doc.DueDate.HasValue ? $"DUE: {_formatting.FormatCalendarDate(doc.DueDate.Value)}" : string.Empty,
            ["{{Document.Notes}}"] = doc.Notes ?? string.Empty,
            ["{{Client.Name}}"] = job.Client.Name,
            ["{{Job.Name}}"] = job.Name,
            ["{{Job.GroupName}}"] = job.JobGroup.Name,
            ["{{Job.PoNumber}}"] = doc.PoNumber ?? string.Empty,
            ["{{Company.Abn}}"] = abn,
            ["{{Payment.Details}}"] = paymentDetails,
            ["{{Payment.Terms}}"] = paymentTerms
        };

        var lines = doc.Lines.OrderBy(l => l.SortOrder).Select(l => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{Line.Description}}"] = string.IsNullOrWhiteSpace(l.Details) ? l.Title : $"{l.Title} — {l.Details}",
            ["{{Line.UnitPrice}}"] = _formatting.FormatMoney(new MoneyCents(l.UnitPriceCents, doc.Currency)),
            ["{{Line.Quantity}}"] = (l.QuantityMilli / 1000m).ToString("0.###"),
            ["{{Line.Total}}"] = _formatting.FormatMoney(new MoneyCents(l.LineTotalCents, doc.Currency))
        }).ToArray();

        var docxBytes = _wordRenderer.RenderDocument(templateBytes, scalars, lines);
        var storageKey = await _fileStore.SaveAsync("artifacts", ".docx", docxBytes, ct);
        var fileName = $"{doc.DocumentNumber}{(isDraft || doc.Status == "Draft" ? "-DRAFT" : string.Empty)}.docx";

        var artifact = new GeneratedArtifactEntity
        {
            Id = _ids.NewId(),
            JobId = doc.JobId,
            SourceType = ToArtifactSourceType(doc.DocumentType),
            SourceId = doc.Id,
            SourceVersion = $"{doc.VersionNumber}:{doc.ConcurrencyToken}",
            Format = "Docx",
            StorageKey = storageKey,
            FileName = fileName,
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ByteLength = docxBytes.LongLength,
            IsDraft = isDraft || doc.Status == "Draft",
            RendererVersion = "OpenXML-SDK-3.5",
            MetadataJson = JsonSerializer.Serialize(new { template.Id, template.Version }, JsonOpts)
        };
        _db.GeneratedArtifacts.Add(artifact);
        await _db.SaveChangesAsync(ct);
        return MapArtifact(artifact);
    }

    public async Task<GeneratedArtifactDto> SaveAsPdfAsync(Guid documentId, bool isDraft, CancellationToken ct = default)
    {
        var doc = await GetAsync(documentId, ct);
        _permissions.Require(ModuleForDocType(doc.DocumentType), AccessLevel.Read);

        // Spec: Do not silently regenerate an issued PDF on download!
        if (doc.Status == "Issued" && doc.ApprovedPdfArtifactId.HasValue)
        {
            var approved = await _db.GeneratedArtifacts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == doc.ApprovedPdfArtifactId.Value, ct);
            if (approved is not null)
            {
                return MapArtifact(approved);
            }
        }

        var companyName = await _admin.GetVariableValueAsync("Company.Name", "Lavari Interior Design", ct);
        var companyAbn = await _admin.GetVariableValueAsync("Company.Abn", "00 000 000 000", ct);
        var paymentDetails = await _admin.GetVariableValueAsync("Finance.PaymentDetails", string.Empty, ct);
        var paymentTerms = await _admin.GetVariableValueAsync("Finance.PaymentTerms", "7 day payment terms", ct);

        var pdfBytes = _pdfRenderer.RenderFinancialDocumentPdf(doc, companyName, companyAbn, paymentDetails, paymentTerms, null);
        var storageKey = await _fileStore.SaveAsync("artifacts", ".pdf", pdfBytes, ct);
        var fileName = $"{doc.DocumentNumber}{(isDraft || doc.Status == "Draft" ? "-DRAFT" : string.Empty)}.pdf";

        var artifact = new GeneratedArtifactEntity
        {
            Id = _ids.NewId(),
            JobId = doc.JobId,
            SourceType = ToArtifactSourceType(doc.DocumentType),
            SourceId = doc.Id,
            SourceVersion = $"{doc.VersionNumber}:{doc.ConcurrencyToken}",
            Format = "Pdf",
            StorageKey = storageKey,
            FileName = fileName,
            ContentType = "application/pdf",
            ByteLength = pdfBytes.LongLength,
            IsDraft = isDraft || doc.Status == "Draft",
            RendererVersion = _pdfRenderer.RendererVersion,
            MetadataJson = JsonSerializer.Serialize(new { doc.VersionNumber, doc.CalculationMode }, JsonOpts)
        };
        _db.GeneratedArtifacts.Add(artifact);
        await _db.SaveChangesAsync(ct);
        return MapArtifact(artifact);
    }

    public async Task<FinancialDocumentDto> IssueAsync(Guid documentId, string concurrencyToken, Guid? leaseId, long? fencingVersion, CancellationToken ct = default)
    {
        var doc = await _db.FinancialDocuments
            .Include(d => d.Job).ThenInclude(j => j.Client)
            .Include(d => d.Lines)
            .Include(d => d.Adjustments)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundServiceException("Financial document not found.");

        _permissions.Require(ModuleForDocType(doc.DocumentType), AccessLevel.Alter);
        await _editLocks.EnsureActiveLeaseHeldAsync(ToLockRecordType(doc.DocumentType), doc.Id, leaseId, fencingVersion, ct);

        if (doc.Status != "Draft")
        {
            throw new ConflictServiceException("Only Draft documents can be issued.", ErrorCodes.RecordImmutable);
        }

        if (!string.Equals(doc.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("This document changed since your last review. Reload and review before issuing.");
        }

        // Generate and bind immutable approved Word and PDF artifacts!
        var wordArt = await GenerateDocxAsync(documentId, isDraft: false, ct);
        var pdfArt = await SaveAsPdfAsync(documentId, isDraft: false, ct);

        var now = _timeProvider.GetUtcNow();
        var actorName = _actor.ActorDisplayName;

        doc.Status = "Issued";
        doc.IssuedAtUtc = now;
        doc.IssuedBy = actorName;
        doc.IssueDate ??= DateOnly.FromDateTime(now.UtcDateTime);
        doc.ApprovedWordArtifactId = wordArt.Id;
        doc.ApprovedPdfArtifactId = pdfArt.Id;

        var snapshotDto = MapDocument(doc);
        _db.FinancialDocumentSnapshots.Add(new FinancialDocumentSnapshotEntity
        {
            Id = _ids.NewId(),
            FinancialDocumentId = doc.Id,
            VersionNumber = doc.VersionNumber,
            SnapshotJson = JsonSerializer.Serialize(snapshotDto, JsonOpts),
            IssuedAtUtc = now,
            IssuedBy = actorName
        });

        await _db.SaveChangesAsync(ct);
        return await GetAsync(doc.Id, ct);
    }

    public async Task<IReadOnlyList<GeneratedArtifactDto>> ListArtifactsAsync(Guid jobId, ArtifactSourceType? sourceType = null, Guid? sourceId = null, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);

        var query = _db.GeneratedArtifacts.AsNoTracking().Where(a => a.JobId == jobId);
        if (sourceType.HasValue) query = query.Where(a => a.SourceType == sourceType.Value);
        if (sourceId.HasValue) query = query.Where(a => a.SourceId == sourceId.Value);

        var rows = await query.OrderByDescending(a => a.CreatedAtUtc).Take(100).ToListAsync(ct);
        return rows.Where(a => CanReadArtifactSource(a.SourceType)).Select(MapArtifact).ToArray();
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> DownloadArtifactAsync(Guid artifactId, CancellationToken ct = default)
    {
        var art = await _db.GeneratedArtifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artifactId, ct)
            ?? throw new NotFoundServiceException("Document artifact not found.");

        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleForArtifactSource(art.SourceType), AccessLevel.Read);

        var bytes = await _fileStore.ReadAsync(art.StorageKey, ct);
        return (bytes, art.ContentType, art.FileName);
    }

    public async Task<IReadOnlyList<ProcurementItemDto>> ListProcurementAsync(Guid jobId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Procurement, AccessLevel.Read);

        var items = await _db.ProcurementItems.AsNoTracking()
            .Include(p => p.Payments)
            .Where(p => p.JobId == jobId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync(ct);

        return items.Select(MapProcurement).ToArray();
    }

    public async Task<ProcurementItemDto> CreateProcurementAsync(Guid jobId, UpsertProcurementItemRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Procurement, AccessLevel.Alter);
        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, jobId, request.LeaseId, request.FencingVersion, ct);

        if (string.IsNullOrWhiteSpace(request.SupplierName))
        {
            throw new ValidationServiceException("Supplier name is required.");
        }
        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        _ = new MoneyCents(request.TotalCostCents, currency);

        var item = new ProcurementItemEntity
        {
            Id = _ids.NewId(),
            JobId = jobId,
            JobLineItemId = request.JobLineItemId,
            SupplierName = request.SupplierName.Trim(),
            OrderReference = request.OrderReference?.Trim(),
            Status = request.Status.Trim(),
            OrderedQuantityMilli = request.OrderedQuantityMilli,
            ReceivedQuantityMilli = request.ReceivedQuantityMilli,
            Currency = currency,
            TotalCostCents = request.TotalCostCents,
            ExpectedDeliveryDate = request.ExpectedDeliveryDate,
            ActualDeliveryDate = request.ActualDeliveryDate,
            ShippingDetails = request.ShippingDetails?.Trim(),
            InstallationDetails = request.InstallationDetails?.Trim(),
            Notes = request.Notes?.Trim()
        };
        _db.ProcurementItems.Add(item);
        await _db.SaveChangesAsync(ct);
        return MapProcurement(item);
    }

    public async Task<ProcurementItemDto> UpdateProcurementAsync(Guid id, UpsertProcurementItemRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Procurement, AccessLevel.Alter);
        var item = await _db.ProcurementItems.Include(p => p.Payments).FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundServiceException("Procurement item not found.");

        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, item.JobId, request.LeaseId, request.FencingVersion, ct);
        if (!string.Equals(item.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Procurement record was modified by another user.");
        }

        item.SupplierName = request.SupplierName.Trim();
        item.OrderReference = request.OrderReference?.Trim();
        item.Status = request.Status.Trim();
        item.OrderedQuantityMilli = request.OrderedQuantityMilli;
        item.ReceivedQuantityMilli = request.ReceivedQuantityMilli;
        item.Currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        item.TotalCostCents = request.TotalCostCents;
        item.ExpectedDeliveryDate = request.ExpectedDeliveryDate;
        item.ActualDeliveryDate = request.ActualDeliveryDate;
        item.ShippingDetails = request.ShippingDetails?.Trim();
        item.InstallationDetails = request.InstallationDetails?.Trim();
        item.Notes = request.Notes?.Trim();

        await _db.SaveChangesAsync(ct);
        return MapProcurement(item);
    }

    public async Task<ProcurementItemDto> AddProcurementPaymentAsync(Guid procurementId, AddProcurementPaymentRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Procurement, AccessLevel.Alter);
        var item = await _db.ProcurementItems.Include(p => p.Payments).FirstOrDefaultAsync(p => p.Id == procurementId, ct)
            ?? throw new NotFoundServiceException("Procurement item not found.");

        await _editLocks.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, item.JobId, request.LeaseId, request.FencingVersion, ct);
        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);
        if (!string.Equals(currency, item.Currency, StringComparison.Ordinal))
        {
            throw new ValidationServiceException("Payment currency must match the procurement item currency.", ErrorCodes.CurrencyMismatch);
        }
        _ = new MoneyCents(request.AmountCents, currency);

        _db.ProcurementPayments.Add(new ProcurementPaymentEntity
        {
            Id = _ids.NewId(),
            ProcurementItemId = item.Id,
            Description = string.IsNullOrWhiteSpace(request.Description) ? "Installment payment" : request.Description.Trim(),
            Currency = currency,
            AmountCents = request.AmountCents,
            PaidAtUtc = request.Status == "Paid" ? (request.PaidAtUtc ?? _timeProvider.GetUtcNow()) : request.PaidAtUtc,
            DueDate = request.DueDate,
            Status = request.Status,
            ReceiptId = request.ReceiptId,
            Reference = request.Reference?.Trim()
        });
        await _db.SaveChangesAsync(ct);

        var reloaded = await _db.ProcurementItems.AsNoTracking().Include(p => p.Payments).FirstAsync(p => p.Id == procurementId, ct);
        return MapProcurement(reloaded);
    }

    public async Task<IReadOnlyList<ReceiptDto>> ListReceiptsAsync(Guid jobId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Receipts, AccessLevel.Read);

        return await _db.Receipts.AsNoTracking()
            .Where(r => r.JobId == jobId)
            .OrderByDescending(r => r.ReceiptDate)
            .Select(r => new ReceiptDto
            {
                Id = r.Id,
                JobId = r.JobId,
                JobLineItemId = r.JobLineItemId,
                ProcurementItemId = r.ProcurementItemId,
                MerchantName = r.MerchantName,
                ReceiptDate = r.ReceiptDate,
                Currency = r.Currency,
                AmountCents = r.AmountCents,
                Notes = r.Notes,
                ImageAssetId = r.ImageAssetId,
                ConcurrencyToken = r.ConcurrencyToken,
                CreatedAtUtc = r.CreatedAtUtc
            })
            .ToListAsync(ct);
    }

    public async Task<ReceiptDto> UploadReceiptAsync(
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
        CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Receipts, AccessLevel.Alter);

        if (string.IsNullOrWhiteSpace(merchantName))
        {
            throw new ValidationServiceException("Merchant name is required.");
        }
        var normCurrency = MoneyCents.NormalizeAndValidateCurrency(currency);
        _ = new MoneyCents(amountCents, normCurrency);

        var uploaded = await _images.UploadAsync(
            $"Receipt - {merchantName.Trim()} ({receiptDate:yyyy-MM-dd})",
            notes,
            "Receipt Capture",
            rightsConfirmed: true,
            isGlobalLibrary: false,
            clientId: null,
            jobId: jobId,
            productId: null,
            moodBoardId: null,
            rawBytes: imageBytes,
            declaredContentType: contentType,
            ct: ct);

        var actorId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var receipt = new ReceiptEntity
        {
            Id = _ids.NewId(),
            JobId = jobId,
            JobLineItemId = jobLineItemId,
            ProcurementItemId = procurementItemId,
            MerchantName = merchantName.Trim(),
            ReceiptDate = receiptDate,
            Currency = normCurrency,
            AmountCents = amountCents,
            Notes = notes?.Trim(),
            ImageAssetId = uploaded.Id,
            UploadedByUserId = actorId
        };
        _db.Receipts.Add(receipt);
        await _db.SaveChangesAsync(ct);

        return new ReceiptDto
        {
            Id = receipt.Id,
            JobId = receipt.JobId,
            JobLineItemId = receipt.JobLineItemId,
            ProcurementItemId = receipt.ProcurementItemId,
            MerchantName = receipt.MerchantName,
            ReceiptDate = receipt.ReceiptDate,
            Currency = receipt.Currency,
            AmountCents = receipt.AmountCents,
            Notes = receipt.Notes,
            ImageAssetId = receipt.ImageAssetId,
            ConcurrencyToken = receipt.ConcurrencyToken,
            CreatedAtUtc = receipt.CreatedAtUtc
        };
    }

    private async Task SyncInvoiceTimeAllocationsAsync(FinancialDocumentEntity invoice, CancellationToken ct)
    {
        var activeTimeLines = await _db.FinancialDocumentLines
            .Where(l => l.FinancialDocumentId == invoice.Id && l.LineSourceType == FinancialLineSourceType.TimeEntry && l.SourceEntityId != null)
            .ToListAsync(ct);

        // Deactivate old allocations for this draft invoice
        var existingForInvoice = await _db.TimeBillingAllocations
            .Where(a => a.FinancialDocumentId == invoice.Id && a.IsActive)
            .ToListAsync(ct);
        foreach (var old in existingForInvoice)
        {
            old.IsActive = false;
        }

        foreach (var line in activeTimeLines)
        {
            var timeEntryId = line.SourceEntityId!.Value;
            var alreadyAllocated = await _db.TimeBillingAllocations
                .AnyAsync(a => a.TimeEntryId == timeEntryId && a.IsActive && a.FinancialDocumentId != invoice.Id, ct);
            if (alreadyAllocated)
            {
                throw new ConflictServiceException(
                    "One or more selected time entries have already been allocated to another invoice.",
                    ErrorCodes.DuplicateBillingAllocation);
            }

            var entry = await _db.TimeEntries.AsNoTracking().Include(t => t.Intervals)
                .FirstOrDefaultAsync(t => t.Id == timeEntryId, ct)
                ?? throw new NotFoundServiceException("Selected time entry not found.");

            if (entry.State != "Stopped" || !entry.IsBillable)
            {
                throw new ValidationServiceException("Only stopped, billable time entries can be invoiced.");
            }

            // Cross-entry duplicate overlap check: ensure the same worker's overlapping interval is not already invoiced on another entry!
            if (entry.Intervals.Count > 0)
            {
                var otherAllocatedEntries = await _db.TimeBillingAllocations.AsNoTracking()
                    .Where(a => a.IsActive && a.FinancialDocumentId != invoice.Id && a.TimeEntry.WorkerUserId == entry.WorkerUserId)
                    .Select(a => a.TimeEntryId)
                    .Distinct()
                    .ToListAsync(ct);

                if (otherAllocatedEntries.Count > 0)
                {
                    var otherIntervals = await _db.TimeIntervals.AsNoTracking()
                        .Where(i => otherAllocatedEntries.Contains(i.TimeEntryId) && i.EndedAtUtc != null)
                        .ToListAsync(ct);

                    foreach (var mine in entry.Intervals.Where(i => i.EndedAtUtc != null))
                    {
                        var ms = mine.EffectiveStartedAtUtc ?? mine.StartedAtUtc;
                        var me = mine.EffectiveEndedAtUtc ?? mine.EndedAtUtc!.Value;
                        foreach (var other in otherIntervals)
                        {
                            var os = other.EffectiveStartedAtUtc ?? other.StartedAtUtc;
                            var oe = other.EffectiveEndedAtUtc ?? other.EndedAtUtc!.Value;
                            if (ms < oe && os < me)
                            {
                                throw new ConflictServiceException(
                                    "This time entry overlaps a worker interval already billed on another invoice. Adjust or exclude overlapping time before invoicing.",
                                    ErrorCodes.DuplicateBillingAllocation);
                            }
                        }
                    }
                }
            }

            _db.TimeBillingAllocations.Add(new TimeBillingAllocationEntity
            {
                Id = _ids.NewId(),
                TimeEntryId = entry.Id,
                FinancialDocumentId = invoice.Id,
                AllocatedSeconds = entry.DurationSeconds,
                BilledAmountCents = line.LineTotalCents,
                Currency = invoice.Currency,
                IsActive = true
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<string> GenerateNextDocumentNumberAsync(FinancialDocumentType docType, CancellationToken ct)
    {
        await DocumentNumberGate.WaitAsync(ct);
        try
        {
            var prefix = docType == FinancialDocumentType.Quote ? "Q" : "INV";
            var year = _timeProvider.GetUtcNow().Year;
            var pattern = $"{prefix}-{year}-";

            var existingNumbers = await _db.FinancialDocuments.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(d => d.DocumentType == docType && d.DocumentNumber.StartsWith(pattern))
                .Select(d => d.DocumentNumber)
                .ToListAsync(ct);

            var maxSeq = 0;
            foreach (var n in existingNumbers)
            {
                if (int.TryParse(n[pattern.Length..], out var seq) && seq > maxSeq)
                {
                    maxSeq = seq;
                }
            }

            return $"{pattern}{(maxSeq + 1):D4}";
        }
        finally
        {
            DocumentNumberGate.Release();
        }
    }

    private static long CalculateLineTotalCents(long unitPriceCents, long quantityMilli, string currency)
    {
        if (quantityMilli <= 0)
        {
            throw new ValidationServiceException("Line quantity must be greater than zero.");
        }
        var unit = new MoneyCents(unitPriceCents, currency);
        return unit.MultiplyByRatio(quantityMilli, 1000L).Cents;
    }

    private static (long SubtotalCents, long DiscountCents, long CreditCents, long TaxCents, long CalculatedTotalCents, long AppliedTotalCents, long DepositCents)
        ComputeTotals(
            string calculationMode,
            string currency,
            int taxBps,
            int depositBps,
            long? manualTotalCents,
            IReadOnlyList<SaveFinancialDocumentLineInput> lines,
            IReadOnlyList<SaveFinancialDocumentAdjustmentInput> adjustments)
    {
        if (calculationMode == "TotalOnly")
        {
            if (!manualTotalCents.HasValue)
            {
                throw new ValidationServiceException("Manual total is required for a Total-Only document.");
            }
            var manual = new MoneyCents(manualTotalCents.Value, currency);
            var dep = depositBps > 0 ? manual.ApplyBasisPoints(depositBps).Cents : 0L;
            return (0L, 0L, 0L, 0L, 0L, manual.Cents, dep);
        }

        var subtotal = MoneyCents.Zero(currency);
        var taxableSubtotal = MoneyCents.Zero(currency);
        foreach (var line in lines)
        {
            var lt = new MoneyCents(CalculateLineTotalCents(line.UnitPriceCents, line.QuantityMilli, currency), currency);
            subtotal = subtotal.Add(lt);
            if (line.IsTaxable)
            {
                taxableSubtotal = taxableSubtotal.Add(lt);
            }
        }

        var discounts = MoneyCents.Zero(currency);
        var credits = MoneyCents.Zero(currency);
        var preTaxAdj = MoneyCents.Zero(currency);
        var postTaxAdj = MoneyCents.Zero(currency);

        foreach (var adj in adjustments)
        {
            var amt = new MoneyCents(Math.Abs(adj.AmountCents), currency);
            if (adj.AdjustmentType == FinancialAdjustmentType.Discount)
            {
                discounts = discounts.Add(amt);
            }
            else
            {
                credits = credits.Add(amt);
            }

            if (adj.IsPreTax)
            {
                preTaxAdj = preTaxAdj.Add(amt);
            }
            else
            {
                postTaxAdj = postTaxAdj.Add(amt);
            }
        }

        var netTaxable = taxableSubtotal.Cents > preTaxAdj.Cents
            ? taxableSubtotal.Subtract(preTaxAdj)
            : MoneyCents.Zero(currency);
        var tax = taxBps > 0 ? netTaxable.ApplyBasisPoints(taxBps) : MoneyCents.Zero(currency);
        var calculated = subtotal.Subtract(preTaxAdj).Add(tax).Subtract(postTaxAdj);

        MoneyCents applied;
        if (calculationMode == "ManualOverride")
        {
            if (!manualTotalCents.HasValue)
            {
                throw new ValidationServiceException("Manual override total is required in ManualOverride mode.");
            }
            applied = new MoneyCents(manualTotalCents.Value, currency);
        }
        else
        {
            applied = calculated;
        }

        var deposit = depositBps > 0 ? applied.ApplyBasisPoints(depositBps).Cents : 0L;
        return (subtotal.Cents, discounts.Cents, credits.Cents, tax.Cents, calculated.Cents, applied.Cents, deposit);
    }

    private bool CanReadArtifactSource(ArtifactSourceType sourceType)
        => _permissions.Has(ModuleForArtifactSource(sourceType), AccessLevel.Read);

    private static string ModuleForArtifactSource(ArtifactSourceType sourceType) => sourceType switch
    {
        ArtifactSourceType.Quote => ModuleKeys.Quotes,
        ArtifactSourceType.Invoice => ModuleKeys.Invoices,
        ArtifactSourceType.MoodBoard => ModuleKeys.MoodBoards,
        _ => ModuleKeys.Jobs
    };

    private static string ModuleForDocType(FinancialDocumentType docType) =>
        docType == FinancialDocumentType.Quote ? ModuleKeys.Quotes : ModuleKeys.Invoices;

    private static LockableRecordType ToLockRecordType(FinancialDocumentType docType) =>
        docType == FinancialDocumentType.Quote ? LockableRecordType.Quote : LockableRecordType.Invoice;

    private static ArtifactSourceType ToArtifactSourceType(FinancialDocumentType docType) =>
        docType == FinancialDocumentType.Quote ? ArtifactSourceType.Quote : ArtifactSourceType.Invoice;

    private static string NormalizeCalculationMode(string mode) => mode.Trim() switch
    {
        "LineCalculated" or "ManualOverride" or "TotalOnly" => mode.Trim(),
        _ => throw new ValidationServiceException("Calculation mode must be LineCalculated, ManualOverride or TotalOnly.")
    };

    private static FinancialDocumentDto MapDocument(FinancialDocumentEntity d) => new()
    {
        Id = d.Id,
        JobId = d.JobId,
        JobName = d.Job?.Name ?? string.Empty,
        ClientName = d.Job?.Client?.Name ?? string.Empty,
        DocumentType = d.DocumentType,
        DocumentNumber = d.DocumentNumber,
        Status = d.Status,
        CalculationMode = d.CalculationMode,
        Currency = d.Currency,
        TaxBasisPoints = d.TaxBasisPoints,
        TaxLabel = d.TaxLabel,
        DepositBasisPoints = d.DepositBasisPoints,
        DepositLabel = d.DepositLabel,
        SubtotalCents = d.SubtotalCents,
        DiscountTotalCents = d.DiscountTotalCents,
        CreditTotalCents = d.CreditTotalCents,
        TaxAmountCents = d.TaxAmountCents,
        CalculatedTotalCents = d.CalculatedTotalCents,
        AppliedTotalCents = d.AppliedTotalCents,
        DepositAmountCents = d.DepositAmountCents,
        ManualTotalCents = d.ManualTotalCents,
        ManualOverrideReason = d.ManualOverrideReason,
        SourceQuoteId = d.SourceQuoteId,
        IssueDate = d.IssueDate,
        DueDate = d.DueDate,
        IssuedAtUtc = d.IssuedAtUtc,
        IssuedBy = d.IssuedBy,
        PoNumber = d.PoNumber,
        Notes = d.Notes,
        ApprovedWordArtifactId = d.ApprovedWordArtifactId,
        ApprovedPdfArtifactId = d.ApprovedPdfArtifactId,
        VersionNumber = d.VersionNumber,
        ConcurrencyToken = d.ConcurrencyToken,
        Lines = d.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.SortOrder).Select(l => new FinancialDocumentLineDto
        {
            Id = l.Id,
            LineSourceType = l.LineSourceType,
            SourceEntityId = l.SourceEntityId,
            SourceEntityVersion = l.SourceEntityVersion,
            Title = l.Title,
            Details = l.Details,
            QuantityMilli = l.QuantityMilli,
            UnitLabel = l.UnitLabel,
            UnitPriceCents = l.UnitPriceCents,
            InternalUnitCostCents = l.InternalUnitCostCents,
            LineTotalCents = l.LineTotalCents,
            IsTaxable = l.IsTaxable,
            SortOrder = l.SortOrder
        }).ToArray(),
        Adjustments = d.Adjustments.Where(a => !a.IsDeleted).OrderBy(a => a.SortOrder).Select(a => new FinancialDocumentAdjustmentDto
        {
            Id = a.Id,
            AdjustmentType = a.AdjustmentType,
            Description = a.Description,
            AmountCents = a.AmountCents,
            BasisPoints = a.BasisPoints,
            IsPreTax = a.IsPreTax,
            SortOrder = a.SortOrder
        }).ToArray()
    };

    private static GeneratedArtifactDto MapArtifact(GeneratedArtifactEntity a) => new()
    {
        Id = a.Id,
        JobId = a.JobId,
        SourceType = a.SourceType,
        SourceId = a.SourceId,
        Format = a.Format,
        FileName = a.FileName,
        ContentType = a.ContentType,
        ByteLength = a.ByteLength,
        IsDraft = a.IsDraft,
        RendererVersion = a.RendererVersion,
        CreatedAtUtc = a.CreatedAtUtc
    };

    private static ProcurementItemDto MapProcurement(ProcurementItemEntity p)
    {
        var activePayments = p.Payments.Where(x => !x.IsDeleted).ToList();
        var paid = activePayments.Where(x => x.Status == "Paid").Sum(x => x.AmountCents);
        return new ProcurementItemDto
        {
            Id = p.Id,
            JobId = p.JobId,
            JobLineItemId = p.JobLineItemId,
            SupplierName = p.SupplierName,
            OrderReference = p.OrderReference,
            Status = p.Status,
            OrderedQuantityMilli = p.OrderedQuantityMilli,
            ReceivedQuantityMilli = p.ReceivedQuantityMilli,
            Currency = p.Currency,
            TotalCostCents = p.TotalCostCents,
            PaidCents = paid,
            OutstandingCents = Math.Max(0, p.TotalCostCents - paid),
            ExpectedDeliveryDate = p.ExpectedDeliveryDate,
            ActualDeliveryDate = p.ActualDeliveryDate,
            ShippingDetails = p.ShippingDetails,
            InstallationDetails = p.InstallationDetails,
            Notes = p.Notes,
            ConcurrencyToken = p.ConcurrencyToken,
            Payments = activePayments.OrderBy(x => x.DueDate ?? DateOnly.MaxValue).Select(x => new ProcurementPaymentDto
            {
                Id = x.Id,
                Description = x.Description,
                Currency = x.Currency,
                AmountCents = x.AmountCents,
                PaidAtUtc = x.PaidAtUtc,
                DueDate = x.DueDate,
                Status = x.Status,
                ReceiptId = x.ReceiptId,
                Reference = x.Reference
            }).ToArray()
        };
    }
}
