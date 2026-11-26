using System.Text.Json;

namespace Lavari.Application.Abstractions;

// This is a provider boundary, not an authorized application entry point.
public interface IGenerativeTextProvider
{
    Task<GenerativeTextResponse> GenerateAsync(GenerativeTextRequest request, CancellationToken ct = default);
}

public sealed record GenerativeTextRequest
{
    public required string Model { get; init; }
    public required string SystemInstruction { get; init; }
    public required string Input { get; init; }
    public JsonElement? ResponseSchema { get; init; }
    public required int MaxOutputTokens { get; init; }
}

public sealed record GenerativeTextResponse
{
    public required string Text { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? TotalTokens { get; init; }
}
