# Lavari

Lavari is an interior design business application for managing clients, jobs,
product sourcing, mood boards, time tracking, forecasts, quotes, invoices and
procurement.

## Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)

TickerQ 10.4.0 and EF Core 10.0.15 are intentionally pinned: the documented
license and EF Core 11 compatibility blocker prevents safely upgrading either
dependency until the scheduler decision is resolved.

## Running locally

```powershell
dotnet run --project src\Lavari.AppHost\Lavari.AppHost.csproj
```

## Design documents

- [Application design](APPLICATION_DESIGN.md)
- [Technical specification](TECHNICAL_SPEC.md)
- [Agent rules](AGENTS.md)

## Repository contents

- `src/` — Aspire AppHost, ServiceDefaults, Blazor Web host, API class library,
  Material 3 Razor component library, Contracts, Application, Domain and
  Infrastructure.
- `tests/` — Unit tests, Integration tests and BenchmarkDotNet benchmarks.
- `.gitignore` excludes generated .NET build output, IDE files, and other local
  artifacts.
