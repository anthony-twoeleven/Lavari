namespace Lavari.Contracts.Common;

public sealed record PagedRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public bool IncludeDeleted { get; init; }
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }

    public int NormalizedPage => Page < 1 ? 1 : Page;
    public int NormalizedPageSize => PageSize < 1 ? 25 : Math.Min(PageSize, 200);
}

public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed record ApiErrorResponse
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required string PossibleCause { get; init; }
    public required string CorrelationId { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? FieldErrors { get; init; }
}

public sealed record FormattedDateTimeDto
{
    public required DateTimeOffset Utc { get; init; }
    public required string JobLocalFormatted { get; init; }
    public required string JobZoneId { get; init; }
    public required string JobZoneLabel { get; init; }
    public string? BrowserLocalFormatted { get; init; }
    public string? BrowserZoneId { get; init; }
    public string? BrowserZoneLabel { get; init; }
    public required bool ZonesDiffer { get; init; }
    public required bool BrowserZoneAvailable { get; init; }
}
