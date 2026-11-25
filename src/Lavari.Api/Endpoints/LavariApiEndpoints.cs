using System.Security.Claims;
using Lavari.Api.Hubs;
using Lavari.Application.Abstractions;
using Lavari.Application.Locking;
using Lavari.Application.Services;
using Lavari.Contracts.Admin;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Contracts.Security;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lavari.Api.Endpoints;

public static class LavariApiEndpoints
{
    public static IEndpointRouteBuilder MapLavariApi(this IEndpointRouteBuilder app)
    {
        app.MapHub<LavariHub>("/hubs/lavari");

        var api = app.MapGroup("/api");

        // --- Auth & Session ---
        api.MapGet("/auth/antiforgery", (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(ctx);
            return Results.Ok(new { token = tokens.RequestToken });
        });

        api.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx, IAuthService auth, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            var editingSessionId = $"sess-{Guid.NewGuid():N}";
            var session = await auth.AuthenticateAsync(req.Email, req.Password, editingSessionId, ct);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, session.UserId.ToString("D")),
                new(ClaimTypes.Email, session.Email),
                new(ClaimTypes.Name, session.DisplayName),
                new("lavari:session", editingSessionId)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });

            ctx.User = principal;
            var tokens = antiforgery.GetAndStoreTokens(ctx);
            return Results.Ok(new { session, csrfToken = tokens.RequestToken });
        });

        api.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        });

        api.MapGet("/auth/me", async (ICurrentActorContext actor, IAuthService auth, CancellationToken ct) =>
        {
            if (!actor.IsAuthenticated || !actor.ActorId.HasValue)
            {
                throw new UnauthorizedServiceException();
            }
            var session = await auth.LoadCurrentSessionAsync(actor.ActorId.Value, actor.EditingSessionId, ct)
                ?? throw new UnauthorizedServiceException();
            return Results.Ok(session);
        });

        api.MapGet("/auth/modules", (IPermissionEvaluator perms) =>
        {
            perms.RequireAuthenticated();
            return Results.Ok(PermissionRegistry.Modules.Select(m => new ModuleInfoDto
            {
                Key = m.Key,
                DisplayName = m.DisplayName,
                Category = m.Category
            }).ToArray());
        });

        // --- Clients, Job Groups & Jobs ---
        api.MapGet("/clients", (IClientAndJobService svc, int page = 1, int pageSize = 25, string? search = null, bool includeDeleted = false, CancellationToken ct = default) =>
            svc.ListClientsAsync(new PagedRequest { Page = page, PageSize = pageSize, Search = search, IncludeDeleted = includeDeleted }, ct));
        api.MapGet("/clients/{id:guid}", (Guid id, IClientAndJobService svc, CancellationToken ct) => svc.GetClientAsync(id, ct));
        api.MapPost("/clients", (UpsertClientRequest req, IClientAndJobService svc, CancellationToken ct) => svc.CreateClientAsync(req, ct));
        api.MapPut("/clients/{id:guid}", (Guid id, UpsertClientRequest req, IClientAndJobService svc, CancellationToken ct) => svc.UpdateClientAsync(id, req, ct));
        api.MapDelete("/clients/{id:guid}", async (Guid id, [FromQuery] string token, IClientAndJobService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteClientAsync(id, token, ct);
            return Results.NoContent();
        });
        api.MapPost("/clients/{id:guid}/restore", async (Guid id, [FromQuery] string token, IClientAndJobService svc, CancellationToken ct) =>
        {
            await svc.RestoreClientAsync(id, token, ct);
            return Results.NoContent();
        });

        api.MapGet("/job-groups", (IClientAndJobService svc, Guid? clientId = null, bool includeDeleted = false, CancellationToken ct = default) =>
            svc.ListJobGroupsAsync(clientId, includeDeleted, ct));
        api.MapPost("/job-groups", (UpsertJobGroupRequest req, IClientAndJobService svc, CancellationToken ct) => svc.CreateJobGroupAsync(req, ct));
        api.MapPut("/job-groups/{id:guid}", (Guid id, UpsertJobGroupRequest req, IClientAndJobService svc, CancellationToken ct) => svc.UpdateJobGroupAsync(id, req, ct));
        api.MapDelete("/job-groups/{id:guid}", async (Guid id, [FromQuery] string token, IClientAndJobService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteJobGroupAsync(id, token, ct);
            return Results.NoContent();
        });
        api.MapPost("/job-groups/{id:guid}/restore", async (Guid id, [FromQuery] string token, IClientAndJobService svc, CancellationToken ct) =>
        {
            await svc.RestoreJobGroupAsync(id, token, ct);
            return Results.NoContent();
        });

        api.MapGet("/jobs", (IClientAndJobService svc, Guid? clientId = null, Guid? jobGroupId = null, int page = 1, int pageSize = 25, string? search = null, bool includeDeleted = false, CancellationToken ct = default) =>
            svc.ListJobsAsync(clientId, jobGroupId, new PagedRequest { Page = page, PageSize = pageSize, Search = search, IncludeDeleted = includeDeleted }, ct));
        api.MapGet("/jobs/{id:guid}", (Guid id, IClientAndJobService svc, CancellationToken ct) => svc.GetJobAsync(id, ct));
        api.MapPost("/jobs", (UpsertJobRequest req, IClientAndJobService svc, CancellationToken ct) => svc.CreateJobAsync(req, ct));
        api.MapPut("/jobs/{id:guid}", (Guid id, UpsertJobRequest req, IClientAndJobService svc, CancellationToken ct) => svc.UpdateJobAsync(id, req, ct));
        api.MapDelete("/jobs/{id:guid}", async (Guid id, [FromQuery] string token, IClientAndJobService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteJobAsync(id, token, ct);
            return Results.NoContent();
        });
        api.MapPost("/jobs/{id:guid}/restore", async (Guid id, [FromQuery] string token, IClientAndJobService svc, CancellationToken ct) =>
        {
            await svc.RestoreJobAsync(id, token, ct);
            return Results.NoContent();
        });

        // --- Exclusive Edit Locks, Wait Queues & Notifications ---
        api.MapGet("/locks/{recordType}/{recordId:guid}", (string recordType, Guid recordId, IEditLockService svc, CancellationToken ct) =>
            svc.GetStatusAsync(recordType, recordId, ct));
        api.MapPost("/locks/modify", (EditLockActionRequest req, IEditLockService svc, CancellationToken ct) =>
            svc.RequestModifyAsync(req.RecordType, req.RecordId, ct));
        api.MapPost("/locks/claim", (EditLockActionRequest req, IEditLockService svc, CancellationToken ct) =>
            svc.ClaimReservationAsync(req.RecordType, req.RecordId, ct));
        api.MapPost("/locks/renew", (EditLockActionRequest req, IEditLockService svc, CancellationToken ct) =>
            svc.RenewLeaseAsync(req.RecordType, req.RecordId, req.LeaseId ?? Guid.Empty, req.FencingVersion ?? 0, ct));
        api.MapPost("/locks/release", (EditLockActionRequest req, IEditLockService svc, CancellationToken ct) =>
            svc.ReleaseLeaseAsync(req.RecordType, req.RecordId, req.LeaseId, req.FencingVersion, ct));
        api.MapPost("/locks/cancel-queue", (EditLockActionRequest req, IEditLockService svc, CancellationToken ct) =>
            svc.CancelQueueEntryAsync(req.RecordType, req.RecordId, ct));

        api.MapGet("/notifications", (IEditLockService svc, bool unreadOnly = false, CancellationToken ct = default) =>
            svc.GetNotificationsAsync(unreadOnly, ct));
        api.MapPost("/notifications/{id:guid}/read", async (Guid id, IEditLockService svc, CancellationToken ct) =>
        {
            await svc.MarkNotificationReadAsync(id, ct);
            return Results.NoContent();
        });

        // --- Job Dates & Milestones ---
        api.MapGet("/jobs/{jobId:guid}/dates", (Guid jobId, IJobDateService svc, CancellationToken ct) => svc.ListForJobAsync(jobId, ct));
        api.MapPost("/jobs/{jobId:guid}/dates", (Guid jobId, UpsertJobDateRequest req, IJobDateService svc, CancellationToken ct) => svc.CreateAsync(jobId, req, ct));
        api.MapPut("/job-dates/{id:guid}", (Guid id, UpsertJobDateRequest req, IJobDateService svc, CancellationToken ct) => svc.UpdateAsync(id, req, ct));
        api.MapDelete("/job-dates/{id:guid}", async (Guid id, [FromQuery] string token, Guid? leaseId, long? fencingVersion, IJobDateService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteAsync(id, token, leaseId, fencingVersion, ct);
            return Results.NoContent();
        });

        // --- Time Tracking ---
        api.MapGet("/jobs/{jobId:guid}/time", (Guid jobId, bool includeDeleted, ITimeTrackingService svc, CancellationToken ct) =>
            svc.ListForJobAsync(jobId, includeDeleted, ct));
        api.MapGet("/time/active", (ITimeTrackingService svc, CancellationToken ct) => svc.ListActiveTimersForCurrentUserAsync(ct));
        api.MapPost("/time/start", (StartTimerRequest req, ITimeTrackingService svc, CancellationToken ct) => svc.StartTimerAsync(req, ct));
        api.MapPost("/time/{id:guid}/pause", (Guid id, [FromQuery] string token, ITimeTrackingService svc, CancellationToken ct) => svc.PauseTimerAsync(id, token, ct));
        api.MapPost("/time/{id:guid}/resume", (Guid id, [FromQuery] string token, ITimeTrackingService svc, CancellationToken ct) => svc.ResumeTimerAsync(id, token, ct));
        api.MapPost("/time/{id:guid}/stop", (Guid id, [FromQuery] string token, ITimeTrackingService svc, CancellationToken ct) => svc.StopTimerAsync(id, token, ct));
        api.MapPost("/time/{id:guid}/cancel", (Guid id, [FromQuery] string token, ITimeTrackingService svc, CancellationToken ct) => svc.CancelTimerAsync(id, token, ct));
        api.MapPost("/time/{id:guid}/preview-stop-at", (Guid id, [FromQuery] DateTimeOffset effectiveTimeUtc, ITimeTrackingService svc, CancellationToken ct) => svc.PreviewStopAtAsync(id, effectiveTimeUtc, ct));
        api.MapPost("/time/{id:guid}/stop-at", (Guid id, TimerAtTimeRequest req, ITimeTrackingService svc, CancellationToken ct) => svc.StopAtAsync(id, req, ct));
        api.MapPost("/time/{id:guid}/preview-resume-at", (Guid id, [FromQuery] DateTimeOffset effectiveTimeUtc, ITimeTrackingService svc, CancellationToken ct) => svc.PreviewResumeAtAsync(id, effectiveTimeUtc, ct));
        api.MapPost("/time/{id:guid}/resume-at", (Guid id, TimerAtTimeRequest req, ITimeTrackingService svc, CancellationToken ct) => svc.ResumeAtAsync(id, req, ct));
        api.MapPost("/time/flat", (CreateFlatTimeEntryRequest req, ITimeTrackingService svc, CancellationToken ct) => svc.CreateFlatEntryAsync(req, ct));
        api.MapPost("/time/range", (CreateRangeTimeEntryRequest req, ITimeTrackingService svc, CancellationToken ct) => svc.CreateRangeEntryAsync(req, ct));
        api.MapPut("/time/{id:guid}", (Guid id, UpdateTimeEntryRequest req, ITimeTrackingService svc, CancellationToken ct) => svc.UpdateEntryAsync(id, req, ct));
        api.MapDelete("/time/{id:guid}", async (Guid id, [FromQuery] string token, ITimeTrackingService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteAsync(id, token, ct);
            return Results.NoContent();
        });
        api.MapPost("/time/{id:guid}/restore", async (Guid id, [FromQuery] string token, ITimeTrackingService svc, CancellationToken ct) =>
        {
            await svc.RestoreAsync(id, token, ct);
            return Results.NoContent();
        });

        // --- Product Catalog & Job Line Items ---
        api.MapGet("/products", (IProductAndLineItemService svc, int page = 1, int pageSize = 25, string? search = null, bool includeDeleted = false, CancellationToken ct = default) =>
            svc.ListProductsAsync(new PagedRequest { Page = page, PageSize = pageSize, Search = search, IncludeDeleted = includeDeleted }, ct));
        api.MapGet("/products/{id:guid}", (Guid id, IProductAndLineItemService svc, CancellationToken ct) => svc.GetProductAsync(id, ct));
        api.MapPost("/products", (UpsertProductRequest req, IProductAndLineItemService svc, CancellationToken ct) => svc.CreateProductAsync(req, ct));
        api.MapPut("/products/{id:guid}", (Guid id, UpsertProductRequest req, IProductAndLineItemService svc, CancellationToken ct) => svc.UpdateProductAsync(id, req, ct));
        api.MapDelete("/products/{id:guid}", async (Guid id, [FromQuery] string token, IProductAndLineItemService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteProductAsync(id, token, ct);
            return Results.NoContent();
        });
        api.MapPost("/products/{productId:guid}/variants", (Guid productId, UpsertProductVariantRequest req, IProductAndLineItemService svc, CancellationToken ct) =>
            svc.AddVariantAsync(productId, req, ct));

        api.MapGet("/jobs/{jobId:guid}/line-items", (Guid jobId, IProductAndLineItemService svc, CancellationToken ct) => svc.ListJobLineItemsAsync(jobId, ct));
        api.MapPost("/jobs/{jobId:guid}/line-items", (Guid jobId, UpsertJobLineItemRequest req, IProductAndLineItemService svc, CancellationToken ct) => svc.AddJobLineItemAsync(jobId, req, ct));
        api.MapPut("/line-items/{id:guid}", (Guid id, UpsertJobLineItemRequest req, IProductAndLineItemService svc, CancellationToken ct) => svc.UpdateJobLineItemAsync(id, req, ct));
        api.MapDelete("/line-items/{id:guid}", async (Guid id, [FromQuery] string token, Guid? leaseId, long? fencingVersion, IProductAndLineItemService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteJobLineItemAsync(id, token, leaseId, fencingVersion, ct);
            return Results.NoContent();
        });
        api.MapGet("/line-items/{id:guid}/timeline", (Guid id, IProductAndLineItemService svc, CancellationToken ct) => svc.GetLineItemTimelineAsync(id, ct));

        // --- Image Library (1080p SkiaSharp + Disk Storage) ---
        api.MapGet("/images", (IImageLibraryService svc, int page = 1, int pageSize = 24, string? search = null, Guid? clientId = null, Guid? jobId = null, bool globalOnly = false, string? sortBy = null, bool sortDesc = true, CancellationToken ct = default) =>
            svc.ListAsync(new ImageLibraryFilterRequest { Page = page, PageSize = pageSize, Search = search, ClientId = clientId, JobId = jobId, GlobalOnly = globalOnly, SortBy = sortBy, SortDescending = sortDesc }, ct));

        api.MapPost("/images/upload", async (HttpRequest req, IImageLibraryService svc, CancellationToken ct) =>
        {
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new ValidationServiceException("Image file is required.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            var title = form["title"].FirstOrDefault() ?? Path.GetFileNameWithoutExtension(file.FileName);
            var description = form["description"].FirstOrDefault();
            var provenance = form["sourceProvenance"].FirstOrDefault();
            var rightsConfirmed = bool.TryParse(form["rightsConfirmed"].FirstOrDefault(), out var rc) && rc;
            var isGlobal = !bool.TryParse(form["isGlobalLibrary"].FirstOrDefault(), out var ig) || ig;
            Guid? clientId = Guid.TryParse(form["clientId"].FirstOrDefault(), out var cid) ? cid : null;
            Guid? jobId = Guid.TryParse(form["jobId"].FirstOrDefault(), out var jid) ? jid : null;
            Guid? productId = Guid.TryParse(form["productId"].FirstOrDefault(), out var pid) ? pid : null;
            Guid? moodBoardId = Guid.TryParse(form["moodBoardId"].FirstOrDefault(), out var mbid) ? mbid : null;

            return await svc.UploadAsync(title, description, provenance, rightsConfirmed, isGlobal, clientId, jobId, productId, moodBoardId, ms.ToArray(), file.ContentType, ct);
        }).DisableAntiforgery();

        api.MapGet("/images/{id:guid}/content", async (Guid id, bool thumb, IImageLibraryService svc, CancellationToken ct) =>
        {
            var (bytes, contentType) = await svc.ReadImageBytesAsync(id, thumb, ct);
            return Results.File(bytes, contentType);
        });
        api.MapDelete("/images/{id:guid}", async (Guid id, [FromQuery] string token, IImageLibraryService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteAsync(id, token, ct);
            return Results.NoContent();
        });

        // --- Mood Boards & Canvases ---
        api.MapGet("/jobs/{jobId:guid}/mood-boards", (Guid jobId, IMoodBoardService svc, CancellationToken ct) => svc.ListForJobAsync(jobId, ct));
        api.MapGet("/mood-boards/{boardId:guid}", (Guid boardId, IMoodBoardService svc, CancellationToken ct) => svc.GetAsync(boardId, ct));
        api.MapPost("/jobs/{jobId:guid}/mood-boards", (Guid jobId, UpsertMoodBoardRequest req, IMoodBoardService svc, CancellationToken ct) => svc.CreateBoardAsync(jobId, req, ct));
        api.MapPost("/mood-boards/{boardId:guid}/canvases", (Guid boardId, UpsertCanvasRequest req, IMoodBoardService svc, CancellationToken ct) => svc.AddCanvasAsync(boardId, req, ct));
        api.MapPut("/mood-board-canvases/{canvasId:guid}", (Guid canvasId, UpsertCanvasRequest req, IMoodBoardService svc, CancellationToken ct) => svc.UpdateCanvasMetaAsync(canvasId, req, ct));
        api.MapPut("/mood-board-canvases/{canvasId:guid}/elements", (Guid canvasId, SaveCanvasElementsRequest req, IMoodBoardService svc, CancellationToken ct) => svc.SaveCanvasElementsAsync(canvasId, req, ct));
        api.MapPost("/mood-board-canvases/{canvasId:guid}/reapply-branding", (Guid canvasId, [FromQuery] string token, IMoodBoardService svc, CancellationToken ct) => svc.ReapplyCurrentBrandingAsync(canvasId, token, ct));
        api.MapDelete("/mood-board-canvases/{canvasId:guid}", async (Guid canvasId, [FromQuery] string token, IMoodBoardService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteCanvasAsync(canvasId, token, ct);
            return Results.NoContent();
        });
        api.MapPost("/mood-boards/{boardId:guid}/pdf", (Guid boardId, Guid? canvasId, IMoodBoardService svc, CancellationToken ct) => svc.SaveAsPdfAsync(boardId, canvasId, ct));

        // --- Quotes, Invoices, Procurement, Receipts & Artifacts ---
        api.MapGet("/jobs/{jobId:guid}/documents", (Guid jobId, string? documentType, IFinancialDocumentService svc, CancellationToken ct) =>
            svc.ListForJobAsync(jobId, documentType, ct));
        api.MapGet("/documents/{id:guid}", (Guid id, IFinancialDocumentService svc, CancellationToken ct) => svc.GetAsync(id, ct));
        api.MapPost("/documents", (CreateFinancialDocumentRequest req, IFinancialDocumentService svc, CancellationToken ct) => svc.CreateAsync(req, ct));
        api.MapPut("/documents/{id:guid}", (Guid id, UpdateFinancialDocumentRequest req, IFinancialDocumentService svc, CancellationToken ct) => svc.UpdateAsync(id, req, ct));
        api.MapPost("/documents/{quoteId:guid}/convert-to-invoice", (Guid quoteId, IFinancialDocumentService svc, CancellationToken ct) => svc.ConvertQuoteToInvoiceAsync(quoteId, ct));
        api.MapPost("/documents/{id:guid}/docx", (Guid id, bool isDraft, IFinancialDocumentService svc, CancellationToken ct) => svc.GenerateDocxAsync(id, isDraft, ct));
        api.MapPost("/documents/{id:guid}/pdf", (Guid id, bool isDraft, IFinancialDocumentService svc, CancellationToken ct) => svc.SaveAsPdfAsync(id, isDraft, ct));
        api.MapPost("/documents/{id:guid}/issue", (Guid id, [FromQuery] string token, Guid? leaseId, long? fencingVersion, IFinancialDocumentService svc, CancellationToken ct) =>
            svc.IssueAsync(id, token, leaseId, fencingVersion, ct));

        api.MapGet("/jobs/{jobId:guid}/artifacts", (Guid jobId, string? sourceType, Guid? sourceId, IFinancialDocumentService svc, CancellationToken ct) =>
            svc.ListArtifactsAsync(jobId, sourceType, sourceId, ct));
        api.MapGet("/artifacts/{artifactId:guid}/download", async (Guid artifactId, IFinancialDocumentService svc, CancellationToken ct) =>
        {
            var (bytes, contentType, fileName) = await svc.DownloadArtifactAsync(artifactId, ct);
            return Results.File(bytes, contentType, fileName);
        });

        api.MapGet("/jobs/{jobId:guid}/procurement", (Guid jobId, IFinancialDocumentService svc, CancellationToken ct) => svc.ListProcurementAsync(jobId, ct));
        api.MapPost("/jobs/{jobId:guid}/procurement", (Guid jobId, UpsertProcurementItemRequest req, IFinancialDocumentService svc, CancellationToken ct) => svc.CreateProcurementAsync(jobId, req, ct));
        api.MapPut("/procurement/{id:guid}", (Guid id, UpsertProcurementItemRequest req, IFinancialDocumentService svc, CancellationToken ct) => svc.UpdateProcurementAsync(id, req, ct));
        api.MapPost("/procurement/{id:guid}/payments", (Guid id, AddProcurementPaymentRequest req, IFinancialDocumentService svc, CancellationToken ct) => svc.AddProcurementPaymentAsync(id, req, ct));

        api.MapGet("/jobs/{jobId:guid}/receipts", (Guid jobId, IFinancialDocumentService svc, CancellationToken ct) => svc.ListReceiptsAsync(jobId, ct));
        api.MapPost("/jobs/{jobId:guid}/receipts", async (Guid jobId, HttpRequest req, IFinancialDocumentService svc, CancellationToken ct) =>
        {
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new ValidationServiceException("Receipt image is required.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            var merchant = form["merchantName"].FirstOrDefault() ?? string.Empty;
            var date = DateOnly.TryParse(form["receiptDate"].FirstOrDefault(), out var rd) ? rd : DateOnly.FromDateTime(DateTime.UtcNow);
            var currency = form["currency"].FirstOrDefault() ?? "AUD";
            var amountCents = long.TryParse(form["amountCents"].FirstOrDefault(), out var ac) ? ac : 0L;
            var notes = form["notes"].FirstOrDefault();
            Guid? lineItemId = Guid.TryParse(form["jobLineItemId"].FirstOrDefault(), out var lid) ? lid : null;
            Guid? procId = Guid.TryParse(form["procurementItemId"].FirstOrDefault(), out var pid) ? pid : null;

            return await svc.UploadReceiptAsync(jobId, lineItemId, procId, merchant, date, currency, amountCents, notes, ms.ToArray(), file.ContentType, ct);
        }).DisableAntiforgery();

        // --- Forecasting & Integrations ---
        api.MapPost("/forecast", (ForecastRequest req, IForecastingService svc, CancellationToken ct) => svc.CalculateForecastAsync(req, ct));
        api.MapGet("/integrations/status", (IIntegrationService svc, CancellationToken ct) => svc.GetStatusAsync(ct));
        api.MapPost("/integrations/supplier-capture", (SupplierCaptureRequest req, IIntegrationService svc, CancellationToken ct) => svc.ExtractSupplierPageDraftAsync(req, ct));
        api.MapGet("/integrations/drafts", (Guid? jobId, IIntegrationService svc, CancellationToken ct) => svc.ListExtractionDraftsAsync(jobId, ct));

        // --- Administration ---
        api.MapGet("/admin/users", (IAdminService svc, int page = 1, int pageSize = 25, string? search = null, bool includeDeleted = false, CancellationToken ct = default) =>
            svc.ListUsersAsync(new PagedRequest { Page = page, PageSize = pageSize, Search = search, IncludeDeleted = includeDeleted }, ct));
        api.MapPost("/admin/users", (CreateUserRequest req, IAdminService svc, CancellationToken ct) => svc.CreateUserAsync(req, ct));
        api.MapPut("/admin/users/{id:guid}", (Guid id, UpdateUserRequest req, IAdminService svc, CancellationToken ct) => svc.UpdateUserAsync(id, req, ct));
        api.MapDelete("/admin/users/{id:guid}", async (Guid id, [FromQuery] string token, IAdminService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteUserAsync(id, token, ct);
            return Results.NoContent();
        });
        api.MapPost("/admin/users/{id:guid}/restore", async (Guid id, [FromQuery] string token, IAdminService svc, CancellationToken ct) =>
        {
            await svc.RestoreUserAsync(id, token, ct);
            return Results.NoContent();
        });

        api.MapGet("/admin/roles", (IAdminService svc, CancellationToken ct) => svc.ListRolesAsync(ct));
        api.MapPost("/admin/roles", (UpsertRoleRequest req, IAdminService svc, CancellationToken ct) => svc.CreateRoleAsync(req, ct));
        api.MapPut("/admin/roles/{id:guid}", (Guid id, UpsertRoleRequest req, IAdminService svc, CancellationToken ct) => svc.UpdateRoleAsync(id, req, ct));
        api.MapDelete("/admin/roles/{id:guid}", async (Guid id, [FromQuery] string token, IAdminService svc, CancellationToken ct) =>
        {
            await svc.SoftDeleteRoleAsync(id, token, ct);
            return Results.NoContent();
        });

        api.MapGet("/admin/variables", (IAdminService svc, CancellationToken ct) => svc.ListVariablesAsync(ct));
        api.MapPut("/admin/variables", (UpsertApplicationVariableRequest req, IAdminService svc, CancellationToken ct) => svc.UpsertVariableAsync(req, ct));

        api.MapGet("/admin/error-codes", (IAdminService svc, CancellationToken ct) => svc.ListErrorCodesAsync(ct));
        api.MapGet("/admin/error-occurrences", (IAdminService svc, int page = 1, int pageSize = 25, string? search = null, CancellationToken ct = default) =>
            svc.ListErrorOccurrencesAsync(new PagedRequest { Page = page, PageSize = pageSize, Search = search }, ct));
        api.MapGet("/admin/audit", (IAdminService svc, string? entityType = null, string? entityId = null, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
            svc.ListAuditEntriesAsync(entityType, entityId, new PagedRequest { Page = page, PageSize = pageSize }, ct));

        api.MapGet("/admin/cache/stats", (IPermissionEvaluator perms, IAppCacheService cache) =>
        {
            perms.Require(ModuleKeys.ApplicationVariables, AccessLevel.Read);
            return Results.Ok(cache.GetStatistics());
        });
        api.MapPost("/admin/cache/clear", (IPermissionEvaluator perms, IAppCacheService cache) =>
        {
            perms.Require(ModuleKeys.ApplicationVariables, AccessLevel.Alter);
            cache.ClearAll();
            return Results.Ok(cache.GetStatistics());
        });

        return app;
    }
}
