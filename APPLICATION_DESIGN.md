# Lavari application design

Status: initial design, not an implemented application.

## Purpose and assumptions

Lavari will help an interior design firm source products, build mood boards,
track job dates and time, prepare quotes and invoices, forecast finances, and
track procurement for its clients' jobs.
Today, product links and requirements are recorded in Word, mood boards are
assembled in Canva, and prices are calculated manually. The aim is to bring
these activities together while preserving the original sources and history.

The initial design assumes one firm and its authenticated staff, rather than
a multi-tenant SaaS product. External client access is not yet specified.
Specific business rules and retention periods must be agreed before the
relevant features are implemented.

## Initial capabilities

### Clients, job groups and jobs

- Authorized staff can create, find, view and update client records.
- Each client has user-named job groups, like folders; each group contains
  jobs. Authorized staff can create, find, view and update groups and jobs.
  Lists support server-side filtering and pagination.
- Each job has an explicit time zone. Store date-and-time values in UTC and
  use the job's zone for its schedule. When the user's browser is in a
  different zone, show both job and browser-local times with clear labels.
- Each job holds its dates and milestones, time entries, sourced line items,
  mood boards, quotes, invoices, procurement records, receipts and email
  associations.
- Staff with delete permission can soft-delete records; authorized staff can
  view deleted records and restore them. Normal lists exclude deleted records.
- A group must reference an active client, and a job an active group belonging
  to that client. Block deletion of a client or group while it has active
  children rather than silently deleting them. Existing deleted records
  retain their historical associations.
- Users with the relevant read permission can see the history of a record;
  in particular, every job line item has a history showing what changed,
  when, and by whom. History must not expose data the user could not
  otherwise access. A more restrictive audit permission can be added if
  required.

### Exclusive editing

- Jobs, quotes and invoices open read-only. An authorized user selects
  **Modify** to acquire exclusive editing access to that record.
- If another user is editing, the requester joins a queue and can continue
  viewing the record. When their turn arrives, an in-app notification invites
  them to open the latest version and begin editing.
- Save, cancel or leave editing to release the lock. Locks expire if an
  editor disconnects or stops responding, so work is not blocked forever.
  The application must protect unsaved changes and explain when a lock is lost.
- Queueing, lock status and notifications do not grant permissions: every
  operation remains server-authorized and audited.

### Job dates, time and forecasting

- Record named, flexible job dates and milestones, such as a start date,
  expected or actual completion, installation, or a payment installment.
  Different jobs can have different dates and multiple milestones. Preserve
  changes and distinguish planned dates from actual dates.
- Track time against a job using a start/pause/resume/stop timer, a manual
  duration entry, or a start/end range. A running or paused timer can be
  cancelled without recording billable time, and time records can be
  soft-deleted by authorized users. Users can review and correct entries,
  and authorized users can see their history.
- If a timer was not stopped or resumed on time, choose **Stop at** or
  **Resume at** and supply the actual time. Show the resulting worked
  duration to date for review and retain the original history.
- When starting a timer on another job while one or more timers are running,
  show a modal offering to stop the existing running timers and start the new
  one, keep them running alongside the new one, or cancel the request. Make
  overlapping time clear and do not automatically bill it twice.
- Select tracked time for a quote or invoice, with explicit billable time and
  rates. Keep internal labor costs distinct from the amount charged to a
  client. Once a document is issued, later time or rate changes do not alter
  it.
- Forecast and review costs, revenue and profit for a chosen date range using
  job dates, planned and actual amounts, and labor estimates or recorded time.
  Clearly distinguish estimates from actuals and show the assumptions and
  source records. Historical time on comparable jobs can inform a new job's
  estimate, without assuming every job follows the same schedule or rate.

### Sourcing and mood boards

- Maintain a global, reusable catalog of furniture and other products. A
  product can have supplier links, images, descriptions, product-specific
  attributes, identifiers, our purchase cost and recommended retail price
  (RRP). Products differ: do not impose a single fixed set of attributes.
