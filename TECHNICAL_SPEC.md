# Lavari technical specification

Status: proposed architecture. No projects or infrastructure have been
implemented yet.

## Runtime and dependency policy

Target .NET 11 (`net11.0`) for the application, using the latest supported
stable SDK, runtime and compatible package releases when implementing and
updating the solution. As of 25 November 2026, .NET 11 is a standard-term
support release with support ending 14 November 2028; keep its runtime and
container base images serviced and plan upgrades before end of support. See
the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
Use a compatible current Aspire release and verify all packages against the
selected framework and each other.

Add third-party libraries only where required, preferring built-in .NET
capabilities when suitable. Every third-party library and its transitive
dependencies must have an open-source or otherwise open license that permits
the intended use and distribution; verify the exact artifact's license,
not merely its source visibility or price. Review native dependencies, assets
and license obligations too. Proprietary, source-available or
eligibility-restricted licenses are not treated as open licenses. Document
the necessity, version, license and compatibility of each dependency.

Choose the latest stable compatible release satisfying these rules, and
apply security and compatibility updates promptly after validation. Pin
reviewed versions and use reproducible restores rather than floating
versions or silently upgrading production. Test updates before release.
Any unmet license or compatibility requirement blocks adoption rather than
quietly adding an exception; TickerQ currently has such a conflict below.

## Solution structure and dependency direction

The following is a target layout, not a claim that these projects exist:

```text
src/
  Lavari.AppHost/          Aspire orchestration
  Lavari.ServiceDefaults/  shared Aspire service defaults
  Lavari.Web/              Blazor runtime and initial API/SignalR host
  Lavari.Api/              API endpoint and hub class library
  Lavari.Components/       Material 3 Razor component library
  Lavari.Contracts/        immutable request, response and permission contracts
  Lavari.Application/      use cases, authorization and service abstractions
  Lavari.Domain/           business rules and domain types
  Lavari.Infrastructure/   database, integrations and other adapters
tests/
  Lavari.UnitTests/
  Lavari.IntegrationTests/
  Lavari.Benchmarks/
```

Dependencies point inward: Domain has no infrastructure or UI dependency;
Application depends on Domain and its own abstractions; Infrastructure
implements those abstractions; Api uses Application and Contracts; Components
does not depend on Api or Infrastructure. Web composes the implementations,
hosts Api, and uses Components and Contracts. AppHost orchestrates deployable
hosts and dependencies. Keep contracts independent of persistence entities.
Do not pass ORM entities across HTTP, component or integration boundaries.

Use a Blazor Web App with an interactive server runtime initially. UI business
actions use typed HTTP API clients, not direct infrastructure or application
service calls. Implement the initial client transport with browser-side
`fetch` through a small JavaScript interop adapter, against a trusted
same-origin API. The browser sends the user's session cookie; do not assume a
server-side `HttpClient` automatically inherits browser credentials or
substitute a server-wide privileged identity. Issue and send an anti-forgery
token on cookie-authenticated state-changing requests and validate origin as
applicable. Requests still pass through authentication, authorization and
validation when the API shares a process with the UI. API-dependent UI must
wait until interactivity is available; configure prerendering accordingly.
Await JavaScript interop inside a try/catch. UI-only operations may stop when
the circuit disconnects, but never treat a missing confirmation as consent.
Browser API interop failures must propagate rather than masquerade as HTTP
responses or successful operations. Log the operation name, not its arguments.
Revalidate the identity for long-lived interactive sessions.

A future separate API host reuses the same endpoint library and application
services. Keep a same-origin gateway or explicitly implement a suitable
delegated authentication, token validation, CORS, anti-forgery and SignalR
strategy before enabling the split.

An optional browser companion is a separate client, not part of the Blazor
runtime. It submits user-selected supplier-page data to the same API with the
user's own authenticated and authorized context. It has no direct database
access. Document rendering, Gmail, LLM and file storage are replaceable
adapters; Aspire may orchestrate local development dependencies without
requiring paid services in the initial deployment.

## Hosting and deployment

