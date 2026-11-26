using Lavari.Application.Abstractions;
using Lavari.Application.Security;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Locking;
using Lavari.Infrastructure.Integrations;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Security;
using Lavari.Infrastructure.Services;
using Lavari.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
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
    public async Task AiImageSourceMigration_PreservesExistingImagesAndAuditsDerivedRelationships()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        await db.GetService<IMigrator>().MigrateAsync("20261126022828_AddAccountAppearance");
        var sourceId = Guid.NewGuid();
        var associationId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ImageAssets
                (Id, Title, StorageKey, ThumbnailStorageKey, ContentType, Width, Height, ByteLength,
                 ThumbWidth, ThumbHeight, RightsConfirmed, UploadedByUserId, IsGlobalLibrary,
                 CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, ConcurrencyToken)
            VALUES
                ({sourceId}, 'Original', 'original', 'original-thumb', 'image/png', 10, 10, 100,
                 10, 10, 1, {Guid.Empty}, 1, '2026-11-25T10:00:00.0000000Z', 'test',
                 '2026-11-25T10:00:00.0000000Z', 'test', 'original-token')
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ImageAssociations
                (Id, ImageAssetId, AssociationType, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, ConcurrencyToken)
            VALUES
                ({associationId}, {sourceId}, 'Library', '2026-11-25T10:00:00.0000000Z', 'test',
                 '2026-11-25T10:00:00.0000000Z', 'test', 'association-token')
            """);

        await db.Database.MigrateAsync();
        var source = await db.ImageAssets.AsNoTracking().SingleAsync(i => i.Id == sourceId);
        Assert.Equal("original", source.StorageKey);
        Assert.Null(source.AiSourceImageAssetId);
        Assert.True(await db.ImageAssociations.AnyAsync(a => a.Id == associationId && a.ImageAssetId == sourceId));

        var derived = new ImageAssetEntity
        {
            Id = Guid.NewGuid(), Title = "AI version", StorageKey = "derived", ThumbnailStorageKey = "derived-thumb",
            ContentType = "image/png", Width = 10, Height = 10, ByteLength = 100, ThumbWidth = 10, ThumbHeight = 10,
            AiSourceImageAssetId = sourceId
        };
        db.ImageAssets.Add(derived);
        await db.SaveChangesAsync();
        var audit = await db.AuditEntries.SingleAsync(a => a.EntityId == derived.Id.ToString("D"));
        Assert.Contains(sourceId.ToString(), audit.AfterJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", (await db.ImageAssets.AsNoTracking().SingleAsync(i => i.Id == sourceId)).StorageKey);

        derived.AiSourceImageAssetId = null;
        var immutable = await Assert.ThrowsAsync<ConflictServiceException>(() => db.SaveChangesAsync());
        Assert.Equal(ErrorCodes.RecordImmutable, immutable.ErrorCode);
        derived.AiSourceImageAssetId = sourceId;

        await using var detachedDb = CreateDbContext();
        detachedDb.ImageAssets.Update(new ImageAssetEntity
        {
            Id = derived.Id, Title = derived.Title, StorageKey = derived.StorageKey,
            ThumbnailStorageKey = derived.ThumbnailStorageKey, AiSourceImageAssetId = null
        });
        var detachedError = await Assert.ThrowsAsync<ConflictServiceException>(() => detachedDb.SaveChangesAsync());
        Assert.Equal(ErrorCodes.RecordImmutable, detachedError.ErrorCode);

        db.ImageAssets.Add(new ImageAssetEntity
        {
            Id = Guid.NewGuid(), Title = "Invalid", StorageKey = "invalid", ThumbnailStorageKey = "invalid-thumb",
            AiSourceImageAssetId = Guid.NewGuid()
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        await using var selfReferenceDb = CreateDbContext();
        var selfId = Guid.NewGuid();
        selfReferenceDb.ImageAssets.Add(new ImageAssetEntity
        {
            Id = selfId, Title = "Invalid self reference", StorageKey = "self", ThumbnailStorageKey = "self-thumb",
            AiSourceImageAssetId = selfId
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => selfReferenceDb.SaveChangesAsync());
    }

    [Fact]
    public async Task SupplierAiAuthorization_RechecksInitiatorAndLeastPrivilegePrincipal()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var ids = new TimeOrderedIdGenerator(_time);
        var store = new LocalPrivateFileStore(
            Options.Create(new LavariStorageOptions { StorageRoot = _tempStorageDir }), ids, _time);
        var hasher = new Pbkdf2PasswordHasher();
        await new DatabaseSeeder(db, hasher, store, ids, NullLogger<DatabaseSeeder>.Instance)
            .SeedAsync(true, null, null, null);
        var admin = await db.Users.SingleAsync(u => u.Email == DatabaseSeeder.DefaultDevSuperAdminEmail);
        var principal = await db.Users.SingleAsync(u => u.Email == DatabaseSeeder.LlmPrincipalEmail);
        var serviceRole = await db.Roles.SingleAsync(r => r.Name == "LLM Extraction Service");
        var grant = await db.RolePermissions.SingleAsync(p => p.RoleId == serviceRole.Id && p.ModuleKey == ModuleKeys.SupplierCapture);
        var authorization = new SupplierAiAuthorization(db, new AuthService(db, hasher));

        var context = await authorization.AuthorizeAsync(admin.Id, null, null, "test");
        Assert.Equal(principal.Id, context.ActorId);
        Assert.False(context.IsFullAccess);
        Assert.True(context.IsSystemPrincipal);
        Assert.False(PermissionRegistry.Satisfies(context.EffectivePermissions, ModuleKeys.Jobs, AccessLevel.Read));

        grant.AccessLevel = AccessLevel.Read;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ForbiddenServiceException>(() => authorization.AuthorizeAsync(admin.Id, null, null, "test"));
        grant.AccessLevel = AccessLevel.Alter;
        serviceRole.IsFullAccess = true;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ForbiddenServiceException>(() => authorization.AuthorizeAsync(admin.Id, null, null, "test"));
        serviceRole.IsFullAccess = false;
        var role = new RoleEntity { Id = ids.NewId(), Name = "AI requester" };
        var user = new UserEntity
        {
            Id = ids.NewId(), Email = "requester@example.test", NormalizedEmail = "REQUESTER@EXAMPLE.TEST",
            DisplayName = "Requester", IsActive = true
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        db.UserRoles.Add(new UserRoleEntity { Id = ids.NewId(), UserId = user.Id, RoleId = role.Id });
        db.RolePermissions.AddRange(
            new RolePermissionEntity { Id = ids.NewId(), RoleId = role.Id, ModuleKey = ModuleKeys.SupplierCapture, AccessLevel = AccessLevel.Alter },
            new RolePermissionEntity { Id = ids.NewId(), RoleId = role.Id, ModuleKey = ModuleKeys.LlmAdministration, AccessLevel = AccessLevel.Read });
        var client = new ClientEntity { Id = ids.NewId(), Name = "AI client" };
        var group = new JobGroupEntity { Id = ids.NewId(), Client = client, Name = "AI group" };
        var job = new JobEntity { Id = ids.NewId(), Client = client, JobGroup = group, Name = "AI job", TimeZoneIana = "Australia/Sydney" };
        db.Clients.Add(client);
        db.JobGroups.Add(group);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        await authorization.AuthorizeAsync(user.Id, null, null, "test");
        await Assert.ThrowsAsync<ForbiddenServiceException>(() => authorization.AuthorizeAsync(user.Id, client.Id, job.Id, "test"));
        db.RolePermissions.AddRange(
            new RolePermissionEntity { Id = ids.NewId(), RoleId = role.Id, ModuleKey = ModuleKeys.Clients, AccessLevel = AccessLevel.Read },
            new RolePermissionEntity { Id = ids.NewId(), RoleId = role.Id, ModuleKey = ModuleKeys.Jobs, AccessLevel = AccessLevel.Read });
        await db.SaveChangesAsync();
        await authorization.AuthorizeAsync(user.Id, client.Id, job.Id, "test");
        await Assert.ThrowsAsync<ValidationServiceException>(() => authorization.AuthorizeAsync(user.Id, Guid.NewGuid(), job.Id, "test"));
        group.DeletedAtUtc = _time.GetUtcNow();
        group.DeletedBy = "test";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundServiceException>(() => authorization.AuthorizeAsync(user.Id, client.Id, job.Id, "test"));
        group.DeletedAtUtc = null;
        group.DeletedBy = null;
        client.DeletedAtUtc = _time.GetUtcNow();
        client.DeletedBy = "test";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundServiceException>(() => authorization.AuthorizeAsync(user.Id, client.Id, job.Id, "test"));

        admin.IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ForbiddenServiceException>(() => authorization.AuthorizeAsync(admin.Id, null, null, "test"));
    }

    [Fact]
    public async Task SupplierAiReservations_EnforceBudgetAndAuditTransitionsWithoutContent()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var ids = new TimeOrderedIdGenerator(_time);
        var hasher = new Pbkdf2PasswordHasher();
        var store = new LocalPrivateFileStore(Options.Create(new LavariStorageOptions { StorageRoot = _tempStorageDir }), ids, _time);
        await new DatabaseSeeder(db, hasher, store, ids, NullLogger<DatabaseSeeder>.Instance).SeedAsync(true, null, null, null);
        var admin = await db.Users.SingleAsync(u => u.Email == DatabaseSeeder.DefaultDevSuperAdminEmail);
        _actor.SwitchTo(admin.Id, "Admin", "test");
        var authorization = new SupplierAiAuthorization(db, new AuthService(db, hasher));
        var reservations = new SupplierAiSpendReservations(db, _actor, new PermissionEvaluator(_actor), authorization, _time, ids);
        var requestId = Guid.NewGuid();
        var fingerprint = new string('a', 64);
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            reservations.ReserveAsync(requestId, fingerprint + "\n", null, null, "Gemini", "text-model"));
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            reservations.ReserveAsync(requestId, fingerprint, null, null, "Gemini\n", "text-model"));
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            reservations.ReserveAsync(requestId, fingerprint, null, null, "Gemini", "text-model\n"));
        await Assert.ThrowsAsync<ExternalIntegrationServiceException>(() =>
            reservations.ReserveAsync(requestId, fingerprint, null, null, "Gemini", "text-model"));

        db.ApplicationVariables.AddRange(
            BudgetVariable("Ai.SpendCurrency", "AUD", ids),
            BudgetVariable("Ai.DailyLimitCents", "100", ids),
            BudgetVariable("Ai.UserDailyLimitCents", "80", ids),
            BudgetVariable("Ai.UserDailyRequests", "3", ids),
            BudgetVariable("Ai.ReservationCents", "35", ids));
        await db.SaveChangesAsync();

        var first = await reservations.ReserveAsync(requestId, fingerprint, null, null, "Gemini", "text-model");
        Assert.Equal(first, await reservations.ReserveAsync(requestId, fingerprint, null, null, "Gemini", "text-model"));
        await Assert.ThrowsAsync<ConflictServiceException>(() =>
            reservations.ReserveAsync(requestId, new string('b', 64), null, null, "Gemini", "text-model"));
        var second = await reservations.ReserveAsync(Guid.NewGuid(), fingerprint, null, null, "Gemini", "text-model");
        await Assert.ThrowsAsync<ConflictServiceException>(() =>
            reservations.ReserveAsync(Guid.NewGuid(), fingerprint, null, null, "Gemini", "text-model"));
        await reservations.CancelAsync(first);
        await Assert.ThrowsAsync<ConflictServiceException>(() =>
            reservations.ReserveAsync(requestId, fingerprint, null, null, "Gemini", "text-model"));
        var thirdRequest = Guid.NewGuid();
        await reservations.ReserveAsync(thirdRequest, fingerprint, null, null, "Gemini", "text-model");
        await Assert.ThrowsAsync<ConflictServiceException>(() =>
            reservations.ReserveAsync(Guid.NewGuid(), fingerprint, null, null, "Gemini", "text-model"));
        await Assert.ThrowsAsync<ForbiddenServiceException>(() => reservations.BeginAsync(second));

        var principal = await authorization.AuthorizeAsync(admin.Id, null, null, "corr-test");
        var amount = await db.ApplicationVariables.SingleAsync(x => x.Key == "Ai.ReservationCents");
        amount.Value = "20";
        await db.SaveChangesAsync();
        _actor.SwitchToPrincipal(principal);
        await Assert.ThrowsAsync<ConflictServiceException>(() => reservations.BeginAsync(second));
        _actor.SwitchTo(admin.Id, "Admin", "test");
        amount.Value = "35";
        await db.SaveChangesAsync();
        _actor.SwitchToPrincipal(principal);
        await reservations.BeginAsync(second);
        await Assert.ThrowsAsync<ValidationServiceException>(() => reservations.CompleteAsync(second, -1, null, null, null));
        await reservations.CompleteAsync(second, 12, 4, 2, 18);
        await Assert.ThrowsAsync<ConflictServiceException>(() => reservations.BeginAsync(second));
        var stored = await db.AiSpendReservations.AsNoTracking().SingleAsync(x => x.Id == second);
        Assert.Equal("Completed", stored.State);
        Assert.Equal(18, stored.TotalTokens);
        var audit = await db.AuditEntries.Where(x => x.EntityId == second.ToString("D")).ToListAsync();
        Assert.True(audit.Count >= 3);
        Assert.All(audit, row => Assert.DoesNotContain(fingerprint, row.AfterJson ?? string.Empty));
        Assert.All(audit, row => Assert.DoesNotContain(nameof(AiSpendReservationEntity.RequestFingerprint), row.AfterJson ?? string.Empty));
        _actor.SwitchTo(admin.Id, "Admin", "test");
        await Assert.ThrowsAsync<ConflictServiceException>(() => reservations.CancelAsync(second));
        _time.Advance(TimeSpan.FromDays(1));
        await Assert.ThrowsAsync<ConflictServiceException>(() =>
            reservations.ReserveAsync(thirdRequest, fingerprint, null, null, "Gemini", "text-model"));
        await reservations.ReserveAsync(Guid.NewGuid(), fingerprint, null, null, "Gemini", "text-model");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261126124631_IndexImageAccess"));
        Assert.Equal(4, await db.AiSpendReservations.CountAsync());
    }

    [Fact]
    public async Task SupplierAiReservations_RecheckAccessAndRollbackWithAudit()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var ids = new TimeOrderedIdGenerator(_time);
        var hasher = new Pbkdf2PasswordHasher();
        var store = new LocalPrivateFileStore(Options.Create(new LavariStorageOptions { StorageRoot = _tempStorageDir }), ids, _time);
        await new DatabaseSeeder(db, hasher, store, ids, NullLogger<DatabaseSeeder>.Instance).SeedAsync(true, null, null, null);
        var admin = await db.Users.SingleAsync(u => u.Email == DatabaseSeeder.DefaultDevSuperAdminEmail);
        _actor.SwitchTo(admin.Id, "Admin", "test");
        db.ApplicationVariables.AddRange(
            BudgetVariable("Ai.SpendCurrency", "AUD", ids),
            BudgetVariable("Ai.DailyLimitCents", "100", ids),
            BudgetVariable("Ai.UserDailyLimitCents", "100", ids),
            BudgetVariable("Ai.UserDailyRequests", "3", ids),
            BudgetVariable("Ai.ReservationCents", "35", ids));
        await db.SaveChangesAsync();
        var authorization = new SupplierAiAuthorization(db, new AuthService(db, hasher));
        var reservations = new SupplierAiSpendReservations(db, _actor, new PermissionEvaluator(_actor), authorization, _time, ids);
        var fingerprint = new string('a', 64);
        db.SimulateAuditWriteFailure = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reservations.ReserveAsync(Guid.NewGuid(), fingerprint, null, null, "Gemini", "text-model"));
        db.SimulateAuditWriteFailure = false;
        await using (var verify = CreateDbContext())
            Assert.Empty(await verify.AiSpendReservations.ToListAsync());
        db.ChangeTracker.Clear();

        var id = await reservations.ReserveAsync(Guid.NewGuid(), fingerprint, null, null, "Gemini", "text-model");
        var pendingId = await reservations.ReserveAsync(Guid.NewGuid(), fingerprint, null, null, "Gemini", "text-model");
        var principal = await authorization.AuthorizeAsync(admin.Id, null, null, "corr-test");
        _actor.SwitchToPrincipal(principal);
        await reservations.BeginAsync(id);
        _actor.SwitchTo(admin.Id, "Admin", "test");
        admin = await db.Users.SingleAsync(u => u.Id == admin.Id);
        admin.IsActive = false;
        await db.SaveChangesAsync();
        _actor.SwitchToPrincipal(principal);
        await Assert.ThrowsAsync<ForbiddenServiceException>(() => reservations.BeginAsync(pendingId));
        Assert.Equal("Reserved", (await db.AiSpendReservations.AsNoTracking().SingleAsync(x => x.Id == pendingId)).State);
        await reservations.CompleteAsync(id, 12, 4, null, 16);
        Assert.Equal(16, (await db.AiSpendReservations.AsNoTracking().SingleAsync(x => x.Id == id)).TotalTokens);
    }

    [Fact]
    public async Task SupplierAiReservations_ConcurrentRequestsCannotExceedBudget()
    {
        var path = Path.Combine(_tempStorageDir, "reservation-concurrency.db");
        var options = new DbContextOptionsBuilder<LavariDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = path, Cache = SqliteCacheMode.Shared, DefaultTimeout = 10, Pooling = false
            }.ToString())
            .Options;
        var ids = new TimeOrderedIdGenerator(_time);
        var hasher = new Pbkdf2PasswordHasher();
        var secondActor = new TestActorContext();
        await using (var seed = new LavariDbContext(options, _time, _actor, ids))
        {
            await seed.Database.MigrateAsync();
            var store = new LocalPrivateFileStore(Options.Create(new LavariStorageOptions { StorageRoot = _tempStorageDir }), ids, _time);
            await new DatabaseSeeder(seed, hasher, store, ids, NullLogger<DatabaseSeeder>.Instance).SeedAsync(true, null, null, null);
            var admin = await seed.Users.SingleAsync(u => u.Email == DatabaseSeeder.DefaultDevSuperAdminEmail);
            _actor.SwitchTo(admin.Id, "Admin", "test");
            var secondUser = new UserEntity
            {
                Id = ids.NewId(), Email = "second@example.test", NormalizedEmail = "SECOND@EXAMPLE.TEST",
                DisplayName = "Second", IsActive = true
            };
            var fullRole = await seed.Roles.SingleAsync(r => r.Name == DatabaseSeeder.FullAccessRoleName);
            seed.Users.Add(secondUser);
            seed.UserRoles.Add(new UserRoleEntity { Id = ids.NewId(), UserId = secondUser.Id, RoleId = fullRole.Id });
            secondActor.SwitchTo(secondUser.Id, "Second", "test");
            seed.ApplicationVariables.AddRange(
                BudgetVariable("Ai.SpendCurrency", "AUD", ids),
                BudgetVariable("Ai.DailyLimitCents", "35", ids),
                BudgetVariable("Ai.UserDailyLimitCents", "35", ids),
                BudgetVariable("Ai.UserDailyRequests", "5", ids),
                BudgetVariable("Ai.ReservationCents", "35", ids));
            await seed.SaveChangesAsync();
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> AttemptAsync(TestActorContext requester)
        {
            await start.Task;
            await using var context = new LavariDbContext(options, _time, requester, ids);
            var authorization = new SupplierAiAuthorization(context, new AuthService(context, hasher));
            var service = new SupplierAiSpendReservations(context, requester, new PermissionEvaluator(requester), authorization, _time, ids);
            try
            {
                await service.ReserveAsync(Guid.NewGuid(), new string('a', 64), null, null, "Gemini", "text-model");
                return true;
            }
            catch (ConflictServiceException)
            {
                return false;
            }
        }
        var attempts = new[] { Task.Run(() => AttemptAsync(_actor)), Task.Run(() => AttemptAsync(secondActor)) };
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, x => x);
        await using var verify = new LavariDbContext(options, _time, _actor, ids);
        Assert.Equal(35, await verify.AiSpendReservations.SumAsync(x => x.ReservedCents));
        Assert.Single(await verify.AiSpendReservations.ToListAsync());
    }

    private static ApplicationVariableEntity BudgetVariable(string key, string value, IIdGenerator ids)
        => new() { Id = ids.NewId(), Key = key, Value = value };

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
        var budgetConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lavari:AiBudget:Currency"] = "AUD",
                ["Lavari:AiBudget:DailyLimitCents"] = "1200",
                ["Lavari:AiBudget:UserDailyLimitCents"] = "600",
                ["Lavari:AiBudget:UserDailyRequests"] = "4",
                ["Lavari:AiBudget:ReservationCents"] = "100"
            })
            .Build();
        var seeder = new DatabaseSeeder(
            db, new Pbkdf2PasswordHasher(), fileStore, new TimeOrderedIdGenerator(_time),
            NullLogger<DatabaseSeeder>.Instance, budgetConfiguration);
        await seeder.SeedAsync(true, null, null, templatesDir);

        var budgetVariables = await db.ApplicationVariables.AsNoTracking()
            .Where(x => x.Key.StartsWith("Ai."))
            .ToDictionaryAsync(x => x.Key, x => x.Value);
        Assert.Equal("AUD", budgetVariables["Ai.SpendCurrency"]);
        Assert.Equal("1200", budgetVariables["Ai.DailyLimitCents"]);
        Assert.Equal("600", budgetVariables["Ai.UserDailyLimitCents"]);
        Assert.Equal("4", budgetVariables["Ai.UserDailyRequests"]);
        Assert.Equal("100", budgetVariables["Ai.ReservationCents"]);

        var userDailyLimit = await db.ApplicationVariables.SingleAsync(x => x.Key == "Ai.UserDailyLimitCents");
        userDailyLimit.Value = "700";
        await db.SaveChangesAsync();
        await seeder.SeedAsync(true, null, null, templatesDir);
        Assert.Equal("700", (await db.ApplicationVariables.AsNoTracking()
            .SingleAsync(x => x.Key == "Ai.UserDailyLimitCents")).Value);

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
        public bool IsSystemPrincipal { get; private set; }
        public Guid? ActorId { get; private set; } = Guid.CreateVersion7();
        public string ActorDisplayName { get; private set; } = "Test Admin";
        public string CorrelationId => "corr-test";
        public string EditingSessionId { get; private set; } = "sess-init";
        public bool IsFullAccess => !IsSystemPrincipal;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions { get; private set; } = FullGrants;

        public void SwitchTo(Guid id, string name, string sessionId)
        {
            ActorId = id;
            ActorDisplayName = name;
            EditingSessionId = sessionId;
            IsSystemPrincipal = false;
            EffectivePermissions = FullGrants;
        }

        public void SwitchToPrincipal(ICurrentActorContext principal)
        {
            ActorId = principal.ActorId;
            ActorDisplayName = principal.ActorDisplayName;
            EditingSessionId = principal.EditingSessionId;
            IsSystemPrincipal = principal.IsSystemPrincipal;
            EffectivePermissions = principal.EffectivePermissions;
        }
    }
}