- Add a product to a job as a separate line item, with its own quantity,
  requirements, selected variant, costs, selling price and notes. For example,
  record a black picture frame in a particular size even if the supplier URL
  does not encode those selections. Job-specific edits do not silently change
  the global catalog or other jobs.
- Allow users to define which attributes distinguish a particular product or
  variant. Preserve the relevant identifiers and selected values on each job
  item; do not assume one product type's attributes apply to every other type.
- Support capturing information from a supplier page, including a
  user-authenticated supplier session. A proposed browser companion extracts
  selected page content, and an optional LLM suggests structured details.
  The designer reviews and corrects the result before saving. Manual entry
  remains available for unsupported sites, cost control and accuracy.
- Provide an image library for uploading and finding reusable images across
  the firm, with filters for associated clients and jobs. Access to linked
  images and associations remains permission-controlled.
- Resize stored image assets to at most 1080p while preserving their aspect
  ratio, and provide thumbnails for browsing. Larger uploaded originals are
  not retained; warn users that this can affect fine detail in receipts.
- Build mood boards for a job, each containing multiple company-branded
  canvases. Select images from the job's catalog of resources, upload
  additional images from outside the system, and add text. Arrange and resize
  these elements on each canvas and review the result. Uploading an image for
  a board does not automatically add a product to the global catalog.
- Save and download a canvas or the complete mood board as a PDF, preserving
  the branding, images, text and canvas order.
- Initially treat Canva as an existing manual workflow, not an assumed
  integration. Respect supplier and uploaded image rights.

### Quotes, invoices and procurement

- Prepare quotes and invoices for a job using selected job items, manually
  entered lines, or a mixture. A document can also consist of a manually
  entered total without lines. Show quantities, unit prices, discounts, credits,
  taxes and totals transparently; distinguish our cost from client-facing
  prices. A quote can be used as the basis for an invoice.
- Allow users to apply discounts or credits and override a calculated total.
  Warn and obtain confirmation before a change alters an existing calculated
  total or overrides the applied total, showing the previous and proposed
  amounts. Make manual totals and any difference from the calculated amount
  clear.
- Use a Word `.docx` template with documented placeholders to generate both
  quotes and invoices. The supplied invoice is a design reference, not yet
  a reusable placeholder template. Review and approve generated documents
  before issuing them; retain issued versions and their source snapshots.
  Save and download quotes and invoices as PDFs, including the approved
  issued version.
- Track purchasing and procurement per job item: supplier, order details,
  amounts paid and outstanding, shipping and delivery, and installation
  details. Track payment installments where relevant. These are records of
  work; automatic purchasing or payment processing is not assumed.
- Capture paper receipts using a device camera or upload an existing image,
  and attach them to a job and optionally a specific item or expense. Staff
  can review the image and enter or correct its details.

### Email and AI integrations

- Let a user connect their Gmail account with explicit consent and link a
  Gmail label to a job to follow relevant correspondence. Gmail uses labels
  rather than exclusive folders. The connected user can view the linked
  messages subject to their Gmail and Lavari permissions; account sharing
  requires separate explicit consent and policy.
- LLM extraction is an optional, replaceable integration with its own named
  service identity, least-privilege permissions and audit history. Treat
  suggestions as untrusted drafts, not authoritative facts or automatic
  purchases, and protect supplier sessions and customer data.
- Future AI assistance can answer questions using explicitly authorized
  application information, suggest links and prepare job drafts for review.
  Future image editing can create a separately retained AI-derived image
  without replacing its source. Both require explicit consent, appropriate
  data rights and the same permission, audit and review protections.

The conceptual model includes Client, JobGroup, Job, JobDate, TimeEntry,
Product, ProductVariant, JobLineItem, MoodBoard, MoodBoardCanvas, Quote,
Invoice, Procurement, Receipt, ImageAsset, EmailConnection, User, Role,
Permission, ApplicationVariable and AuditEntry. Detailed fields and business
workflows remain to be agreed with the firm.

### Access and accountability

