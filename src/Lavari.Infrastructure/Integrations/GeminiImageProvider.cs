using System.Collections.Immutable;
using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Domain.Errors;

namespace Lavari.Infrastructure.Integrations;

internal sealed class GeminiImageProvider(GeminiApiClient client) : IGenerativeImageProvider
{
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxResponseBytes = 12 * 1024 * 1024;

    public async Task<GenerativeImageResponse> EditAsync(GenerativeImageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 4000 ||
            request.ContentType is not ("image/jpeg" or "image/png" or "image/webp") ||
            request.Image.IsDefaultOrEmpty || request.Image.Length > MaxImageBytes)
            throw new ValidationServiceException("Provide an image and an edit prompt within the supported limits.");

        var body = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = request.Prompt },
                        new { inlineData = new { mimeType = request.ContentType, data = Convert.ToBase64String(request.Image.AsSpan()) } }
                    }
                }
            },
            generationConfig = new { responseModalities = new[] { "TEXT", "IMAGE" } }
        };

        try
        {
            using var document = await client.GenerateAsync(request.Model, body, MaxResponseBytes, ct);
            var root = document.RootElement;
            var parts = GeminiApiClient.GetCandidateParts(root);
            JsonElement image = default;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("inlineData", out var candidate))
                {
                    if (image.ValueKind != JsonValueKind.Undefined) throw GeminiApiClient.Failure();
                    image = candidate;
                }
            }

            if (image.ValueKind != JsonValueKind.Object ||
                !image.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String ||
                mime.GetString() is not ("image/jpeg" or "image/png" or "image/webp") ||
                !image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                throw GeminiApiClient.Failure();
            var encoded = data.GetString();
            if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > ((MaxImageBytes + 2) / 3) * 4)
                throw GeminiApiClient.Failure();
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is 0 or > MaxImageBytes) throw GeminiApiClient.Failure();

            return new GenerativeImageResponse
            {
                ContentType = mime.GetString()!,
                Image = ImmutableArray.CreateRange(bytes),
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
        catch (FormatException)
        {
            throw GeminiApiClient.Failure();
        }
    }
}
