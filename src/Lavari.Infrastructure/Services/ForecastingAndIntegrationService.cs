using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lavari.Application.Abstractions;
using Lavari.Application.Services;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Domain.Errors;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Domain.Time;
using Lavari.Infrastructure.Integrations;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lavari.Infrastructure.Services;

public sealed class ForecastingService : IForecastingService
{
    private readonly LavariDbContext _db;
    private readonly IPermissionEvaluator _permissions;

    public ForecastingService(LavariDbContext db, IPermissionEvaluator permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task<ForecastReportDto> CalculateForecastAsync(ForecastRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.FinancialForecasting, AccessLevel.Read);
        _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);

        if (request.EndDate < request.StartDate)
        {
            throw new ValidationServiceException("End date must be on or after start date.");
        }

        var reportingZone = JobTimeZone.Create(request.ReportingTimeZone);
        var currency = MoneyCents.NormalizeAndValidateCurrency(request.Currency);

        var jobQuery = _db.Jobs.AsNoTracking().AsQueryable();
        if (request.JobId.HasValue)
        {
            jobQuery = jobQuery.Where(j => j.Id == request.JobId.Value);
        }
        var jobs = await jobQuery.Select(j => new { j.Id, j.Name }).ToDictionaryAsync(j => j.Id, j => j.Name, ct);
        var jobIds = jobs.Keys.ToList();

        var sources = new List<ForecastSourceItemDto>();
        var plannedRev = MoneyCents.Zero(currency);
        var actualRev = MoneyCents.Zero(currency);
        var plannedCost = MoneyCents.Zero(currency);
        var actualCost = MoneyCents.Zero(currency);
        var incompleteCount = 0;

        // 1. Issued invoices = Actual Revenue; Draft quotes NOT converted to an invoice = Planned Revenue (no double-counting!)
        var canReadInvoices = _permissions.Has(ModuleKeys.Invoices, AccessLevel.Read);
        var canReadQuotes = _permissions.Has(ModuleKeys.Quotes, AccessLevel.Read);
        if (canReadInvoices || canReadQuotes)
        {
            var docs = await _db.FinancialDocuments.AsNoTracking()
                .Where(d => jobIds.Contains(d.JobId) && d.Currency == currency && d.Status != "Void")
                .Select(d => new { d.Id, d.JobId, d.DocumentType, d.DocumentNumber, d.Status, d.SourceQuoteId, EffectiveDate = d.IssueDate ?? d.DueDate, d.AppliedTotalCents })
                .ToListAsync(ct);

            var convertedQuoteIds = docs.Where(d => d.SourceQuoteId.HasValue).Select(d => d.SourceQuoteId!.Value).ToHashSet();

            foreach (var doc in docs)
            {
                if (doc.DocumentType == FinancialDocumentType.Quote && !canReadQuotes) continue;
                if (doc.DocumentType == FinancialDocumentType.Invoice && !canReadInvoices) continue;
                if (doc.DocumentType == FinancialDocumentType.Quote && convertedQuoteIds.Contains(doc.Id)) continue; // Avoid double-counting quote + resulting invoice

                var effDate = doc.EffectiveDate;
                if (effDate is null)
                {
                    incompleteCount++;
                    continue;
                }

                if (effDate.Value < request.StartDate || effDate.Value > request.EndDate) continue;

                var amt = new MoneyCents(doc.AppliedTotalCents, currency);
                var isActual = doc.DocumentType == FinancialDocumentType.Invoice && doc.Status == "Issued";

                if (isActual) actualRev = actualRev.Add(amt);
                else plannedRev = plannedRev.Add(amt);

                sources.Add(new ForecastSourceItemDto
                {
                    JobId = doc.JobId,
                    JobName = jobs[doc.JobId],
                    SourceType = doc.DocumentType == FinancialDocumentType.Quote ? ForecastSourceType.Quote : ForecastSourceType.Invoice,
                    SourceId = doc.Id,
                    Label = $"{doc.DocumentNumber} ({doc.Status})",
                    EffectiveDate = effDate.Value,
                    IsActual = isActual,
                    Direction = "Revenue",
                    AmountCents = amt.Cents,
                    Currency = currency
                });
            }
        }

