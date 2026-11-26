using System.Reflection;
using System.Text.Json;
using Lavari.Application.Abstractions;
using Lavari.Contracts.Common;
using Lavari.Domain.Errors;
using Lavari.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lavari.Infrastructure.Persistence;

public sealed class LavariDbContext : DbContext
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly ValueConverter<DateTimeOffset, string> UtcIsoConverter = new(
        v => v.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
        v => DateTimeOffset.Parse(v).ToUniversalTime());

    private static readonly ValueConverter<DateTimeOffset?, string?> NullableUtcIsoConverter = new(
        v => v.HasValue ? v.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") : null,
        v => string.IsNullOrWhiteSpace(v) ? null : DateTimeOffset.Parse(v).ToUniversalTime());

    private readonly TimeProvider _timeProvider;
    private readonly ICurrentActorContext _actor;
    private readonly IIdGenerator _idGenerator;

    // Optional test hook to verify atomic rollback if audit persistence fails.
    internal bool SimulateAuditWriteFailure { get; set; }

    public LavariDbContext(
        DbContextOptions<LavariDbContext> options,
        TimeProvider timeProvider,
        ICurrentActorContext actor,
        IIdGenerator idGenerator)
        : base(options)
    {
        _timeProvider = timeProvider;
        _actor = actor;
        _idGenerator = idGenerator;
    }

    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<OperationalErrorOccurrenceEntity> OperationalErrorOccurrences => Set<OperationalErrorOccurrenceEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<RolePermissionEntity> RolePermissions => Set<RolePermissionEntity>();
    public DbSet<UserRoleEntity> UserRoles => Set<UserRoleEntity>();
    public DbSet<ApplicationVariableEntity> ApplicationVariables => Set<ApplicationVariableEntity>();
    public DbSet<ErrorCodeDefinitionEntity> ErrorCodeDefinitions => Set<ErrorCodeDefinitionEntity>();

    public DbSet<ClientEntity> Clients => Set<ClientEntity>();
    public DbSet<JobGroupEntity> JobGroups => Set<JobGroupEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<EditLeaseEntity> EditLeases => Set<EditLeaseEntity>();
    public DbSet<EditQueueEntryEntity> EditQueueEntries => Set<EditQueueEntryEntity>();
    public DbSet<InAppNotificationEntity> InAppNotifications => Set<InAppNotificationEntity>();

    public DbSet<JobDateEntity> JobDates => Set<JobDateEntity>();
    public DbSet<TimeEntryEntity> TimeEntries => Set<TimeEntryEntity>();
    public DbSet<TimeIntervalEntity> TimeIntervals => Set<TimeIntervalEntity>();

    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<ProductVariantEntity> ProductVariants => Set<ProductVariantEntity>();
    public DbSet<JobLineItemEntity> JobLineItems => Set<JobLineItemEntity>();
    public DbSet<ImageAssetEntity> ImageAssets => Set<ImageAssetEntity>();
    public DbSet<ImageAssociationEntity> ImageAssociations => Set<ImageAssociationEntity>();
    public DbSet<MoodBoardEntity> MoodBoards => Set<MoodBoardEntity>();
    public DbSet<MoodBoardCanvasEntity> MoodBoardCanvases => Set<MoodBoardCanvasEntity>();
    public DbSet<MoodBoardElementEntity> MoodBoardElements => Set<MoodBoardElementEntity>();

    public DbSet<FinancialDocumentEntity> FinancialDocuments => Set<FinancialDocumentEntity>();
    public DbSet<FinancialDocumentLineEntity> FinancialDocumentLines => Set<FinancialDocumentLineEntity>();
    public DbSet<FinancialDocumentAdjustmentEntity> FinancialDocumentAdjustments => Set<FinancialDocumentAdjustmentEntity>();
    public DbSet<FinancialDocumentSnapshotEntity> FinancialDocumentSnapshots => Set<FinancialDocumentSnapshotEntity>();
    public DbSet<TimeBillingAllocationEntity> TimeBillingAllocations => Set<TimeBillingAllocationEntity>();
    public DbSet<ProcurementItemEntity> ProcurementItems => Set<ProcurementItemEntity>();
    public DbSet<ProcurementPaymentEntity> ProcurementPayments => Set<ProcurementPaymentEntity>();
    public DbSet<ReceiptEntity> Receipts => Set<ReceiptEntity>();

    public DbSet<DocumentTemplateEntity> DocumentTemplates => Set<DocumentTemplateEntity>();
    public DbSet<GeneratedArtifactEntity> GeneratedArtifacts => Set<GeneratedArtifactEntity>();
    public DbSet<EmailConnectionEntity> EmailConnections => Set<EmailConnectionEntity>();
    public DbSet<JobEmailLabelEntity> JobEmailLabels => Set<JobEmailLabelEntity>();
    public DbSet<ForecastSnapshotEntity> ForecastSnapshots => Set<ForecastSnapshotEntity>();
    public DbSet<LlmExtractionDraftEntity> LlmExtractionDrafts => Set<LlmExtractionDraftEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure soft-delete filters, concurrency tokens and UTC ISO converters uniformly across all entities.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(AuditableSoftDeletableEntity).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .Property(nameof(AuditableSoftDeletableEntity.ConcurrencyToken))
                    .IsConcurrencyToken()
                    .HasMaxLength(64);

                var method = typeof(LavariDbContext)
                    .GetMethod(nameof(ApplySoftDeleteFilter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(entityType.ClrType);
                method.Invoke(null, [modelBuilder]);
            }

            foreach (var prop in entityType.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset))
                {
                    prop.SetValueConverter(UtcIsoConverter);
                }
                else if (prop.ClrType == typeof(DateTimeOffset?))
                {
                    prop.SetValueConverter(NullableUtcIsoConverter);
                }
                else
                {
                    var underlying = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
                    if (underlying.IsEnum && underlying.Namespace == typeof(FinancialDocumentType).Namespace)
                    {
                        var converterType = typeof(CaseInsensitiveEnumToStringConverter<>).MakeGenericType(underlying);
                        prop.SetValueConverter((ValueConverter)Activator.CreateInstance(converterType)!);
                    }
                }
            }
        }

        // Security & Admin indexes
        modelBuilder.Entity<UserEntity>(e =>
        {
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.AppearanceMode).HasMaxLength(16).HasDefaultValue("system");
            e.Property(x => x.AppearanceColor).HasMaxLength(16).HasDefaultValue("lavender");
        });

        modelBuilder.Entity<RoleEntity>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<RolePermissionEntity>(e =>
        {
            e.HasIndex(x => new { x.RoleId, x.ModuleKey }).IsUnique();
            e.HasOne(x => x.Role).WithMany(r => r.Permissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserRoleEntity>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApplicationVariableEntity>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(160);
        });

        modelBuilder.Entity<ErrorCodeDefinitionEntity>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(64);
        });

        // Audit & Operational Error indexes
        modelBuilder.Entity<AuditEntryEntity>(e =>
        {
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAtUtc });
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<OperationalErrorOccurrenceEntity>(e =>
        {
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => x.ErrorCode);
        });

        // Client -> JobGroup -> Job hierarchy (Restrict deletes — never cascade physical deletes)
        modelBuilder.Entity<JobGroupEntity>(e =>
        {
            e.HasOne(x => x.Client).WithMany(c => c.JobGroups).HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ClientId, x.Name });
        });

        modelBuilder.Entity<JobEntity>(e =>
        {
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JobGroup).WithMany(g => g.Jobs).HasForeignKey(x => x.JobGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.JobGroupId);
            e.HasIndex(x => x.ClientId);
        });

        // Exclusive Edit Leases & FIFO Queue
        modelBuilder.Entity<EditLeaseEntity>(e =>
        {
            e.HasIndex(x => new { x.RecordType, x.RecordId, x.State });
            // Enforce at most one Active or Reserved lease per lockable record at the database level.
            e.HasIndex(x => new { x.RecordType, x.RecordId })
                .IsUnique()
                .HasFilter("\"State\" IN ('Active', 'Reserved') AND \"DeletedAtUtc\" IS NULL");
        });

        modelBuilder.Entity<EditQueueEntryEntity>(e =>
        {
            e.HasIndex(x => new { x.RecordType, x.RecordId, x.State, x.SequenceNumber });
        });

        modelBuilder.Entity<InAppNotificationEntity>(e =>
        {
            e.HasIndex(x => new { x.RecipientUserId, x.IsRead, x.CreatedAtUtc });
        });

        // Job Dates & Time Tracking
        modelBuilder.Entity<JobDateEntity>(e =>
        {
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<TimeEntryEntity>(e =>
        {
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.WorkerUserId, x.State });
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => new { x.WorkerUserId, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });

        modelBuilder.Entity<TimeIntervalEntity>(e =>
        {
            e.HasOne(x => x.TimeEntry).WithMany(t => t.Intervals).HasForeignKey(x => x.TimeEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TimeEntryId, x.Sequence });
        });

        // Products, Line Items, Images, Mood Boards
        modelBuilder.Entity<ProductVariantEntity>(e =>
        {
            e.HasOne(x => x.Product).WithMany(p => p.Variants).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JobLineItemEntity>(e =>
        {
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<ImageAssetEntity>(e =>
        {
            e.HasIndex(x => x.StorageKey).IsUnique();
        });

        modelBuilder.Entity<ImageAssociationEntity>(e =>
        {
            e.HasOne(x => x.ImageAsset).WithMany(i => i.Associations).HasForeignKey(x => x.ImageAssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ImageAssetId, x.JobId, x.ClientId });
        });

        modelBuilder.Entity<MoodBoardEntity>(e =>
        {
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<MoodBoardCanvasEntity>(e =>
        {
            e.HasOne(x => x.MoodBoard).WithMany(b => b.Canvases).HasForeignKey(x => x.MoodBoardId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.MoodBoardId, x.SortOrder });
        });

        modelBuilder.Entity<MoodBoardElementEntity>(e =>
        {
            e.HasOne(x => x.Canvas).WithMany(c => c.Elements).HasForeignKey(x => x.CanvasId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CanvasId, x.ZIndex });
        });

        // Financial Documents, Procurement, Receipts
        modelBuilder.Entity<FinancialDocumentEntity>(e =>
        {
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.DocumentType, x.DocumentNumber }).IsUnique();
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<FinancialDocumentLineEntity>(e =>
        {
            e.HasOne(x => x.FinancialDocument).WithMany(d => d.Lines).HasForeignKey(x => x.FinancialDocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialDocumentAdjustmentEntity>(e =>
        {
            e.HasOne(x => x.FinancialDocument).WithMany(d => d.Adjustments).HasForeignKey(x => x.FinancialDocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialDocumentSnapshotEntity>(e =>
        {
            e.HasOne(x => x.FinancialDocument).WithMany(d => d.Snapshots).HasForeignKey(x => x.FinancialDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.FinancialDocumentId, x.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<TimeBillingAllocationEntity>(e =>
        {
            e.HasOne(x => x.TimeEntry).WithMany().HasForeignKey(x => x.TimeEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FinancialDocument).WithMany().HasForeignKey(x => x.FinancialDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TimeEntryId, x.IsActive });
        });

        modelBuilder.Entity<ProcurementItemEntity>(e =>
        {
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<ProcurementPaymentEntity>(e =>
        {
            e.HasOne(x => x.ProcurementItem).WithMany(p => p.Payments).HasForeignKey(x => x.ProcurementItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReceiptEntity>(e =>
        {
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<DocumentTemplateEntity>(e =>
        {
            e.HasIndex(x => new { x.DocumentType, x.Name, x.Version }).IsUnique();
        });

        modelBuilder.Entity<GeneratedArtifactEntity>(e =>
        {
            e.HasIndex(x => new { x.SourceType, x.SourceId, x.Format });
            e.HasIndex(x => x.StorageKey).IsUnique();
        });

        modelBuilder.Entity<EmailConnectionEntity>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.AccountEmail });
        });

        modelBuilder.Entity<JobEmailLabelEntity>(e =>
        {
            e.HasIndex(x => new { x.JobId, x.EmailConnectionId, x.GmailLabelId });
        });
    }

    private static void ApplySoftDeleteFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : AuditableSoftDeletableEntity
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.DeletedAtUtc == null);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var actorId = _actor.ActorId?.ToString("D") ?? (_actor.IsSystemPrincipal ? "system" : "anonymous");
        var actorName = string.IsNullOrWhiteSpace(_actor.ActorDisplayName) ? actorId : _actor.ActorDisplayName;
        var correlationId = string.IsNullOrWhiteSpace(_actor.CorrelationId) ? _idGenerator.NewId().ToString("N") : _actor.CorrelationId;
        var transactionId = _idGenerator.NewId().ToString("N");

        var auditRows = new List<AuditEntryEntity>();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnlyEntity)
            {
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                {
                    throw new ConflictServiceException(
                        "Append-only history records cannot be modified or deleted.",
                        ErrorCodes.RecordImmutable);
                }
                continue;
            }

            if (entry.State == EntityState.Deleted && entry.Entity is AuditableSoftDeletableEntity)
            {
                throw new ConflictServiceException(
                    "Physical deletion of application records is prohibited. Use soft deletion instead.",
                    ErrorCodes.RecordImmutable);
            }

            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.Entity is AuditableSoftDeletableEntity mutable)
            {
                if (entry.State == EntityState.Added)
                {
                    if (mutable.Id == Guid.Empty)
                    {
                        mutable.Id = _idGenerator.NewId();
                    }
                    mutable.CreatedAtUtc = now;
                    mutable.CreatedBy = actorId;
                    mutable.UpdatedAtUtc = now;
                    mutable.UpdatedBy = actorId;
                    mutable.ConcurrencyToken = _idGenerator.NewId().ToString("N");
                }
                else
                {
                    mutable.UpdatedAtUtc = now;
                    mutable.UpdatedBy = actorId;
                    mutable.ConcurrencyToken = _idGenerator.NewId().ToString("N");
                }

                var operation = DetermineOperation(entry);
                var beforeJson = entry.State == EntityState.Added ? null : SerializeEntry(entry, useOriginal: true);
                var afterJson = SerializeEntry(entry, useOriginal: false);

                auditRows.Add(new AuditEntryEntity
                {
                    Id = _idGenerator.NewId(),
                    EntityType = entry.Metadata.ClrType.Name,
                    EntityId = mutable.Id.ToString("D"),
                    Operation = operation,
                    OccurredAtUtc = now,
                    ActorId = actorId,
                    ActorDisplayName = actorName,
                    CorrelationId = correlationId,
                    TransactionId = transactionId,
                    SchemaVersion = 1,
                    BeforeJson = beforeJson,
                    AfterJson = afterJson
                });
            }
        }

        if (auditRows.Count > 0)
        {
            if (SimulateAuditWriteFailure)
            {
                throw new InvalidOperationException("Simulated atomic audit write failure.");
            }

            AuditEntries.AddRange(auditRows);
        }

        // Ensure business changes and audit entries commit atomically inside a single transaction.
        var ownsTransaction = Database.CurrentTransaction is null && Database.IsRelational();
        if (!ownsTransaction)
        {
            try
            {
                return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConflictServiceException(
                    "The record was modified by another user or process. Reload and try again.",
                    ErrorCodes.ConcurrencyConflict,
                    technicalDetails: ex.Message);
            }
        }

        await using var tx = await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var affected = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new ConflictServiceException(
                "The record was modified by another user or process. Reload and try again.",
                ErrorCodes.ConcurrencyConflict,
                technicalDetails: ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static string DetermineOperation(EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
        {
            return "Insert";
        }

        var deletedProp = entry.Property(nameof(AuditableSoftDeletableEntity.DeletedAtUtc));
        var wasDeleted = deletedProp.OriginalValue is not null;
        var isDeleted = deletedProp.CurrentValue is not null;

        if (!wasDeleted && isDeleted)
        {
            return "SoftDelete";
        }
        if (wasDeleted && !isDeleted)
        {
            return "Restore";
        }
        return "Update";
    }

    private static string SerializeEntry(EntityEntry entry, bool useOriginal)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in entry.Properties)
        {
            if (prop.Metadata.PropertyInfo?.GetCustomAttribute<ExcludeFromAuditAttribute>() is not null)
            {
                continue;
            }

            dict[prop.Metadata.Name] = useOriginal ? prop.OriginalValue : prop.CurrentValue;
        }

        return JsonSerializer.Serialize(dict, AuditJsonOptions);
    }

    private sealed class CaseInsensitiveEnumToStringConverter<TEnum>()
        : ValueConverter<TEnum, string>(
            v => v.ToString(),
            v => Enum.Parse<TEnum>(v, true))
        where TEnum : struct, Enum;
}
