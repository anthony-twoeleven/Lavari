using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lavari.Domain.Errors;

namespace Lavari.Infrastructure.Integrations;

internal sealed partial class GeminiApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    public GeminiApiClient(string apiKey)
        : this(apiKey, new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    internal GeminiApiClient(string apiKey, HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        if (!IsValidApiKey(apiKey))
            throw new ArgumentException("A valid Gemini API key is required.", nameof(apiKey));

        _apiKey = apiKey;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static bool IsValidApiKey([NotNullWhen(true)] string? apiKey)
        => !string.IsNullOrEmpty(apiKey) && apiKey.All(c => c is >= '!' and <= '~');

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z")]
    private static partial Regex ModelPattern();

    public async Task<JsonDocument> GenerateAsync(string model, object body, int maxResponseBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model) || !ModelPattern().IsMatch(model))
            throw new ValidationServiceException("Invalid AI model identifier.");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        message.Headers.Add("x-goog-api-key", _apiKey);
        message.Content = JsonContent.Create(body, options: JsonOptions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw Failure($"Gemini API returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > maxResponseBytes)
                throw Failure("Gemini API response exceeded the configured size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + count > maxResponseBytes)
                    throw Failure("Gemini API response exceeded the configured size limit.");
                buffer.Write(chunk, 0, count);
            }

            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Failure("Gemini API request timed out.");
        }
        catch (HttpRequestException)
        {
            throw Failure("Gemini API request failed due to a transport error.");
        }
        catch (IOException)
        {
            throw Failure("Gemini API response could not be read.");
        }
        catch (JsonException)
        {
            throw Failure("Gemini API response was not valid JSON.");
        }
    }

    public static JsonElement GetCandidateParts(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() != 1)
            throw Failure("Gemini API response did not contain exactly one candidate.");
        var candidate = candidates[0];
        if (!candidate.TryGetProperty("finishReason", out var finish) || finish.ValueKind != JsonValueKind.String)
            throw Failure("Gemini API response did not include a valid finish reason.");
        var finishReason = finish.GetString();
        if (finishReason != "STOP")
        {
            var detail = finishReason is not null && finishReason.Length <= 32 &&
                         finishReason.All(c => c is >= 'A' and <= 'Z' or '_')
                ? $"Gemini API generation ended with finish reason {finishReason}."
                : "Gemini API generation ended without a valid finish reason.";
            throw Failure(detail);
        }
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw Failure("Gemini API response did not contain valid candidate content.");
        return parts;
    }

    public static int? ReadTokens(JsonElement root, string name)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage)) return null;
        if (usage.ValueKind != JsonValueKind.Object) throw Failure();
        if (!usage.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetInt32(out var count) || count < 0) throw Failure();
        return count;
    }

    public static ExternalIntegrationServiceException Failure(string? technicalDetails = null) => new(
        "The AI provider was unavailable or returned an invalid response.",
        possibleCause: "Check the integration configuration or use manual entry.",
        technicalDetails: technicalDetails);

    public void Dispose() => _client.Dispose();
}