        // 2. Procurement: paid installments = Actual Cost; remaining unpaid balance = Planned Cost (never double-counted!)
        if (_permissions.Has(ModuleKeys.Procurement, AccessLevel.Read))
        {
            var procs = await _db.ProcurementItems.AsNoTracking()
                .Where(p => jobIds.Contains(p.JobId) && p.Currency == currency && p.Status != "Cancelled")
                .Select(p => new
                {
                    p.Id, p.JobId, p.SupplierName, p.TotalCostCents, p.ExpectedDeliveryDate,
                    Payments = p.Payments.Where(pay => pay.Status == "Paid")
                        .Select(pay => new { pay.Id, pay.Description, pay.AmountCents, pay.PaidAtUtc, pay.DueDate }).ToList()
                })
                .ToListAsync(ct);

            foreach (var p in procs)
            {
                long paidSum = 0;
                foreach (var pay in p.Payments)
                {
                    paidSum += pay.AmountCents;
                    var payDate = pay.PaidAtUtc.HasValue
                        ? DateOnly.FromDateTime(reportingZone.ConvertUtcToZone(pay.PaidAtUtc.Value).Date)
                        : pay.DueDate;
                    if (payDate is null) { incompleteCount++; continue; }
                    if (payDate.Value < request.StartDate || payDate.Value > request.EndDate) continue;

                    var amt = new MoneyCents(pay.AmountCents, currency);
                    actualCost = actualCost.Add(amt);
                    sources.Add(new ForecastSourceItemDto
                    {
                        JobId = p.JobId,
                        JobName = jobs[p.JobId],
                        SourceType = ForecastSourceType.Procurement,
                        SourceId = pay.Id,
                        Label = $"{p.SupplierName} — {pay.Description}",
                        EffectiveDate = payDate.Value,
                        IsActual = true,
                        Direction = "Cost",
                        AmountCents = amt.Cents,
                        Currency = currency
                    });
                }

                var remaining = p.TotalCostCents - paidSum;
                if (remaining > 0)
                {
                    var expectedDate = p.ExpectedDeliveryDate;
                    if (expectedDate is null) { incompleteCount++; continue; }
                    if (expectedDate.Value >= request.StartDate && expectedDate.Value <= request.EndDate)
                    {
                        var remAmt = new MoneyCents(remaining, currency);
                        plannedCost = plannedCost.Add(remAmt);
                        sources.Add(new ForecastSourceItemDto
                        {
                            JobId = p.JobId,
                            JobName = jobs[p.JobId],
                            SourceType = ForecastSourceType.Procurement,
                            SourceId = p.Id,
                            Label = $"{p.SupplierName} (Outstanding balance)",
                            EffectiveDate = expectedDate.Value,
                            IsActual = false,
                            Direction = "Cost",
                            AmountCents = remAmt.Cents,
                            Currency = currency
                        });
                    }
                }
            }
        }

        // 3. Explicit unlinked job date forecast milestones (skip milestones linked to an invoice/procurement to prevent double-counting!)
        if (_permissions.Has(ModuleKeys.JobDates, AccessLevel.Read))
        {
            var dates = await _db.JobDates.AsNoTracking()
                .Where(d => jobIds.Contains(d.JobId) &&
                            d.ForecastAmountCents != null &&
                            d.ForecastCurrency == currency &&
                            d.LinkedEntityId == null &&
                            d.Status != "Cancelled")
                .Select(d => new { d.Id, d.JobId, d.Label, d.CalendarDate, d.OccurredAtUtc, d.ForecastAmountCents, d.ForecastDirection, d.IsActual })
                .ToListAsync(ct);

            foreach (var d in dates)
            {
                var effDate = d.CalendarDate ?? (d.OccurredAtUtc.HasValue
                    ? DateOnly.FromDateTime(reportingZone.ConvertUtcToZone(d.OccurredAtUtc.Value).Date)
                    : (DateOnly?)null);
                if (effDate is null) { incompleteCount++; continue; }
                if (effDate.Value < request.StartDate || effDate.Value > request.EndDate) continue;

                var amt = new MoneyCents(d.ForecastAmountCents!.Value, currency);
                if (d.ForecastDirection == "Revenue")
                {
                    if (d.IsActual) actualRev = actualRev.Add(amt);
                    else plannedRev = plannedRev.Add(amt);
                }
                else if (d.ForecastDirection == "Cost")
                {
                    if (d.IsActual) actualCost = actualCost.Add(amt);
                    else plannedCost = plannedCost.Add(amt);
                }

                sources.Add(new ForecastSourceItemDto
                {
                    JobId = d.JobId,
                    JobName = jobs[d.JobId],
                    SourceType = ForecastSourceType.Milestone,
                    SourceId = d.Id,
                    Label = d.Label,
                    EffectiveDate = effDate.Value,
                    IsActual = d.IsActual,
                    Direction = d.ForecastDirection,
                    AmountCents = amt.Cents,
                    Currency = currency
                });
            }
        }

