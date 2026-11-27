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

Development startup seeds three sample clients with 1, 2 and 6 folders,
respectively; the first two have one job per folder, and the third has four.
Legacy duplicate sample clients are soft-deleted with their audit history kept.

For receipt capture, open **Procurement & Receipts → Capture / Upload Receipt
→ Open camera** and allow camera access. Use HTTPS when testing from a
phone over the network: plain HTTP to a LAN address does not allow browser
camera access. You can still choose an existing photo.

### Chrome and Microsoft Edge supplier companion

The same unpacked extension works in both Chromium-based browsers:

- **Chrome:** open `chrome://extensions`, enable **Developer mode**, and
  select **Load unpacked**.
- **Microsoft Edge:** open `edge://extensions`, enable **Developer mode**,
  and select **Load unpacked**.

Choose the repository's `extension` directory and pin the companion to the
toolbar. Set the fixed Lavari origin in `extension/config.js` before loading
the extension; it defaults to `https://lavari.com.au`. For local development,
change it to the HTTPS origin used by your local Lavari host. The extension
requests site access for that configured origin. Its full workspace stays
hidden until **Continue to Lavari** verifies an authenticated session in the
same browser profile. If needed, sign in on the opened Lavari tab and return
to continue. The companion uses Lavari's existing session and never receives
your password.

Select a client and job. On a supplier page, open the companion and choose
**Capture visible page HTML**. It snapshots rendered page content from the
current browser tab (including the signed-in view), excluding hidden content,
scripts, forms and field values; it does not send browser cookies or
credentials. Review the captured HTML and URL, remove any private or unrelated
information, then confirm before processing. Sensitive URL query parameters
and fragments are stripped while ordinary product/variant parameters are
retained; add product-specific requirements separately.
Review the suggested fields, then explicitly add the line item. Alternatively,
enter or paste the fields manually without processing or a prompt. Saving
requires the job Modify lock and the relevant permissions. If another editor
holds the job, retry after they finish. An interrupted save may have succeeded;
check the job before retrying.

The companion keeps unsaved drafts in the browser's session storage until the
browser session ends or the draft is discarded. Chrome and Edge keep separate
sessions and drafts. Avoid shared browser profiles for confidential drafts.
It uses the user's saved Lavari colors when the account appearance permission
is available; otherwise it uses the system theme. **Open in window** provides
more space while editing.

When Gemini is available to the signed-in user, select **Use Gemini to extract
product details**, optionally provide a prompt, and separately confirm consent
to send the captured page content and prompt to Google. The API returns a review-only
draft; suggestions fill only empty fields (and the default zero RRP), so check
all values and the currency before saving. Without Gemini access, processing
uses local heuristic suggestions. Manual entry remains available either way.

Supplier capture is also available under **Product Catalog → Capture
Supplier Page** in Lavari. Captured drafts do not automatically become job
line items.
Page text is optional: when omitted, Lavari makes one bounded request to the
provided public HTTP(S) product page, without cookies, sign-in or redirects.
If the supplier blocks automated access, paste up to 16,000 characters of
selected text instead. Raw page content is not stored as a separate field;
extracted suggestions can still contain portions of it. Local parsing produces
heuristic suggestions, not AI results. Prompt-based Gemini requests fail
closed unless the provider and budget are configured and the user explicitly
consents to sending the fetched or pasted content. Extraction is awaited
directly in the authenticated API request; it does not use a scheduled task.

Users with supplier-capture and catalog permissions can review a pending
draft, edit its proposed product fields, and accept it into the global catalog.
Acceptance creates the product and approves the draft atomically. Catalog
products can be edited with concurrency checks and assigned to a job as a
separate, locked job line item. Supplier image URLs are retained as references
only; Lavari does not fetch or display them. Upload an image through the Media
Library after reviewing usage rights.

The codebase registers Gemini text and image-provider adapters sharing a
transport. Supplier extraction calls the provider asynchronously within the
API request, with user and service-principal authorization checks, explicit
provider consent, audited spend reservations and a review-only result. It
does not use TickerQ. The providers resolve only when
`Lavari:Integrations:Gemini:Enabled` is true and an API key is configured
through a secret provider. The status endpoint reports readiness, and the
supplier UI requires separate consent before selected content can be sent to
Google. The transport applies a 30-second limit to the entire provider
request, including reading its response. Chat over business
data, job drafting and AI image editing are not yet implemented; they need
separate permission-aware workflows and review, and image edits must retain
the original through the private image storage pipeline. The database has an
audited source-image link for future derived assets, but no workflow creates
or exposes AI edits yet. Image
browsing, file reads and mood-board exports require access to linked records;
derived assets remain hidden until their source ancestry can be authorized.
If an image upload fails after database persistence starts, its private files
are retained because the commit outcome may be uncertain. Operators must
verify that files are unreferenced before removing any resulting orphans.

The supplier parser can suggest flexible identifiers and image URLs present
in the selected text. It rejects URLs containing credentials, fragments or
credential-like query parameters; benign image transformation query
parameters are permitted. Image URLs are inert references and are never
fetched. All suggestions remain untrusted and require review.

An internal grounded-chat generator can use caller-supplied excerpts and map
validated citation numbers back to caller-owned record references. It is not
connected to authorized retrieval or used by an application workflow; citation
numbers do not establish that an answer is factually correct. Authorization,
consent, response-time rechecks and the controls above remain required.
An internal job-draft generator likewise returns only reviewable text
suggestions; it does not select business records or create a job.

The supplier-AI reservation ledger records estimates and token usage without
recording prompts in audit history. Its UTC-daily limits use the application variables
`Ai.SpendCurrency`, `Ai.DailyLimitCents`, `Ai.UserDailyLimitCents`,
`Ai.UserDailyRequests` and `Ai.ReservationCents`; absent or invalid settings
deny reservations. Cancelled requests still count toward the request quota.
Once execution starts, the full estimate remains reserved, including on an
ambiguous failure. This does not yet cap provider charges: reviewed,
model-specific worst-case pricing and execution safeguards are still required.
Missing variables are seeded from `Lavari:AiBudget` in
`appsettings.json` (AUD 150/day globally, AUD 150/user/day, up to 150
requests/user, and AUD 1 reserved per request). Existing application-variable
values are never overwritten by configuration defaults. These reservation limits do not cap
actual Gemini billing. To enable Gemini locally, set
`Lavari:Integrations:Gemini:Enabled` to `true` and store the API key with
`dotnet user-secrets set "Lavari:Integrations:Gemini:ApiKey" "<key>" --project src\Lavari.Web`.

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

The permission-aware image-list benchmark uses synthetic linked records in
in-memory SQLite. A short in-process .NET 11 run on a Ryzen 9 8945H measured
about 100 ms before and 13 ms after adding image-reference indexes at 1,000
images; the indexed run allocated about 562 KB per request. These are local
measurements, not a VPS capacity estimate.

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
