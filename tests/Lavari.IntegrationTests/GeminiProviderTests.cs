using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Lavari.Application.Abstractions;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure;
using Lavari.Infrastructure.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lavari.IntegrationTests;

public sealed class GeminiProviderTests
{
    [Theory]
    [InlineData(false, "key")]
    [InlineData(true, null)]
    [InlineData(true, "bad\nkey")]
    [InlineData(true, "bad\0key")]
    [InlineData(true, "bad\tkey")]
    public void Registration_FailsExplicitlyWhenDisabledOrMissingValidCredentials(bool enabled, string? key)
    {
        using var services = CreateServices(enabled, key);
        Assert.Throws<ExternalIntegrationServiceException>(() => services.GetRequiredService<IGenerativeTextProvider>());
        Assert.Throws<ExternalIntegrationServiceException>(() => services.GetRequiredService<IGenerativeImageProvider>());
    }

    [Fact]
    public void Registration_ProvidesBothCapabilitiesWhenConfigured()
    {
        using var services = CreateServices(true, "test-key");
        Assert.IsType<GeminiTextProvider>(services.GetRequiredService<IGenerativeTextProvider>());
        Assert.IsType<GeminiImageProvider>(services.GetRequiredService<IGenerativeImageProvider>());
    }

    [Fact]
    public void Registration_AllowsReplacingOnlyTheTextProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var replacement = new FakeProvider("replacement");
        var collection = new ServiceCollection();
        collection.AddSingleton<IGenerativeTextProvider>(replacement);
        collection.AddLavariInfrastructure(configuration, enableTickerQ: false);
        using var services = collection.BuildServiceProvider();

