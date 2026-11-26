using BenchmarkDotNet.Attributes;
using Lavari.Application.Abstractions;
using Lavari.Application.Security;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Media;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lavari.Benchmarks;

[MemoryDiagnoser]
public class ImageAccessBenchmarks
{
    private SqliteConnection _connection = null!;
    private LavariDbContext _db = null!;
    private ImageLibraryService _images = null!;

    [Params(100, 1000)]
    public int ImageCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        var actor = new BenchmarkActor();
        var time = TimeProvider.System;
        var ids = new TimeOrderedIdGenerator(time);
        _db = new LavariDbContext(new DbContextOptionsBuilder<LavariDbContext>()
            .UseSqlite(_connection).Options, time, actor, ids);
        await _db.Database.EnsureCreatedAsync();

        var client = new ClientEntity { Id = Guid.NewGuid(), Name = "Synthetic client" };
        var group = new JobGroupEntity { Id = Guid.NewGuid(), ClientId = client.Id, Client = client, Name = "Synthetic group" };
        var job = new JobEntity { Id = Guid.NewGuid(), ClientId = client.Id, Client = client, JobGroupId = group.Id, JobGroup = group, Name = "Synthetic job" };
        _db.AddRange(client, group, job);
        for (var index = 0; index < ImageCount; index++)
        {
            var image = new ImageAssetEntity
            {
                Id = Guid.NewGuid(), Title = $"Image {index:D5}",
                StorageKey = $"image-{index:D5}", ThumbnailStorageKey = $"thumb-{index:D5}",
                UploadedByUserId = actor.ActorId!.Value, IsGlobalLibrary = true
            };
            _db.ImageAssets.Add(image);
            if (index % 2 == 0)
                _db.ImageAssociations.Add(new ImageAssociationEntity
                {
                    Id = Guid.NewGuid(), ImageAsset = image, ImageAssetId = image.Id,
                    ClientId = client.Id, JobId = job.Id
                });
            if (index % 4 == 0)
                _db.Products.Add(new ProductEntity { Id = Guid.NewGuid(), Name = "Synthetic product", PrimaryImageAssetId = image.Id });
            if (index % 4 == 1)
                _db.Receipts.Add(new ReceiptEntity { Id = Guid.NewGuid(), Job = job, JobId = job.Id, ImageAssetId = image.Id });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _images = new ImageLibraryService(_db, actor, new PermissionEvaluator(actor),
            new SkiaImageProcessingService(), new UnusedFileStore(), time, ids);
    }

    [Benchmark]
    public Task<PagedResult<ImageAssetDto>> ListAuthorizedImages()
        => _images.ListAsync(new ImageLibraryFilterRequest { Page = 1, PageSize = 24 });

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class BenchmarkActor : ICurrentActorContext
    {
        public bool IsAuthenticated => true;
        public bool IsSystemPrincipal => false;
        public Guid? ActorId => Guid.Empty;
        public string ActorDisplayName => "Benchmark";
        public string CorrelationId => "benchmark";
        public string EditingSessionId => "benchmark";
        public bool IsFullAccess => false;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions { get; } =
            new Dictionary<string, AccessLevel>
            {
                [ModuleKeys.ImageLibrary] = AccessLevel.Read,
                [ModuleKeys.Clients] = AccessLevel.Read,
                [ModuleKeys.Jobs] = AccessLevel.Read
            };
    }

    private sealed class UnusedFileStore : IPrivateFileStore
    {
        public string StorageRoot => throw new NotSupportedException();
        public Task<string> SaveAsync(string category, string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string storageKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteTemporaryAsync(string storageKey, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
