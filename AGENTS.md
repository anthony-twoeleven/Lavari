# Agent rules

These rules apply throughout this repository. Read
[APPLICATION_DESIGN.md](APPLICATION_DESIGN.md) and
[TECHNICAL_SPEC.md](TECHNICAL_SPEC.md) before implementing or changing a
feature. The documents describe the intended design, not existing code.

## Security and permissions

- All business UI, API and SignalR operations require authentication and an
  explicit registered module permission. Deny missing or unknown grants.
- Use the shared permission registry and consistently enforce None, Read,
  Alter and Delete as defined in the technical specification. Full Access
  is an explicit, audited role, never a bypass of authentication, validation,
  auditing or soft deletion.
- Enforce authorization on the server for every entry point, including
  application services, background work and integrations. UI visibility is
  not authorization. Recheck permissions after changes and protect role
  management against escalation.
- Never expose secrets, stack traces or sensitive technical details to users.
  Do not commit credentials; use .NET user secrets locally and an appropriate
  secret provider in deployed environments.

## Data integrity

- Do not implement physical deletion of application records. Filter
  soft-deleted data by default; authorize and audit deletion and restoration.
- Preserve full, append-only history for every application-owned table as
  specified, including administration and non-UI writes. Commit each mutable
  record change and its audit entry atomically; do not recursively audit
  immutable audit entries. Never place secrets in audit history.
- Validate input server-side, use concurrency checks and preserve data
  invariants under concurrent requests.
- Enforce exclusive edit leases and fair queues for existing jobs, quotes and
  invoices on the server, including background and integration writes. Retain
  optimistic concurrency checks, audit lease transitions, and notify queued
  users only when authorized.
- Store all monetary amounts and monetary rates as integer cents with a
  currency; calculate in checked integer arithmetic and convert to decimal
  only for human-readable formatting. Reject unsupported precision.
- Store datetimes in UTC and require a time zone for each job. Preserve
  date-only values as calendar dates; use the shared formatting service for
  financial values and dates/times, showing job and browser-local times when
  their zones differ.
- Preserve flexible product identifiers, job-specific requirements and
  issued quote/invoice snapshots; never silently overwrite job data from
  catalog or LLM updates. Audit every job line-item change.
- Support manual quote/invoice lines and total-only documents. Warn and
  require confirmation before changing calculated totals; audit adjustments
  and overrides and preserve the calculated and applied amounts.
- Preserve auditable job dates, timer transitions and manual time entries.
  Use server-authoritative, concurrency-safe time tracking and do not bill
  the same time twice or change issued financial snapshots.
- Support timer cancellation, soft deletion and reviewed Stop at/Resume at
  corrections. Require an explicit, concurrency-safe choice before starting
  a timer on another job while timers are running; preserve intentional
  overlaps and require billing review.
- Keep forecast estimates and actuals distinct and traceable; do not
  double-count linked financial records or expose unauthorized job data.
- Treat supplier pages, uploaded files and LLM output as untrusted. Never
  transmit supplier credentials or sessions; require user review of extracted
  information. Give integrations named, least-privilege identities.
- Protect Gmail OAuth credentials in a secret provider, isolate each user's
  mailbox and require explicit consent. Do not send, alter or delete Gmail
  messages without a separately approved design.
- Keep cache access behind the shared service; invalidate affected keys and
  scopes after committed changes and recheck authorization on cache hits.
- Process and store image assets through shared abstractions. Enforce the
  1080p aspect-preserving limit, private access, auditable image associations
  and durable storage outside build output and container writable layers.

## Architecture and implementation

- Keep the Blazor runtime, API class library and Razor component library
  separate. Respect the inward dependency direction; keep business behavior
  independent of hosting and infrastructure.
- Target .NET 11 and use EF Core with SQLite. Use the latest compatible
  stable packages, pin reviewed versions, and keep dependencies current.
- Add third-party libraries only when needed, and only under open-source or
  open licenses. Verify direct and transitive artifact licenses; source
  availability or a free restricted entitlement alone is insufficient.
- Use TickerQ behind the scheduler abstraction only after resolving the
  documented license and EF Core compatibility blocker; do not silently
  relax the license policy or downgrade packages.
- Design services and external dependencies behind replaceable abstractions,
  including time via injected `TimeProvider`; use explicit test and
  development adapters. Never enable dummy services in production.
- Prefer sealed reference types, readonly data and `get; init;` contracts.
  Keep payloads lightweight and collections actually immutable. Document
  necessary exceptions for framework integration or editable state.
- Use application variables for changeable non-secret business values and
  configuration providers for deployment configuration and secrets.
- Build and own the shared Razor component library using Material 3
  foundations as design guidance, without depending on Material Web. Follow
  system light/dark preferences and preserve accessibility.
- Validate required fields before requests; keep create/update disabled when
  clean, invalid, unauthorized or loading. Handle failure, dirty-state reset
  and navigation consistently.
- Measure performance and allocations, page large queries, and schedule
  heavy work. Provide focused tests and representative benchmarks as
  functionality is introduced. Measure whole-host resource use against the
  planned small VPS before deciding whether to increase its size.
- Preserve portability across VPS providers and SQLite/MySQL; do not assume
  a provider has been provisioned. Include encrypted off-server backups and
  tested recovery in deployment work.
- Build and test releases in GitHub Actions; deploy approved immutable image
  digests with Docker Compose. Keep production credentials and persistent
  data outside images, and never expose them to untrusted pull requests.
- Protect SSH and Docker daemon access, preserve database, files and Data
  Protection keys across container replacement, and test deployment health
  and migration-aware recovery.
- Use authorized SignalR notifications with minimal payloads and recover
  from missed notifications. Keep integration adapters replaceable.
- Throw meaningful service exceptions; map them centrally to safe, actionable
  coded errors and log restricted occurrence details with correlation IDs.
  Do not swallow failures or return success-shaped defaults.
