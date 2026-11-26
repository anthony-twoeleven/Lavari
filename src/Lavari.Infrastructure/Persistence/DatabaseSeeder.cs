using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Contracts.Common;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lavari.Infrastructure.Persistence;

public sealed class DatabaseSeeder
{
    public const string DefaultDevSuperAdminEmail = "admin@lavari.local";
    public const string DefaultDevSuperAdminPassword = "LavariAdmin!2026";
    public const string FullAccessRoleName = "Full Access";
    public const string DesignerRoleName = "Interior Designer";
    public const string ReadOnlyRoleName = "Read Only";
    public const string LlmPrincipalEmail = "llm-extractor@system.lavari.local";

    private readonly LavariDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPrivateFileStore _fileStore;
    private readonly IIdGenerator _ids;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        LavariDbContext db,
        IPasswordHasher passwordHasher,
        IPrivateFileStore fileStore,
        IIdGenerator ids,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _fileStore = fileStore;
        _ids = ids;
        _logger = logger;
    }

    public async Task SeedAsync(
        bool seedDevSuperAdmin,
        string? superAdminEmail,
        string? superAdminPassword,
        string? templatesDirectory,
        CancellationToken ct = default)
    {
        await SeedRolesAsync(ct);
        await SeedErrorCodeCatalogAsync(ct);
        await SeedApplicationVariablesAsync(ct);
        await SeedLlmServicePrincipalAsync(ct);

        if (seedDevSuperAdmin)
        {
            var email = string.IsNullOrWhiteSpace(superAdminEmail) ? DefaultDevSuperAdminEmail : superAdminEmail.Trim();
            var password = string.IsNullOrWhiteSpace(superAdminPassword) ? DefaultDevSuperAdminPassword : superAdminPassword;
            await SeedSuperAdminAsync(email, password, ct);
        }

        if (!string.IsNullOrWhiteSpace(templatesDirectory) && Directory.Exists(templatesDirectory))
        {
            await SeedDocumentTemplatesAsync(templatesDirectory, ct);
        }
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        if (!await _db.Roles.AnyAsync(r => r.Name == FullAccessRoleName, ct))
        {
            _db.Roles.Add(new RoleEntity
            {
                Id = _ids.NewId(),
                Name = FullAccessRoleName,
                Description = "Explicit audited wildcard role granting full module permissions for administrators and developers.",
                IsFullAccess = true
            });
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.Roles.AnyAsync(r => r.Name == DesignerRoleName, ct))
        {
            var designerRole = new RoleEntity
            {
                Id = _ids.NewId(),
                Name = DesignerRoleName,
                Description = "Standard interior designer role with Alter access to clients, jobs, sourcing, mood boards, time tracking, quotes and invoices.",
                IsFullAccess = false
            };
            _db.Roles.Add(designerRole);

            var designerModules = new[]
            {
                ModuleKeys.Clients, ModuleKeys.JobGroups, ModuleKeys.Jobs, ModuleKeys.JobDates,
                ModuleKeys.TimeTracking, ModuleKeys.ProductCatalog, ModuleKeys.JobLineItems,
                ModuleKeys.ImageLibrary, ModuleKeys.MoodBoards, ModuleKeys.Quotes, ModuleKeys.Invoices,
                ModuleKeys.Procurement, ModuleKeys.Receipts, ModuleKeys.SupplierCapture,
                ModuleKeys.GmailConnections, ModuleKeys.FinancialForecasting, ModuleKeys.AccountAppearance
            };

            foreach (var mod in designerModules)
            {
                _db.RolePermissions.Add(new RolePermissionEntity
                {
                    Id = _ids.NewId(),
                    RoleId = designerRole.Id,
                    ModuleKey = mod,
                    AccessLevel = AccessLevel.Alter
                });
            }

            _db.RolePermissions.Add(new RolePermissionEntity
            {
                Id = _ids.NewId(),
                RoleId = designerRole.Id,
                ModuleKey = ModuleKeys.ApplicationVariables,
                AccessLevel = AccessLevel.Read
            });

            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.Roles.AnyAsync(r => r.Name == ReadOnlyRoleName, ct))
        {
            var readOnlyRole = new RoleEntity
            {
                Id = _ids.NewId(),
                Name = ReadOnlyRoleName,
                Description = "Read-only viewer role for core business modules.",
                IsFullAccess = false
            };
            _db.Roles.Add(readOnlyRole);

            foreach (var mod in new[] { ModuleKeys.Clients, ModuleKeys.JobGroups, ModuleKeys.Jobs, ModuleKeys.JobDates, ModuleKeys.ProductCatalog, ModuleKeys.JobLineItems, ModuleKeys.MoodBoards, ModuleKeys.Quotes, ModuleKeys.Invoices })
            {
                _db.RolePermissions.Add(new RolePermissionEntity
                {
                    Id = _ids.NewId(),
                    RoleId = readOnlyRole.Id,
                    ModuleKey = mod,
                    AccessLevel = AccessLevel.Read
                });
            }

            _db.RolePermissions.Add(new RolePermissionEntity
            {
                Id = _ids.NewId(),
                RoleId = readOnlyRole.Id,
                ModuleKey = ModuleKeys.AccountAppearance,
                AccessLevel = AccessLevel.Alter
            });

            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedSuperAdminAsync(string email, string password, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var existing = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (existing is not null)
        {
            return;
        }

        var fullAccessRole = await _db.Roles.FirstAsync(r => r.IsFullAccess, ct);
        var admin = new UserEntity
        {
            Id = _ids.NewId(),
            Email = email.Trim(),
            NormalizedEmail = normalized,
            DisplayName = "Lavari SuperAdmin",
            PasswordHash = _passwordHasher.HashPassword(password),
            IsActive = true,
            IsSystemPrincipal = false
        };
        _db.Users.Add(admin);
        _db.UserRoles.Add(new UserRoleEntity
        {
            Id = _ids.NewId(),
            UserId = admin.Id,
            RoleId = fullAccessRole.Id
        });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded SuperAdmin user '{Email}' with '{Role}' role.", admin.Email, fullAccessRole.Name);
    }

    private async Task SeedLlmServicePrincipalAsync(CancellationToken ct)
    {
        var normalized = LlmPrincipalEmail.ToUpperInvariant();
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            return;
        }

        var llmRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "LLM Extraction Service", ct);
        if (llmRole is null)
        {
            llmRole = new RoleEntity
            {
                Id = _ids.NewId(),
                Name = "LLM Extraction Service",
                Description = "Least-privilege service role for creating draft supplier product extractions only.",
                IsFullAccess = false
            };
            _db.Roles.Add(llmRole);
            _db.RolePermissions.Add(new RolePermissionEntity
            {
                Id = _ids.NewId(),
                RoleId = llmRole.Id,
                ModuleKey = ModuleKeys.SupplierCapture,
                AccessLevel = AccessLevel.Alter
            });
            _db.RolePermissions.Add(new RolePermissionEntity
            {
                Id = _ids.NewId(),
                RoleId = llmRole.Id,
                ModuleKey = ModuleKeys.LlmAdministration,
                AccessLevel = AccessLevel.Read
            });
        }

        var principal = new UserEntity
        {
            Id = _ids.NewId(),
            Email = LlmPrincipalEmail,
            NormalizedEmail = normalized,
            DisplayName = "LLM Extraction Service",
            PasswordHash = string.Empty,
            IsActive = true,
            IsSystemPrincipal = true
        };
        _db.Users.Add(principal);
        _db.UserRoles.Add(new UserRoleEntity
        {
            Id = _ids.NewId(),
            UserId = principal.Id,
            RoleId = llmRole.Id
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedApplicationVariablesAsync(CancellationToken ct)
    {
        var defaults = new (string Key, ApplicationVariableValueType Type, string Value, string Description)[]
        {
            ("Company.Name", ApplicationVariableValueType.String, "Lavari Interior Design", "Firm display name used on branding and documents."),
            ("Company.Abn", ApplicationVariableValueType.String, "00 000 000 000", "Business ABN displayed on quotes and invoices."),
            ("Company.ContactEmail", ApplicationVariableValueType.Email, "hello@lavari.com.au", "Primary firm contact email address."),
            ("Company.WebsiteUrl", ApplicationVariableValueType.Url, "https://lavari.com.au", "Public firm website link."),
            ("Company.BrandPrimaryHex", ApplicationVariableValueType.String, "#6750A4", "Primary company brand color for mood board canvases."),
            ("Company.BrandSurfaceHex", ApplicationVariableValueType.String, "#F9F7F4", "Default mood board canvas surface background color."),
            ("Company.BrandTextHex", ApplicationVariableValueType.String, "#1F1B16", "Default company brand typography color."),
            ("Finance.DefaultCurrency", ApplicationVariableValueType.String, "AUD", "Default two-decimal ISO currency code."),
            ("Finance.DefaultTaxBasisPoints", ApplicationVariableValueType.Int, "1000", "Default GST tax rate in basis points (1000 = 10%)."),
            ("Finance.DefaultTaxLabel", ApplicationVariableValueType.String, "10% GST", "Default tax line label for quotes and invoices."),
            ("Finance.DefaultDepositBasisPoints", ApplicationVariableValueType.Int, "7000", "Default deposit percentage in basis points (7000 = 70%)."),
            ("Finance.DefaultDepositLabel", ApplicationVariableValueType.String, "70% DEPOSIT REQUIRED", "Default deposit label on invoices."),
            ("Finance.PaymentDetails", ApplicationVariableValueType.String, "Configure payment details in Application Variables", "Payment instructions displayed on invoices."),
            ("Finance.PaymentTerms", ApplicationVariableValueType.String, "7 day payment terms", "Standard payment terms line."),
            ("Time.DefaultJobTimeZone", ApplicationVariableValueType.String, "Australia/Sydney", "Default IANA time zone for new jobs."),
            ("Lock.LeaseDurationSeconds", ApplicationVariableValueType.Int, "300", "Exclusive Modify edit lease duration in seconds."),
            ("Lock.ReservationClaimSeconds", ApplicationVariableValueType.Int, "120", "Queued editor turn claim reservation window in seconds.")
        };

        var existingKeys = await _db.ApplicationVariables
            .IgnoreQueryFilters()
            .Select(x => x.Key)
            .ToHashSetAsync(StringComparer.Ordinal, ct);

        var added = false;
        foreach (var (key, type, value, desc) in defaults)
        {
            if (existingKeys.Contains(key))
            {
                continue;
            }

            _db.ApplicationVariables.Add(new ApplicationVariableEntity
            {
                Id = _ids.NewId(),
                Key = key,
                ValueType = type,
                Value = value,
                Description = desc
            });
            added = true;
        }

        if (added)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedErrorCodeCatalogAsync(CancellationToken ct)
    {
        var defs = new (string Code, string Tech, string User, string Cause, int Status)[]
        {
            (ErrorCodes.ValidationFailed, "Request failed domain or schema validation.", "Some entered information is missing or invalid.", "Review the highlighted fields and try again.", 400),
            (ErrorCodes.UnsupportedCurrency, "Currency is not a supported two-decimal ISO 4217 currency.", "The selected currency is not currently supported.", "Choose a supported two-decimal currency such as AUD or USD.", 400),
            (ErrorCodes.SubCentAmountNotSupported, "Monetary input included sub-cent precision.", "Amounts must have at most two decimal places.", "Round the entered value to whole cents.", 400),
            (ErrorCodes.CurrencyMismatch, "Operation attempted to combine amounts in different currencies.", "Cannot combine amounts in different currencies.", "Ensure all selected items share the same currency.", 400),
            (ErrorCodes.ArithmeticOverflow, "Checked integer cent calculation overflowed safe bounds.", "The calculated monetary amount exceeds supported limits.", "Verify quantities and unit prices.", 400),
            (ErrorCodes.InvalidTimeZone, "Invalid or unsupported IANA time zone identifier.", "The selected time zone is not valid.", "Choose a valid IANA time zone such as Australia/Sydney.", 400),
            (ErrorCodes.AmbiguousOrInvalidLocalTime, "Local time is nonexistent or ambiguous across a DST boundary.", "The entered local time falls on a daylight-saving transition.", "Choose a valid time or specify the daylight-saving occurrence.", 400),
            (ErrorCodes.InvalidImageUpload, "Image validation failed content, format, dimension or size checks.", "The uploaded image is unsupported or exceeds size limits.", "Upload a valid JPEG, PNG or WebP image.", 400),
            (ErrorCodes.Unauthenticated, "Request lacked a valid authenticated session.", "You must sign in to continue.", "Your session may have expired. Sign in again.", 401),
            (ErrorCodes.Forbidden, "Authenticated principal lacked required module AccessLevel.", "You do not have permission to perform this action.", "Contact an administrator to update your role permissions.", 403),
            (ErrorCodes.RoleEscalationBlocked, "Administrator attempted to grant permissions exceeding their own grants.", "You cannot grant permissions you do not hold.", "Have an administrator with higher permissions make this change.", 403),
            (ErrorCodes.LastFullAccessAdminProtected, "Attempted to disable or remove the last viable Full Access administrator.", "Cannot remove or disable the last active Full Access administrator.", "Assign another active Full Access administrator first.", 409),
            (ErrorCodes.NotFound, "Entity not found or filtered out by soft-delete or authorization scope.", "The requested record was not found.", "It may have been deleted or moved.", 404),
            (ErrorCodes.ConcurrencyConflict, "Optimistic concurrency token mismatch.", "This record was modified by someone else.", "Reload the latest version and reapply your changes.", 409),
            (ErrorCodes.ActiveChildrenPreventDelete, "Blocked soft-delete of parent record with active children.", "Cannot delete this record while active child records exist.", "Delete or move child records first.", 409),
            (ErrorCodes.ActiveReferencesPreventDelete, "Blocked soft-delete of image asset still referenced by active records.", "Cannot delete this image while it is in use.", "Remove active references before deleting.", 409),
            (ErrorCodes.ParentInactive, "Parent record is deleted or inactive.", "The parent record is no longer active.", "Restore the parent record before continuing.", 409),
            (ErrorCodes.RecordImmutable, "Attempted mutation or physical deletion of immutable or append-only data.", "This record is immutable and cannot be changed.", "Create a new version or correction record instead.", 409),
            (ErrorCodes.DuplicateBillingAllocation, "Attempted to invoice an already allocated time entry or overlapping worker interval.", "This time has already been allocated to an invoice.", "Review existing allocations or void/correct the invoice first.", 409),
            (ErrorCodes.EditLockRequired, "Write attempted without holding an active exclusive Modify lease.", "Select Modify before editing this record.", "Click Modify to acquire the exclusive edit lock.", 409),
            (ErrorCodes.EditLockHeldByAnother, "Exclusive Modify lease is currently held by another editing session.", "Another user is currently editing this record.", "Click Modify to join the queue and be notified when it opens.", 409),
            (ErrorCodes.EditLockExpired, "Exclusive Modify lease expired or fencing version is stale.", "Your editing lock has expired.", "Reacquire Modify and review the latest version before saving.", 409),
            (ErrorCodes.ReservationExpired, "Queued editor claim reservation expired before claiming.", "Your editing turn expired.", "Click Modify to rejoin the queue.", 409),
            (ErrorCodes.TotalChangeConfirmationRequired, "Financial document change altered calculated or applied total without bound confirmation.", "Confirm the updated total before saving.", "Review the previous and proposed totals and confirm.", 409),
            (ErrorCodes.RunningTimersChoiceRequired, "Starting a timer on another job while running timers exist requires an explicit modal choice.", "You have another timer running.", "Choose to stop existing running timers, keep them running, or cancel.", 409),
            (ErrorCodes.TimerStateInvalid, "Requested timer state transition or StopAt/ResumeAt timestamp is invalid.", "This timer action cannot be applied to the current timer state.", "Refresh the timer and check the chosen time.", 400),
            (ErrorCodes.TemplateValidationFailed, "Word template failed placeholder or structure validation.", "The document template is invalid or incomplete.", "Check the template placeholders and try again.", 400),
            (ErrorCodes.DocumentRenderFailed, "Word or PDF renderer failed to generate document artifact.", "The document could not be rendered.", "Retry or contact support with the correlation reference.", 500),
            (ErrorCodes.StorageFailure, "Private file storage read or write operation failed.", "A file storage error occurred.", "Retry the action or contact support.", 502),
            (ErrorCodes.ExternalIntegrationUnavailable, "External integration (Gmail/LLM) is unconfigured or unavailable.", "This integration is not currently configured or reachable.", "Configure the integration credentials or use manual entry.", 502),
            (ErrorCodes.UnexpectedError, "Unhandled server exception.", "An unexpected error occurred.", "Retry the operation or share the correlation ID with support.", 500)
        };

        var existingCodes = await _db.ErrorCodeDefinitions
            .IgnoreQueryFilters()
            .Select(x => x.Code)
            .ToHashSetAsync(StringComparer.Ordinal, ct);

        var added = false;
        foreach (var (code, tech, user, cause, status) in defs)
        {
            if (existingCodes.Contains(code))
            {
                continue;
            }

            _db.ErrorCodeDefinitions.Add(new ErrorCodeDefinitionEntity
            {
                Id = _ids.NewId(),
                Code = code,
                TechnicalDefinition = tech,
                UserMessage = user,
                PossibleCause = cause,
                DefaultHttpStatus = status
            });
            added = true;
        }

        if (added)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedDocumentTemplatesAsync(string templatesDirectory, CancellationToken ct)
    {
        var templates = new (string FileName, string Name, FinancialDocumentType DocType)[]
        {
            ("Lavari-Invoice-Template.docx", "Lavari Standard Invoice", FinancialDocumentType.Invoice),
            ("Lavari-Quote-Template.docx", "Lavari Standard Quote", FinancialDocumentType.Quote)
        };

        var standardPlaceholders = JsonSerializer.Serialize(new[]
        {
            "{{Document.Title}}", "{{Document.Number}}", "{{Document.Date}}", "{{Document.Currency}}",
            "{{Document.Subtotal}}", "{{Document.TaxLabel}}", "{{Document.TaxAmount}}", "{{Document.Total}}",
            "{{Document.DepositLabel}}", "{{Document.DepositAmount}}", "{{Document.AdjustmentsSummary}}",
            "{{Document.DueDateLabel}}", "{{Document.Notes}}", "{{Client.Name}}", "{{Job.Name}}",
            "{{Job.GroupName}}", "{{Job.PoNumber}}", "{{Company.Abn}}", "{{Payment.Details}}",
            "{{Payment.Terms}}", "{{#Lines}}", "{{Line.Description}}", "{{Line.UnitPrice}}",
            "{{Line.Quantity}}", "{{Line.Total}}", "{{/Lines}}"
        });

        foreach (var (fileName, name, docType) in templates)
        {
            var fullPath = Path.Combine(templatesDirectory, fileName);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var existing = await _db.DocumentTemplates
                .FirstOrDefaultAsync(t => t.DocumentType == docType && t.IsDefault, ct);
            if (existing is not null && await _fileStore.ExistsAsync(existing.StorageKey, ct))
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            var storageKey = await _fileStore.SaveAsync("templates", ".docx", bytes, ct);

            if (existing is null)
            {
                _db.DocumentTemplates.Add(new DocumentTemplateEntity
                {
                    Id = _ids.NewId(),
                    Name = name,
                    DocumentType = docType,
                    Version = 1,
                    StorageKey = storageKey,
                    PlaceholdersJson = standardPlaceholders,
                    IsDefault = true,
                    IsActive = true
                });
            }
            else
            {
                existing.StorageKey = storageKey;
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Seeded {DocType} Word template '{Name}' into private storage.", docType, name);
        }
    }
}
