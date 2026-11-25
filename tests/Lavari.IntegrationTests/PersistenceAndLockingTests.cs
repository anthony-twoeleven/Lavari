using Lavari.Application.Abstractions;
using Lavari.Application.Security;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Locking;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Security;
using Lavari.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lavari.IntegrationTests;

public sealed class PersistenceAndLockingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _tempStorageDir;
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly TestActorContext _actor = new();

    public PersistenceAndLockingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _tempStorageDir = Path.Combine(Path.GetTempPath(), $"lavari-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempStorageDir);
    }

    private LavariDbContext CreateDbContext()
    {
        var opts = new DbContextOptionsBuilder<LavariDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new LavariDbContext(opts, _time, _actor, new TimeOrderedIdGenerator(_time));
    }

    [Fact]
    public async Task MigrationsSeedingAuditAndSoftDelete_WorkAtomicallyWithoutLeakingSecrets()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        var fileStore = new LocalPrivateFileStore(
            Options.Create(new LavariStorageOptions { StorageRoot = _tempStorageDir }),
            new TimeOrderedIdGenerator(_time),
            _time);

        var templatesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Templates"));
        var seeder = new DatabaseSeeder(db, new Pbkdf2PasswordHasher(), fileStore, new TimeOrderedIdGenerator(_time), NullLogger<DatabaseSeeder>.Instance);
        await seeder.SeedAsync(true, null, null, templatesDir);

        var superAdmin = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == DatabaseSeeder.DefaultDevSuperAdminEmail);
        Assert.NotNull(superAdmin);
        Assert.Contains(superAdmin.UserRoles, ur => ur.Role.IsFullAccess);

        // Verify password hash is NEVER stored in AuditEntries
        var userAudits = await db.AuditEntries.Where(a => a.EntityType == nameof(UserEntity)).ToListAsync();
        Assert.NotEmpty(userAudits);
        Assert.All(userAudits, a => Assert.DoesNotContain("PasswordHash", a.AfterJson ?? string.Empty));
        Assert.All(userAudits, a => Assert.DoesNotContain(superAdmin.PasswordHash, a.AfterJson ?? string.Empty));

        // Verify Quote and Invoice Word templates were seeded into private storage
        var seededTemplates = await db.DocumentTemplates.ToListAsync();
        Assert.Equal(2, seededTemplates.Count);
        Assert.Contains(seededTemplates, t => t.DocumentType == FinancialDocumentType.Invoice);
        Assert.Contains(seededTemplates, t => t.DocumentType == FinancialDocumentType.Quote);

        // Verify physical delete is prohibited
        var client = new ClientEntity { Name = "Acme Design Co" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.Clients.Remove(client);
        await Assert.ThrowsAsync<ConflictServiceException>(() => db.SaveChangesAsync());

        // Verify soft delete hides record from default query and appends SoftDelete audit entry
        db.Entry(client).State = EntityState.Unchanged;
        client.DeletedAtUtc = _time.GetUtcNow();
        client.DeletedBy = _actor.ActorId!.Value.ToString("D");
        await db.SaveChangesAsync();

        Assert.Null(await db.Clients.FirstOrDefaultAsync(c => c.Id == client.Id));
        Assert.NotNull(await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == client.Id));
        Assert.Contains(await db.AuditEntries.Where(a => a.EntityId == client.Id.ToString("D")).ToListAsync(),
            a => a.Operation == "SoftDelete");

        // Verify restore clears deletion and appends Restore audit entry
        client.DeletedAtUtc = null;
        client.DeletedBy = null;
        await db.SaveChangesAsync();
        Assert.NotNull(await db.Clients.FirstOrDefaultAsync(c => c.Id == client.Id));
        Assert.Contains(await db.AuditEntries.Where(a => a.EntityId == client.Id.ToString("D")).ToListAsync(),
            a => a.Operation == "Restore");

        // Verify failed audit write rolls back the business mutation
        var rollbackClient = new ClientEntity { Name = "Must Roll Back" };
        db.Clients.Add(rollbackClient);
        db.SimulateAuditWriteFailure = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.SimulateAuditWriteFailure = false;
        db.ChangeTracker.Clear();

        Assert.Null(await db.Clients.FirstOrDefaultAsync(c => c.Name == "Must Roll Back"));
    }

    [Fact]
    public async Task ExclusiveEditLock_EnforcesFifoQueueNotificationsAndExpiry()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        var fileStore = new LocalPrivateFileStore(
            Options.Create(new LavariStorageOptions { StorageRoot = _tempStorageDir }),
            new TimeOrderedIdGenerator(_time),
            _time);
        var seeder = new DatabaseSeeder(db, new Pbkdf2PasswordHasher(), fileStore, new TimeOrderedIdGenerator(_time), NullLogger<DatabaseSeeder>.Instance);
        await seeder.SeedAsync(true, null, null, null);

        var designerRole = await db.Roles.FirstAsync(r => r.Name == DatabaseSeeder.DesignerRoleName);
        var userA = CreateUser(db, "a@lavari.local", "Designer A", designerRole.Id);
        var userB = CreateUser(db, "b@lavari.local", "Designer B", designerRole.Id);
        var userC = CreateUser(db, "c@lavari.local", "Designer C", designerRole.Id);

        var client = new ClientEntity { Name = "Client One" };
        var group = new JobGroupEntity { Client = client, Name = "Residential" };
        var job = new JobEntity { Client = client, JobGroup = group, Name = "Coastal Villa", TimeZoneIana = "Australia/Sydney" };
        db.Clients.Add(client);
        db.JobGroups.Add(group);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var lockService = new EditLockService(
            db, _actor, new PermissionEvaluator(_actor), _time, new TimeOrderedIdGenerator(_time), new NullRealtimeNotifier());

        // User A clicks Modify -> acquires lock
        _actor.SwitchTo(userA.Id, "Designer A", "sess-a");
        var statusA = await lockService.RequestModifyAsync(LockableRecordType.Job, job.Id);
        Assert.True(statusA.IsHeldByCurrentSession);
        Assert.Equal(1, statusA.FencingVersion);

        // User B clicks Modify -> queued at position 1
        _actor.SwitchTo(userB.Id, "Designer B", "sess-b");
        var statusB = await lockService.RequestModifyAsync(LockableRecordType.Job, job.Id);
        Assert.False(statusB.IsHeldByCurrentSession);
        Assert.True(statusB.IsCurrentSessionQueued);
        Assert.Equal(1, statusB.QueuePosition);

        // User B cannot save while A holds lock
        await Assert.ThrowsAsync<ConflictServiceException>(() =>
            lockService.EnsureActiveLeaseHeldAsync(LockableRecordType.Job, job.Id, null, null));

        // User C clicks Modify -> queued at position 2
        _actor.SwitchTo(userC.Id, "Designer C", "sess-c");
        var statusC = await lockService.RequestModifyAsync(LockableRecordType.Job, job.Id);
        Assert.Equal(2, statusC.QueuePosition);

        // User A releases lock -> B is reserved and notified
        _actor.SwitchTo(userA.Id, "Designer A", "sess-a");
        await lockService.ReleaseLeaseAsync(LockableRecordType.Job, job.Id, statusA.LeaseId, statusA.FencingVersion);

        _actor.SwitchTo(userB.Id, "Designer B", "sess-b");
        var notifsB = await lockService.GetNotificationsAsync(unreadOnly: true);
        Assert.Single(notifsB);
        Assert.Equal("edit_turn_ready", notifsB[0].Category);

        // User B claims turn -> becomes active with fencing version 2
        var claimedB = await lockService.ClaimReservationAsync(LockableRecordType.Job, job.Id);
        Assert.True(claimedB.IsHeldByCurrentSession);
        Assert.Equal(2, claimedB.FencingVersion);

        // Advance time beyond lease duration (300s) -> sweep expires B and reserves C
        _time.Advance(TimeSpan.FromSeconds(305));
        var swept = await lockService.SweepExpiredLeasesAsync();
        Assert.Equal(1, swept);

        _actor.SwitchTo(userC.Id, "Designer C", "sess-c");
        var statusCAfterExpiry = await lockService.GetStatusAsync(LockableRecordType.Job, job.Id);
        Assert.True(statusCAfterExpiry.IsReservedForCurrentSession);
    }

    private static UserEntity CreateUser(LavariDbContext db, string email, string name, Guid roleId)
    {
        var u = new UserEntity
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = name,
            PasswordHash = "hash",
            IsActive = true
        };
        db.Users.Add(u);
        db.UserRoles.Add(new UserRoleEntity { User = u, RoleId = roleId });
        return u;
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_tempStorageDir))
        {
            Directory.Delete(_tempStorageDir, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class TestActorContext : ICurrentActorContext
    {
        private static readonly IReadOnlyDictionary<string, AccessLevel> FullGrants =
            PermissionRegistry.ComputeEffectiveGrants(true, [new RoleGrantSnapshot(Guid.NewGuid(), "Full Access", true, new Dictionary<string, AccessLevel>())]);

        public bool IsAuthenticated => true;
        public bool IsSystemPrincipal => false;
        public Guid? ActorId { get; private set; } = Guid.CreateVersion7();
        public string ActorDisplayName { get; private set; } = "Test Admin";
        public string CorrelationId => "corr-test";
        public string EditingSessionId { get; private set; } = "sess-init";
        public bool IsFullAccess => true;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions => FullGrants;

        public void SwitchTo(Guid id, string name, string sessionId)
        {
            ActorId = id;
            ActorDisplayName = name;
            EditingSessionId = sessionId;
        }
    }
}
