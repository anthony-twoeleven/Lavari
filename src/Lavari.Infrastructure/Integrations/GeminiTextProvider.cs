using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Domain.Errors;

namespace Lavari.Infrastructure.Integrations;

internal sealed class GeminiTextProvider(GeminiApiClient client) : IGenerativeTextProvider
{
    private const int MaxResponseBytes = 131072;

    public async Task<GenerativeTextResponse> GenerateAsync(GenerativeTextRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SystemInstruction) || request.SystemInstruction.Length > 8000 ||
            string.IsNullOrWhiteSpace(request.Input) || request.Input.Length > 121000 ||
            (request.ResponseSchema.HasValue && request.ResponseSchema.Value.ValueKind != JsonValueKind.Object) ||
            request.MaxOutputTokens is < 1 or > 2048)
            throw new ValidationServiceException("Invalid AI generation request.");

        object generationConfig = request.ResponseSchema.HasValue
            ? new
            {
                maxOutputTokens = request.MaxOutputTokens,
                responseFormat = new { text = new { mimeType = "APPLICATION_JSON", schema = request.ResponseSchema.Value } }
            }
            : new { maxOutputTokens = request.MaxOutputTokens };
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = request.SystemInstruction } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = request.Input } } } },
            generationConfig
        };

        try
        {
            using var document = await client.GenerateAsync(request.Model, body, MaxResponseBytes, ct);
            var root = document.RootElement;
            var parts = GeminiApiClient.GetCandidateParts(root);
            if (parts.GetArrayLength() != 1 || !parts[0].TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
                throw GeminiApiClient.Failure();
            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 20000)
                throw GeminiApiClient.Failure();

            return new GenerativeTextResponse
            {
                Text = text,
                InputTokens = GeminiApiClient.ReadTokens(root, "promptTokenCount"),
                OutputTokens = GeminiApiClient.ReadTokens(root, "candidatesTokenCount"),
                ReasoningTokens = GeminiApiClient.ReadTokens(root, "thoughtsTokenCount"),
                TotalTokens = GeminiApiClient.ReadTokens(root, "totalTokenCount")
            };
        }
        catch (InvalidOperationException)
        {
            throw GeminiApiClient.Failure();
        }
    }
}
