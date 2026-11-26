using BenchmarkDotNet.Attributes;
using Lavari.Application.Abstractions;
using Lavari.Application.Security;
using Lavari.Contracts.Business;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lavari.Benchmarks;

[MemoryDiagnoser]
public class SupplierCaptureBenchmarks
{
    private SqliteConnection _connection = null!;
    private LavariDbContext _db = null!;
    private IntegrationService _service = null!;
    private SupplierCaptureRequest _request = null!;

    [Params(1000, 16000)]
    public int TextLength { get; set; }

    [IterationSetup]
    public void Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var actor = new BenchmarkActor();
        var time = TimeProvider.System;
        var ids = new TimeOrderedIdGenerator(time);
        _db = new LavariDbContext(new DbContextOptionsBuilder<LavariDbContext>()
            .UseSqlite(_connection).Options, time, actor, ids);
        _db.Database.EnsureCreated();

        var client = new ClientEntity { Id = Guid.NewGuid(), Name = "Synthetic client" };
        var group = new JobGroupEntity { Id = Guid.NewGuid(), ClientId = client.Id, Client = client, Name = "Synthetic group" };
        var job = new JobEntity { Id = Guid.NewGuid(), ClientId = client.Id, Client = client, JobGroupId = group.Id, JobGroup = group, Name = "Synthetic job" };
        _db.AddRange(client, group, job);
        _db.SaveChangesAsync().GetAwaiter().GetResult();
        _service = new IntegrationService(_db, actor, new PermissionEvaluator(actor),
            Options.Create(new LavariIntegrationsOptions()), ids);
        _request = new SupplierCaptureRequest
        {
            ClientId = client.Id,
            JobId = job.Id,
            SourceUrl = "https://supplier.example/item",
            PageTextContent = "Sample chair\n$125.00\n" + new string('x', TextLength - 21),
            UserConfirmedConsent = true
        };
        _db.ChangeTracker.Clear();
    }

    [Benchmark]
    public Task<LlmExtractionDraftDto> CaptureAndAudit()
        => _service.ExtractSupplierPageDraftAsync(_request);

    [IterationCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _connection.Dispose();
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
                [ModuleKeys.SupplierCapture] = AccessLevel.Alter,
                [ModuleKeys.Clients] = AccessLevel.Read,
                [ModuleKeys.Jobs] = AccessLevel.Read
            };
    }
}
