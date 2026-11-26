using System.Data.Common;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Lavari.Application.Abstractions;
using Lavari.Application.Formatting;
using Lavari.Application.Locking;
using Lavari.Application.Security;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Contracts.Locking;
using Lavari.Domain.Errors;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Caching;
using Lavari.Infrastructure.Documents;
using Lavari.Infrastructure.Locking;
using Lavari.Infrastructure.Media;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Security;
using Lavari.Infrastructure.Services;
using Lavari.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Xunit;

namespace Lavari.IntegrationTests;

public sealed class BusinessWorkflowsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _tempDir;
    private readonly TestTime _time = new(new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero));
    private readonly TestActor _actor = new();

    public BusinessWorkflowsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _tempDir = Path.Combine(Path.GetTempPath(), $"lavari-biz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<TestHarness> CreateHarnessAsync(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LavariDbContext>().UseSqlite(_connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var opts = options.Options;
        var ids = new TimeOrderedIdGenerator(_time);
        var db = new LavariDbContext(opts, _time, _actor, ids);
        await db.Database.MigrateAsync();

        var fileStore = new LocalPrivateFileStore(Options.Create(new LavariStorageOptions { StorageRoot = _tempDir }), ids, _time);
        var hasher = new Pbkdf2PasswordHasher();
        var cache = new AppCacheService(Options.Create(new AppCacheOptions()), _time);
        var perms = new PermissionEvaluator(_actor);
        var formatting = new FormattingService();
        var wordSvc = new OpenXmlWordTemplateService();
        var pdfRenderer = new SkiaPdfRenderer(formatting);
        var imgProc = new SkiaImageProcessingService();

        var templatesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Templates"));
        var seeder = new DatabaseSeeder(db, hasher, fileStore, ids, NullLogger<DatabaseSeeder>.Instance);
        await seeder.SeedAsync(true, null, null, templatesDir);

        var adminUser = await db.Users.FirstAsync(u => u.Email == DatabaseSeeder.DefaultDevSuperAdminEmail);
        _actor.ActorId = adminUser.Id;
        _actor.ActorDisplayName = adminUser.DisplayName;

        var admin = new AdminService(db, _actor, perms, hasher, cache, _time, ids);
        var locks = new EditLockService(db, _actor, perms, _time, ids, new NullRealtimeNotifier());
        var clientsJobs = new ClientAndJobService(db, _actor, perms, locks, cache, _time, ids, new NullRealtimeNotifier());
        var timeSvc = new TimeTrackingService(db, _actor, perms, _time, ids);
        var images = new ImageLibraryService(db, _actor, perms, imgProc, fileStore, _time, ids);
        var moodBoards = new MoodBoardService(db, _actor, perms, admin, fileStore, images, pdfRenderer, _time, ids);
        var finance = new FinancialDocumentService(db, _actor, perms, locks, admin, formatting, wordSvc, pdfRenderer, fileStore, images, _time, ids);
        var forecast = new ForecastingService(db, perms);

        return new TestHarness(db, cache, locks, clientsJobs, timeSvc, images, moodBoards, finance, forecast, wordSvc);
    }

    [Fact]
    public async Task JobSearch_BeyondLastPageReturnsEmptyWithoutOffsetOverflow()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Coastal Builders" });
        var group = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Display Homes" });
        await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest { JobGroupId = group.Id, Name = "Job Alpha", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD" });

        var result = await h.ClientsJobs.ListJobsAsync(client.Id, group.Id,
            new PagedRequest { Page = int.MaxValue, PageSize = 25, Search = "alpha" });
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(int.MaxValue, result.Page);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task LineItemCreate_RollsBackIfLeaseExpiresBeforeCommit()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Lease client" });
        var group = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Lease group" });
        var job = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest
        {
            JobGroupId = group.Id, Name = "Lease job", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD"
        });
        var lease = await h.Locks.RequestModifyAsync(LockableRecordType.Job, job.Id);
        var advancingLocks = new ExpiringLock(h.Locks, _time);
        var service = new ProductAndLineItemService(h.Db, _actor, new PermissionEvaluator(_actor),
            advancingLocks, h.Cache, _time, new TimeOrderedIdGenerator(_time));
        var error = await Assert.ThrowsAsync<ConflictServiceException>(() => service.AddJobLineItemAsync(job.Id,
            new UpsertJobLineItemRequest
            {
                ClientId = client.Id, Title = "Must roll back", Currency = "AUD",
                LeaseId = lease.LeaseId, FencingVersion = lease.FencingVersion
            }));
        Assert.Equal(ErrorCodes.EditLockExpired, error.ErrorCode);
        h.Db.ChangeTracker.Clear();
        Assert.False(await h.Db.JobLineItems.AnyAsync(i => i.Title == "Must roll back"));
        Assert.False(await h.Db.AuditEntries.AnyAsync(a => a.EntityType == "JobLineItemEntity"));
    }

    [Fact]
    public async Task TimeTracking_CancelDeleteStopAtResumeAtAndMultiTimerModal_WorkEndToEnd()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Coastal Builders" });
        var grp = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Display Homes" });
        var jobA = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest { JobGroupId = grp.Id, Name = "Job Alpha", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD" });
        var jobB = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest { JobGroupId = grp.Id, Name = "Job Beta", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD" });
        var jobC = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest { JobGroupId = grp.Id, Name = "Job Gamma", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD" });

        // 1. Start on Job A, advance 30m, pause, advance 20m, Resume At 10m ago (valid), advance 15m, Stop At 5m ago
        var startA = await h.Time.StartTimerAsync(new StartTimerRequest { JobId = jobA.Id, Description = "Concept board" });
        Assert.False(startA.RequiresMultiTimerChoice);
        var timerA = startA.StartedEntry!;

        _time.Advance(TimeSpan.FromMinutes(30));
        timerA = await h.Time.PauseTimerAsync(timerA.Id, timerA.ConcurrencyToken);
        Assert.Equal(1800, timerA.DurationSeconds);

        _time.Advance(TimeSpan.FromMinutes(20));
        var resumeAtTime = _time.GetUtcNow().AddMinutes(-10);

        // Future time rejected
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            h.Time.PreviewResumeAtAsync(timerA.Id, _time.GetUtcNow().AddMinutes(5)));

        var resumePreview = await h.Time.PreviewResumeAtAsync(timerA.Id, resumeAtTime);
        Assert.Equal(2400, resumePreview.ResultingDurationSeconds); // 30m + 10m = 40m
        timerA = await h.Time.ResumeAtAsync(timerA.Id, new TimerAtTimeRequest
        {
            EffectiveTimeUtc = resumeAtTime,
            ConcurrencyToken = timerA.ConcurrencyToken,
            Confirmed = true
        });
        Assert.Equal("Running", timerA.State);

        _time.Advance(TimeSpan.FromMinutes(15)); // Now 25m since resumeAtTime
        var stopAtTime = _time.GetUtcNow().AddMinutes(-5); // 20m worked in 2nd interval -> total 50m = 3000s
        var stopPreview = await h.Time.PreviewStopAtAsync(timerA.Id, stopAtTime);
        Assert.Equal(3000, stopPreview.ResultingDurationSeconds);
        timerA = await h.Time.StopAtAsync(timerA.Id, new TimerAtTimeRequest
        {
            EffectiveTimeUtc = stopAtTime,
            ConcurrencyToken = timerA.ConcurrencyToken,
            Confirmed = true
        });
        Assert.Equal("Stopped", timerA.State);
        Assert.Equal(3000, timerA.DurationSeconds);

        // 2. Multi-timer modal: Start on Job A, then start on Job B -> requires choice
        var runA = (await h.Time.StartTimerAsync(new StartTimerRequest { JobId = jobA.Id })).StartedEntry!;
        _time.Advance(TimeSpan.FromMinutes(5));

        var conflictB = await h.Time.StartTimerAsync(new StartTimerRequest { JobId = jobB.Id });
        Assert.True(conflictB.RequiresMultiTimerChoice);
        Assert.Single(conflictB.RunningTimersOnOtherJobs);
        Assert.Equal(runA.Id, conflictB.RunningTimersOnOtherJobs[0].TimeEntryId);

        // Choose StopExisting -> stops runA and starts B atomically
        var startedB = await h.Time.StartTimerAsync(new StartTimerRequest
        {
            JobId = jobB.Id,
            ConflictResolution = "StopExisting",
            ObservedRunningTimers = [new ObservedRunningTimerRef { TimeEntryId = runA.Id, ConcurrencyToken = conflictB.RunningTimersOnOtherJobs[0].ConcurrencyToken }]
        });
        Assert.False(startedB.RequiresMultiTimerChoice);
        Assert.Contains(runA.Id, startedB.StoppedTimerIds);
        var runB = startedB.StartedEntry!;

        // Now start on Job C with KeepRunning -> both B and C run concurrently!
        var conflictC = await h.Time.StartTimerAsync(new StartTimerRequest { JobId = jobC.Id });
        Assert.True(conflictC.RequiresMultiTimerChoice);
        var startedC = await h.Time.StartTimerAsync(new StartTimerRequest
        {
            JobId = jobC.Id,
            ConflictResolution = "KeepRunning",
            ObservedRunningTimers = [new ObservedRunningTimerRef { TimeEntryId = runB.Id, ConcurrencyToken = conflictC.RunningTimersOnOtherJobs[0].ConcurrencyToken }]
        });
        Assert.True(startedC.StartedEntry!.HasIntentionalOverlap);

        // 3. Cancel running timer C -> Cancelled, 0 billable duration
        var cancelledC = await h.Time.CancelTimerAsync(startedC.StartedEntry.Id, startedC.StartedEntry.ConcurrencyToken);
        Assert.Equal("Cancelled", cancelledC.State);
        Assert.False(cancelledC.IsBillable);
        Assert.Equal(0, cancelledC.DurationSeconds);

        // 4. Delete running timer B -> atomically cancels & soft-deletes; restore does not restart
        var freshB = (await h.Time.ListActiveTimersForCurrentUserAsync()).First(x => x.Id == runB.Id);
        await h.Time.SoftDeleteAsync(freshB.Id, freshB.ConcurrencyToken);
        Assert.Empty(await h.Time.ListActiveTimersForCurrentUserAsync());

        var deletedList = await h.Time.ListForJobAsync(jobB.Id, includeDeleted: true);
        var deletedB = deletedList.First(x => x.Id == runB.Id);
        Assert.True(deletedB.IsDeleted);
        Assert.Equal("Cancelled", deletedB.State);
        await h.Time.RestoreAsync(deletedB.Id, deletedB.ConcurrencyToken);
        Assert.Empty(await h.Time.ListActiveTimersForCurrentUserAsync());
    }

    [Fact]
    public async Task ImageUpload_PostCommitReadFailurePreservesCommittedFiles()
    {
        var interceptor = new FailImageReadInterceptor();
        await using var h = await CreateHarnessAsync(interceptor);
        interceptor.Enabled = true;
        var bytes = CreateSyntheticImage(20, 20, SKEncodedImageFormat.Png);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Images.UploadAsync("Committed image", null, null, true, true, null, null, null, null, bytes, "image/png"));

        var asset = await h.Db.ImageAssets.AsNoTracking().SingleAsync(i => i.Title == "Committed image");
        Assert.NotEmpty((await h.Images.ReadImageBytesAsync(asset.Id, false)).Bytes);
        Assert.NotEmpty((await h.Images.ReadImageBytesAsync(asset.Id, true)).Bytes);
        Assert.True(await h.Db.AuditEntries.AnyAsync(a => a.EntityType == nameof(ImageAssetEntity) && a.EntityId == asset.Id.ToString("D")));
    }

    [Fact]
    public async Task ImageUpload_UncertainCommitPreservesReferencedFiles()
    {
        var interceptor = new FailAfterCommitInterceptor();
        await using var h = await CreateHarnessAsync(interceptor);
        interceptor.Enabled = true;
        var bytes = CreateSyntheticImage(20, 20, SKEncodedImageFormat.Png);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Images.UploadAsync("Uncertain image", null, null, true, true, null, null, null, null, bytes, "image/png"));

        h.Db.ChangeTracker.Clear();
        var asset = await h.Db.ImageAssets.AsNoTracking().SingleAsync(i => i.Title == "Uncertain image");
        Assert.NotEmpty((await h.Images.ReadImageBytesAsync(asset.Id, false)).Bytes);
        Assert.NotEmpty((await h.Images.ReadImageBytesAsync(asset.Id, true)).Bytes);
    }

    [Fact]
    public async Task ImageReads_FilterJobImagesAndRecheckActiveHierarchy()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Private client" });
        var group = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Group" });
        var job = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest
        {
            JobGroupId = group.Id, Name = "Private job", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD"
        });
        var bytes = CreateSyntheticImage(20, 20, SKEncodedImageFormat.Png);
        var publicImage = await h.Images.UploadAsync("Global", null, null, true, true, null, null, null, null, bytes, "image/png");
        var jobImage = await h.Images.UploadAsync("Job image", null, null, true, false, client.Id, job.Id, null, null, bytes, "image/png");
        await h.Images.UploadAsync("Private orphan", null, null, true, false, null, null, null, null, bytes, "image/png");
        var derivedId = Guid.NewGuid();
        h.Db.ImageAssets.Add(new ImageAssetEntity
        {
            Id = derivedId, Title = "Derived", StorageKey = "future-derived", ThumbnailStorageKey = "future-derived-thumb",
            AiSourceImageAssetId = publicImage.Id, IsGlobalLibrary = true
        });
        await h.Db.SaveChangesAsync();
        var actor = new LimitedActor(Guid.NewGuid(), ModuleKeys.ImageLibrary);
        var images = CreateImageReader(h, actor);

        var visible = await images.ListAsync(new ImageLibraryFilterRequest());
        Assert.Equal(1, visible.TotalCount);
        Assert.Equal(publicImage.Id, Assert.Single(visible.Items).Id);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(derivedId, false));
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(jobImage.Id, false));
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(jobImage.Id, true));
        await Assert.ThrowsAsync<ConflictServiceException>(() => h.Images.SoftDeleteAsync(publicImage.Id, publicImage.ConcurrencyToken));
        actor.GrantDelete(ModuleKeys.ImageLibrary);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.SoftDeleteAsync(jobImage.Id, jobImage.ConcurrencyToken));

        actor.Grant(ModuleKeys.Clients, ModuleKeys.Jobs);
        Assert.Equal(2, (await images.ListAsync(new ImageLibraryFilterRequest())).TotalCount);
        Assert.NotEmpty((await images.ReadImageBytesAsync(jobImage.Id, false)).Bytes);
        Assert.NotEmpty((await images.ReadImageBytesAsync(jobImage.Id, true)).Bytes);

        actor.Revoke(ModuleKeys.Jobs);
        Assert.Equal(1, (await images.ListAsync(new ImageLibraryFilterRequest())).TotalCount);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(jobImage.Id, false));
        actor.Grant(ModuleKeys.Jobs);
        var storedClient = await h.Db.Clients.SingleAsync(c => c.Id == client.Id);
        storedClient.DeletedAtUtc = _time.GetUtcNow();
        await h.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(jobImage.Id, false));
    }

    [Fact]
    public async Task ImageRestore_RequiresActiveAssociationPermissions()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Client" });
        var group = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Group" });
        var job = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest
        {
            JobGroupId = group.Id, Name = "Job", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD"
        });
        var bytes = CreateSyntheticImage(20, 20, SKEncodedImageFormat.Png);
        var image = await h.Images.UploadAsync("Linked", null, null, true, true, client.Id, job.Id, null, null, bytes, "image/png");
        await h.Images.SoftDeleteAsync(image.Id, image.ConcurrencyToken);
        var deleted = await h.Db.ImageAssets.IgnoreQueryFilters().SingleAsync(i => i.Id == image.Id);
        var actor = new LimitedActor(Guid.NewGuid());
        actor.GrantDelete(ModuleKeys.ImageLibrary);
        var images = CreateImageReader(h, actor);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.RestoreAsync(image.Id, deleted.ConcurrencyToken));
        actor.Grant(ModuleKeys.Clients, ModuleKeys.Jobs);
        await images.RestoreAsync(image.Id, deleted.ConcurrencyToken);

        var restored = await h.Db.ImageAssets.SingleAsync(i => i.Id == image.Id);
        await h.Images.SoftDeleteAsync(image.Id, restored.ConcurrencyToken);
        var association = await h.Db.ImageAssociations.SingleAsync(a => a.ImageAssetId == image.Id);
        association.DeletedAtUtc = _time.GetUtcNow();
        await h.Db.SaveChangesAsync();
        actor.Revoke(ModuleKeys.Jobs);
        actor.Revoke(ModuleKeys.Clients);
        deleted = await h.Db.ImageAssets.IgnoreQueryFilters().SingleAsync(i => i.Id == image.Id);
        await images.RestoreAsync(image.Id, deleted.ConcurrencyToken);
        Assert.NotEmpty((await images.ReadImageBytesAsync(image.Id, false)).Bytes);
    }

    [Fact]
    public async Task ImageWrites_RequireAccessToSourceAndConsistentRelatedRecords()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Client" });
        var group = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Group" });
        var job = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest
        {
            JobGroupId = group.Id, Name = "Job", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD"
        });
        var bytes = CreateSyntheticImage(20, 20, SKEncodedImageFormat.Png);
        var jobImage = await h.Images.UploadAsync("Job image", null, null, true, false, client.Id, job.Id, null, null, bytes, "image/png");
        var actor = new LimitedActor(Guid.NewGuid());
        actor.GrantAlter(ModuleKeys.ImageLibrary);
        actor.Grant(ModuleKeys.Jobs);
        var images = CreateImageReader(h, actor);
        await Assert.ThrowsAsync<ForbiddenServiceException>(() =>
            images.UploadAsync("New", null, null, true, false, null, job.Id, null, null, bytes, "image/png"));
        await Assert.ThrowsAsync<NotFoundServiceException>(() =>
            images.AddAssociationAsync(jobImage.Id, null, null, null, ImageAssociationType.Library));

        actor.Grant(ModuleKeys.Clients);
        await Assert.ThrowsAsync<ValidationServiceException>(() =>
            images.UploadAsync("New", null, null, true, false, Guid.NewGuid(), job.Id, null, null, bytes, "image/png"));
        await Assert.ThrowsAsync<ForbiddenServiceException>(() =>
            images.AddAssociationAsync(jobImage.Id, null, null, Guid.NewGuid(), ImageAssociationType.Product));
        actor.Grant(ModuleKeys.ProductCatalog);
        await Assert.ThrowsAsync<NotFoundServiceException>(() =>
            images.AddAssociationAsync(jobImage.Id, null, null, Guid.NewGuid(), ImageAssociationType.Product));
    }

    [Fact]
    public async Task ImageReads_RequirePermissionsForDirectReferencesAndBoardExport()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Client" });
        var group = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Group" });
        var job = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest
        {
            JobGroupId = group.Id, Name = "Job", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD"
        });
        var bytes = CreateSyntheticImage(20, 20, SKEncodedImageFormat.Png);
        var image = await h.Images.UploadAsync("Linked", null, null, true, true, null, null, null, null, bytes, "image/png");
        h.Db.Products.Add(new ProductEntity { Id = Guid.NewGuid(), Name = "Linked product", PrimaryImageAssetId = image.Id });
        h.Db.Receipts.Add(new ReceiptEntity
        {
            Id = Guid.NewGuid(), JobId = job.Id, ImageAssetId = image.Id, MerchantName = "Merchant",
            ReceiptDate = new DateOnly(2026, 11, 25), UploadedByUserId = _actor.ActorId!.Value
        });
        await h.Db.SaveChangesAsync();
        var board = await h.MoodBoards.CreateBoardAsync(job.Id, new UpsertMoodBoardRequest { Name = "Board" });
        var canvas = board.Canvases[0];
        await h.MoodBoards.SaveCanvasElementsAsync(canvas.Id, new SaveCanvasElementsRequest
        {
            ConcurrencyToken = canvas.ConcurrencyToken,
            Elements = [new MoodBoardElementDto
            {
                Id = Guid.Empty, ElementType = MoodBoardElementType.Image, ImageAssetId = image.Id,
                FontSizePx = 16, TextColorHex = "#000000", X = 0, Y = 0, Width = 100, Height = 100, ZIndex = 0
            }]
        });

        var actor = new LimitedActor(_actor.ActorId!.Value, ModuleKeys.ImageLibrary, ModuleKeys.Clients, ModuleKeys.Jobs);
        var images = CreateImageReader(h, actor);
        Assert.Equal(0, (await images.ListAsync(new ImageLibraryFilterRequest())).TotalCount);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(image.Id, false));
        actor.Grant(ModuleKeys.ProductCatalog);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(image.Id, false));
        actor.Grant(ModuleKeys.Receipts);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => images.ReadImageBytesAsync(image.Id, false));
        actor.Grant(ModuleKeys.MoodBoards);
        Assert.Single((await images.ListAsync(new ImageLibraryFilterRequest())).Items);
        Assert.NotEmpty((await images.ReadImageBytesAsync(image.Id, false)).Bytes);
        actor.Revoke(ModuleKeys.ImageLibrary);
        await Assert.ThrowsAsync<ForbiddenServiceException>(() => images.ReadImageBytesAsync(image.Id, false));
        actor.Grant(ModuleKeys.ImageLibrary);

        actor.Revoke(ModuleKeys.Receipts);
        var ids = new TimeOrderedIdGenerator(_time);
        var store = new LocalPrivateFileStore(Options.Create(new LavariStorageOptions { StorageRoot = _tempDir }), ids, _time);
        var permissions = new PermissionEvaluator(actor);
        var admin = new AdminService(h.Db, actor, permissions, new Pbkdf2PasswordHasher(), h.Cache, _time, ids);
        var boards = new MoodBoardService(h.Db, actor, permissions, admin, store, images,
            new SkiaPdfRenderer(new FormattingService()), _time, ids);
        await Assert.ThrowsAsync<NotFoundServiceException>(() => boards.SaveAsPdfAsync(board.Id, null));
    }

    private ImageLibraryService CreateImageReader(TestHarness h, LimitedActor actor)
    {
        var ids = new TimeOrderedIdGenerator(_time);
        return new ImageLibraryService(h.Db, actor, new PermissionEvaluator(actor), new SkiaImageProcessingService(),
            new LocalPrivateFileStore(Options.Create(new LavariStorageOptions { StorageRoot = _tempDir }), ids, _time),
            _time, ids);
    }

    [Fact]
    public async Task Image1080pProcessing_MoodBoardsAndPdfExport_WorkEndToEnd()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Harbour Living" });
        var grp = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Renovations" });
        var job = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest { JobGroupId = grp.Id, Name = "Penthouse", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD" });

        // Create real 4000x3000 landscape JPEG and 3000x4000 portrait PNG
        var landscapeBytes = CreateSyntheticImage(4000, 3000, SKEncodedImageFormat.Jpeg);
        var portraitBytes = CreateSyntheticImage(3000, 4000, SKEncodedImageFormat.Png);

        var uploadedLand = await h.Images.UploadAsync("Sofa Hero", "Living room sofa", "Supplier A", true, true, client.Id, job.Id, null, null, landscapeBytes, "image/jpeg");
        Assert.Equal(1440, uploadedLand.Width);
        Assert.Equal(1080, uploadedLand.Height);
        Assert.True(uploadedLand.WasDownscaledFromLargerOriginal);

        var uploadedPort = await h.Images.UploadAsync("Tall Lamp", null, "Upload", true, true, client.Id, job.Id, null, null, portraitBytes, "image/png");
        Assert.Equal(1080, uploadedPort.Width);
        Assert.Equal(1440, uploadedPort.Height);

        // Verify filter by job works
        var filtered = await h.Images.ListAsync(new ImageLibraryFilterRequest { JobId = job.Id });
        Assert.Equal(2, filtered.TotalCount);

        // Create mood board with 2 branded canvases, add image & text, export full board PDF
        var board = await h.MoodBoards.CreateBoardAsync(job.Id, new UpsertMoodBoardRequest { Name = "Living Concept" });
        Assert.Single(board.Canvases);
        var canvas1 = board.Canvases[0];
        var canvas2 = await h.MoodBoards.AddCanvasAsync(board.Id, new UpsertCanvasRequest { Name = "Bedroom Concept" });

        canvas1 = await h.MoodBoards.SaveCanvasElementsAsync(canvas1.Id, new SaveCanvasElementsRequest
        {
            ConcurrencyToken = canvas1.ConcurrencyToken,
            Elements =
            [
                new MoodBoardElementDto { Id = Guid.Empty, ElementType = MoodBoardElementType.Image, ImageAssetId = uploadedLand.Id, FontSizePx = 16, TextColorHex = "#1F1B16", X = 40, Y = 40, Width = 600, Height = 450, ZIndex = 1 },
                new MoodBoardElementDto { Id = Guid.Empty, ElementType = MoodBoardElementType.Text, TextContent = "Warm travertine & linen palette", FontSizePx = 28, TextColorHex = "#1F1B16", X = 700, Y = 60, Width = 500, Height = 80, ZIndex = 2 }
            ]
        });

        // Deleting an image referenced by the mood board must be blocked
        await Assert.ThrowsAsync<ConflictServiceException>(() => h.Images.SoftDeleteAsync(uploadedLand.Id, uploadedLand.ConcurrencyToken));

        var pdfArtifact = await h.MoodBoards.SaveAsPdfAsync(board.Id, singleCanvasId: null);
        var (pdfBytes, contentType, fileName) = await h.Finance.DownloadArtifactAsync(pdfArtifact.Id);
        Assert.Equal("application/pdf", contentType);
        Assert.EndsWith(".pdf", fileName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdfBytes, 0, 5));
        Assert.True(pdfBytes.Length > 1000);
        Assert.NotNull(canvas2);
    }

    [Fact]
    public async Task QuotesInvoicesWordTemplatePdfIssuanceAndForecasting_WorkEndToEnd()
    {
        await using var h = await CreateHarnessAsync();
        var client = await h.ClientsJobs.CreateClientAsync(new UpsertClientRequest { Name = "Bayside Homes Pty Ltd" });
        var grp = await h.ClientsJobs.CreateJobGroupAsync(new UpsertJobGroupRequest { ClientId = client.Id, Name = "Furniture and Styling" });
        var job = await h.ClientsJobs.CreateJobAsync(new UpsertJobRequest { JobGroupId = grp.Id, Name = "Byron Bay Display", Status = "Active", TimeZoneIana = "Australia/Sydney", DefaultCurrency = "AUD", PoNumber = "PO-2026-99" });

        // Create Quote with manual lines + discount
        var quote = await h.Finance.CreateAsync(new CreateFinancialDocumentRequest
        {
            JobId = job.Id,
            DocumentType = FinancialDocumentType.Quote,
            IssueDate = new DateOnly(2026, 11, 25),
            Lines =
            [
                new SaveFinancialDocumentLineInput { Title = "Console - Entry", Details = "Porcelain 1500mm", QuantityMilli = 1000, UnitPriceCents = 126_000 },
                new SaveFinancialDocumentLineInput { Title = "Dining Chair", QuantityMilli = 6000, UnitPriceCents = 48_900 }
            ],
            Adjustments =
            [
                new SaveFinancialDocumentAdjustmentInput { AdjustmentType = FinancialAdjustmentType.Discount, Description = "Trade loyalty", AmountCents = 19_400, IsPreTax = true }
            ]
        });

        // Subtotal = 126,000 + 293,400 = 419,400; less 19,400 = 400,000; 10% GST = 40,000; Total = 440,000 ($4,400.00)
        Assert.Equal(419_400, quote.SubtotalCents);
        Assert.Equal(440_000, quote.AppliedTotalCents);

        // Acquire Modify lock on quote before editing
        var lockStatus = await h.Locks.RequestModifyAsync(LockableRecordType.Quote, quote.Id);
        Assert.True(lockStatus.IsHeldByCurrentSession);

        // Updating total without confirmation triggers warning!
        var warnResult = await h.Finance.UpdateAsync(quote.Id, new UpdateFinancialDocumentRequest
        {
            CalculationMode = "ManualOverride",
            TaxBasisPoints = 1000,
            TaxLabel = "10% GST",
            DepositBasisPoints = 7000,
            DepositLabel = "70% DEPOSIT REQUIRED",
            IssueDate = quote.IssueDate,
            ManualTotalCents = 420_000,
            ManualOverrideReason = "Agreed fixed price",
            Lines = quote.Lines.Select(l => new SaveFinancialDocumentLineInput { Title = l.Title, Details = l.Details, QuantityMilli = l.QuantityMilli, UnitPriceCents = l.UnitPriceCents }).ToArray(),
            Adjustments = quote.Adjustments.Select(a => new SaveFinancialDocumentAdjustmentInput { AdjustmentType = a.AdjustmentType, Description = a.Description, AmountCents = a.AmountCents, IsPreTax = a.IsPreTax }).ToArray(),
            ConcurrencyToken = quote.ConcurrencyToken,
            LeaseId = lockStatus.LeaseId,
            FencingVersion = lockStatus.FencingVersion,
            ConfirmTotalChange = false
        });
        Assert.True(warnResult.RequiresTotalChangeConfirmation);
        Assert.Equal(440_000, warnResult.Warning!.PreviousAppliedTotalCents);
        Assert.Equal(420_000, warnResult.Warning.ProposedAppliedTotalCents);

        // Confirm with exact bound totals
        var confirmed = await h.Finance.UpdateAsync(quote.Id, new UpdateFinancialDocumentRequest
        {
            CalculationMode = "ManualOverride",
            TaxBasisPoints = 1000,
            TaxLabel = "10% GST",
            DepositBasisPoints = 7000,
            DepositLabel = "70% DEPOSIT REQUIRED",
            IssueDate = quote.IssueDate,
            ManualTotalCents = 420_000,
            ManualOverrideReason = "Agreed fixed price",
            Lines = quote.Lines.Select(l => new SaveFinancialDocumentLineInput { Title = l.Title, Details = l.Details, QuantityMilli = l.QuantityMilli, UnitPriceCents = l.UnitPriceCents }).ToArray(),
            Adjustments = quote.Adjustments.Select(a => new SaveFinancialDocumentAdjustmentInput { AdjustmentType = a.AdjustmentType, Description = a.Description, AmountCents = a.AmountCents, IsPreTax = a.IsPreTax }).ToArray(),
            ConcurrencyToken = quote.ConcurrencyToken,
            LeaseId = lockStatus.LeaseId,
            FencingVersion = lockStatus.FencingVersion,
            ConfirmTotalChange = true,
            ExpectedPreviousCalculatedCents = warnResult.Warning.PreviousCalculatedTotalCents,
            ExpectedPreviousAppliedCents = warnResult.Warning.PreviousAppliedTotalCents,
            ExpectedProposedCalculatedCents = warnResult.Warning.ProposedCalculatedTotalCents,
            ExpectedProposedAppliedCents = warnResult.Warning.ProposedAppliedTotalCents
        });
        Assert.False(confirmed.RequiresTotalChangeConfirmation);
        Assert.Equal(420_000, confirmed.Document!.AppliedTotalCents);

        // Convert quote to invoice, generate Word .docx & PDF, and Issue!
        var invoice = await h.Finance.ConvertQuoteToInvoiceAsync(quote.Id);
        var invLock = await h.Locks.RequestModifyAsync(LockableRecordType.Invoice, invoice.Id);
        var issued = await h.Finance.IssueAsync(invoice.Id, invoice.ConcurrencyToken, invLock.LeaseId, invLock.FencingVersion);
        Assert.Equal("Issued", issued.Status);
        Assert.NotNull(issued.ApprovedWordArtifactId);
        Assert.NotNull(issued.ApprovedPdfArtifactId);

        // Verify generated Word .docx opens with OpenXML, has client/job/lines and ZERO unresolved {{...}} placeholders!
        var (docxBytes, _, _) = await h.Finance.DownloadArtifactAsync(issued.ApprovedWordArtifactId!.Value);
        Assert.Empty(h.WordSvc.ExtractPlaceholders(docxBytes));
        using (var ms = new MemoryStream(docxBytes))
        using (var wdoc = WordprocessingDocument.Open(ms, false))
        {
            var bodyText = wdoc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Bayside Homes Pty Ltd", bodyText);
            Assert.Contains("Byron Bay Display", bodyText);
            Assert.Contains("Console - Entry", bodyText);
            Assert.Contains("$4,200.00", bodyText);
        }

        // Verify generated PDF is valid %PDF-
        var (pdfBytes, _, _) = await h.Finance.DownloadArtifactAsync(issued.ApprovedPdfArtifactId!.Value);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdfBytes, 0, 5));

        // Verify forecast does NOT double-count the converted quote + issued invoice!
        var report = await h.Forecast.CalculateForecastAsync(new ForecastRequest
        {
            StartDate = new DateOnly(2026, 11, 1),
            EndDate = new DateOnly(2026, 11, 30),
            ReportingTimeZone = "Australia/Sydney",
            Currency = "AUD"
        });
        Assert.Equal(420_000, report.ActualRevenueCents);
        Assert.Equal(0, report.PlannedRevenueCents);
    }

    private static byte[] CreateSyntheticImage(int w, int h, SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(0xD4, 0xC8, 0xB8));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(format, 75);
        return data.ToArray();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class TestHarness(
        LavariDbContext db,
        AppCacheService cache,
        EditLockService locks,
        ClientAndJobService clientsJobs,
        TimeTrackingService time,
        ImageLibraryService images,
        MoodBoardService moodBoards,
        FinancialDocumentService finance,
        ForecastingService forecast,
        OpenXmlWordTemplateService wordSvc) : IAsyncDisposable
    {
        public LavariDbContext Db { get; } = db;
        public AppCacheService Cache { get; } = cache;
        public EditLockService Locks { get; } = locks;
        public ClientAndJobService ClientsJobs { get; } = clientsJobs;
        public TimeTrackingService Time { get; } = time;
        public ImageLibraryService Images { get; } = images;
        public MoodBoardService MoodBoards { get; } = moodBoards;
        public FinancialDocumentService Finance { get; } = finance;
        public ForecastingService Forecast { get; } = forecast;
        public OpenXmlWordTemplateService WordSvc { get; } = wordSvc;

        public async ValueTask DisposeAsync()
        {
            Cache.Dispose();
            await Db.DisposeAsync();
        }
    }

    private sealed class FailAfterCommitInterceptor : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Enabled = false;
                throw new InvalidOperationException("Simulated failure after commit.");
            }
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class FailImageReadInterceptor : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("FROM \"ImageAssets\"", StringComparison.Ordinal))
            {
                Enabled = false;
                throw new InvalidOperationException("Simulated image read failure.");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class TestTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class ExpiringLock(IEditLockService inner, TestTime time) : IEditLockService
    {
        private int _checks;
        public async Task EnsureActiveLeaseHeldAsync(LockableRecordType type, Guid id, Guid? leaseId, long? fence, CancellationToken ct = default)
        {
            if (++_checks == 2) time.Advance(TimeSpan.FromMinutes(6));
            await inner.EnsureActiveLeaseHeldAsync(type, id, leaseId, fence, ct);
        }
        public Task<EditLockStatusDto> GetStatusAsync(LockableRecordType type, Guid id, CancellationToken ct = default) => inner.GetStatusAsync(type, id, ct);
        public Task<EditLockStatusDto> RequestModifyAsync(LockableRecordType type, Guid id, CancellationToken ct = default) => inner.RequestModifyAsync(type, id, ct);
        public Task<EditLockStatusDto> ClaimReservationAsync(LockableRecordType type, Guid id, CancellationToken ct = default) => inner.ClaimReservationAsync(type, id, ct);
        public Task<EditLockStatusDto> RenewLeaseAsync(LockableRecordType type, Guid id, Guid lease, long fence, CancellationToken ct = default) => inner.RenewLeaseAsync(type, id, lease, fence, ct);
        public Task<EditLockStatusDto> ReleaseLeaseAsync(LockableRecordType type, Guid id, Guid? lease = null, long? fence = null, CancellationToken ct = default) => inner.ReleaseLeaseAsync(type, id, lease, fence, ct);
        public Task<EditLockStatusDto> CancelQueueEntryAsync(LockableRecordType type, Guid id, CancellationToken ct = default) => inner.CancelQueueEntryAsync(type, id, ct);
        public Task<int> SweepExpiredLeasesAsync(CancellationToken ct = default) => inner.SweepExpiredLeasesAsync(ct);
        public Task<IReadOnlyList<InAppNotificationDto>> GetNotificationsAsync(bool unreadOnly = false, CancellationToken ct = default) => inner.GetNotificationsAsync(unreadOnly, ct);
        public Task MarkNotificationReadAsync(Guid id, CancellationToken ct = default) => inner.MarkNotificationReadAsync(id, ct);
    }

    private sealed class LimitedActor : ICurrentActorContext
    {
        private readonly Dictionary<string, AccessLevel> _grants = new(StringComparer.Ordinal);

        public LimitedActor(Guid id, params string[] modules)
        {
            ActorId = id;
            Grant(modules);
        }

        public void Grant(params string[] modules)
        {
            foreach (var module in modules) _grants[module] = AccessLevel.Read;
        }

        public void GrantAlter(params string[] modules)
        {
            foreach (var module in modules) _grants[module] = AccessLevel.Alter;
        }

        public void GrantDelete(params string[] modules)
        {
            foreach (var module in modules) _grants[module] = AccessLevel.Delete;
        }

        public void Revoke(string module) => _grants.Remove(module);
        public bool IsAuthenticated => true;
        public bool IsSystemPrincipal => false;
        public Guid? ActorId { get; }
        public string ActorDisplayName => "Limited";
        public string CorrelationId => "image-access-test";
        public string EditingSessionId => "image-access-test";
        public bool IsFullAccess => false;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions => _grants;
    }

    private sealed class TestActor : ICurrentActorContext
    {
        private static readonly IReadOnlyDictionary<string, AccessLevel> Full =
            PermissionRegistry.ComputeEffectiveGrants(true, [new RoleGrantSnapshot(Guid.NewGuid(), "Full Access", true, new Dictionary<string, AccessLevel>())]);

        public bool IsAuthenticated => true;
        public bool IsSystemPrincipal => false;
        public Guid? ActorId { get; set; } = Guid.CreateVersion7();
        public string ActorDisplayName { get; set; } = "SuperAdmin";
        public string CorrelationId => "corr-biz-test";
        public string EditingSessionId => "sess-biz-1";
        public bool IsFullAccess => true;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions => Full;
    }
}
