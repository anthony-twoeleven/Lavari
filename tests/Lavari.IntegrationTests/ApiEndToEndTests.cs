using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lavari.Contracts.Admin;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Contracts.Security;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lavari.IntegrationTests;

public sealed class ApiEndToEndTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly string _tempRoot;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiEndToEndTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"lavari-api-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Lavari:DatabasePath"] = Path.Combine(_tempRoot, "test.db"),
                        ["Lavari:StorageRoot"] = Path.Combine(_tempRoot, "storage"),
                        ["Lavari:DataProtectionKeysPath"] = Path.Combine(_tempRoot, "keys"),
                        ["Lavari:SeedDevelopmentSuperAdmin"] = "true",
                        ["Lavari:DisableTickerQ"] = "true"
                    });
                });
            });
    }

    [Fact]
    public async Task Appearance_IsPrivateAuthorizedAuditedAndConcurrencyChecked()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        AssertIsolatedDatabase();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/account/appearance")).StatusCode);

        using var admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await SignInAsync(admin, DatabaseSeeder.DefaultDevSuperAdminEmail, DatabaseSeeder.DefaultDevSuperAdminPassword);

        var roleResponse = await admin.PostAsJsonAsync("/api/admin/roles", new UpsertRoleRequest
        {
            Name = $"Appearance reader {Guid.NewGuid():N}", Description = "May only view appearance", IsFullAccess = false,
            ModulePermissions = new Dictionary<string, int> { [ModuleKeys.AccountAppearance] = (int)AccessLevel.Read }
        });
        roleResponse.EnsureSuccessStatusCode();
        var role = (await roleResponse.Content.ReadFromJsonAsync<RoleDto>(JsonOpts))!;
        var password = $"Test-{Guid.NewGuid():N}!";
        var email = $"reader-{Guid.NewGuid():N}@example.test";
        var created = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest
        {
            Email = email, DisplayName = "Appearance reader", Password = password, RoleIds = [role.Id]
        });
        created.EnsureSuccessStatusCode();

        using var reader = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await SignInAsync(reader, email, password);
        var initial = (await reader.GetFromJsonAsync<AppearanceDto>("/api/account/appearance", JsonOpts))!;
        Assert.Equal("system", initial.Mode);
        Assert.Equal("lavender", initial.Color);
        var readerPut = await reader.PutAsJsonAsync("/api/account/appearance",
            new UpdateAppearanceRequest { Mode = "dark", Color = "rose", ConcurrencyToken = initial.ConcurrencyToken });
        Assert.Equal(HttpStatusCode.Forbidden, readerPut.StatusCode);

        var noGrantEmail = $"no-grant-{Guid.NewGuid():N}@example.test";
        var noGrant = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest
        {
            Email = noGrantEmail, DisplayName = "No appearance grant", Password = password, RoleIds = []
        });
        noGrant.EnsureSuccessStatusCode();
        using var ungranted = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await SignInAsync(ungranted, noGrantEmail, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await ungranted.GetAsync("/api/account/appearance")).StatusCode);

        var adminInitial = (await admin.GetFromJsonAsync<AppearanceDto>("/api/account/appearance", JsonOpts))!;
        var invalid = await admin.PutAsJsonAsync("/api/account/appearance",
            new UpdateAppearanceRequest { Mode = "invalid", Color = "rose", ConcurrencyToken = adminInitial.ConcurrencyToken });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var changed = await admin.PutAsJsonAsync("/api/account/appearance",
            new UpdateAppearanceRequest { Mode = "dark", Color = "blue", ConcurrencyToken = adminInitial.ConcurrencyToken });
        changed.EnsureSuccessStatusCode();
        var saved = (await changed.Content.ReadFromJsonAsync<AppearanceDto>(JsonOpts))!;
        Assert.Equal("dark", saved.Mode);
        Assert.Equal("blue", saved.Color);
        Assert.NotEqual(adminInitial.ConcurrencyToken, saved.ConcurrencyToken);
        Assert.Equal(saved, await admin.GetFromJsonAsync<AppearanceDto>("/api/account/appearance", JsonOpts));
        Assert.Equal(initial, await reader.GetFromJsonAsync<AppearanceDto>("/api/account/appearance", JsonOpts));
        var stale = await admin.PutAsJsonAsync("/api/account/appearance",
            new UpdateAppearanceRequest { Mode = "light", Color = "green", ConcurrencyToken = adminInitial.ConcurrencyToken });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var audit = await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/admin/audit?page=1&pageSize=50", JsonOpts);
        Assert.Contains(audit!.Items, row => row.EntityType == "UserEntity" &&
            row.BeforeJson?.Contains("\"AppearanceMode\":\"system\"", StringComparison.Ordinal) == true &&
            row.AfterJson?.Contains("\"AppearanceMode\":\"dark\"", StringComparison.Ordinal) == true);
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("csrfToken").GetString());
    }

    [Fact]
    public async Task Api_EnforcesAuthCsrfLocksAndFullHttpBusinessFlow()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        AssertIsolatedDatabase();

        // 1. Anonymous request to business endpoint returns 401 with coded ApiErrorResponse
        var anonResp = await client.GetAsync("/api/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);
        var anonErr = await anonResp.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOpts);
        Assert.NotNull(anonErr);
        Assert.Equal(ErrorCodes.Unauthenticated, anonErr.Code);
        Assert.False(string.IsNullOrWhiteSpace(anonErr.CorrelationId));

        // 2. Login as seeded SuperAdmin
        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = DatabaseSeeder.DefaultDevSuperAdminEmail,
            Password = DatabaseSeeder.DefaultDevSuperAdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        using var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
        var csrfToken = loginDoc.RootElement.GetProperty("csrfToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));

        // 3. State-changing request WITHOUT CSRF token is rejected with 403
        var noCsrfResp = await client.PostAsJsonAsync("/api/clients", new UpsertClientRequest { Name = "Blocked No CSRF" });
        Assert.Equal(HttpStatusCode.Forbidden, noCsrfResp.StatusCode);

        // Attach CSRF header for all subsequent requests
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrfToken);

        // 4. Create Client -> JobGroup -> Job
        var createdClient = await (await client.PostAsJsonAsync("/api/clients", new UpsertClientRequest { Name = "Sydney Harbour Developments" }))
            .Content.ReadFromJsonAsync<ClientDto>(JsonOpts);
        Assert.NotNull(createdClient);

        var createdGroup = await (await client.PostAsJsonAsync("/api/job-groups", new UpsertJobGroupRequest { ClientId = createdClient.Id, Name = "Waterfront Residences" }))
            .Content.ReadFromJsonAsync<JobGroupDto>(JsonOpts);
        Assert.NotNull(createdGroup);

        var createdJob = await (await client.PostAsJsonAsync("/api/jobs", new UpsertJobRequest
        {
            JobGroupId = createdGroup.Id,
            Name = "Balmain Terrace",
            Status = "Active",
            TimeZoneIana = "Australia/Sydney",
            DefaultCurrency = "AUD"
        })).Content.ReadFromJsonAsync<JobDto>(JsonOpts);
        Assert.NotNull(createdJob);

        // 5. Updating job WITHOUT Modify lock is rejected with 409 EditLockRequired
        var lockedPut = await client.PutAsJsonAsync($"/api/jobs/{createdJob.Id}", new UpsertJobRequest
        {
            JobGroupId = createdGroup.Id,
            Name = "Balmain Terrace Updated",
            Status = "Active",
            TimeZoneIana = "Australia/Sydney",
            DefaultCurrency = "AUD",
            ConcurrencyToken = createdJob.ConcurrencyToken
        });
        Assert.Equal(HttpStatusCode.Conflict, lockedPut.StatusCode);
        var lockErr = await lockedPut.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOpts);
        Assert.Equal(ErrorCodes.EditLockRequired, lockErr!.Code);

        // 6. Acquire Modify lock -> update succeeds -> release lock
        var lockStatus = await (await client.PostAsJsonAsync("/api/locks/modify", new EditLockActionRequest
        {
            RecordType = LockableRecordType.Job,
            RecordId = createdJob.Id
        })).Content.ReadFromJsonAsync<EditLockStatusDto>(JsonOpts);
        Assert.True(lockStatus!.IsHeldByCurrentSession);

        var okPut = await client.PutAsJsonAsync($"/api/jobs/{createdJob.Id}", new UpsertJobRequest
        {
            JobGroupId = createdGroup.Id,
            Name = "Balmain Terrace Renovated",
            Status = "Active",
            TimeZoneIana = "Australia/Sydney",
            DefaultCurrency = "AUD",
            ConcurrencyToken = createdJob.ConcurrencyToken,
            LeaseId = lockStatus.LeaseId,
            FencingVersion = lockStatus.FencingVersion
        });
        Assert.Equal(HttpStatusCode.OK, okPut.StatusCode);

        var currentLock = await client.GetFromJsonAsync<EditLockStatusDto>($"/api/locks/Job/{createdJob.Id}", JsonOpts);
        Assert.Equal(lockStatus.LeaseId, currentLock!.LeaseId);
        var line = await client.PostAsJsonAsync($"/api/jobs/{createdJob.Id}/line-items", new UpsertJobLineItemRequest
        {
            Title = "Side table", Currency = "AUD", LeaseId = currentLock.LeaseId, FencingVersion = currentLock.FencingVersion
        });
        Assert.Equal(HttpStatusCode.OK, line.StatusCode);

        var timerResponse = await client.PostAsJsonAsync("/api/time/start", new StartTimerRequest { JobId = createdJob.Id });
        timerResponse.EnsureSuccessStatusCode();
        var timer = (await timerResponse.Content.ReadFromJsonAsync<StartTimerResultDto>(JsonOpts))!.StartedEntry!;
        var stop = await client.PostAsJsonAsync($"/api/time/{timer.Id}/stop?token={timer.ConcurrencyToken}",
            new StopTimerRequest { Description = "Measured the living room" });
        stop.EnsureSuccessStatusCode();
        var stopped = (await stop.Content.ReadFromJsonAsync<TimeEntryDto>(JsonOpts))!;
        Assert.Equal("Measured the living room", stopped.Description);
        var edit = await client.PutAsJsonAsync($"/api/time/{timer.Id}", new UpdateTimeEntryRequest
        {
            ConcurrencyToken = stopped.ConcurrencyToken,
            Description = "Measured the living room and entry",
            DurationSeconds = 1200
        });
        edit.EnsureSuccessStatusCode();
        var edited = (await edit.Content.ReadFromJsonAsync<TimeEntryDto>(JsonOpts))!;
        Assert.Equal(1200, edited.LiveDurationSeconds);
        Assert.Equal(stopped.Intervals[0], edited.Intervals[0]);

        var audit = await client.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/admin/audit?page=1&pageSize=50", JsonOpts);
        Assert.Contains(audit!.Items, row => row.EntityId == timer.Id.ToString("D") &&
            row.BeforeJson?.Contains("Measured the living room", StringComparison.Ordinal) == true &&
            row.AfterJson?.Contains("Measured the living room and entry", StringComparison.Ordinal) == true);

        // 7. Create Quote -> Generate DOCX & PDF -> Download PDF over HTTP
        var quote = await (await client.PostAsJsonAsync("/api/documents", new CreateFinancialDocumentRequest
        {
            JobId = createdJob.Id,
            DocumentType = FinancialDocumentType.Quote,
            Lines = [new SaveFinancialDocumentLineInput { Title = "Custom Oak Table", QuantityMilli = 1000, UnitPriceCents = 320_000 }]
        })).Content.ReadFromJsonAsync<FinancialDocumentDto>(JsonOpts);
        Assert.NotNull(quote);

        var pdfArt = await (await client.PostAsync($"/api/documents/{quote.Id}/pdf?isDraft=true", null))
            .Content.ReadFromJsonAsync<GeneratedArtifactDto>(JsonOpts);
        Assert.NotNull(pdfArt);

        var pdfResp = await client.GetAsync($"/api/artifacts/{pdfArt.Id}/download");
        Assert.Equal(HttpStatusCode.OK, pdfResp.StatusCode);
        var pdfBytes = await pdfResp.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdfBytes, 0, 5));

        // 8. Check Health endpoint works cleanly
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }

        catch { /* SQLite file handle cleanup tolerance on Windows */ }
    }

    private void AssertIsolatedDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LavariDbContext>();
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempRoot, "test.db")), Path.GetFullPath(db.Database.GetDbConnection().DataSource));
    }
}