        Assert.Same(replacement, services.GetRequiredService<IGenerativeTextProvider>());
        Assert.Throws<ExternalIntegrationServiceException>(() => services.GetRequiredService<IGenerativeImageProvider>());
    }

    [Fact]
    public async Task DisabledScheduler_DoesNotClaimToScheduleWork()
    {
        using var services = CreateServices(false, null);
        using var scope = services.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>();
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() =>
            scheduler.EnqueueImmediateAsync("example", new { value = 1 }));
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() =>
            scheduler.ScheduleOneTimeAsync("example", new { value = 1 }, DateTimeOffset.UtcNow));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.EnqueueImmediateAsync("example", new { value = 1 }, ct: cancellation.Token));
    }

    [Fact]
    public async Task Provider_SendsStructuredRequestAndReadsUsage()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri!.Host);
            Assert.Equal("/v1beta/models/gemini-test:generateContent", request.RequestUri.AbsolutePath);
            Assert.Equal("test-key", request.Headers.GetValues("x-goog-api-key").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("APPLICATION_JSON", body.RootElement.GetProperty("generationConfig")
                .GetProperty("responseFormat").GetProperty("text").GetProperty("mimeType").GetString());
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            return JsonResponse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"name\":\"Chair\"}"}]}}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":5,"thoughtsTokenCount":3,"totalTokenCount":20}}""");
        });
        using var client = new GeminiApiClient("test-key", handler);
        var provider = new GeminiTextProvider(client);
        var result = await provider.GenerateAsync(Request());
        Assert.Equal("""{"name":"Chair"}""", result.Text);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);
        Assert.Equal(3, result.ReasoningTokens);
        Assert.Equal(20, result.TotalTokens);
    }

    [Fact]
    public async Task Provider_SupportsTextWithoutForcingStructuredOutput()
    {
        using var client = new GeminiApiClient("test-key", new StubHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("generationConfig").TryGetProperty("responseFormat", out _));
            return JsonResponse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"Answer"}]}}]}""");
        }));
        var result = await new GeminiTextProvider(client).GenerateAsync(Request() with { ResponseSchema = null });
        Assert.Equal("Answer", result.Text);
    }

    [Theory]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY","content":{"parts":[{"text":"{}"}]}}]}""")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{}"},{"text":"{}"}]}}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{}"}]}}],"usageMetadata":{"totalTokenCount":-1}}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{}"}]}}],"usageMetadata":"invalid"}""")]
    [InlineData("not json")]
    public async Task Provider_RejectsBlockedOrInvalidResponses(string body)
    {
        using var client = new GeminiApiClient("test-key", new StubHandler(_ => Task.FromResult(JsonResponse(body))));
        var provider = new GeminiTextProvider(client);
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() => provider.GenerateAsync(Request()));
    }

    [Fact]
    public async Task Provider_RejectsUnsafeModelAndOversizedResponse()
    {
        using var client = new GeminiApiClient("test-key", new StubHandler(_ =>
            Task.FromResult(JsonResponse(new string('x', 131073)))));
        var provider = new GeminiTextProvider(client);
        await Assert.ThrowsAsync<ValidationServiceException>(() => provider.GenerateAsync(Request() with { Model = "../other" }));
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() => provider.GenerateAsync(Request()));
    }

    [Theory]
    [InlineData("gemini-test\n")]
    [InlineData("gemini-test\r\n")]
    public async Task Provider_RejectsModelIdentifiersWithTrailingNewlines(string model)
    {
        var calls = 0;
        using var client = new GeminiApiClient("test-key", new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(JsonResponse("{}"));
        }));
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            new GeminiTextProvider(client).GenerateAsync(Request() with { Model = model }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Provider_DoesNotExposeProviderErrorOrRetry()
    {
        var calls = 0;
        using var client = new GeminiApiClient("test-key", new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("sensitive provider details")
            });
        }));
        var provider = new GeminiTextProvider(client);
        var error = await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() => provider.GenerateAsync(Request()));
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Provider_PreservesCallerCancellation()
    {
        using var client = new GeminiApiClient("test-key", new StubHandler(_ => throw new OperationCanceledException()));
        var provider = new GeminiTextProvider(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Request(), cancellation.Token));
    }

    [Fact]
    public async Task Provider_TimesOutWhileReadingResponseBody()
    {
        using var client = new GeminiApiClient("test-key", new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledStream())
            })), TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<ExternalIntegrationServiceException>(
            () => new GeminiTextProvider(client).GenerateAsync(Request()));
        Assert.DoesNotContain("test-key", error.ToString());
    }

    [Fact]
    public async Task Provider_PreservesCallerCancellationWhileReadingResponseBody()
    {
        var stream = new StalledStream();
        using var client = new GeminiApiClient("test-key", new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        using var cancellation = new CancellationTokenSource();
        var request = new GeminiTextProvider(client).GenerateAsync(Request(), cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Extractor_ReturnsReviewedSuggestionsWithoutGuessingCurrency()
    {
        var fake = new FakeProvider("""{"name":"Chair","description":null,"candidateRrpCents":12500,"currency":"AUD","attributes":[{"label":"Finish","value":"Black"}],"identifiers":[{"label":"SKU","value":"A-12"}],"imageUrls":["https://images.example.test/chair.png"]}""");
        var result = await new SupplierPageTextExtractor(fake).ExtractAsync("model", "Chair $125 AUD https://images.example.test/chair.png", "Find finish");
        Assert.Equal(12500, result.Suggestion.CandidateRrpCents);
        Assert.Equal("Black", result.Suggestion.Attributes[0].Value);
        Assert.Equal("A-12", result.Suggestion.Identifiers[0].Value);
        Assert.Equal("https://images.example.test/chair.png", result.Suggestion.ImageUrls[0]);
        Assert.Contains("untrusted", fake.Request!.SystemInstruction);
        Assert.Contains("Chair $125 AUD", fake.Request.Input);
    }

    [Fact]
    public async Task Extractor_AllowsInertImageUrlWithTransformationQuery()
    {
        const string imageUrl = "https://images.example.test/chair.png?width=600";
        var response = JsonSerializer.Serialize(new
        {
            name = "Chair", description = (string?)null, candidateRrpCents = (long?)null, currency = (string?)null,
            attributes = Array.Empty<object>(), identifiers = Array.Empty<object>(), imageUrls = new[] { imageUrl }
        });
        var result = await new SupplierPageTextExtractor(new FakeProvider(response))
            .ExtractAsync("model", $"Chair {imageUrl}", "Extract the product details");
        Assert.Equal(imageUrl, Assert.Single(result.Suggestion.ImageUrls));
    }

    [Theory]
    [InlineData("""{"name":"Chair","description":null,"candidateRrpCents":12500,"currency":null,"attributes":[],"identifiers":[],"imageUrls":[]}""")]
    [InlineData("""{"name":"Chair","description":null,"candidateRrpCents":1,"currency":"JPY","attributes":[],"identifiers":[],"imageUrls":[]}""")]
    [InlineData("""{"name":"Chair","name":"Other","description":null,"candidateRrpCents":null,"currency":null,"attributes":[],"identifiers":[],"imageUrls":[]}""")]
    [InlineData("""{"name":"Chair","description":null,"candidateRrpCents":9007199254740992,"currency":"AUD","attributes":[],"identifiers":[],"imageUrls":[]}""")]
    [InlineData("""{"name":"Chair","description":null,"candidateRrpCents":null,"currency":null,"attributes":[],"identifiers":[],"imageUrls":[],"extra":"x"}""")]
    [InlineData("""{"name":"Chair","description":null,"candidateRrpCents":null,"currency":null,"attributes":[],"identifiers":[{"label":"","value":"A-12"}],"imageUrls":[]}""")]
    public async Task Extractor_RejectsUntrustedInvalidOutput(string output)
    {
        var extractor = new SupplierPageTextExtractor(new FakeProvider(output));
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() => extractor.ExtractAsync("model", "page", "prompt"));
    }

    [Fact]
    public async Task Extractor_RejectsOversizedOutputFromAnyProvider()
    {
        var extractor = new SupplierPageTextExtractor(new FakeProvider(new string('x', 20001)));
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() => extractor.ExtractAsync("model", "page", "prompt"));
    }

    [Theory]
    [InlineData("javascript:alert(1)", true)]
    [InlineData("https://user:pass@images.example.test/chair.png", true)]
    [InlineData("https://images.example.test/chair.png?token=secret", true)]
    [InlineData("https://images.example.test/chair.png#private", true)]
    [InlineData("https://images.example.test/not-present.png", false)]
    public async Task Extractor_RejectsUnsafeOrUngroundedImageUrls(string imageUrl, bool includeInPage)
    {
        var response = JsonSerializer.Serialize(new
        {
            name = "Chair", description = (string?)null, candidateRrpCents = (long?)null, currency = (string?)null,
            attributes = Array.Empty<object>(), identifiers = Array.Empty<object>(), imageUrls = new[] { imageUrl }
        });
        var page = includeInPage ? $"Chair {imageUrl}" : "Chair";
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() =>
            new SupplierPageTextExtractor(new FakeProvider(response)).ExtractAsync("model", page, "Find images"));
    }

    [Fact]
    public async Task GroundedChat_ReturnsOnlyCallerOwnedCitationReferences()
    {
        var source = new GroundedSource
        {
            Reference = new GroundedSourceReference
            {
                ModuleKey = ModuleKeys.Jobs, EntityId = Guid.NewGuid(), DisplayName = "Private job"
            },
            Excerpt = "The selected job is scheduled for Friday."
        };
        var provider = new FakeProvider("""{"answer":"The job is scheduled for Friday.","insufficientInformation":false,"citationNumbers":[1]}""");
        var result = await new GroundedChatGenerator(provider).GenerateAsync("model", "When is the job?", [source]);

        Assert.Equal(source.Reference, Assert.Single(result.Answer.Citations));
        Assert.False(result.Answer.InsufficientInformation);
        Assert.Contains("untrusted", provider.Request!.SystemInstruction);
        Assert.DoesNotContain(source.Reference.EntityId.ToString(), provider.Request.Input);
        Assert.DoesNotContain(source.Reference.DisplayName, provider.Request.Input);
        Assert.Contains(source.Excerpt, provider.Request.Input);
    }

    [Fact]
    public async Task GroundedChat_AllowsExplicitUncertaintyWithoutSources()
    {
        var provider = new FakeProvider("""{"answer":"I cannot determine that from the selected sources.","insufficientInformation":true,"citationNumbers":[]}""");
        var result = await new GroundedChatGenerator(provider).GenerateAsync("model", "When is the job?", []);
        Assert.True(result.Answer.InsufficientInformation);
        Assert.Empty(result.Answer.Citations);
    }

    [Theory]
    [InlineData("""{"answer":"Answer","insufficientInformation":false,"citationNumbers":[2]}""")]
    [InlineData("""{"answer":"Answer","insufficientInformation":false,"citationNumbers":[]}""")]
    [InlineData("""{"answer":"Answer","insufficientInformation":false,"citationNumbers":[1,1]}""")]
    [InlineData("""{"answer":"Answer","Answer":"Other","insufficientInformation":false,"citationNumbers":[1]}""")]
    [InlineData("""{"answer":"Answer","insufficientInformation":false,"citationNumbers":[1],"url":"https://example.test"}""")]
    public async Task GroundedChat_RejectsInvalidOrInventedCitations(string output)
    {
        var source = new GroundedSource
        {
            Reference = new GroundedSourceReference
            {
                ModuleKey = ModuleKeys.Jobs, EntityId = Guid.NewGuid(), DisplayName = "Job"
            },
            Excerpt = "Selected content"
        };
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() =>
            new GroundedChatGenerator(new FakeProvider(output)).GenerateAsync("model", "Question", [source]));
    }

    [Fact]
    public async Task GroundedChat_RejectsUnknownModuleBeforeCallingProvider()
    {
        var provider = new FakeProvider("{}");
        var source = new GroundedSource
        {
            Reference = new GroundedSourceReference
            {
                ModuleKey = "unknown", EntityId = Guid.NewGuid(), DisplayName = "Job"
            },
            Excerpt = "Selected content"
        };
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            new GroundedChatGenerator(provider).GenerateAsync("model", "Question", [source]));
        Assert.Null(provider.Request);
    }

    [Fact]
    public async Task GroundedChat_RejectsEncodedInputExceedingProviderLimit()
    {
        var provider = new FakeProvider("{}");
        var sources = Enumerable.Range(0, 12).Select(_ => new GroundedSource
        {
            Reference = new GroundedSourceReference
            {
                ModuleKey = ModuleKeys.Jobs, EntityId = Guid.NewGuid(), DisplayName = "Job"
            },
            Excerpt = new string('\u0001', 4000)
        }).ToImmutableArray();
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            new GroundedChatGenerator(provider).GenerateAsync("model", "Question", sources));
        Assert.Null(provider.Request);
    }

    [Fact]
    public async Task JobDraft_ReturnsReviewOnlyTextSuggestions()
    {
        var provider = new FakeProvider("""{"name":"Kitchen renovation","description":"Replace cabinets.","notes":null}""");
        var result = await new JobDraftGenerator(provider).GenerateAsync("model", "Kitchen renovation: replace cabinets");
        Assert.Equal("Kitchen renovation", result.Suggestion.Name);
        Assert.Equal("Replace cabinets.", result.Suggestion.Description);
        Assert.Null(result.Suggestion.Notes);
        Assert.Contains("user must review", provider.Request!.SystemInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kitchen renovation", provider.Request.Input);
    }

    [Theory]
    [InlineData("""{"name":"Job","name":"Other","description":null,"notes":null}""")]
    [InlineData("""{"name":"Job","description":null,"notes":null,"jobGroupId":"123"}""")]
    [InlineData("""{"name":"Job","description":null}""")]
    [InlineData("""{"name":1,"description":null,"notes":null}""")]
    public async Task JobDraft_RejectsMalformedOrActionableOutput(string output)
    {
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() =>
            new JobDraftGenerator(new FakeProvider(output)).GenerateAsync("model", "A kitchen renovation"));
    }

    [Fact]
    public async Task JobDraft_RejectsOversizedInputBeforeCallingProvider()
    {
        var provider = new FakeProvider("{}");
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            new JobDraftGenerator(provider).GenerateAsync("model", new string('x', 16001)));
        Assert.Null(provider.Request);
    }

    [Fact]
    public async Task ImageProvider_UsesImageModelAndReturnsGeneratedBytes()
    {
        using var client = new GeminiApiClient("test-key", new StubHandler(async request =>
        {
            Assert.EndsWith("/models/image-model:generateContent", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
            Assert.Equal("Make the chair blue", parts[0].GetProperty("text").GetString());
            Assert.Equal("AQID", parts[1].GetProperty("inlineData").GetProperty("data").GetString());
            return JsonResponse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"Done"},{"inlineData":{"mimeType":"image/png","data":"BAUG"}}]}}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":20,"thoughtsTokenCount":5,"totalTokenCount":35}}""");
        }));
        var result = await new GeminiImageProvider(client).EditAsync(ImageRequest());
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(new byte[] { 4, 5, 6 }, result.Image.ToArray());
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(5, result.ReasoningTokens);
        Assert.Equal(35, result.TotalTokens);
    }

    [Theory]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"No image"}]}}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"inlineData":{"mimeType":"image/svg+xml","data":"AQID"}}]}}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"inlineData":{"mimeType":"image/png","data":"not base64"}}]}}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"inlineData":{"mimeType":"image/png","data":"AQID"}},{"inlineData":{"mimeType":"image/png","data":"AQID"}}]}}]}""")]
    public async Task ImageProvider_RejectsInvalidOrAmbiguousResults(string response)
    {
        using var client = new GeminiApiClient("test-key", new StubHandler(_ => Task.FromResult(JsonResponse(response))));
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() => new GeminiImageProvider(client).EditAsync(ImageRequest()));
    }

    private static GenerativeImageRequest ImageRequest() => new()
    {
        Model = "image-model",
        Prompt = "Make the chair blue",
        ContentType = "image/png",
        Image = ImmutableArray.Create<byte>(1, 2, 3)
    };

    private static GenerativeTextRequest Request() => new()
    {
        Model = "gemini-test",
        SystemInstruction = "Extract data",
        Input = "selected text",
        ResponseSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        MaxOutputTokens = 100
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static ServiceProvider CreateServices(bool enabled, string? key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lavari:Integrations:Gemini:Enabled"] = enabled.ToString(),
            ["Lavari:Integrations:Gemini:ApiKey"] = key
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLavariInfrastructure(configuration, enableTickerQ: false);
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FakeProvider(string text) : IGenerativeTextProvider
    {
        public GenerativeTextRequest? Request { get; private set; }
        public Task<GenerativeTextResponse> GenerateAsync(GenerativeTextRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new GenerativeTextResponse { Text = text });
        }
    }
}
