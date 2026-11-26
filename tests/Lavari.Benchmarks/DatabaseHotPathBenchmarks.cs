using BenchmarkDotNet.Attributes;
using Lavari.Application.Abstractions;
using Lavari.Application.Security;
using Lavari.Contracts.Business;
using Lavari.Contracts.Common;
using Lavari.Domain.Security;
using Lavari.Infrastructure.Caching;
using Lavari.Infrastructure.Locking;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Persistence.Entities;
using Lavari.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lavari.Benchmarks;

[MemoryDiagnoser]
public class DatabaseHotPathBenchmarks
{
    private readonly BenchmarkActorContext _actor = new();
    private SqliteConnection _connection = null!;
    private LavariDbContext _db = null!;
    private ClientAndJobService _clientsAndJobs = null!;
    private ForecastingService _forecasting = null!;
    private Guid _clientId;

    [Params(25, 100)]
    public int JobCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var timeProvider = TimeProvider.System;
        var ids = new TimeOrderedIdGenerator(timeProvider);
        var permissions = new PermissionEvaluator(_actor);
        var options = new DbContextOptionsBuilder<LavariDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new LavariDbContext(options, timeProvider, _actor, ids);
        await _db.Database.EnsureCreatedAsync();

        var client = new ClientEntity { Id = Guid.NewGuid(), Name = "Benchmark Client" };
        var group = new JobGroupEntity
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Client = client,
            Name = "Benchmark Jobs"
        };
        _clientId = client.Id;

        var jobs = Enumerable.Range(0, JobCount)
            .Select(index => new JobEntity
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                Client = client,
                JobGroupId = group.Id,
                JobGroup = group,
                Name = $"Display Home {index:D4}",
                Status = "Active"
            })
            .ToArray();

        var documents = new List<FinancialDocumentEntity>(JobCount * 2);
        var procurement = new List<ProcurementItemEntity>(JobCount);
        var milestones = new List<JobDateEntity>(JobCount);
        var reportDate = new DateOnly(2026, 6, 15);
        var paidAt = new DateTimeOffset(2026, 6, 15, 2, 0, 0, TimeSpan.Zero);

        foreach (var job in jobs)
        {
            var quoteId = Guid.NewGuid();
            documents.Add(new FinancialDocumentEntity
            {
                Id = quoteId,
                JobId = job.Id,
                DocumentType = FinancialDocumentType.Quote,
                DocumentNumber = $"Q-{job.Id:N}",
                Status = "Draft",
                Currency = "AUD",
                AppliedTotalCents = 100_000,
                IssueDate = reportDate
            });
            documents.Add(new FinancialDocumentEntity
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                DocumentType = FinancialDocumentType.Invoice,
                DocumentNumber = $"I-{job.Id:N}",
                Status = "Issued",
                Currency = "AUD",
                AppliedTotalCents = 100_000,
                SourceQuoteId = quoteId,
                IssueDate = reportDate
            });

            var item = new ProcurementItemEntity
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                SupplierName = "Benchmark Supplier",
                Status = "Ordered",
                Currency = "AUD",
                TotalCostCents = 50_000,
                ExpectedDeliveryDate = reportDate
            };
            item.Payments.Add(new ProcurementPaymentEntity
            {
                Id = Guid.NewGuid(),
                ProcurementItemId = item.Id,
                ProcurementItem = item,
                Description = "Deposit",
                Currency = "AUD",
                AmountCents = 20_000,
                PaidAtUtc = paidAt,
                Status = "Paid"
            });
            procurement.Add(item);

            milestones.Add(new JobDateEntity
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                Label = "Additional estimate",
                CategoryKey = "milestone",
                CategoryLabel = "Milestone",
                Status = "Planned",
                CalendarDate = reportDate,
                ForecastAmountCents = 10_000,
                ForecastCurrency = "AUD",
                ForecastDirection = "Revenue"
            });
        }

        _db.AddRange(client, group);
        _db.Jobs.AddRange(jobs);
        _db.FinancialDocuments.AddRange(documents);
        _db.ProcurementItems.AddRange(procurement);
        _db.JobDates.AddRange(milestones);
        await _db.SaveChangesAsync();

        var editLocks = new EditLockService(_db, _actor, permissions, timeProvider, ids, new NullRealtimeNotifier());
        var cache = new AppCacheService(Options.Create(new AppCacheOptions()), timeProvider);
        _clientsAndJobs = new ClientAndJobService(
            _db, _actor, permissions, editLocks, cache, timeProvider, ids, new NullRealtimeNotifier());
        _forecasting = new ForecastingService(_db, permissions);
    }

    [Benchmark]
    public Task<PagedResult<JobDto>> SearchPagedJobs()
        => _clientsAndJobs.ListJobsAsync(
            _clientId,
            null,
            new PagedRequest { Page = 2, PageSize = 25, Search = "display home" });

    [Benchmark]
    public Task<ForecastReportDto> CalculateForecast()
        => _forecasting.CalculateForecastAsync(new ForecastRequest
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            ReportingTimeZone = "Australia/Sydney",
            Currency = "AUD"
        });

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class BenchmarkActorContext : ICurrentActorContext
    {
        private readonly IReadOnlyDictionary<string, AccessLevel> _grants =
            new Dictionary<string, AccessLevel>(StringComparer.Ordinal)
            {
                [ModuleKeys.Clients] = AccessLevel.Delete,
                [ModuleKeys.FinancialForecasting] = AccessLevel.Delete,
                [ModuleKeys.Invoices] = AccessLevel.Delete,
                [ModuleKeys.JobDates] = AccessLevel.Delete,
                [ModuleKeys.Jobs] = AccessLevel.Delete,
                [ModuleKeys.Procurement] = AccessLevel.Delete,
                [ModuleKeys.Quotes] = AccessLevel.Delete
            };

        public bool IsAuthenticated => true;
        public bool IsSystemPrincipal => false;
        public Guid? ActorId => Guid.Empty;
        public string ActorDisplayName => "Benchmark";
        public string CorrelationId => "benchmark";
        public string EditingSessionId => "benchmark";
        public bool IsFullAccess => false;
        public IReadOnlyDictionary<string, AccessLevel> EffectivePermissions => _grants;
    }
}
