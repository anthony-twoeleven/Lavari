using System.Globalization;
using System.Text.RegularExpressions;
using Lavari.Application.Abstractions;
using Lavari.Domain.Errors;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Infrastructure.Integrations;

// Tracks conservative budgets and reported usage for authorized AI provider calls.
public sealed partial class SupplierAiSpendReservations(
    LavariDbContext db,
    ICurrentActorContext actor,
    IPermissionEvaluator permissions,
    SupplierAiAuthorization authorization,
    TimeProvider time,
    IIdGenerator ids)
{
    private const string Reserved = "Reserved";
    private const string Consumed = "Consumed";
    private const string Completed = "Completed";
    private const string Cancelled = "Cancelled";

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex FingerprintPattern();

    public async Task<Guid> ReserveAsync(
        Guid clientRequestId, string fingerprint, Guid? clientId, Guid? jobId,
        string provider, string model, CancellationToken ct = default)
    {
        permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Alter);
        permissions.Require(ModuleKeys.LlmAdministration, AccessLevel.Read);
        var userId = actor.ActorId ?? throw new UnauthorizedServiceException();
        if (actor.IsSystemPrincipal || clientRequestId == Guid.Empty ||
            fingerprint is null || !FingerprintPattern().IsMatch(fingerprint) ||
            provider is null || !IdentifierPattern().IsMatch(provider) ||
            model is null || !IdentifierPattern().IsMatch(model))
            throw new ValidationServiceException("Invalid AI reservation request.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var principal = await authorization.AuthorizeAsync(userId, clientId, jobId, actor.CorrelationId, ct);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var existing = await db.AiSpendReservations.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.InitiatingUserId == userId && x.ClientRequestId == clientRequestId, ct);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != fingerprint || existing.ClientId != clientId ||
                existing.JobId != jobId || existing.Provider != provider || existing.Model != model)
                throw new ConflictServiceException("This request reference has already been used for different content.");
            if (existing.State == Cancelled || existing.DeletedAtUtc.HasValue ||
                (existing.State == Reserved && existing.BudgetDateUtc != today) ||
                existing.ServicePrincipalId != principal.ActorId)
                throw new ConflictServiceException("This request is no longer available; submit a new request.");
            return existing.Id;
        }

        var policy = await ReadPolicyAsync(ct);
        var reservations = db.AiSpendReservations.IgnoreQueryFilters().Where(x => x.BudgetDateUtc == today);
        if (await reservations.AnyAsync(x => x.State != Cancelled && x.Currency != policy.Currency, ct))
            throw new ConflictServiceException("The AI budget currency cannot change while today's reservations exist.");
        var active = reservations.Where(x => x.State != Cancelled);
        var globalUsed = await active.SumAsync(x => (long?)x.ReservedCents, ct) ?? 0;
        var userUsed = await active.Where(x => x.InitiatingUserId == userId)
            .SumAsync(x => (long?)x.ReservedCents, ct) ?? 0;
        var userRequests = await reservations.CountAsync(x => x.InitiatingUserId == userId, ct);
        if (policy.ReservationCents > policy.DailyLimitCents ||
            globalUsed > policy.DailyLimitCents - policy.ReservationCents ||
            policy.ReservationCents > policy.UserDailyLimitCents ||
            userUsed > policy.UserDailyLimitCents - policy.ReservationCents ||
            userRequests >= policy.UserDailyRequests)
            throw new ConflictServiceException("The AI request or reservation limit has been reached.",
                possibleCause: "Try again later or ask an administrator to review the AI budget.");

        var reservation = new AiSpendReservationEntity
        {
            Id = ids.NewId(),
            InitiatingUserId = userId,
            ServicePrincipalId = principal.ActorId!.Value,
            ClientRequestId = clientRequestId,
            RequestFingerprint = fingerprint,
            ClientId = clientId,
            JobId = jobId,
            Provider = provider,
            Model = model,
            CorrelationId = actor.CorrelationId,
            BudgetDateUtc = today,
            Currency = policy.Currency,
            ReservedCents = policy.ReservationCents,
            State = Reserved
        };
        db.AiSpendReservations.Add(reservation);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return reservation.Id;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => await ReadPolicyIfConfiguredAsync(ct) is not null;

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Alter);
        permissions.Require(ModuleKeys.LlmAdministration, AccessLevel.Read);
        var userId = actor.ActorId ?? throw new UnauthorizedServiceException();
        if (actor.IsSystemPrincipal) throw new ForbiddenServiceException();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reservation = await db.AiSpendReservations.SingleOrDefaultAsync(x => x.Id == id && x.InitiatingUserId == userId, ct)
            ?? throw new NotFoundServiceException();
        await authorization.AuthorizeAsync(userId, reservation.ClientId, reservation.JobId, reservation.CorrelationId, ct);
        if (reservation.State != Reserved)
            throw new ConflictServiceException("Only a reservation that has not started can be cancelled.");
        reservation.State = Cancelled;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task BeginAsync(Guid id, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reservation = await RequireWorkerAsync(id, recheckInitiator: true, ct);
        if (reservation.State != Reserved || reservation.BudgetDateUtc != DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
            throw new ConflictServiceException("The AI reservation is no longer available for execution.");
        var policy = await ReadPolicyAsync(ct);
        var active = db.AiSpendReservations.IgnoreQueryFilters()
            .Where(x => x.BudgetDateUtc == reservation.BudgetDateUtc && x.State != Cancelled);
        var total = await active.SumAsync(x => (long?)x.ReservedCents, ct) ?? 0;
        var userTotal = await active.Where(x => x.InitiatingUserId == reservation.InitiatingUserId)
            .SumAsync(x => (long?)x.ReservedCents, ct) ?? 0;
        var userRequests = await db.AiSpendReservations.IgnoreQueryFilters().CountAsync(
            x => x.BudgetDateUtc == reservation.BudgetDateUtc && x.InitiatingUserId == reservation.InitiatingUserId, ct);
        if (policy.Currency != reservation.Currency || reservation.ReservedCents != policy.ReservationCents ||
            total > policy.DailyLimitCents || userTotal > policy.UserDailyLimitCents ||
            userRequests > policy.UserDailyRequests)
            throw new ConflictServiceException("The AI reservation exceeds the current budget.");
        reservation.State = Consumed;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CompleteAsync(
        Guid id, int? inputTokens, int? outputTokens, int? reasoningTokens, int? totalTokens,
        CancellationToken ct = default)
    {
        if (inputTokens < 0 || outputTokens < 0 || reasoningTokens < 0 || totalTokens < 0)
            throw new ValidationServiceException("AI usage counts cannot be negative.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reservation = await RequireWorkerAsync(id, recheckInitiator: false, ct);
        if (reservation.State != Consumed)
            throw new ConflictServiceException("The AI reservation is not awaiting usage.");
        reservation.InputTokens = inputTokens;
        reservation.OutputTokens = outputTokens;
        reservation.ReasoningTokens = reasoningTokens;
        reservation.TotalTokens = totalTokens;
        reservation.State = Completed;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReleaseIfNotStartedAsync(Guid id, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reservation = await RequireWorkerAsync(id, recheckInitiator: false, ct);
        if (reservation.State == Reserved)
        {
            reservation.State = Cancelled;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task<AiSpendReservationEntity> RequireWorkerAsync(Guid id, bool recheckInitiator, CancellationToken ct)
    {
        permissions.Require(ModuleKeys.SupplierCapture, AccessLevel.Alter);
        permissions.Require(ModuleKeys.LlmAdministration, AccessLevel.Read);
        if (!actor.IsSystemPrincipal || !actor.ActorId.HasValue) throw new ForbiddenServiceException();
        var reservation = await db.AiSpendReservations.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundServiceException();
        var principal = recheckInitiator
            ? await authorization.AuthorizeAsync(reservation.InitiatingUserId,
                reservation.ClientId, reservation.JobId, reservation.CorrelationId, ct)
            : await authorization.AuthorizeServicePrincipalAsync(reservation.CorrelationId, ct);
        if (actor.ActorId != reservation.ServicePrincipalId || actor.ActorId != principal.ActorId)
            throw new ForbiddenServiceException();
        return reservation;
    }

    private async Task<BudgetPolicy> ReadPolicyAsync(CancellationToken ct)
    {
        var policy = await ReadPolicyIfConfiguredAsync(ct);
        return policy ?? throw new ExternalIntegrationServiceException("AI reservation limits are not configured.",
            possibleCause: "Ask an administrator to configure valid AI budget limits.");
    }

    private async Task<BudgetPolicy?> ReadPolicyIfConfiguredAsync(CancellationToken ct)
    {
        var keys = new[] { "Ai.SpendCurrency", "Ai.DailyLimitCents", "Ai.UserDailyLimitCents",
            "Ai.UserDailyRequests", "Ai.ReservationCents" };
        var values = await db.ApplicationVariables.AsNoTracking().Where(x => keys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        if (!values.TryGetValue(keys[0], out var currency) ||
            !MoneyCents.SupportedTwoDecimalCurrencies.Contains(currency) ||
            !TryPositive(values, keys[1], out var daily) ||
            !TryPositive(values, keys[2], out var userDaily) ||
            !TryPositive(values, keys[3], out var requests) || requests > int.MaxValue ||
            !TryPositive(values, keys[4], out var amount))
            return null;
        return new BudgetPolicy(currency, daily, userDaily, (int)requests, amount);
    }

    private static bool TryPositive(IReadOnlyDictionary<string, string> values, string key, out long value)
    {
        value = 0;
        return values.TryGetValue(key, out var raw) &&
            long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value is > 0 and <= MoneyCents.MaxSafeJsInteger;
    }

    private sealed record BudgetPolicy(string Currency, long DailyLimitCents,
        long UserDailyLimitCents, int UserDailyRequests, long ReservationCents);
}