        return new ForecastReportDto
        {
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            ReportingTimeZone = reportingZone.IanaId,
            Currency = currency,
            PlannedRevenueCents = plannedRev.Cents,
            ActualRevenueCents = actualRev.Cents,
            PlannedCostCents = plannedCost.Cents,
            ActualCostCents = actualCost.Cents,
            PlannedProfitCents = plannedRev.Subtract(plannedCost).Cents,
            ActualProfitCents = actualRev.Subtract(actualCost).Cents,
            IncompleteRecordCount = incompleteCount,
            Assumptions = $"Inclusive reporting range {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd} ({reportingZone.IanaId}) in {currency}. Converted quotes and linked milestones are excluded from duplicate totals. Operational estimate only; not accounting-grade P&L.",
            Sources = sources.OrderBy(s => s.EffectiveDate).ToArray()
        };
    }
}

public sealed class LavariIntegrationsOptions
{
    public GmailIntegrationOptions Gmail { get; set; } = new();
    public GeminiIntegrationOptions Gemini { get; set; } = new();
}

public sealed class GmailIntegrationOptions
{
    public bool Enabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public sealed class GeminiIntegrationOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gemini-2.5-flash";
}

/// <summary>
/// Integration service for Gmail status and direct supplier-page draft extraction.
/// </summary>
public sealed partial class IntegrationService : IIntegrationService
{
    [GeneratedRegex(@"\$\s*([0-9][0-9,]*(\.[0-9]{1,2})?)", RegexOptions.Compiled)]
    private static partial Regex PriceRegex();

    private readonly LavariDbContext _db;
    private readonly ICurrentActorContext _actor;
    private readonly IPermissionEvaluator _permissions;
    private readonly LavariIntegrationsOptions _options;
    private readonly IIdGenerator _ids;
    private readonly IProductAndLineItemService _products;
    private readonly SupplierAiAuthorization _aiAuthorization;
    private readonly SupplierAiSpendReservations _spendReservations;
    private readonly ICurrentActorContextScope _actorScope;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IntegrationService> _logger;

