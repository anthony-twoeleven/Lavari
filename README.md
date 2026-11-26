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

For receipt capture, open **Procurement & Receipts → Capture / Upload Receipt
→ Open camera** and allow camera access. Use HTTPS when testing from a
phone over the network: plain HTTP to a LAN address does not allow browser
camera access. You can still choose an existing photo.

The browser companion is not implemented. To test supplier capture, use
**Product Catalog → Capture Supplier Page**, paste the supplier URL and
selected product text without credentials, and review the resulting draft.
To test job insertion, use **Clients & Jobs → Workspace → Line Items →
Add Line Item**. This requests the job's Modify lock if you don't already hold
it. Captured drafts do not automatically become job line items.

## Performance benchmarks

Run the full suite in Release to measure execution time and managed allocations:

```powershell
dotnet run --project tests\Lavari.Benchmarks\Lavari.Benchmarks.csproj -c Release -- --filter '*' --exporters markdown csv
```

For comparison with the .NET 11 performance sweep, use BenchmarkDotNet
`0.16.0-preview.2` as a temporary local override in `Directory.Packages.props`:
the pinned stable `0.15.8` does not recognize .NET 11. Restore the stable pin
afterward. Compare results with the same runner, runtime, and input data.
The 25-job search requests page 2 and measures an empty page; the 100-job
search measures a populated page.

## Design documents

Appearance settings are available under **Account settings**. System follows
the device's light/dark preference; highlight colors use curated palettes.
Administrators must explicitly grant existing roles Account Appearance Read
or Alter access. Full Access includes the registered module.

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
