using System.Text.Json;
using System.Text.RegularExpressions;
using Lavari.Application.Abstractions;
using Lavari.Application.Services;
using Lavari.Contracts.Business;
using Lavari.Domain.Errors;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Domain.Time;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        var jobs = await jobQuery.ToDictionaryAsync(j => j.Id, j => j, ct);
        var jobIds = jobs.Keys.ToList();

        var sources = new List<ForecastSourceItemDto>();
        var plannedRev = MoneyCents.Zero(currency);
        var actualRev = MoneyCents.Zero(currency);
        var plannedCost = MoneyCents.Zero(currency);
        var actualCost = MoneyCents.Zero(currency);
        var incompleteCount = 0;

        // 1. Issued invoices = Actual Revenue; Draft quotes NOT converted to an invoice = Planned Revenue (no double-counting!)
        if (_permissions.Has(ModuleKeys.Invoices, AccessLevel.Read) || _permissions.Has(ModuleKeys.Quotes, AccessLevel.Read))
        {
            var docs = await _db.FinancialDocuments.AsNoTracking()
                .Where(d => jobIds.Contains(d.JobId) && d.Currency == currency && d.Status != "Void")
                .ToListAsync(ct);

            var convertedQuoteIds = docs.Where(d => d.SourceQuoteId.HasValue).Select(d => d.SourceQuoteId!.Value).ToHashSet();

            foreach (var doc in docs)
            {
                if (doc.DocumentType == "Quote" && !_permissions.Has(ModuleKeys.Quotes, AccessLevel.Read)) continue;
                if (doc.DocumentType == "Invoice" && !_permissions.Has(ModuleKeys.Invoices, AccessLevel.Read)) continue;
                if (doc.DocumentType == "Quote" && convertedQuoteIds.Contains(doc.Id)) continue; // Avoid double-counting quote + resulting invoice

                var effDate = doc.IssueDate ?? doc.DueDate;
                if (effDate is null)
                {
                    incompleteCount++;
                    continue;
                }

                if (effDate.Value < request.StartDate || effDate.Value > request.EndDate) continue;

                var amt = new MoneyCents(doc.AppliedTotalCents, currency);
                var isActual = doc.DocumentType == "Invoice" && doc.Status == "Issued";

                if (isActual) actualRev = actualRev.Add(amt);
                else plannedRev = plannedRev.Add(amt);

                sources.Add(new ForecastSourceItemDto
                {
                    JobId = doc.JobId,
                    JobName = jobs[doc.JobId].Name,
                    SourceType = doc.DocumentType,
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
                .Include(p => p.Payments)
                .Where(p => jobIds.Contains(p.JobId) && p.Currency == currency && p.Status != "Cancelled")
                .ToListAsync(ct);

            foreach (var p in procs)
            {
                long paidSum = 0;
                foreach (var pay in p.Payments.Where(x => !x.IsDeleted && x.Status == "Paid"))
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
                        JobName = jobs[p.JobId].Name,
                        SourceType = "Procurement",
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
                            JobName = jobs[p.JobId].Name,
                            SourceType = "Procurement",
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
                    JobName = jobs[d.JobId].Name,
                    SourceType = "Milestone",
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
/// Replaceable integration adapter for Gmail and Gemini LLM extraction.
/// Safely operates in manual/draft heuristic fallback mode until API keys are supplied in user secrets / secret provider.
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

    public IntegrationService(
        LavariDbContext db,
        ICurrentActorContext actor,
        IPermissionEvaluator permissions,
        IOptions<LavariIntegrationsOptions> options,
        IIdGenerator ids)
    {
        _db = db;
        _actor = actor;
        _permissions = permissions;
        _options = options.Value;
        _ids = ids;
    }

    public async Task<IntegrationStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        _permissions.RequireAuthenticated();
        var userId = _actor.ActorId ?? Guid.Empty;
        var conn = await _db.EmailConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected, ct);

        var gmailConfigured = _options.Gmail.Enabled && !string.IsNullOrWhiteSpace(_options.Gmail.ClientId);
        var geminiConfigured = _options.Gemini.Enabled && !string.IsNullOrWhiteSpace(_options.Gemini.ApiKey);

        return new IntegrationStatusDto
        {
            GmailConfigured = gmailConfigured,
            GmailConnectedForCurrentUser = conn is not null,
            GmailConnectedAccount = conn?.AccountEmail,
            GeminiConfigured = geminiConfigured,
            GeminiModel = _options.Gemini.Model,
            StatusSummary = (!gmailConfigured && !geminiConfigured)
                ? "Gmail OAuth and Gemini API credentials are not yet configured. Add them via dotnet user-secrets when ready; manual entry and draft parsing remain active."
                : "Configured integrations are active."
        };
    }

    public async Task<LlmExtractionDraftDto> ExtractSupplierPageDraftAsync(SupplierCaptureRequest request, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Alter);

        if (!request.UserConfirmedConsent)
        {
            throw new ValidationServiceException("Explicit user confirmation is required before capturing supplier page content.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceUrl) || !Uri.TryCreate(request.SourceUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ValidationServiceException("A valid http/https supplier URL is required.");
        }

        var sanitized = SanitizeContent(request.PageTextContent);
        var lines = sanitized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateTitle = lines.FirstOrDefault() ?? uri.Host;
        var priceMatch = PriceRegex().Match(sanitized);
        long? candidateRrpCents = null;
        if (priceMatch.Success && decimal.TryParse(priceMatch.Groups[1].Value.Replace(",", string.Empty), out var dec))
        {
            try { candidateRrpCents = MoneyCents.FromExactDecimalInput(dec, "AUD").Cents; } catch { /* leave null for review */ }
        }

        var extractedPayload = JsonSerializer.Serialize(new
        {
            name = candidateTitle,
            supplierName = uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase),
            supplierUrl = uri.ToString(),
            candidateRrpCents,
            currency = "AUD",
            requiresUserReview = true,
            note = _options.Gemini.Enabled && !string.IsNullOrWhiteSpace(_options.Gemini.ApiKey)
                ? "Parsed candidate draft ready for review."
                : "Gemini API key not configured yet — generated local heuristic draft for manual review."
        });

        var draft = new LlmExtractionDraftEntity
        {
            Id = _ids.NewId(),
            JobId = request.JobId,
            SourceUrl = uri.ToString(),
            CapturedContentSanitized = sanitized.Length > 4000 ? sanitized[..4000] : sanitized,
            ExtractedJson = extractedPayload,
            Status = "PendingReview",
            ProviderName = _options.Gemini.Enabled && !string.IsNullOrWhiteSpace(_options.Gemini.ApiKey) ? "Gemini" : "LocalHeuristicDraft",
            ModelName = _options.Gemini.Model
        };
        _db.LlmExtractionDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);

        return MapDraft(draft);
    }

    public async Task<IReadOnlyList<LlmExtractionDraftDto>> ListExtractionDraftsAsync(Guid? jobId, CancellationToken ct = default)
    {
        _permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Read);
        var query = _db.LlmExtractionDrafts.AsNoTracking();
        if (jobId.HasValue)
        {
            query = query.Where(d => d.JobId == jobId.Value);
        }
        var rows = await query.OrderByDescending(d => d.CreatedAtUtc).Take(50).ToListAsync(ct);
        return rows.Select(MapDraft).ToArray();
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
        SourceUrl = d.SourceUrl,
        Status = d.Status,
        ProviderName = d.ProviderName,
        ModelName = d.ModelName,
        ExtractedJson = d.ExtractedJson,
        CreatedAtUtc = d.CreatedAtUtc
    };
}