Every application screen, operation, API endpoint and realtime subscription
requires an authenticated identity and the appropriate module permission.
There is no anonymous application access; authentication-provider callbacks
and health endpoints are infrastructure exceptions, not business features,
and must be restricted as appropriate.

Permissions are assigned to roles per module using **None, Read, Alter and
Delete** access levels. Read permits viewing; Alter includes Read and permits
creation and editing; Delete includes Alter and permits soft deletion and
restoration. A user with multiple roles receives the highest granted level for
each module. Missing, unknown or revoked permissions deny access. Fine-grained
modules can be added without changing this model.

An explicitly assigned Full Access role is available for trusted developers
and administrators. It grants all levels for all current and future modules,
but does not bypass authentication, validation, audit or soft deletion.
Provision it securely, restrict and audit its assignment, and never give it to
all users by default. Managing users, roles and permissions is itself a
permission-protected administrative module.

Each persisted business or administrative change has a durable, attributable
history, including changes to permissions and application variables. History
cannot be edited through the application. Operational access, including
system and integration activity, is attributable to a named actor.

### User experience

- Build a reusable, in-house Razor component library using
  [Material 3 foundations](https://m3.material.io/foundations) for its visual
  and interaction design, including accessible keyboard, focus, validation
  and feedback behavior. Do not depend on Material Web components.
- Format monetary values, dates and times through a shared formatting service.
  Store money as integer cents and convert it to decimal only for readable
  presentation. Date-only milestones remain calendar dates, not fabricated
  UTC timestamps.
- Follow the device's light or dark preference, including changes while the
  application is open. Do not rely on color alone to convey state.
- Account appearance offers System (default), Light and Dark modes and static
  Purple, Green, Coral, Blue and Neutral palettes with coordinated primary,
  secondary, tertiary and surface roles in both themes. Save preferences per
  user, preview them in selectable cards before saving, and apply them across
  devices. Require an explicit account appearance grant; audit and
  concurrency-check updates.
- Validate required inputs before a request and display actionable,
  field-specific feedback; the server validates independently.
- Create and update buttons are disabled until a form differs from its
  baseline, is valid and is not loading. Updates to a locked record also
  require the user's active edit lock. Reverting all changes makes the form
  clean again. Unsaved changes receive a navigation warning.
- Disable affected controls while a request is in flight, show progress, and
  restore an actionable state after success or failure.
- Show clear, non-sensitive errors with a possible cause or next step and a
  support reference. Do not show stack traces or private technical details.
- Use realtime notifications to refresh affected views when permitted changes
  occur, without leaking data to unauthorized users.

### Configuration and integrations

Non-secret business values that can change, such as contact email addresses
and links, are managed as typed, validated application variables with
permissions and audit history. Deployment settings use application
configuration. Secrets are kept in a secret provider, using .NET user secrets
for local development, and never in the variables table or source control.

Supplier capture, the LLM, document rendering, file storage and Gmail use
explicit, replaceable integration interfaces and adapters. Only connect a
provider or send data when its contract, permissions, consent, costs,
security and failure behavior are understood.

Use a self-contained in-memory cache service for suitable data, with managed
keys, bounded memory and invalidation on changes. It is an optimization, not
the source of truth or a replacement for authorization. Use a replaceable
image processor and private storage service for the image library, receipts
and other image assets.

Generate PDFs locally using open-source components rather than requiring a
paid service. Validate Word conversion fidelity, security and resource usage
on the planned VPS before choosing the final renderer.

Target .NET 11 and Entity Framework Core with SQLite. Use third-party
libraries only when needed and only under open-source or open licenses.
TickerQ is the requested scheduler, subject to resolving the license and
compatibility conflict described in the technical specification.

## Architecture

```text
                  Aspire AppHost (orchestration / local development)
                                      |
Browser -- HTTPS --> Blazor web host + API endpoints + SignalR
                        |             |
                 Razor UI library   API class library
                        |             |
                   shared contracts / application services
                                      |
                          domain and service abstractions
                                      |
                     adapters --> relational database / private file storage
                                      |
                TickerQ scheduler / Gmail / LLM / document rendering
                                      ^
                     optional browser companion for supplier capture
```

The initial deployment runs the Blazor runtime and API in one web host. The
API is a separate class library and the UI component library is a separate
Razor class library. Application services and domain rules do not depend on
the web host. The browser-facing UI calls the API contract even when both
endpoints share a process, so authentication, authorization and validation
remain identical when the API later runs as a separate service. Aspire
orchestrates the host and development dependencies; it does not replace
application security or persistence.

A future split adds an API host around the existing API library, moves
deployment and routing, and configures cross-origin, authentication,
anti-forgery and realtime connectivity explicitly. It must not duplicate
business rules or weaken permissions. A separate worker can similarly host
scheduled work when scale requires it.

## Infrastructure direction

Self-hosting is not currently available. The preferred starting point is an
OVHcloud VPS-1 in Sydney, subject to availability and final pricing: 2 vCores,
4 GB RAM and 40 GB NVMe storage. Run the initial Linux web host, API,
background work, SQLite database and private files on that single server.
The planned user count is one initially. Point an admin subdomain of the
existing GoDaddy-managed domain to the server and serve it over HTTPS; the
exact hostname is to be confirmed.

Package the combined web/API application as one Docker image and run it with
Docker Compose on the VPS. GitHub Actions will build and test releases, publish
versioned images and run a manually initiated production deployment. Keep the
SQLite database, private files and application keys outside the container, with
restricted access and independent backups. Single-server deployments may
briefly interrupt service; validate health and keep a safe rollback path.
Aspire remains the development orchestrator rather than a required public
production service. On Windows development machines, use a configured local
storage directory that is separate from build output and survives rebuilds.

The VPS is self-managed and a single point of failure, so include security
updates, monitoring and encrypted off-server backups with tested restoration.
The 40 GB must also accommodate the operating system, database, audit history,
images, documents and working space. Monitor development and representative
workloads, then choose or increase the server size based on actual CPU,
memory, storage and response times rather than assuming this plan is enough.

Larger OVHcloud plans, AWS Lightsail and other providers remain alternatives
if capacity, cost or operational requirements change. SQLite with Entity
Framework Core is the initial database; MySQL is a possible future migration
when measured usage or operational needs justify a server database. No hosting
or migration has been provisioned. See the
[technical specification](TECHNICAL_SPEC.md) for options, assumptions and
operational requirements.

## Non-functional priorities

- **Security:** authenticated access, deny-by-default authorization,
  least-privilege roles, secure configuration, privacy-aware logging, and
  server-side enforcement.
- **Accountability:** complete and durable change history, soft deletion,
  concurrency checks and recoverable operations.
- **Performance:** measure allocations, memory, payload sizes, database
  queries and latency; use lightweight immutable contracts and paged reads.
  Expensive work runs as scheduled background work rather than blocking a
  request. Targets must be established from representative workloads.
- **Maintainability:** clean boundaries, open/closed extension, mockable
  services, immutable and sealed types by default, automated tests and
  benchmarks. Avoid abstractions that compromise correctness or create
  measurable unnecessary overhead.
- **Reliability:** observable failures, explicit retries for suitable
  background operations, and no success-shaped response when an operation
  fails.
- **Cost:** start with the simplest viable deployment, avoid mandatory paid
  integrations, measure usage and provide configurable limits before
  introducing managed services or LLM usage.

## Decisions still needed

Before deployment, select the identity provider, confirm the hostname and
provider/region availability and pricing, and define the secret provider,
backup destination and recovery objectives. Confirm database/deployment
trade-offs, supplier sites and browser support; agree product fields, tax,
pricing, time rounding, billing and forecasting rules, and document rules;
define email-sharing, privacy, retention and legal requirements; and establish
performance and cost targets. Confirm permission to use supplier images and
the Word template. Verify Aspire and package compatibility with .NET 11 and
resolve the TickerQ license and compatibility conflict before implementation.
See the [technical specification](TECHNICAL_SPEC.md) for proposed contracts
and acceptance criteria.
