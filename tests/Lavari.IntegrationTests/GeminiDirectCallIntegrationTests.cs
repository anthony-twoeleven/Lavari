using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Contracts.Admin;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Contracts.Security;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Integrations;
using Lavari.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Lavari.IntegrationTests;

public sealed class GeminiDirectCallIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"lavari-gemini-direct-{Guid.NewGuid():N}");
    private readonly TestTextProvider _provider = new();
    private readonly TestSupplierPageFetcher _pageFetcher = new();
    private readonly WebApplicationFactory<Program> _factory;

    public GeminiDirectCallIntegrationTests()
    {
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
                        ["Lavari:DisableTickerQ"] = "true",
                        ["Lavari:Integrations:Gemini:Enabled"] = "true",
                        ["Lavari:Integrations:Gemini:ApiKey"] = "test-key",
                        ["Lavari:Integrations:Gemini:Model"] = "gemini-test"
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IGenerativeTextProvider>();
                    services.AddSingleton<IGenerativeTextProvider>(_provider);
                    services.RemoveAll<ISupplierPageFetcher>();
                    services.AddSingleton<ISupplierPageFetcher>(_pageFetcher);
                });
            });
    }

    [Fact]
    public async Task Capture_AwaitsGeminiAndReturnsAuditedReviewOnlyDraft()
    {
        using var admin = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        await SignInAsync(admin);
        await SetBudgetAsync(admin, "Ai.SpendCurrency", ApplicationVariableValueType.String, "AUD");
        await SetBudgetAsync(admin, "Ai.DailyLimitCents", ApplicationVariableValueType.Int, "100");
        await SetBudgetAsync(admin, "Ai.UserDailyLimitCents", ApplicationVariableValueType.Int, "50");
        await SetBudgetAsync(admin, "Ai.UserDailyRequests", ApplicationVariableValueType.Int, "5");
        await SetBudgetAsync(admin, "Ai.ReservationCents", ApplicationVariableValueType.Int, "10");

        var ready = await admin.GetFromJsonAsync<IntegrationStatusDto>("/api/integrations/status", JsonOptions);
        Assert.True(ready!.GeminiExtractionReady);

        const string imageUrl = "https://images.example.test/magnus.jpg?width=600";
        var response = await admin.PostAsJsonAsync("/api/integrations/supplier-capture", new SupplierCaptureRequest
        {
            SourceUrl = "https://supplier.example/magnus",
            PageTextContent = string.Empty,
            Prompt = "Extract the price, image URL, concise description and selected finish.",
            UseGemini = true,
            UserConfirmedConsent = true,
            UserConfirmedProviderConsent = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<LlmExtractionDraftDto>(JsonOptions))!;
        Assert.Equal("PendingReview", result.Status);
        Assert.Equal("Gemini", result.ProviderName);
        Assert.Equal(1, _pageFetcher.FetchCount);
        Assert.Equal("https://supplier.example/magnus", _pageFetcher.LastUri?.AbsoluteUri);
        Assert.NotNull(result.ProviderConsentGrantedAtUtc);
        Assert.NotNull(_provider.Request);
        using (var extracted = JsonDocument.Parse(result.ExtractedJson))
        {
            Assert.Equal("Magnus Console Table", extracted.RootElement.GetProperty("name").GetString());
            Assert.Equal("MCT-001", extracted.RootElement.GetProperty("sku").GetString());
            Assert.Equal("selectedfinish", extracted.RootElement.GetProperty("customFields")[0].GetProperty("key").GetString());
            Assert.Equal(imageUrl, extracted.RootElement.GetProperty("imageUrls")[0].GetString());
            Assert.True(extracted.RootElement.GetProperty("requiresUserReview").GetBoolean());
        }

        Assert.Contains("Private supplier detail", _provider.Request!.Input);
        Assert.Contains(imageUrl, _provider.Request.Input);
        await using var auditScope = _factory.Services.CreateAsyncScope();
        var db = auditScope.ServiceProvider.GetRequiredService<LavariDbContext>();
        var reservation = await db.AiSpendReservations.SingleAsync();
        Assert.Equal("Completed", reservation.State);
        Assert.Equal(120, reservation.InputTokens);
        Assert.Equal(35, reservation.OutputTokens);
        Assert.Equal(155, reservation.TotalTokens);
        var audits = await db.AuditEntries.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(audits, audit =>
            audit.AfterJson?.Contains("Private supplier detail", StringComparison.Ordinal) == true ||
            audit.AfterJson?.Contains("Extract the price", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Capture_ProviderFailureMarksDraftFailedAndKeepsStartedReservation()
    {
        _provider.ThrowOnCall = true;
        using var admin = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        await SignInAsync(admin);
        await SetBudgetAsync(admin, "Ai.SpendCurrency", ApplicationVariableValueType.String, "AUD");
        await SetBudgetAsync(admin, "Ai.DailyLimitCents", ApplicationVariableValueType.Int, "100");
        await SetBudgetAsync(admin, "Ai.UserDailyLimitCents", ApplicationVariableValueType.Int, "50");
        await SetBudgetAsync(admin, "Ai.UserDailyRequests", ApplicationVariableValueType.Int, "5");
        await SetBudgetAsync(admin, "Ai.ReservationCents", ApplicationVariableValueType.Int, "10");

        var response = await admin.PostAsJsonAsync("/api/integrations/supplier-capture", new SupplierCaptureRequest
        {
            SourceUrl = "https://supplier.example/item",
            PageTextContent = "Sample table details",
            Prompt = "Extract product information",
            UseGemini = true,
            UserConfirmedConsent = true,
            UserConfirmedProviderConsent = true
        });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("provider-private-failure", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, _pageFetcher.FetchCount);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LavariDbContext>();
        Assert.Equal("Failed", (await db.LlmExtractionDrafts.SingleAsync()).Status);
        Assert.Equal("Consumed", (await db.AiSpendReservations.SingleAsync()).State);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = DatabaseSeeder.DefaultDevSuperAdminEmail,
            Password = DatabaseSeeder.DefaultDevSuperAdminPassword
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("csrfToken").GetString());
    }

    private static async Task SetBudgetAsync(
        HttpClient client,
        string key,
        ApplicationVariableValueType valueType,
        string value)
    {
        var response = await client.PutAsJsonAsync("/api/admin/variables", new UpsertApplicationVariableRequest
        {
            Key = key,
            ValueType = valueType,
            Value = value,
            Description = "Test-only supplier extraction budget."
        });
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _factory.Dispose();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_tempRoot, "test.db"),
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_tempRoot, recursive: true);
    }

    private sealed class TestTextProvider : IGenerativeTextProvider
    {
        public GenerativeTextRequest? Request { get; private set; }
        public bool ThrowOnCall { get; set; }

        public Task<GenerativeTextResponse> GenerateAsync(
            GenerativeTextRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            if (ThrowOnCall)
                return Task.FromException<GenerativeTextResponse>(
                    new ExternalIntegrationServiceException("provider-private-failure"));

            return Task.FromResult(new GenerativeTextResponse
            {
                Text = JsonSerializer.Serialize(new
                {
                    name = "Magnus Console Table",
                    description = "Console table with porcelain top.",
                    candidateRrpCents = 227500,
                    currency = "AUD",
                    sku = "MCT-001",
                    attributes = new[] { new { key = "top", label = "Top", value = "Porcelain", valueType = "text", unit = (string?)null } },
                    identifiers = Array.Empty<object>(),
                    customFields = new[] { new { key = "selectedfinish", label = "Selected finish", value = "Porcelain", valueType = "text", unit = (string?)null } },
                    imageUrls = new[] { "https://images.example.test/magnus.jpg?width=600" }
                }),
                InputTokens = 120,
                OutputTokens = 35,
                TotalTokens = 155
            });
        }

    }

    private sealed class TestSupplierPageFetcher : ISupplierPageFetcher
    {
        public int FetchCount { get; private set; }
        public Uri? LastUri { get; private set; }

        public Task<string> FetchTextAsync(Uri sourceUri, CancellationToken ct = default)
        {
            FetchCount++;
            LastUri = sourceUri;
            return Task.FromResult(
                "Magnus Console Table\nRRP AUD $2,275.00\nPorcelain top\n" +
                "https://images.example.test/magnus.jpg?width=600\nPrivate supplier detail");
        }
    }
}
