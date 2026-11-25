using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Contracts.Security;
using Lavari.Domain.Errors;
using Lavari.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
    public async Task Api_EnforcesAuthCsrfLocksAndFullHttpBusinessFlow()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

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
}