The preferred initial deployment is a single OVHcloud VPS-1, targeting the
Sydney region for an Australian user, if available at acceptable final terms.
On 25 November 2026, [OVHcloud's Australian VPS page](https://www.ovhcloud.com/en-au/vps/vps-australia/)
advertised it from A$6.29/month excluding GST, with 2 vCores, 4 GB RAM and
40 GB NVMe. This is a starting price, not a confirmed Sydney checkout price
or final monthly bill. Confirm plan availability, region, IPv4, term,
renewal and setup fees, GST and support before ordering.

| Option | Advertised resources and price at research time | Consideration |
| --- | --- | --- |
| OVHcloud VPS-1, preferred | 2 vCores, 4 GB RAM, 40 GB; from A$6.29/month ex GST | Lower-cost starting point; storage includes the OS and all application data. |
| [OVHcloud VPS-2](https://www.ovhcloud.com/en-au/vps/vps-australia/) | 4 vCores, 8 GB RAM, 75 GB; from A$11.90/month ex GST | Candidate if measurement shows VPS-1 is insufficient; confirm final regional and contract pricing. |
| [AWS Lightsail Linux](https://aws.amazon.com/lightsail/pricing/) | 2 vCPUs, 2 GB RAM, 60 GB, IPv4; US$12/month | Alternative; burstable CPU and Sydney transfer allowance is half the advertised general allowance. |
| [Oracle Always Free](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm) | Up to 2 Arm OCPUs, 12 GB RAM and 200 GB combined storage within free limits | Capacity may be unavailable and idle instances can be reclaimed; assess suitability before relying on it for business data. |
| [DigitalOcean Basic](https://www.digitalocean.com/pricing/droplets) | 2 vCPUs, 2 GiB RAM, 60 GiB; US$18/month | Alternative; check Sydney availability and backup charges. |

These are historical research figures, not quotes; currencies and taxes
differ. Prices exclude any additional backup, storage, transfer overage,
LLM or integration charges. OVHcloud advertises a daily backup of the
previous 24 hours and applies an APAC traffic quota before reducing bandwidth;
verify its current terms and coverage. Provider backups do not replace the
independent, encrypted application backups and restore tests below.

Deploy to a supported Linux distribution on the VPS. Package the combined
Blazor/API host and initially in-process scheduled worker as one Docker image,
and run it with Docker Compose. SQLite and private files reside on the VPS
outside the container's writable layer. The reverse proxy terminates HTTPS
and forwards to the application over loopback. Use Aspire for development
orchestration and service configuration; the Aspire dashboard and AppHost are
not required to run publicly or permanently in production. Aspire can
[publish Docker Compose artifacts](https://aspire.dev/deployment/docker-compose/);
assess generated configuration against these requirements before using it.

Use a supported, reviewed .NET runtime base image and build release images on
GitHub-hosted runners, not on the production VPS. Run the application as a
non-root user, limit container capabilities and writable paths, and apply
resource limits after measuring realistic workloads. Restart the service on
failure or host reboot. Do not mount the Docker socket into the application
or expose the Docker daemon publicly.

Mount persistent, access-controlled storage for the SQLite database directory
(including its WAL and shared-memory files when used), private uploads,
templates, generated documents and ASP.NET Core Data Protection keys. Persist
and protect the Data Protection key ring across deployments and restarts so
authentication and anti-forgery tokens are not invalidated unnecessarily.
Encrypt keys at rest, restrict access to the application identity, and
include them in the recovery plan with their encryption key managed separately.
Keep deployment-managed secrets outside images and the repository. Treat
container replacement, Compose changes and storage cleanup as potentially
destructive: never remove persistent volumes as part of normal deployments.

### GitHub Actions release pipeline

1. Run build, unit and relevant integration tests on pull requests using
   GitHub-hosted runners. Pull-request code must not receive production
   credentials or be deployed.
2. From a trusted commit on the protected main branch, build the release image
   once, associate it with the commit, and publish it to GitHub Container
   Registry (GHCR). Tag it for discovery, but deploy by immutable image digest;
   never use a mutable `latest` tag as the deployment identity. Run the
   appropriate tests, built-image smoke tests and dependency/image security
   checks before publishing.
3. Start production deployment manually through GitHub Actions, choosing a
   verified successful main-branch release. Use the same image that passed
   validation, not a rebuild or an arbitrary user-supplied image. Serialize
   production deployments and record the commit, digest, operator, time and
   result. Use a protected production environment and approval where available.
4. The runner connects securely to the VPS and invokes a narrowly scoped,
   server-owned deployment procedure. Pull the approved digest, start the
   replacement with Compose and check a minimal local health endpoint and
   externally reachable HTTPS. Report success only when health checks pass;
   report failures explicitly. An endpoint exposed for infrastructure health
   must not reveal application or customer data.
5. Keep the previously deployed image digest and deployment configuration for
   recovery. A code rollback is permitted only when the current database
   schema is compatible with that image. If a migration is incompatible or
   fails, stop and follow the tested recovery plan; do not automatically
   restore an old database and discard later writes. Back up before schema
   migrations, and design changes for backward compatibility where feasible.

A single-instance replacement can cause brief downtime. Define and test the
deployment and recovery procedure, including interruptions, before production.
The host must pull from the intended registry; newly published GHCR packages
are private by default, so provide a narrowly scoped read credential in the
server's secret provider unless the package is intentionally made public.
Never put credentials, customer files or production configuration into the
image. GitHub currently states that standard hosted runners for public
repositories and GHCR storage/bandwidth are free; check
[Actions](https://docs.github.com/en/billing/concepts/product-billing/github-actions)
and [Packages](https://docs.github.com/en/billing/concepts/product-billing/github-packages)
pricing and quotas when deploying, particularly if repository visibility
changes.

Limit workflow token permissions per job, pin third-party actions to verified
full commit SHAs, and keep production secrets in a restricted GitHub
environment. Never use untrusted pull-request code or artifacts in a
privileged deployment workflow. Use a dedicated deploy identity and SSH key,
pin the verified server host key, and do not grant unrestricted root, sudo
or Docker socket access. A privileged deployment helper must be owned and
protected by an administrator, accept only validated release digests from
the expected registry, and not allow arbitrary Docker arguments or filesystem
operations. Treat control of the Docker daemon as root-equivalent. GitHub's
hosted-runner IP addresses can change; choose a secure connectivity method
compatible with firewall restrictions rather than disabling host verification
or opening administrative access indiscriminately.

Do not install a self-hosted GitHub Actions runner on the production VPS for
untrusted workflows. Keep deployment credentials and host keys rotatable,
and restrict modification of release workflows and protected-branch rules.

### Host operations and backups

Use the existing domain's GoDaddy DNS to point the confirmed admin subdomain
to the server's stable public address. Terminate HTTPS at a maintained reverse
proxy with an automatically renewed certificate, forward to the app over
loopback, and configure forwarded headers only from the trusted proxy.
Ensure the proxy supports SignalR/WebSockets. Restrict public inbound access
to HTTPS and any port needed for certificate validation; restrict SSH to
trusted sources where feasible and use key-based access. Apply OS and
dependency security updates, least-privilege service accounts, provider
account MFA and appropriate firewall rules. A deployment-managed secret
provider is required; choose one that protects and rotates credentials within
the operational budget, without committing secrets or placing them in the
database, variables table, audit history or logs.

The single VPS is a single point of failure; planned maintenance or failure
can interrupt the application. Establish acceptable recovery-point and
recovery-time objectives before production. Run scheduled SQLite-consistent
backups and back up private files, template and issued-document artifacts,
and necessary configuration; encrypt them and copy them off the VPS to
independent storage with restricted access and a defined retention policy.
Keep encryption keys separate and recoverable. Alert on backup failures and
regularly restore the database and corresponding files into an isolated
environment to verify usability and consistency. Provider snapshots alone do
not establish database/file consistency or protection from account compromise.
Account for backup storage, operational monitoring and restore costs.

### Measuring and sizing

During development, collect baselines for representative one-user workflows,
including job browsing and editing, receipt uploads, mood boards, quote and
invoice generation, and background processing. Include realistic image,
document and audit-history sizes; use synthetic or explicitly authorized data.
Measure application and whole-host CPU, peak and sustained memory, allocations,
disk usage and growth, I/O, network, request latency, job duration, queue depth
and failures. Include the OS, proxy and workers rather than measuring the app
in isolation. Compare runs using documented workloads and environments.

Use a 2-vCPU/4-GB resource-constrained test environment where practical,
leaving headroom for system services; validate results on comparable VPS
hardware because CPU performance and storage vary by provider. Monitor
production after deployment with accessible dashboards and alerts for
sustained resource pressure, low disk space, failed jobs and backups, and
unavailability. Set alert thresholds and acceptable latency from measured
baselines and business requirements. Review peaks and growth trends, not
just averages. Size up, optimize or move work off the host when sustained
pressure, insufficient headroom or unmet latency/recovery targets justify
it. Do not treat 40 GB as wholly available to user uploads or treat the
advertised vCore count as a performance guarantee.

## Coding and data conventions

- Apply clean architecture and the open/closed principle: extend behavior via
  focused abstractions and adapters, without modifying unrelated modules.
  Every application service and external dependency has a replaceable
  contract; production, test and explicitly configured development adapters
  use the same contract. Do not register dummy implementations in production.
- Default reference types to `sealed` and immutable data to `get; init;`.
  Prefer readonly value types where they are appropriate and measured;
  `sealed` does not apply to structs. Keep contracts small and avoid sending
  redundant fields. Use explicit, justified exceptions for framework-required
  mutable types, EF persistence mappings or editable UI state; isolate those
  types and never expose them as public transport contracts. `init` alone
  does not make referenced collections immutable.
- Wrap nondeterministic or external platform behavior behind injectable
  abstractions. Use .NET `TimeProvider` for current time, injected into
  services and substitutable in tests. Apply the same approach to I/O, IDs,
  scheduling and external systems where used. Do not add wrappers for pure
  language primitives with no external behavior.
- Store all date-and-time values as UTC instants; keep date-only values as
  calendar dates. Use the shared formatting service and job time zones as
  specified below. Use a provider-supported concurrency token for updates and
  return a conflict rather than silently overwriting another user's changes.
- Keep requests cancellable, use asynchronous I/O, avoid unbounded
  allocations, and measure before introducing caches or object pools.

## Identity and permissions

Use a trusted identity provider; the provider and login flow are to be chosen.
Validate identities on every API and hub connection. The application stores
only the identity reference and profile data required for its roles; do not
store passwords or tokens in application variables or audit snapshots.
Protect all business routes by default. Anonymous access is limited to the
minimum infrastructure endpoints required for authentication and operations.

Define a single permission registry shared by UI, endpoints and authorization
tests. Each module has a stable key; each UI action, endpoint and hub operation
declares a required module and level. The effective level is:

| Level | Allowed operations |
| --- | --- |
| None | No access |
| Read | View and list, including authorized history |
| Alter | Read, create and update |
| Delete | Alter, soft-delete, view deleted records and restore |

Grants combine by taking the highest level across assigned roles, separately
for each module. No explicit deny overrides an existing grant. Unregistered
modules, absent grants, unauthenticated users and disabled users are denied.
The Full Access role is an explicit, audited wildcard across registered
modules, including ones added later. It is not a database superuser or an
authorization bypass. Include separate modules for clients, jobs, users,
role/permission administration, application variables, and operational
errors. Register separate modules for job groups, product catalog, job line
items, mood boards, quotes, invoices, procurement, receipts, supplier capture,
Gmail connections and email, job dates, time tracking, financial forecasting,
image library, and LLM administration/use. Each entry point
checks every module whose data or operation it needs. For example, a job view
requiring client details also checks client Read. Return only authorized data,
including on history, exports and downloads.

The server is the authority for every operation, including direct application
service entry points, HTTP endpoints, hub subscriptions and background or
integration work. The UI uses the same registry and current effective grants
to hide or disable inaccessible actions, but is never the security boundary.
Anonymous requests receive 401; authenticated requests lacking permission
receive 403. Apply record-level checks as well if later required.

Provision the first Full Access administrator through a controlled,
documented deployment process, not a hard-coded credential or public
bootstrap endpoint. Only authorized administrators can manage roles and
assignments. A role administrator cannot grant permissions they do not
possess, including Full Access; prevent self-escalation. Protect against
removing or disabling the last viable Full Access administrator and audit all
changes. Permission changes take effect on the next operation: validate
current grants server-side rather than trusting long-lived client claims.
If caching is introduced, use cross-instance versioning/invalidation and
test revocation. Terminate or reauthorize existing hub subscriptions on
revocation, and recheck authorization before sending sensitive events.

## Persistence, soft deletion and auditing

Use Entity Framework Core with its `Microsoft.EntityFrameworkCore.Sqlite`
provider, a transactional SQLite database and EF Core migrations for the
planned single-instance, low-write-volume deployment with persistent storage,
subject to validating backup, concurrency and security requirements. Use the
latest compatible stable EF Core and provider release; EF Core SQLite 11.0.0
is available for .NET 11 as of 25 November 2026. Keep EF entities and
provider-specific behavior in infrastructure.
Relational foreign keys,
transactions and audit history suit the client/group/job hierarchy, line
items and financial records; flexible product attributes can be stored in
validated JSON rather than requiring a separate document database. Compare
actual hosting, storage, backup, operational and license costs before
committing. If write contention, multiple instances or operational needs
outgrow SQLite, consider MySQL behind the same abstractions; PostgreSQL is
another option. Choose self-managed or managed hosting and assess its cost
at that time. Do not rely on SQLite as a shared database file over a network
filesystem or pretend it supports distributed concurrent writers.

Before any database migration, select and validate a supported provider and
create a tested schema and data migration plan, including JSON, integer-cent
money, UTC timestamps, IDs, concurrency tokens, soft-deleted data and full audit
history. Rehearse on restored copies, compare record counts and representative
data, verify permissions and transactional invariants, and plan a write
freeze, cutover and rollback. Do not assume switching providers is automatic.

Document databases also support variable product attributes, but do not
eliminate the need for transactional relationships, stable financial
snapshots, audit history and consistent queries across jobs. A separate
document store would add hosting and operational complexity at this scale;
reconsider it only if measured access patterns and total cost justify it.

Normal business records have a stable ID, creation/modification
metadata, a concurrency token and nullable deletion metadata
(`DeletedAt`, `DeletedBy`). Delete is an authorized, concurrency-checked
update, never a physical delete. Normal queries exclude deleted records;
explicit administrative deleted-record queries and restore operations require
Delete permission. Restore clears deletion metadata and is itself audited.
Do not cascade a physical delete. Block client deletion while active groups
exist and group deletion while active jobs exist. Validate active parents on
group/job creation, reassignment and restoration. Enforce these invariants
transactionally so concurrent operations cannot produce an active child
linked to a deleted parent. Restore parents first. If a parent or referenced
product has been deleted, do not erase historical references; display them
only where authorized.

For SQLite, configure a persistent volume, foreign keys and concurrency
handling; define a single-instance write strategy and use suitable
transactions or constraints for invariant checks. Test lock contention and
backups, including the WAL if used; use a consistent backup mechanism rather
than copying a live database file. Store files and database backups securely
and test restoration. Select encryption at rest appropriate to the deployment.
SQLite does not provide database-generated rowversion tokens: use and update
an application-managed concurrency token and verify it on writes. Cover EF
bulk operations, direct SQL and scheduler writes in the same authorization,
concurrency and atomic audit guarantees. Test migrations against SQLite's
provider limitations before deployment.

For **every application-owned table**, inventory its write paths and preserve
the full history of inserted, updated, soft-deleted and restored data. For
ordinary mutable records, append an audit entry containing table/entity key,
operation, timestamp, actor or named system principal, correlation ID,
transaction identifier, schema version, and complete before/after values
where applicable. Capture all fields, including deletions and administrative
changes; protect history according to the sensitivity of the source data.
Use a database transaction to commit the business change and its audit entry
together; fail the change if the audit write fails. Cover bulk writes,
background jobs, integrations and administrative paths, not just UI actions.
Design database permissions and tests to prevent bypassing the audited write
path. Backups and recovery must preserve both records and history.

Audit entries are append-only and their initial inserts are their own immutable
history; never recursively audit audit inserts. Likewise, an append-only
operational error occurrence is its own creation history and must not be
updated or deleted through the application. Other application tables,
including role grants, variables and the error-code catalog, use the normal
audit mechanism. Treat framework/provider-owned tables separately: inventory
and document their writes, ownership and equivalent history mechanism before
adopting them; do not silently exclude application data. Audit snapshots must
not capture authentication secrets, tokens or passwords; keep such secrets
out of application-owned tables entirely. Restrict audit read access, encrypt
sensitive data appropriately, and define retention and legal-erasure policy
before production. No permanent deletion is offered by the application;
any future legally required erasure needs an explicit approved design.

## Application variables and configuration

The application-variable table stores a unique stable key, type, value,
description, validation rules or a reference to them, and concurrency
metadata. Provide typed access through an injected service, validate on write
and read, and fail explicitly for missing or invalid required values. Manage
variables only with the appropriate permission, and audit changes. If cached,
invalidate across instances after a commit. These values are not secrets.

Use standard .NET configuration providers for deployment configuration:
committed `appsettings` may contain safe defaults only. Load developer secrets
using .NET user secrets; use a deployment-managed secret provider in other
environments. Never commit secrets, log them, or copy them into the variables
or audit tables.

## In-memory cache

Provide a self-contained application cache service behind an injectable,
replaceable contract, using a dedicated `IMemoryCache` instance initially.
The service owns entry creation, lookup, expiration, eviction and
invalidation; callers do not build keys or access the underlying cache.
Use cache-aside for explicitly chosen, measured read paths, with the
authoritative service or database as the source of truth. A miss or eviction
loads from that source. Do not cache failures as successful values, and do
not make application correctness depend on cache contents or persistence.

Define typed, centrally registered key builders and dependency scopes rather
than scattered strings. For example, a product-by-ID key uses a fixed
application namespace, schema version, module, operation and normalized stable
ID; a query key also includes its validated filters, paging and other
result-affecting parameters. Encode components unambiguously, bound their
size, never put secrets or raw unbounded user input in keys, and avoid key
collisions. Group keys by module and relevant record or collection so the
service can clear a specific key, an affected scope or the entire cache
without requiring callers to enumerate framework internals. Document each
cached value's key, dependencies and invalidation rules as it is introduced.

For create, update, soft-delete and restore, invalidate every affected
record and query scope after the database and audit commit; a rollback must
not publish a change. Coordinate cache reads, in-flight fills and the commit
and invalidation boundary so a fill based on old data cannot repopulate an
invalidated key or be returned as current after a completed write. On
invalidation failure after commit, bypass or discard the affected cache,
log the failure and use authoritative reads until safe; do not report the
committed business write as rolled back. Apply the same rules to background
and integration writes. Recheck current server authorization on every
operation, including cache hits; do not cache authorization decisions, edit
leases or other security-critical state as authority. Cached query results
must be partitioned by all relevant authorized scopes or filtered against
current permissions before return, without leaking data or counts.

Set absolute expiration and a measured size limit on this dedicated cache;
optionally add sliding expiration without extending the absolute limit.
Assign a consistent, bounded size estimate to every entry. Bound key and
scope bookkeeping too, avoid caching image bytes or large unbounded query
results, and measure memory, hit rate and evictions without logging sensitive
keys. Do not impose a size limit on the framework's shared DI cache, where
other consumers may not supply entry sizes. Configure deployment-specific
limits via configuration and tune them against the VPS memory budget.
See Microsoft's
[in-memory caching guidance](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory).

The initial cache is local to one process and is empty after restart. Before
running multiple hosts or enabling another writer, provide reliable
cross-instance invalidation or transactionally maintained source versions
that are checked before serving affected entries; recover from missed
notifications and fence stale in-flight fills. Otherwise disable caching for
those data paths and read from the authority. SignalR delivery, sticky sessions
or a TTL alone do not ensure immediate consistency or permission revocation.

## Business data and workflow

The hierarchy is `Client -> JobGroup -> Job`; a group has a user-provided
name and belongs to exactly one client, and a job belongs to exactly one
group. Use stable IDs and foreign keys, not names, as references. A job's
dates, time entries, products, mood boards, financial documents, procurement
and receipts remain associated with that job. Record a move between groups as
an audited update;
define additional safeguards before allowing a move across clients when
issued documents, email links or other sensitive history exist.
Soft-deleting a job hides its dependent records from normal access without
deleting or modifying them; authorized history and restoration retain the
associations. Restore requires an active parent and must not silently change
any child record.

## Exclusive editing and wait queues

Jobs, quotes and invoices are independently lockable existing records. An
authorized user sees them read-only until selecting **Modify**; creating a new
record does not require a lock on a record that does not yet exist. Editing a
job includes its own fields, job line items, milestones and procurement
details; these edits require that job's lock in addition to the applicable
module permissions. Quotes and invoices each have their own lock even when
associated with the same job. A quote/invoice operation that also changes a
job or another locked document must hold all affected locks. Timer state
transitions and time-entry operations remain independently authorized and
concurrency-checked; they do not hold a job lock for the duration of a timer
unless the operation also changes a job field or other job-locked data.

On Modify, atomically acquire the free record's lease, or add the request to
that record's first-in-first-out queue if it is held or already reserved for
someone else. Prevent duplicate queue entries for the same editing session and
record. A queue is per record, not per job hierarchy. Users can cancel their
request and continue read-only; a queued user cannot edit or save. Show an
appropriate busy/queued state without exposing another user's identity unless
authorized. Grant and queue operations require authentication, current Alter
permission for every applicable module, and a valid active record; Delete
permission is still required for delete or restore operations.

When a lease is released or expires, atomically reserve the next eligible
queued request for a limited claim period and send that user an in-app
notification that it is their turn. While reserved, no other session may
acquire the record. The selected user must claim the turn, reauthorize, and
load the latest record version before entering edit mode; do not silently
apply an old draft. If the reservation expires, is cancelled or the user is
no longer authorized, mark that request accordingly and advance to the next.
The user can rejoin at the end of the queue. Persist the in-app notification
and queue state so navigation, disconnects and reconnects do not lose them;
show current status after reconnect. A notification alone is not a lock or
authorization, and an expired notification must not imply the record is still
reserved. Select lease and claim durations from measured needs and store
changeable policy as non-secret application variables.

Represent leases and queue entries in durable transactional storage using
stable record IDs, request/lease IDs, actor and editing-session identity,
state, ordering, UTC timestamps, expiry and a version/fencing value. Enforce
one active lease or reservation per record with database constraints and
atomic transactions, including under simultaneous requests and multiple
instances. Re-evaluate expired leases and reservations on requests as well as
through background cleanup, so recovery does not depend on a timer firing.
Lease and queue identifiers are not substitutes for authentication
or authorization. Validate the authenticated actor, active lease, editing
session and fencing value inside the same transaction as each protected write;
also check the record's optimistic concurrency token. Serialize competing
lease transitions and protected writes through commit, so a lease cannot be
handed to another editor while a prior editor's authorized write is still in
progress. Reject expired, revoked, superseded or otherwise stale leases even
if a client still displays edit mode. Do not rely on client clocks or SignalR
delivery for correctness.

Use the server's injected `TimeProvider` for expiry and allow the active
editor to renew a lease while editing. On save, cancel or confirmed navigation
away, release it and advance the queue; an explicit continue-editing action
after a save must acquire a new turn if others are waiting. On lost connection
or browser closure, do not immediately assume the user has left: stop
renewing and let the lease expire. If a lease is lost, disable saving, notify
the editor, preserve any local unsaved draft where safely possible, and
require reacquisition and review against the latest version before saving.
Never silently overwrite another user's changes. A queued or reserved request
can be cancelled; a disconnected or offline user who does not claim their
reservation within the claim period loses that turn.

Enforce locks and permissions in application services for UI, API, background
and integration writes, not just in the button. A background operation or
integration that changes a protected record must obtain a lease under its
named, authorized identity and respect the queue; it must not bypass an
editor's lease. For operations requiring multiple locks, acquire them in a
stable order or fail without partially applying the operation, and release
any acquired leases on failure. Delete, restore, issue and other state-changing
operations on these records require the appropriate lock and permission;
issued financial documents remain immutable. A parent deletion must not
silently invalidate a child document's active editor or reservation: block
the operation until conflicting child locks/reservations are resolved. Cancel
and notify affected waiters if the target or its parent becomes unavailable.
Full Access is not an implicit lock bypass.

Audit every persisted lease and queue state change, including renewals,
claims, expiry, cancellations and releases, atomically as required for all
application-owned mutable records. Do not put credentials or bearer secrets
in the audit trail. Deliver minimal authorized SignalR notifications only
after commit, backed by durable in-app state; recheck current permissions on
delivery and recover missed notifications on reconnect. Record conflicts
and expiry without leaking record data to unauthorized users. Measure the
cost of renewals and audit history when choosing the lease policy.

### Monetary and date/time representation and formatting

Store every monetary amount, including prices, costs, discounts, invoice
totals, payments, forecasts and monetary hourly rates, as a signed integer
number of cents with an explicit currency. Use a checked 64-bit integer and
validate ranges and signs according to each business rule. A cent represents
one hundredth of a currency unit; initially support only currencies with
exactly two minor-unit decimal places. Reject unsupported currency precision
explicitly rather than silently misrepresenting it. Do not mix currencies in
totals without an explicitly approved conversion policy.

Perform financial calculations in integer cents, using checked integer
arithmetic and exact integer ratios for non-monetary quantities, durations,
percentages, tax and any approved exchange rates. Apply the agreed rounding
rule at the specified calculation boundary and preserve its inputs. Do not use
binary floating point or convert monetary amounts to decimal for calculations.
Sub-cent inputs require an explicit business rule before support; otherwise
reject them with an actionable validation error rather than silently rounding.
Convert cents to decimal currency units only at a presentation boundary for
human-readable formatting. Define and test API serialization and bounds so
integer cents remain exact in browser JavaScript as well as .NET.

Provide a replaceable, shared formatting service for all user-facing money,
dates and times, including UI, reports and generated quotes/invoices. It
accepts explicit currency, locale and relevant time zones; it formats cents
as readable currency and formats date/time values consistently without
changing stored data. Use the same service or a shared implementation of its
contract on all presentation paths; do not scatter formatting rules through
components or document templates. Parse and validate user-entered values
through a consistent locale-aware input path, and revalidate on the server.
Keep API and persistence contracts machine-readable: integer cents plus
currency, UTC timestamps and explicit zone identifiers, not formatted strings.

Store every date-and-time as a UTC instant, including audit entries, timer
intervals, financial events and integration timestamps. Require each job to
have a validated time-zone identifier; use a stable IANA identifier and
account for platform mapping if needed. Resolve a user's local date/time input
using the chosen job or browser zone and daylight-saving rules before storing
UTC. Reject invalid or ambiguous local inputs until the user resolves them.
Do not silently infer the job zone from the server. If the job zone changes,
audit the change and explicitly review future scheduled times; never silently
move stored instants or rewrite history.

For each job-related date-and-time shown in the front end, use the formatting
service to display the job-zone time and its zone label. Detect the browser
time zone using browser settings, not physical geolocation; when its zone
differs from the job's, also show the browser-local time and its label,
including an offset where needed to disambiguate daylight-saving transitions.
If the browser zone cannot be determined, show the job time and indicate that
local time is unavailable; allow an explicit user-selected zone. Keep those
zones distinct even if their current UTC offsets happen to match.

Date-only values such as a work date or date-only milestone are calendar
dates, not datetimes: retain and display the job's calendar date without
inventing a time or converting it to UTC. The formatter must handle both
date-only and instant values. Document generation uses the same service with
the job zone and an explicit locale; preserve issued document snapshots.

The global product catalog is independent of jobs. A product can have
supplier/source links, approved images, descriptions and multiple variants,
with cost and RRP observations and their currency and observation date.
Never assume a supplier price remains current. Use a typed, versioned,
validated collection of product-specific attribute key/value pairs for
arbitrary details (for example color, dimensions, material and finish).
Preserve display labels and, where applicable, units. Users can mark a
per-product set of attributes as variant identifiers; identify variants by a
stable internal ID plus those values, with supplier SKU or another external
identifier where available. Treat source URLs and missing SKUs as insufficient
proof of uniqueness. Warn of possible duplicates; do not silently merge
products. Validate attribute size, type and duplicate keys, and index common
search fields when measurements justify it.

A job line item references a product/variant where available and stores a
job-specific snapshot of source, description, selected identifying attributes,
requirements, quantity, unit, cost, RRP, client price, currency and notes.
Allow manual, non-catalog items. Make each edit explicit; updates to the global
catalog do not silently overwrite job requirements or issued documents.
Store the selected image/reference and source provenance where available.
Expose a line-item timeline from the audit history, including changes to
flexible attributes and related procurement entries, subject to all
applicable module permissions. Multiple instances of the same product can be
separate job line items.

### Mood boards and canvases

A job can have multiple mood boards, and each mood board can have multiple
ordered canvases. Allow authorized users to create, name, reorder, preview,
soft-delete and restore canvases. Each canvas has a stable ID, dimensions,
branding, ordered elements and a concurrency token. Apply the firm's branding
to new canvases, including its approved name, logo and colors. Store changeable
non-secret branding values as application variables and approved logo files
in private storage. Preserve the branding version used by each canvas; changes
to the firm's branding do not silently rewrite existing canvases. Provide an
explicit way to apply updated branding and audit that change.

The editor lets users select permitted images from resources associated with
the current job, upload images from outside the system, and add and edit text.
An uploaded image need not refer to a catalog product and is not automatically
added to the global catalog. Allow users to position and resize images and
text, and change their stacking order on each canvas. Persist each element's
type, stable ID, content or private image-version reference, position, size
and stacking order, along with source provenance where available. Persist
layout in canvas coordinates so its arrangement remains consistent across
screen sizes and after reload. Removing an element from a canvas must not
delete its source product, job item or image.

Authorize board, canvas and file operations using current job, mood-board and
applicable source-module permissions; selecting a job resource does not grant
access to its underlying catalog data. Use the same checks for previews,
downloads and any generated output, including after permissions change.
Keep uploaded and referenced images in the private-file store with immutable
versioned references; validate actual image content, allowed types, size and
decoded dimensions, and scan or quarantine as described for private files.
Do not expose public file URLs, execute active image content or fetch arbitrary
URLs as a substitute for the requested upload. Confirm the right to use
supplier and uploaded images. Preserve provenance and referenced versions so
catalog updates or deletions do not silently change a saved canvas; do not
expose a historical image when its source is no longer authorized.

Board and canvas changes, including layout, text, uploads, branding, ordering,
deletion and restoration, require server validation, optimistic concurrency
checks and atomic audit history. These edits are independent of the job's
exclusive edit lease unless they also change job-locked data. On a conflict,
retain the user's unsaved draft and require review against the latest version
rather than silently overwriting another edit. Use the shared dirty-state,
loading and failure behavior. Provide keyboard and touch interactions and an
accessible element-list alternative to drag-and-drop. Support previewing a
canvas and saving or downloading it or the complete board as a PDF, as
specified below. Do not imply Canva synchronization or unrestricted image
rights.

### Job dates and milestones

Each job can have multiple user-named dates or milestones. Give each a stable
ID, label, optional description, category, status, date and concurrency token.
Provide common categories for start, completion, installation, payment
installment and milestone, while allowing user-defined categories without a
schema change. Preserve the category's stable identifier and historical label;
do not infer financial behavior from a custom label. Distinguish planned and
actual dates, permit either when the other is unknown, and retain their
history. Changes, deletion and restoration are authorized and audited.

Use calendar dates for date-only milestones. Where a time is needed, retain a
UTC instant and the job's time zone; do not silently shift date-only values by
converting them to UTC. Validate dates and any applicable ordering rules on
the server without assuming every milestone must follow a universal sequence.
Record explicit relationships to the applicable time entry, procurement,
invoice or payment installment when relevant; avoid duplicating a second
financial event merely because a linked milestone exists.

### Job time tracking

Support three auditable ways to record time against an active job: a
start/pause/resume/stop timer, a manually entered duration with a work date,
and a manually entered start/end range. Entries have a stable ID, job, worker,
optional description, billable status, source type, recorded duration,
work date or interval, concurrency token and normal lifecycle metadata.
For a timer or range, preserve the actual intervals; a flat duration has no
invented start/end times. Paused intervals are excluded from worked duration.
Use precise integer duration units and preserve raw time separately from any
rounded billable quantity.

The server is authoritative for state transitions and uses injected
`TimeProvider`; the UI may display an estimate but must reconcile with the
server after reload or reconnect. Persist running/paused state and each
transition atomically with its audit entry. A running timer continues across
browser closure, disconnection and server restart until explicitly paused or
stopped; show its current state and allow correction if it was left running.
Handle retries idempotently and use concurrency checks so duplicate clicks,
multiple tabs or users cannot create duplicate transitions or lose time.

Allow a running or paused timer to be cancelled. Cancellation ends tracking
without producing a billable completed entry; retain its intervals, actor,
time and cancellation in the audit history, rather than physically deleting
it. Require time-tracking Alter permission for cancellation. A cancelled
timer cannot be resumed or selected for billing; a fresh timer may be started.
Time records, including completed entries, can be soft-deleted and restored
with time-tracking Delete permission. Deleting a running or paused timer
atomically cancels tracking and soft-deletes the record; restoring it does not
restart it. Deletion must not silently change issued documents or release
existing invoice allocations. Handle corrections to billed time through the
agreed invoice correction/void process.

Provide **Stop at** on a running timer and **Resume at** on a paused timer
when the user forgot to act at the intended time. Accept an explicitly
chosen past or current time, never a future time. Stop at closes the current
worked interval; Resume at opens a worked interval from the chosen time and
continues running. Validate the time against the applicable start, pause and
other transitions so intervals are ordered and durations are positive; do
not silently change earlier transitions. Normal stop of a paused timer adds
no time since its pause. If an earlier transition itself needs correction,
use the reviewed entry-correction workflow. Preview the resulting intervals
and worked duration to date, then require confirmation before saving. Record
both the effective time and actual operation time, actor and before/after
state in the atomic audit history. Recalculate durations server-side, check
the entry version and require a new review if it has changed.

Before starting a timer on a different job, check for the same worker's
currently running timers. If any exist, show an accessible modal listing the
authorized affected jobs and offer **Stop existing timers and start**, **Keep
them running and start**, or **Cancel**. The first choice stops all those
running timers at the same server-authoritative start instant and starts the
new timer; paused timers are unaffected. The second explicitly permits
concurrent running timers and their resulting overlaps. Cancel or dismiss
leaves all timers unchanged. Do not expose details of jobs the actor cannot
read; if an affected timer cannot be shown or modified with current
permissions, explain the restriction without disclosing protected data and
do not perform an unauthorized stop.

Enforce this choice in the application service for UI, API and other entry
points, not only in the modal. Bind confirmation to the worker, target job
and IDs and versions of the observed running timers. Atomically recheck
permissions, state and the complete running set before applying the choice;
if it changed, return a conflict and require review again. Serialize
competing changes to that worker's running set, including starts and resumes,
so concurrent requests cannot bypass the choice. Stop-and-start transitions
and their audit records commit together; failure leaves all timers unchanged.
Idempotent retries must not stop or start additional timers.

Validate positive durations and start-before-end ranges, rejecting ambiguous
or nonexistent local times unless the user resolves them. Use UTC instants for
intervals with a specified time zone for display and calendar allocation;
account for daylight-saving changes. Warn about overlapping intervals for the
same worker and require explicit resolution before saving: the user may
correct the overlap or explicitly confirm that concurrent time is intended.
The keep-running choice is explicit approval of the overlap it creates; review
new or changed overlaps during later corrections or resumes. Show overlapping
entries when selecting time for billing and require explicit review and
resolution; enforce that the same worker's overlapping interval is not
invoiced twice, even across different entries or jobs. A flat duration has no
interval to compare. Permissions cover viewing, creating, editing,
soft-deleting and restoring time entries, including another worker's time.
Changes retain an attributable audit trail. Do not silently modify issued
invoice snapshots if an entry is edited or deleted.

### Financial forecasting

Provide a job and cross-job view for a user-selected date range, subject to
job, forecasting and all underlying data permissions. Show estimated and
actual costs, revenue and profit separately, with drill-down to authorized
source records and a clear statement of the date basis, status, currency,
time zone and assumptions. Use an inclusive local start date and exclusive
next-day end boundary in the explicitly selected reporting zone for range
queries over UTC instants; allocate date-only milestones by their job calendar
date. Require a consistent currency or an explicit,
auditable conversion policy; never add different currencies silently.

Source inputs can include explicit forecast amounts assigned to job dates or
milestones, procurement and payment records, quote/invoice snapshots, labor
estimates and recorded time. Link each amount to a distinct underlying
economic item and label it as planned or actual and cost or revenue. Moving
a milestone changes only explicitly linked planned timing; it does not
silently rewrite an actual payment, issued invoice or other historical event.
Do not count a quote and its resulting invoice, a forecast and its realized
amount, or a procurement record and its payment twice for the same measure.
Show missing dates, rates or amounts as incomplete rather than zero, and
allow a forecast to be revised with audit history.

Define with the firm whether each view uses expected payment, invoice,
work, delivery, completion or another date; which actuals count as recognized
revenue or expense; tax treatment; allocations across periods; and how
planned amounts are matched to actuals. Label cash flow separately from
revenue/profit and do not claim accounting-grade profit until those rules are
agreed. Keep underlying inputs, selected criteria and calculation version
traceable so a saved forecast can be reproduced. Use integer cents and
explicit currency, calculate on the server, paginate source detail, and
schedule expensive report generation.

For similar-job estimates, allow users to select comparable completed or
historical jobs and review their recorded time and costs. Present the sample
and assumptions, including incomplete entries and changes in rates, and
store the resulting estimate separately from actual recorded time. Enforce
permissions on every job used in an aggregate; never leak unauthorized
job or worker details through totals.

### Financial documents and procurement

Quotes and invoices belong to a job but do not require a catalog product,
job line item or recorded time. A document can contain user-entered lines,
selected job items or time, or any combination; it can also contain only a
manually entered total and no lines. Manual lines have an editable description,
quantity, client-facing unit price and applicable tax inputs, with an optional
internal cost. They need no source record and do not implicitly create or
modify a catalog product or job line item. Store document type, number, dates,
currency and the data needed to reproduce its totals.

For line-based documents, the server calculates line amounts, subtotal,
discounts, credits, applicable tax and total using integer cents and the
agreed calculation rules. Allow explicit line- or document-level discounts and
credits on quotes and invoices, record their descriptions and amounts or
rates, and display their effect separately. Do not silently interpret an
adjustment as a payment or create a separate legal credit note. Determine
permitted adjustment types, signs, limits, tax treatment and the required
process for correcting issued documents with the firm before production.
Do not double-count adjustments and payments in forecasts.

Allow a user to enter a total directly, either for a total-only document or
as an explicit override of a line-based calculated total. A total-only
document has no calculated subtotal to compare against: label its total as
manual and do not invent lines, costs, tax breakdowns or a calculated total.
For an override, retain and display the server-calculated total, manual total
and their difference, with a clear manual-override indicator. Define whether
the entered amount includes tax and collect any tax information required
before issuance; never silently infer a tax breakdown from a manual total.
The server validates manual values, adjustments and document completeness
as well as calculated values. Internal costs are separate from client-facing
amounts; unknown cost remains unknown, not zero.

Before applying a change to an existing calculated total or overriding the
applied total of a line-based document, show the current and proposed
calculated and applied totals and require explicit confirmation. This includes
changes to lines, quantities, prices, discounts, credits, tax inputs, the
manual-total override or a return to the calculated total when they would
change either amount. An initial calculation with no previous total needs no
warning.
Users may preview edits before confirming; cancellation leaves the applied
values and total unchanged. The UI need not prompt on every keystroke, but
must obtain confirmation before applying the total-changing edit. The server
recalculates the proposed result and enforces confirmation for persisted
total-changing operations, binding it to the draft version and the old and
new amounts. If concurrent changes make confirmation stale, return a conflict
and require review again; do not trust a client-supplied calculated total.
For total-only documents, identify the amount as manually entered rather than
claiming a calculated-total comparison exists.

When a line-based document has an active manual override, edits must not
silently overwrite it. Recalculate and show the new underlying calculated
amount and difference, and require confirmation if either the calculated
total or the applied total changes. Permit an explicit return to calculated
mode. Audit manual values, adjustments, confirmations and before/after totals
atomically with the draft changes. Preserve calculation mode, source lines,
adjustments, calculated and applied totals, any override and tax information
where applicable in the approved issued snapshot and quote-to-invoice
conversion.

Define line-versus-document rounding, tax inclusion, tax rates, currency
conversion, numbering, legal content and due dates with the firm before
issuing documents; do not infer these rules or assume RRP is the selling price.
Calculate and display our cost separately and do not include it in
client-facing output unless explicitly authorized and intended. Manual-only
documents use the same permission, audit, validation, concurrency, review,
template and immutable-issuance requirements as other documents.

Allow a user to select completed, reviewed, billable time entries (stopped
timers, flat durations or ranges) for a quote or invoice draft. Snapshot the
source entry IDs and versions, description, raw and billable duration, agreed
client-facing hourly rate, currency, rounding, tax and resulting line amount.
Snapshot internal labor cost separately where
known, with its own restricted access; do not equate revenue with profit.
Have the server validate and calculate all quantities and line-based totals,
while honoring the explicit manual-total mode above. Agree
minimum increments, rounding, rate selection, partial billing and corrections
before production. Use transactional billing allocations and concurrency
checks to prevent the same time from being invoiced twice; flag already
allocated time and enforce the agreed correction/void process. A quote does
not itself consume an invoice allocation. Changing source entries or rates
after a draft snapshot requires explicit refresh and review; changes after
issuance never rewrite the issued document.

An editable draft has a concurrency token. On explicit user approval, issue
an immutable version containing the exact item, price, tax, template version
and generated-file snapshots. Subsequent product or job changes must not
alter issued documents; corrections create an auditable new version or void
record according to agreed business and legal rules, never silently overwrite
or physically delete an issued document. Converting a quote to an invoice
copies an explicit quote snapshot into an invoice draft and preserves its
link to the quote; it does not mark the quote accepted or create payment
implicitly. Assign document numbers atomically to prevent duplicates.
Preview from a specific draft and template version; issuance must confirm
those versions have not changed since review, and retain the exact approved
PDF and associated generated Word output. Fail with a conflict and require a
new review if they changed.

Track procurement against each job line item, including supplier, order
reference, status, quantities, expected/actual dates, shipping and delivery,
installation notes and payment installments. Keep payment records and
outstanding amounts distinct from invoice status; support partial orders,
deliveries and payments. User-confirmed state changes and receipt links are
audited. Define the firm's exact statuses and financial reconciliation rules
before implementing automation. Do not place orders, send emails or process
payments automatically without a separate approved feature.

### Word templates

The supplied local invoice `.docx` is a private design reference. Inspection
found 36 main-body tables, 53 inline/anchored drawings and 52 embedded media
files, and no existing `{{...}}` placeholders. It is not yet a ready-to-use
template. Do not commit the sample, its client data or embedded images
without permission. Obtain permission and prepare a sanitized, versioned
template preserving the intended layout, including images, table structure,
and quote/invoice-specific labels and sections.

Define a documented placeholder schema, for example `{{Document.Number}}`,
`{{Client.Name}}`, `{{Job.Name}}`, `{{Document.Total}}` and repeated
`{{Line.Description}}`, `{{Line.Quantity}}`, `{{Line.UnitPrice}}`,
`{{Line.Total}}` rows. Define template-supported repeat and conditional
sections, including item images, before choosing a renderer. Placeholders
are examples, not evidence that the supplied file contains them. A renderer
must handle Word's split text runs and formatting, repeating tables,
pagination, embedded images and missing optional values; plain string
replacement of the `.docx` bytes is not sufficient. Validate templates and
required placeholders and fail clearly on unsupported structures or
unresolved placeholders. Render from an approved snapshot without evaluating
arbitrary code, macros, external links or untrusted template expressions.
Store the immutable template version and generated file reference with each
issued document; test generated files in Word-compatible readers and confirm
the invoice and quote layouts with the firm.

### PDF generation and storage

Provide a user-initiated **Save as PDF** and authorized download for quotes,
invoices and mood boards. Allow a mood-board export to include one selected
canvas or the complete board in its saved order, with one canvas per page.
Preserve the visible branding, image placement, layering, text and aspect
ratio; scale to fit the chosen page without unexpected cropping. Use saved
canvas versions, not an uncommitted browser draft. Show users which saved
version is being exported.

Generate quote and invoice PDFs from the populated Word document for the
same draft and template version. Allow a draft PDF to be saved and downloaded
without issuing the document; identify it as a draft in the application.
On issuance, the user reviews and approves the exact PDF, and the system
retains that PDF and the corresponding generated Word output with the issued
snapshot. A later source or renderer change must not replace the approved
files; a new version follows the existing issuance/correction process.
Do not silently regenerate an issued PDF on download.

Persist each requested PDF as an immutable versioned private file associated
with its job and source document or board. Record its source versions,
selected canvases and order where relevant, renderer version and requesting
actor; audit persisted metadata and lifecycle changes. Provide a download
that saves the PDF to the user's device. Recheck job, document or mood-board
and applicable source permissions when requesting, generating and
downloading, including after revocation. A PDF must not become a way to
retrieve a source image or document the user can no longer access.

Use replaceable rendering interfaces and locally run open-source components;
no paid PDF service is required. Candidates to validate before implementation:

- The [Open XML SDK](https://github.com/dotnet/Open-XML-SDK) is MIT-licensed
  and can manipulate Word packages for the template pipeline; it is not
  itself a Word-to-PDF layout renderer. The required placeholder, repeating
  section and image behavior still needs implementation and testing.
- [LibreOffice](https://www.libreoffice.org/licenses/) is open source, with
  MPL 2.0 and other bundled licenses. Its headless Writer process can
  [export PDFs](https://help.libreoffice.org/latest/en-US/text/shared/guide/pdf_params.html)
  from populated documents. It is an executable, not a .NET library; run it
  behind a replaceable adapter. Verify the approved template, fonts, images,
  page breaks and output against Word because layouts can differ.
- [Playwright for .NET](https://github.com/microsoft/playwright-dotnet/blob/main/LICENSE)
  is MIT-licensed and can use Chromium to
  [print a rendered page as PDF](https://playwright.dev/dotnet/docs/api/class-page#page-pdf).
  It is a candidate for rendering the mood-board canvases from controlled
  HTML; Playwright's driver and browser have their own licenses and runtime
  dependencies. Confirm page sizing, print colors, fonts and visual fidelity.

Pin supported versions at implementation time and review the license and
redistribution requirements of all bundled components and fonts. Bundle the
required renderer, browser and licensed fonts in the deployment or its
isolated worker; do not download them during a production request. Local
generation avoids a per-document service fee but consumes VPS CPU, memory
and storage. Measure representative multi-page documents and image-heavy
boards against the planned VPS before enabling production use.

Run heavy rendering through the durable background-work abstraction, with
bounded concurrency, resource and time limits, status and actionable failure
reporting. Render in an isolated, non-root environment with no production
secrets or unrestricted network access. Supply only authorized, validated
assets and a per-job temporary workspace; prevent execution of macros or
active content and fetching external links. Validate output before marking
the job successful. Handle failed or interrupted file writes recoverably,
without deleting files referenced by history, and make retries idempotent.
Preserve saved PDFs in encrypted backups and test restoration.

### Image library, processing and storage

Provide a firm-wide image library with upload, paginated browsing and
selection, plus filtering and sorting by associated client or job. Images can
be global or explicitly associated with clients and jobs; a job association
also supports finding the image through its client. Allow authorized users to
link an image to multiple applicable records without duplicating its bytes.
An image is not automatically a product. Mood boards can select authorized
library images or upload an image directly; a direct board upload does not
silently publish it to the global library or create a catalog product.

Use stable image and immutable file-version IDs with metadata including
dimensions, format, source, uploader, timestamps, optional description,
associations and concurrency token. Require image-library permissions plus
the applicable client, job, product, receipt or mood-board permissions for
each operation. Global browsing and filters must not reveal inaccessible
images, linked records, counts or associations. Enforce access again on
every image and thumbnail read, including when referenced by a board or
export. Audit uploads, metadata and association changes, soft deletion and
restoration; normal lists exclude deleted images. Block deletion while an
active reference requires the image, rather than silently breaking that
reference. Never physically delete referenced versions or history.

Expose image processing behind a replaceable service, integrated with the
shared private-file store rather than writing directly from UI or business
code. Use [SkiaSharp](https://github.com/mono/SkiaSharp), an
[MIT-licensed](https://github.com/mono/SkiaSharp/blob/main/LICENSE.txt)
open-source .NET library, as a candidate adapter for resizing and thumbnails.
Verify the selected release and native assets on Windows and the production
Linux architecture, review bundled licenses and security updates, and test
the actual container image. No paid processing service is required.

For all persisted standalone image assets, including library uploads,
product and mood-board images, branding and receipt photos, first apply image
orientation and then fit within a 1920-by-1080-pixel landscape box or a
1080-by-1920-pixel portrait box. Preserve aspect ratio, do not crop or upscale,
and ensure rounding never exceeds either bound. Store only the processed
image, not an oversized original; generate smaller aspect-preserving
thumbnails from that stored version. A square image fits within 1080 by 1080.
For example, 4000 by 3000 becomes 1440 by 1080, and 3000 by 4000 becomes
1080 by 1440. A 640-by-480 image remains 640 by 480.
Temporary upload/quarantine data is not a retained image asset and must be
restricted and removed after processing or failure. Existing issued documents
and their embedded images remain immutable; this limit governs image assets,
not rewriting already issued PDF or Word files.

Validate actual file content and supported static formats, byte size,
dimensions and decoded-pixel limits before full processing; reject malformed,
unsupported or animated inputs with an actionable error. Bound processing
time, memory and concurrency, and use scheduled work for heavy processing.
Scan or quarantine untrusted uploads before use; strip unnecessary metadata
such as location information, preserve intended orientation and transparency
where supported, and do not serve active content. Warn before upload that
larger originals will not be retained, particularly for receipts, and allow
the user to review the processed result for legibility.

The private-file store exposes authorized image read and write operations
through application services and uses opaque generated keys, not filenames
or client-supplied paths. Store processed bytes and thumbnails on local disk
initially; keep database metadata, versions and audit entries separate from
bytes. Stage and validate files before committing their references and audit
records, and make interrupted writes, retries and cleanup recoverable. A
failed database commit must not leave a successful-looking image, and cleanup
may remove only verified unreferenced temporary files, never historical
versions. Report disk, decoding and processing failures explicitly.

Configure an absolute storage root using deployment configuration and resolve
paths with platform-aware APIs. On Windows development hosts, use a
user-writable directory outside build output, such as a Lavari directory
under the user's local application data; allow an explicit override. In
production, require a configured persistent directory mounted into the
container from protected host storage, writable by the non-root application
identity. Never use the container writable layer, web root or deployment
artifacts for persistent images. Protect against path traversal and symlink
escape, restrict file permissions, and do not expose public file URLs.
Fail clearly if the root is unavailable or unwritable. Include image files,
versions and matching metadata in encrypted off-server backups and test
restoration; redeployment and rebuilds must not erase them.

### Receipt capture and private files

Offer a device camera capture control and file upload with a fallback where
camera access is unavailable; browser camera access requires the appropriate
permission and secure context. Store receipt metadata, job and optional line
item or expense reference, uploader, capture date and an immutable versioned
private-file reference. Image captures use the same 1080p processing and
storage policy as other image assets; receipt permissions still apply. Users
enter or correct the merchant, date, amount and currency; any future OCR/LLM
extraction is optional and user-reviewed.

Use an injected private-file store, initially possibly a local persistent
volume to reduce cost, with a path to object storage. Generate opaque storage
keys, authorize every upload/download against the job and receipt module,
enforce file type and size limits and validate actual content. Quarantine or
scan uploads before use; mitigate malicious content, metadata leakage and
image processing risks. Do not expose public URLs. Preserve soft-deleted
receipt/file references and audit metadata; make file writes and record
creation recoverable if either side fails, including orphan cleanup rules
that never erase referenced history. Include files in encrypted backups and
test restoration.

## Supplier capture and LLM-assisted extraction

The browser companion operates only on a user-initiated capture of rendered
content from the active supplier tab, including the supplier's signed-in view.
Capture a bounded sanitized HTML fragment from rendered DOM; exclude hidden
content, scripts, styles, forms, controls, field values and unrelated browser
storage. Strip all attributes except safe image `src` and `alt` references.
Request only necessary browser permissions, show which data will be captured
and sent, and require confirmation. Do not collect or transmit cookies,
credentials, authorization headers, browser storage, hidden account
information or unrelated page content. Avoid
server-side login, session impersonation, bypassing access controls or
unrestricted URL fetching. The authenticated supplier-capture flow may make
one user-initiated GET to the entered product URL only when it is a public
HTTP(S) destination on its default port. Do not send cookies or credentials,
use a proxy, or follow redirects. Resolve and pin the destination address at
connection time, rejecting private, loopback, link-local, reserved and
otherwise non-public addresses. Bound the request to 12 seconds and 1 MB of
HTML, extract at most 16,000 characters of text and image references, and
never fetch linked resources. Require user confirmation before retrieval and
separate provider consent before sending content to an LLM. Support manual
text entry if the site blocks access or requires a logged-in session. Respect
supplier terms, access restrictions, copyright and privacy. Treat captured
HTML and links as untrusted; sanitize content and validate links.

Map the selected page to a versioned draft product configuration with source,
candidate identifiers, attributes, price/currency, images, requirements and
provenance. User-entered requirements remain independent of URL parameters.
Show missing, conflicting, stale or uncertain fields; the user reviews and
explicitly saves the draft. No extracted value, including pricing or variant,
is authoritative without review. If capture or model extraction fails, keep
manual entry available and report an actionable error.

Gemini supplier extraction always requests the standard product fields
supported by the catalog, including name, description, explicitly stated RRP
and currency, SKU, identifiers, product attributes and image references. The
user's optional prompt supplements those fields and populates a separate
collection of custom fields, such as a selected size or finish, only when
supported by the captured page. Every attribute carries a stable key, display
label, string value, type (`text`, `number`, `url` or ISO date) and optional
unit. Keep these values in the versioned JSON-backed attribute collections;
review them in editable typed inputs and preserve them when adding a line item.

For local supplier drafts, acceptance must create the editable global product
and transition the draft to Approved atomically, guarded by the draft's
concurrency token and the user's Supplier Capture and Product Catalog grants.
Retain supplier-provided image URLs as inert source references only unless a
user explicitly imports an image through the shared image pipeline after
rights review; do not fetch or render these untrusted image URLs. Adding an accepted
catalog product to a job is a separate, authorized job-line-item operation
under the job's exclusive edit lease, preserving job-specific values.

The LLM is optional behind a provider-neutral interface; no model, paid
account or provider is assumed. Send only the minimum user-approved,
sanitized content needed for extraction, under an approved provider's data
handling terms. Supplier and client content must not be sent to a third party
without appropriate authorization. Treat page text and model output as
untrusted: prevent prompt injection from granting tools or changing
instructions, validate structured output against the schema, and never let
the model invoke business actions directly. Supplier-page extraction is an
asynchronous provider call awaited directly by the authenticated API request;
it does not use scheduled work. Keep input/output bounds, provider timeouts,
quotas, usage telemetry and configurable spend limits.

Create a named, non-human LLM service principal with only the registered
permissions needed to create extraction drafts, not Full Access. Record the
initiating user, service identity, job/product context, provider/model
identifier, timestamps, status and correlation ID for each request and audit
any resulting data changes. Recheck both the initiating user's current
permissions and the service principal's permissions when executing and when
saving results. Do not log prompts, credentials, supplier sessions or
sensitive page contents by default. Keep provider credentials in a secret
provider, not audit history.

The supplier-extraction implementation persists and audits conservative
UTC-daily reservations, per-user request counts and reported token usage.
The request fingerprint, prompt, captured text and extracted model response
are excluded from audit snapshots. Missing or invalid budget variables deny
reservations; once execution starts, the full estimate remains charged even
if the outcome is uncertain. Supplier extraction records separate provider
consent, current-user and named-service-principal permission rechecks, bounded
provider input/output and review-only results. The provider call runs directly
in the API request after reservation and authorization; the reservation's full
estimate remains charged once execution starts, even if the outcome is
uncertain. Configuration alone does not authorize or send a provider request.
Initial AI reservation limits are seeded from deployment configuration only
when their application variables do not already exist; subsequent administrator
values remain authoritative.
The reservation ledger does not itself
enforce actual provider charges; a reviewed model-specific price cap and
operational recovery remain required.

### AI capabilities and future workflows

Separate the provider transport from task-specific services. A provider-neutral
text capability can support structured supplier suggestions and conversational
answers; an image-editing capability uses a separately selected compatible
model. Model identifiers are deployment configuration, not proof of a model's
capabilities or suitability. Keep each capability replaceable and fail
explicitly when the configured provider does not support it. Gemini's text and
image adapters are registered behind those capabilities and resolve only with
enabled configuration and credentials. Supplier extraction is invoked
directly by its authorized API workflow; it does not depend on the scheduled
work system. Configuration alone does not send requests.

Before enabling chat grounded in application data, define and implement
permission-aware retrieval for each included database record, document and
image. Recheck access at retrieval and response time; never expose protected
content through search results, citations, links, conversation history or
cached context. Send only the minimum content explicitly approved for the
provider, treat retrieved content and model responses as untrusted, and
provide clear provenance and uncertainty. AI-generated job templates are
drafts only: users review and explicitly save them through the ordinary
authorized, validated and audited job workflow. Do not give the model direct
business-action tools.

An internal grounded-chat generator accepts bounded caller-supplied excerpts
and maps model citation numbers to caller-owned record references. It is not
an authorized retrieval or execution workflow, and a valid citation number
does not prove an answer is correct. The caller must enforce consent and
permissions at retrieval and recheck every supplied source, including uncited
sources, at response time. Render model text as untrusted plain text before
the generator can be connected to the application.
An internal job-draft generator can suggest bounded name, description and
notes fields from approved text. It neither selects business records nor
creates a job and is not connected to an application workflow.

Image edits require explicit consent to send the selected image, confirmation
of rights, and current access to the source and all relevant associations.
Process generated bytes through the shared image validation and 1080p storage
pipeline, then persist an auditable derived-image relationship to the
unchanged original. Do not overwrite an existing image, board or issued
snapshot; let the user review and explicitly select the derived image.
Require the same queue, attribution, permissions, quotas, usage telemetry and
spend controls as other AI requests before enabling these workflows.
The image table can persist an audited, immutable source relationship for a
future derived image; it does not by itself authorize, generate, store or
expose AI edits.

## Gmail job association

Use the Gmail API behind a replaceable email adapter. Each user explicitly
connects their own account through OAuth, selects a Gmail label and links it
to a job; record the account identity, stable label ID, job, consent and
connection state without storing tokens in application records or audit.
Labels are not folders: messages can have multiple labels, and newly added
messages in a thread do not automatically inherit a thread's prior labels.
Treat association as label membership on individual messages; do not assume
an entire thread is always included.

Initially show linked messages to the connected user only, requiring both job
Read and email Read and a valid connection; do not use one user's OAuth
authorization to expose mailbox content to other staff. Any shared mailbox
feature needs explicit owner consent, access policy and a separate design.
Start read-only and do not send, modify, label, trash or delete messages.
Fetch message content on demand rather than persisting bodies by default;
show sanitized content and do not automatically load remote images or unsafe
HTML. If retaining metadata or attachments is later required, document its
scope, consent, audit, retention and storage first.

Request the smallest Gmail OAuth scope that supports the approved UI: the
metadata scope suffices only for headers and labels; message bodies require a
scope such as `gmail.readonly`. Gmail scopes can cover an entire mailbox, not
just the chosen label, so enforce label restriction in Lavari and explain the
actual consent. Google's
[scope documentation](https://developers.google.com/workspace/gmail/api/auth/scopes)
classifies both scopes as restricted; verification and a security assessment
may be required, especially when restricted-scope data is transmitted or
stored on servers. Verify current requirements and cost before enabling
production access.

Protect OAuth credentials and refresh tokens in a dedicated secret provider,
never application variables, logs or audit snapshots. Bind OAuth state to the
initiating user, protect callbacks against CSRF, restrict redirect URIs, and
support disconnect/revocation and expired consent. Disconnect revokes access
where supported and removes the secret from the secret provider, while
soft-deleting and auditing the application connection record. This secret
removal is not deletion of application records or audit history. On
revocation, unavailable labels, quota errors or access changes, stop access
and show an actionable state; never silently expose cached content. Use
pagination and bounded polling or approved notifications, with a refresh
and resynchronization path for missed changes. No Gmail messages are deleted
by Lavari.

## API, error codes and observability

API services throw meaningful exceptions for failed operations. A centralized
HTTP boundary maps expected validation, authorization, not-found and
concurrency failures to appropriate status codes and a consistent safe error
response; unexpected exceptions become a generic 500. Include a stable error
code, non-sensitive user message, actionable possible cause or next step,
and unique correlation/support reference. Field validation errors identify
the relevant field without echoing secrets. Do not send technical messages,
stack traces, connection details, request payloads or sensitive values to the
client.

Maintain an administratively controlled, audited error-code catalog with a
stable code, technical definition, safe user-facing message and possible
causes or next steps. Record each occurrence separately with its code,
timestamp, correlation ID, sanitized technical detail and restricted stack
trace; a stack trace belongs to an occurrence, not the shared code
definition. Access to occurrences requires the operational-errors permission
and restricted operational storage. Redact secrets and personal data before
logging. If database logging fails, use a secured independent operational log
and still return the error; never convert the failure to success or expose the
logging failure's internals. Do not create unbounded or recursive logging
attempts. Use structured logs, traces and metrics, with correlation propagated
through API calls, scheduled work and integrations.

## UI, components and realtime

Build and own the Razor component library rather than wrapping or depending on
Material Web. Use the
[Material 3 foundations](https://m3.material.io/foundations) as design
guidance for color, typography, layout, interaction and accessibility, and
create application-owned design tokens and components. Use semantic HTML,
accessible labels, keyboard navigation, focus management and appropriate
ARIA; test with assistive technology and supported browsers. Package assets
locally and review licenses for any fonts, icons or third-party assets. Do not
reproduce protected branding or assets without permission. Deliver components
incrementally as needed rather than attempting to recreate all of Material 3.

Use `prefers-color-scheme` and respond to preference changes for light and
dark tokens; maintain contrast and accessibility in both themes. Account
appearance settings offer System, Light and Dark modes and static Muted Plum,
Sage & Eucalyptus, Terracotta & Blush, Slate & Nordic Dusk, and Warm Stone &
Charcoal palettes with coordinated primary, secondary,
tertiary and surface roles in both themes. Store settings per user, default to
System and Purple, and require the registered account
appearance Read/Alter permission for self-service reads/updates. Persist and
audit updates with optimistic concurrency; never use this grant to edit
another user's profile or permissions. Preview is local until saved, and
cancel or a failed save restores the saved appearance. Existing roles do not
automatically gain the new grant; an authorized administrator must assign it.
Shared form
components validate all required inputs before submission and the API repeats
validation. Define dirty as a value comparison with the initial form snapshot:
for create, the baseline is the initialized draft; for edit, it is the last
successfully loaded or saved server snapshot. Create/update is disabled when
clean, invalid, unauthorized or loading; updates to locked records also
require the current editor lease. Reverting changes returns to clean;
after a successful save, replace the baseline with the server response.
Failed saves keep the draft dirty. Warn before discarding dirty state. Show
loading progress, prevent duplicate submissions and disable affected controls
until the request finishes, including error paths.

SignalR connections and subscriptions are authenticated and authorized per
module, with least-privilege groups managed by the server. Publish only after
the transaction commits; send minimal invalidation notifications rather than
unnecessary record data. On reconnect, permissions and group membership are
re-evaluated and clients refresh from the API. Notifications are best-effort,
not the source of truth; clients must recover from missed events. Plan a
backplane or equivalent before scaling to multiple instances.

## Background processing and integrations

Run processing-heavy work as scheduled background jobs, not on request or
SignalR threads. Requests may enqueue durable work and return a status
reference. Use TickerQ as the requested scheduler behind an application-owned
scheduler/queue abstraction and a replaceable worker, subject to the
dependency-policy blocker below. Jobs execute with a named system identity
and explicit permissions. Persist job
state, use idempotency, bounded retries with backoff, cancellation, failure
visibility and an operational recovery path. Coordinate execution with a
durable lease or equivalent so multiple web instances do not run the same
scheduled occurrence concurrently. Hosting the worker in the web process is
initially acceptable, provided restarts and scaling preserve these
guarantees; it can later move to a separate host.

TickerQ adoption is **blocked pending a compatible open-licensed solution**:
its [current license](https://github.com/Arcenox-co/TickerQ/blob/main/LICENSING.md)
describes current releases as source-available under a commercial agreement,
not open source; a free community entitlement has conditions and does not
satisfy the open-license policy. As of 25 November 2026, the latest stable
[TickerQ 11.5.1](https://www.nuget.org/packages/TickerQ/11.5.1) is in that
commercial line. The prior
[TickerQ 10.4.0](https://www.nuget.org/packages/TickerQ/10.4.0) is
MIT/Apache-2.0 licensed, but its
[EF Core integration](https://www.nuget.org/packages/TickerQ.EntityFrameworkCore/10.4.0)
requires EF Core relational versions below 11, conflicting with EF Core 11
in the same dependency graph. Its compatibility and durable SQLite behavior
with any other arrangement have not been verified. Do not silently use the
commercial release, downgrade EF Core, mix incompatible packages or claim
this blocker is resolved. Re-evaluate the exact artifacts and policy before
implementation; a change to the requested scheduler or dependency policy
requires an explicit decision.

An isolated evaluation on 26 November 2026 rebuilt TickerQ's 10.4.0 source
at commit `c6ed1e7daa90ab3f4c65b40319a153126a910093`, targeting .NET 11
with EF Core 11 and FlexLabs Upsert 11; all 74 upstream EF persistence tests
passed against their SQLite test setup. This is not the published 10.4.0
package and does not establish production compatibility, restart recovery,
concurrent multi-instance behavior, migration or audit guarantees. The
evaluation also reported a vulnerability in its build-only SourceLink
dependency. A maintained, license-reviewed fork and its complete dependency
graph would need review and representative integration tests before adoption;
the scheduler blocker is not resolved by this experiment. The published
`TickerQ.Utilities` 10.4.0 package also constrains several
`Microsoft.Extensions` dependencies to versions below 11, so rebuilding
only the EF persistence adapter is not enough to establish a compatible
single-process dependency graph.

An additional isolated SQLite test against that rebuilt source found that
the [timed-out-job fallback](https://github.com/Arcenox-co/TickerQ/blob/c6ed1e7daa90ab3f4c65b40319a153126a910093/src/TickerQ.EntityFrameworkCore/Infrastructure/BasePersistenceProvider.cs)
can acquire an overdue queued job still owned by
another node. A scratch-only change restricting acquisition to unowned idle
jobs passed the upstream persistence suite and focused ownership tests, but
has not established restart recovery or production-safe coordination. In a
separate simulated restart, invoking the [startup cleanup](https://github.com/Arcenox-co/TickerQ/blob/c6ed1e7daa90ab3f4c65b40319a153126a910093/src/TickerQ.EntityFrameworkCore/DependencyInjection/ServiceExtension.cs)
with a new node
identifier left the previous node's in-progress job locked. Recovery needs
verified node liveness or an equivalent durable lease; the fork must not be
adopted on the strength of those tests alone.

Two additional isolated tests of the unmodified 10.4.0 persistence code on
.NET 10 demonstrated that a stale time-job or cron worker can mark another
node's in-progress claim complete and release its lock. The completion updates
filter by job ID without an ownership or fencing check. A recovery solution
must fence stale completion and business writes, not just reclaim abandoned
jobs. The isolated test dependency graph also reported a high-severity
advisory for `SQLitePCLRaw.lib.e_sqlite3` 2.1.11; this finding is about that
test graph, not a determination that Lavari uses that version.

When an eligible adapter is selected, verify durable scheduler storage,
SQLite concurrency, restart recovery, migrations and audit coverage. Define
ownership and retention for scheduler-managed tables separately from
application-owned job records, preserving the latter's append-only history
and never allowing vendor cleanup to delete application records. Keep any
scheduler dashboard disabled unless its endpoints, permissions and safe
logging satisfy the same application security requirements.

External systems use adapters behind application-owned interfaces. Define
authentication, timeouts, retry/idempotency, data mapping, auditing and
observability for each integration before enabling it. Keep provider failure
diagnostics to bounded status/classification metadata; never log credentials
or submitted supplier content. Never assume a remote operation participates
in the local database transaction; use an outbox or another explicit
consistency mechanism when needed.

## Validation and delivery

Add focused unit tests as features are built, using fake time and substitute
services. Integration tests must exercise the real API authorization and
transactional database behavior. Establish benchmarks with BenchmarkDotNet
for representative hot paths and payloads, recording runtime, workload,
latency and allocations; agree numerical budgets after measuring a baseline.
Run benchmarks on controlled infrastructure and compare meaningful
regressions, not noisy single runs. Include accessibility, security and
end-to-end tests for critical UI flows.

Minimum acceptance coverage:

1. For each registered module, exercise all four access levels against
   matching UI actions, API operations and applicable hub subscriptions;
   test multiple-role union, wildcard Full Access, unknown permissions,
   escalation prevention and immediate revocation.
2. Verify create, update, delete and restore each persist complete history
   atomically, including line-item attributes and procurement; failed audit
   writes roll back changes, unauthorized access cannot read history, and no
   physical deletion occurs. Test bulk/background paths and concurrent
   client/group deletion and child creation.
3. Verify server and client validation, create/edit dirty baselines,
   revert-to-clean, duplicate-submit prevention, failure recovery and
   navigation warnings.
4. Verify error codes, sanitized user responses, restricted per-occurrence
   stack traces and the independent logging-failure path.
5. Verify light/dark preference changes, keyboard and screen-reader behavior,
   authorized realtime delivery, reconnect and permission revocation.
6. Exercise scheduled-job retries, idempotency, restart recovery, durable
   storage, audit and multi-instance coordination with the selected eligible
   scheduler adapter; benchmark representative list, update and serialization
   workloads. Verify dependency licenses and EF Core/SQLite compatibility.
7. Verify a separate API host can reuse the endpoint library and application
   services without bypassing the same authorization or validation rules
   before any production split.
8. Test flexible product identifiers, job-specific requirements not present
   in URLs, duplicate warnings, catalog independence and historical
   snapshots. Test multiple branded canvases per mood board, resource image
   selection, external uploads, text, layout and ordering across save/reload.
   Verify branding changes and explicit reapplication, keyboard access,
   upload validation, source permissions and rights, independent catalog
   data, soft deletion/restoration, audit and concurrent editing conflicts.
9. Test integer-cent totals and agreed rounding/tax rules, quote-to-invoice
   conversion, concurrent numbering and immutability of issued documents.
   Validate representative sanitized Word templates, repeated image/line
   sections, split placeholders, missing data and quote/invoice output.
10. Test supplier capture consent, logged-in sessions without credential
    collection, malicious page/model output, permission revocation and
    user review; measure and bound LLM usage. Cover manual fallback.
11. Test camera/upload fallback, authorization, file validation, partial
    failures and backup restoration. Test Gmail OAuth state, read-only
    scopes, label membership, account isolation, revocation and resync.
12. Measure representative workflows against the proposed VPS resource
    envelope; verify resource and backup alerts, HTTPS/SignalR behind the
    proxy and recovery of SQLite plus private files from off-server backups.
13. Exercise the pipeline on a non-production target: untrusted pull requests
    cannot deploy or access secrets; releases deploy by verified digest and
    concurrent deploys are serialized. Verify persistent data and Data
    Protection keys survive replacement, and test health failures, compatible
    rollback and migration recovery without losing later writes.
14. Verify custom dates, planned/actual milestones, date-only and time-zone
    boundaries, permissions, audit and linked installment changes without
    altering actual financial history.
15. Test timer start/pause/resume/stop, cancellation, soft-delete and restore,
    and Stop at/Resume at with valid, out-of-order, future and daylight-saving
    inputs. Verify previews, durations, audit, permissions, restart,
    reconnect and idempotent retries. Test the multiple-timer modal's stop,
    keep and cancel choices, several running timers, paused timers, concurrent
    starts and stale confirmations. Test flat and range entries, intentional
    and corrected overlaps, billing review, cross-entry duplicate allocation,
    rounding and immutable issued time snapshots.
16. Test forecasts over date boundaries, missing inputs, currencies, planned
    versus actual matching, quote-to-invoice and payment double-counting,
    restricted source records, similar-job comparisons and reproducibility.
17. Verify exact cent storage and calculations, overflow, sub-cent rejection,
    unsupported currencies, locale formatting and precision across the API
    and browser. Verify UTC persistence, job/browser zone labels, differing
    zones with identical offsets, unavailable browser zones, daylight-saving
    transitions, date-only values and shared UI/document formatting.
18. Exercise manual-only lines and total-only quotes/invoices, mixed source
    lines, discounts and credits, tax completeness, overrides and a return to
    calculated mode. Verify the warning shows old/new totals, cancellation
    leaves applied values intact, stale confirmation conflicts, changes are
    audited, and approved snapshots and forecasts retain the correct amounts.
19. Test simultaneous Modify requests, FIFO handoff, claim and expiry,
    cancellation, disconnected users, permission revocation and missed
    notifications. Verify writes from UI, API and background work reject
    stale or absent leases; test release after save/navigation, dirty-draft
    recovery, cross-record operations, audited lease transitions and parent
    deletion with an active child editor.
20. Verify saved and downloaded quote and invoice PDFs match the reviewed
    Word-based output, including representative fonts, images, totals and
    page breaks. Test draft versus issued snapshots, stale approvals and
    download after source changes. Verify single-canvas and whole-board PDF
    pages, order, branding and layout. Test authorization and revocation,
    renderer failures, interrupted writes, retries, audit, backup restoration
    and representative VPS resource consumption.
21. Test typed cache key uniqueness and scoped clearing, bounded expiration
    and eviction, commit versus rollback invalidation, stale concurrent fills,
    cache failure and authorization revocation. Verify multiple writers cannot
    serve stale data without the required cross-instance coordination.
22. Test image library upload, global/client/job filtering, permissions,
    association changes, audit, soft deletion and active-reference protection.
    Verify exact 1080p bounds and aspect ratios for landscape, portrait,
    square, panorama and already-small images, plus orientation, thumbnails,
      malformed/oversized input and receipt legibility. Check the specified
      4000-by-3000, 3000-by-4000 and 640-by-480 results and verify originals
      above the limit are not retained. Test Windows and Linux storage, restart
      and redeployment persistence, failed writes, recovery and backup
      restoration, and measure processing and memory use.
