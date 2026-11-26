using System.Collections.Immutable;

namespace Lavari.Application.Abstractions;

public interface IGenerativeImageProvider
{
    Task<GenerativeImageResponse> EditAsync(GenerativeImageRequest request, CancellationToken ct = default);
}

public sealed record GenerativeImageRequest
{
    public required string Model { get; init; }
    public required string Prompt { get; init; }
    public required string ContentType { get; init; }
    public required ImmutableArray<byte> Image { get; init; }
}

public sealed record GenerativeImageResponse
{
    public required string ContentType { get; init; }
    public required ImmutableArray<byte> Image { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? TotalTokens { get; init; }
}
