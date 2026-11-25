using System.Security.Claims;
using System.Text.Json;
using Lavari.Api.Security;
using Lavari.Application.Services;
using Lavari.Contracts.Common;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lavari.Api.Middleware;

public sealed class LavariActorHydrationMiddleware
{
    private readonly RequestDelegate _next;

    public LavariActorHydrationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        HttpCurrentActorContext actorContext,
        IAuthService authService)
    {
        var correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var existingCorr) && !string.IsNullOrWhiteSpace(existingCorr)
            ? existingCorr.ToString()
            : Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        context.Items["CorrelationId"] = correlationId;

        if (context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var sessionId = context.Request.Headers.TryGetValue("X-Editing-Session", out var headerSess) && !string.IsNullOrWhiteSpace(headerSess)
                ? headerSess.ToString()
                : (context.User.FindFirstValue("lavari:session") ?? $"sess-{userId:N}");

            // Always reload fresh permissions from server authority so role changes/revocations take effect immediately!
            var session = await authService.LoadCurrentSessionAsync(userId, sessionId, context.RequestAborted);
            if (session is not null)
            {
                var grants = session.EffectivePermissions.ToDictionary(
                    kv => kv.Key,
                    kv => (AccessLevel)kv.Value,
                    StringComparer.Ordinal);
                actorContext.Initialize(correlationId, true, session.UserId, session.DisplayName, sessionId, session.IsFullAccess, grants);
            }
            else
            {
                actorContext.Initialize(correlationId, false, null, "Disabled", string.Empty, false, PermissionRegistry.ComputeEffectiveGrants(false, []));
            }
        }
        else
        {
            actorContext.Initialize(correlationId, false, null, "Anonymous", string.Empty, false, PermissionRegistry.ComputeEffectiveGrants(false, []));
        }

        await _next(context);
    }
}

public sealed class LavariAntiforgeryMiddleware
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };
    private readonly RequestDelegate _next;

    public LavariAntiforgeryMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            !SafeMethods.Contains(context.Request.Method) &&
            !context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) &&
            context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException ex)
            {
                throw new ForbiddenServiceException(
                    "Invalid or missing security token. Reload the page and retry.",
                    ErrorCodes.Forbidden,
                    "Refresh the application to obtain a new anti-forgery token.",
                    ex.Message);
            }
        }

        await _next(context);
    }
}

public sealed class LavariExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<LavariExceptionMiddleware> _logger;

    public LavariExceptionMiddleware(RequestDelegate next, ILogger<LavariExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAdminService adminService)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var correlationId = context.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString("N");
            var actorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var path = context.Request.Path.Value;

            ApiErrorResponse payload;
            int statusCode;
            string techDetail;

            if (ex is ServiceException sex)
            {
                statusCode = sex.HttpStatusCode;
                payload = new ApiErrorResponse
                {
                    Code = sex.ErrorCode,
                    Message = sex.UserMessage,
                    PossibleCause = sex.PossibleCause,
                    CorrelationId = correlationId,
                    FieldErrors = sex.FieldErrors
                };
                techDetail = sex.TechnicalDetails ?? sex.Message;
            }
            else
            {
                statusCode = StatusCodes.Status500InternalServerError;
                payload = new ApiErrorResponse
                {
                    Code = ErrorCodes.UnexpectedError,
                    Message = "An unexpected error occurred while processing your request.",
                    PossibleCause = "Retry the action or share the support reference with an administrator.",
                    CorrelationId = correlationId
                };
                techDetail = $"{ex.GetType().Name}: {ex.Message}";
            }

            // Persist restricted occurrence details with stack trace; never let logging failure mask or convert the error!
            try
            {
                await adminService.LogErrorOccurrenceAsync(
                    payload.Code,
                    correlationId,
                    actorId,
                    path,
                    RedactSecrets(techDetail),
                    RedactSecrets(ex.StackTrace ?? string.Empty),
                    CancellationToken.None);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx,
                    "Fallback operational error log (CorrelationId={CorrelationId}, Code={Code}, Path={Path}): {Detail}",
                    correlationId, payload.Code, path, RedactSecrets(techDetail));
            }

            if (statusCode >= 500)
            {
                _logger.LogError(ex, "Unhandled API error (CorrelationId={CorrelationId}, Path={Path})", correlationId, path);
            }
            else
            {
                _logger.LogWarning("Handled ServiceException {Code} (CorrelationId={CorrelationId}, Path={Path})", payload.Code, correlationId, path);
            }

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }

    private static string RedactSecrets(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input.Length > 4000 ? input[..4000] : input;
    }
}