    public IntegrationService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IOptions<LavariIntegrationsOptions> options,
        IIdGenerator ids,
        IProductAndLineItemService products,
        SupplierAiAuthorization aiAuthorization,
        SupplierAiSpendReservations spendReservations,
        ICurrentActorContextScope actorScope,
        IServiceProvider services,
        TimeProvider timeProvider,
        ILogger<IntegrationService> logger)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _options = options.Value;
        _ids = ids;
        _products = products;
        _aiAuthorization = aiAuthorization;
        _spendReservations = spendReservations;
        _actorScope = actorScope;
        _services = services;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IntegrationStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        _permissions.RequireAuthenticated();
        var userId = _actor.ActorId ?? Guid.Empty;
        var conn = await _db.EmailConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected, ct);

        var gmailConfigured = _options.Gmail.Enabled && !string.IsNullOrWhiteSpace(_options.Gmail.ClientId);
        var geminiConfigured = _options.Gemini.Enabled && GeminiApiClient.IsValidApiKey(_options.Gemini.ApiKey);
        var spendConfigured = await _spendReservations.IsConfiguredAsync(ct);
        var extractionReady = geminiConfigured && spendConfigured;

        return new IntegrationStatusDto
        {
            GmailConfigured = gmailConfigured,
            GmailConnectedForCurrentUser = conn is not null,
            GmailConnectedAccount = conn?.AccountEmail,
            GeminiConfigured = geminiConfigured,
            GeminiModel = _options.Gemini.Model,
            GeminiExtractionReady = extractionReady,
            StatusSummary = extractionReady
                ? $"Gemini supplier extraction is available with explicit provider consent and a configured usage budget (model: {_options.Gemini.Model})."
                : geminiConfigured
                    ? $"Gemini is configured (model: {_options.Gemini.Model}), but AI spend-limit settings are missing or invalid. Supplier capture otherwise uses local heuristics."
                    : "Gemini is not configured. Supplier capture uses local draft parsing only."
        };
    }

    public async Task<LlmExtractionDraftDto> ExtractSupplierPageDraftAsync(SupplierCaptureRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Alter);

        if (!request.UserConfirmedConsent)
        {
            throw new ValidationServiceException("Explicit user confirmation is required before capturing supplier page content.");
        }

        if (request.ClientId.HasValue && !request.JobId.HasValue)
        {
            throw new ValidationServiceException("Select a job for the selected client.");
        }

        Guid? resolvedClientId = request.ClientId;
        if (request.JobId.HasValue)
        {
            _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
            var job = await _db.Jobs.AsNoTracking()
                .Where(j => j.Id == request.JobId.Value)
                .Select(j => new { j.ClientId })
                .FirstOrDefaultAsync(ct)
                ?? throw new NotFoundServiceException("Job not found.");
            if (request.ClientId.HasValue && request.ClientId.Value != job.ClientId)
            {
                throw new ValidationServiceException("The selected job does not belong to the selected client.");
            }
            resolvedClientId = job.ClientId;
        }

        if (request.UseGemini)
        {
            _permissions.Require(ModuleKeys.LlmAdministration, AccessLevel.Read);
            if (!_options.Gemini.Enabled || !GeminiApiClient.IsValidApiKey(_options.Gemini.ApiKey))
            {
                throw new ExternalIntegrationServiceException(
                    "Gemini extraction is not configured.",
                    possibleCause: "Ask an administrator to configure the Gemini provider and credentials.");
            }
            if (!request.UserConfirmedProviderConsent)
            {
                throw new ValidationServiceException("Explicit consent to send the selected content to Google Gemini is required.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            _permissions.Require(ModuleKeys.LlmAdministration, AccessLevel.Read);
            throw new ExternalIntegrationServiceException(
                "Prompt-based extraction requires selecting Gemini and confirming provider consent.",
                possibleCause: "Choose Gemini and approve sending the selected content to Google.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceUrl) || !Uri.TryCreate(request.SourceUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ValidationServiceException("A valid http/https supplier URL without credentials is required.");
        }

        if (string.IsNullOrWhiteSpace(request.PageTextContent) || request.PageTextContent.Length > 16000)
        {
            throw new ValidationServiceException("Provide selected supplier text of at most 16,000 characters.");
        }

        var sanitized = SanitizeContent(request.PageTextContent);
        if (request.UseGemini)
        {
            return await ExtractGeminiSupplierPageDraftAsync(request, uri, resolvedClientId, sanitized, ct);
        }

        var lines = sanitized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateTitle = lines.FirstOrDefault() ?? uri.Host;
        var priceMatch = PriceRegex().Match(sanitized);
        long? candidateRrpCents = null;
        if (priceMatch.Success &&
            decimal.TryParse(priceMatch.Groups[1].Value.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dec) &&
            dec <= MoneyCents.MaxSafeJsInteger / 100m)
        {
            candidateRrpCents = MoneyCents.FromExactDecimalInput(dec, "AUD").Cents;
        }

        var extractedPayload = JsonSerializer.Serialize(new
        {
            name = candidateTitle,
            supplierName = uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase),
            supplierUrl = uri.ToString(),
            candidateRrpCents,
            currency = "AUD",
            requiresUserReview = true,
            note = "Generated a local heuristic draft for manual review; no AI model was used."
        });

        var draft = new LlmExtractionDraftEntity
        {
            Id = _ids.NewId(),
            JobId = request.JobId,
            SourceUrl = uri.ToString(),
            CapturedContentSanitized = string.Empty,
            ExtractedJson = extractedPayload,
            Status = "PendingReview",
            ProviderName = "LocalHeuristicDraft",
            ModelName = string.Empty
        };
        _db.LlmExtractionDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);

        return MapDraft(draft);
    }

    private async Task<LlmExtractionDraftDto> ExtractGeminiSupplierPageDraftAsync(
        SupplierCaptureRequest request,
        Uri sourceUri,
        Guid? clientId,
        string selectedText,
        CancellationToken ct)
    {
        var prompt = string.IsNullOrWhiteSpace(request.Prompt)
            ? "Extract the product name, concise description, explicitly stated price and currency, attributes, identifiers, and image URLs."
            : request.Prompt.Trim();
        if (prompt.Length > 4000)
        {
            throw new ValidationServiceException("The extraction prompt must be at most 4,000 characters.");
        }

        var userId = _actor.ActorId ?? throw new UnauthorizedServiceException();
        var correlationId = string.IsNullOrWhiteSpace(_actor.CorrelationId)
            ? _ids.NewId().ToString("N")
            : _actor.CorrelationId;
        var serviceActor = await _aiAuthorization.AuthorizeAsync(userId, clientId, request.JobId, correlationId, ct);
        var provider = _services.GetRequiredService<IGenerativeTextProvider>();
        var fingerprintData = JsonSerializer.Serialize(new
        {
            sourceUrl = sourceUri.ToString(),
            selectedText,
            prompt,
            clientId,
            jobId = request.JobId,
            model = _options.Gemini.Model
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintData))).ToLowerInvariant();
        var requestId = _ids.NewId();
        var reservationId = await _spendReservations.ReserveAsync(
            requestId, fingerprint, clientId, request.JobId, "Gemini", _options.Gemini.Model, ct);

        var draft = new LlmExtractionDraftEntity
        {
            Id = _ids.NewId(),
            JobId = request.JobId,
            SourceUrl = sourceUri.ToString(),
            CapturedContentSanitized = string.Empty,
            ExtractedJson = "{}",
            Status = "Processing",
            ProviderName = "Gemini",
            ModelName = _options.Gemini.Model,
            ProviderConsentGrantedAtUtc = _timeProvider.GetUtcNow()
        };

        try
        {
            _db.LlmExtractionDrafts.Add(draft);
            await _db.SaveChangesAsync(ct);
            SupplierPageSuggestion suggestion;
            GenerativeTextResponse usage;
            using (_actorScope.Use(serviceActor))
            {
                await _spendReservations.BeginAsync(reservationId, ct);
                var extractor = new SupplierPageTextExtractor(provider);
                try
                {
                    (suggestion, usage) = await extractor.ExtractAsync(_options.Gemini.Model, selectedText, prompt, ct);
                }
                catch (ExternalIntegrationServiceException ex)
                {
                    throw new ExternalIntegrationServiceException(
                        "Gemini supplier extraction could not be completed.",
                        possibleCause: "Try again or enter the product details manually.",
                        technicalDetails: ex.Message,
                        innerException: ex);
                }

                var completionActor = await _aiAuthorization.AuthorizeServicePrincipalAsync(correlationId, ct);
                using (_actorScope.Use(completionActor))
                {
                    await _spendReservations.CompleteAsync(
                        reservationId, usage.InputTokens, usage.OutputTokens,
                        usage.ReasoningTokens, usage.TotalTokens, ct);
                }
            }

            var initiator = await _aiAuthorization.AuthorizeInitiatingUserAsync(
                userId, clientId, request.JobId, correlationId, ct);
            using (_actorScope.Use(initiator))
            {
                var name = sourceUri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
                draft.ExtractedJson = JsonSerializer.Serialize(new
                {
                    name = suggestion.Name,
                    supplierName = name,
                    supplierUrl = sourceUri.ToString(),
                    description = suggestion.Description,
                    candidateRrpCents = suggestion.CandidateRrpCents,
                    currency = suggestion.Currency,
                    attributes = suggestion.Attributes.Select(x => new { label = x.Label, value = x.Value }),
                    identifiers = suggestion.Identifiers.Select(x => new { label = x.Label, value = x.Value }),
                    imageUrls = suggestion.ImageUrls,
                    requiresUserReview = true,
                    note = "Gemini suggestion; verify every field before accepting."
                });
                draft.Status = "PendingReview";
                draft.PromptTokens = usage.InputTokens ?? 0;
                draft.CompletionTokens = usage.OutputTokens ?? 0;
                await _db.SaveChangesAsync(ct);
            }

            return MapDraft(draft);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Supplier extraction failed (DraftId={DraftId}, CorrelationId={CorrelationId}, ExceptionType={ExceptionType}).",
                draft.Id, correlationId, ex.GetType().Name);
            try
            {
                var failureActor = await _aiAuthorization.AuthorizeServicePrincipalAsync(correlationId, CancellationToken.None);
                using (_actorScope.Use(failureActor))
                {
                    await _spendReservations.ReleaseIfNotStartedAsync(reservationId, CancellationToken.None);
                    draft.Status = "Failed";
                    await _db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception statusException)
            {
                _logger.LogError(
                    "Supplier extraction failure status could not be recorded (DraftId={DraftId}, CorrelationId={CorrelationId}, ExceptionType={ExceptionType}).",
                    draft.Id, correlationId, statusException.GetType().Name);
                throw new AggregateException("Supplier extraction and failure recording both failed.", ex, statusException);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<LlmExtractionDraftDto>> ListExtractionDraftsAsync(Guid? jobId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Read);
        var query = _db.LlmExtractionDrafts.AsNoTracking();
        if (jobId.HasValue)
        {
            _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
            if (!await _db.Jobs.AnyAsync(j => j.Id == jobId.Value, ct))
            {
                throw new NotFoundServiceException("Job not found.");
            }
            query = query.Where(d => d.JobId == jobId.Value);
        }
        else if (!_permissions.Has(ModuleKeys.Jobs, AccessLevel.Read) || !_permissions.Has(ModuleKeys.Clients, AccessLevel.Read))
        {
            query = query.Where(d => d.JobId == null);
        }
        else
        {
            query = query.Where(d => d.JobId == null || _db.Jobs.Any(j => j.Id == d.JobId));
        }
        var rows = await query.OrderByDescending(d => d.CreatedAtUtc).Take(50).ToListAsync(ct);
        return rows.Select(MapDraft).ToArray();
    }

    public async Task<LlmExtractionDraftDto> GetExtractionDraftAsync(Guid draftId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Read);
        var draft = await _db.LlmExtractionDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct)
            ?? throw new NotFoundServiceException("Supplier draft not found.");
        if (draft.JobId.HasValue)
        {
            _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
            if (!await _db.Jobs.AnyAsync(j => j.Id == draft.JobId.Value, ct))
            {
                throw new NotFoundServiceException("Supplier draft not found.");
            }
        }
        else if (!_permissions.Has(ModuleKeys.Jobs, AccessLevel.Read) || !_permissions.Has(ModuleKeys.Clients, AccessLevel.Read))
        {
            if (draft.JobId.HasValue) throw new NotFoundServiceException("Supplier draft not found.");
        }
        return MapDraft(draft);
    }

    public async Task<LlmExtractionDraftDto> AcceptSupplierDraftAsync(
        Guid draftId,
        AcceptSupplierDraftRequest request,
        CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Alter);
        _permissions.Require(ModuleKeys.ProductCatalog, AccessLevel.Alter);

        var draft = await _db.LlmExtractionDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct)
            ?? throw new NotFoundServiceException("Supplier draft not found.");
        if (draft.Status != "PendingReview")
        {
            throw new ConflictServiceException("Supplier draft is no longer pending review.");
        }
        if (!string.Equals(draft.ConcurrencyToken, request.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new ConflictServiceException("Supplier draft was modified by another user.");
        }
        if (draft.JobId.HasValue)
        {
            _permissions.Require(ModuleKeys.Jobs, AccessLevel.Read);
            _permissions.Require(ModuleKeys.Clients, AccessLevel.Read);
            if (!await _db.Jobs.AnyAsync(j => j.Id == draft.JobId.Value, ct))
            {
                throw new NotFoundServiceException("Job not found.");
            }
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var product = await _products.CreateProductAsync(request.Product, ct);
        draft.Status = "Approved";
        draft.AcceptedProductId = product.Id;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return MapDraft(draft);
    }

    private static string SanitizeContent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return Regex.Replace(raw, "<.*?>", " ").Trim();
    }

    private static LlmExtractionDraftDto MapDraft(LlmExtractionDraftEntity d) => new()
    {
        Id = d.Id,
        JobId = d.JobId,
        AcceptedProductId = d.AcceptedProductId,
        SourceUrl = d.SourceUrl,
        Status = d.Status,
        ProviderName = d.ProviderName,
        ModelName = d.ModelName,
        ExtractedJson = d.ExtractedJson,
        CreatedAtUtc = d.CreatedAtUtc,
        ConcurrencyToken = d.ConcurrencyToken,
        ProviderConsentGrantedAtUtc = d.ProviderConsentGrantedAtUtc
    };
}
