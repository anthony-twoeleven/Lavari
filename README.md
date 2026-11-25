# Lavari

Lavari is an interior design business application for managing clients, jobs,
product sourcing, mood boards, time tracking, forecasts, quotes, invoices and
procurement.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (LTS, matches
  the installed SDK and allows open-source MIT/Apache-2.0 TickerQ 10.4.0 with
  EF Core 10 SQLite)

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
