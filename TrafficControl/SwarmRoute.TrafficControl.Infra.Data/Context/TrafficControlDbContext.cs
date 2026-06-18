using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.Infra.Data.Core.Context;
using SwarmRoute.TrafficControl.Infra.Data.Entities;

namespace SwarmRoute.TrafficControl.Infra.Data.Context;

/// <summary>
/// EF Core context for the TrafficControl bounded context — <b>snapshot / audit only</b>. The authoritative
/// live reservation state is the in-memory singleton <c>ReservationTable</c> aggregate (ADR-002 / R2); this
/// context never carries the reservation hot path. It persists periodic snapshots (crash recovery) and an
/// allocation audit trail via <see cref="ReservationAuditRecord"/>.
/// </summary>
/// <remarks>
/// Derives from <see cref="BaseDbContext"/> for the standard unit-of-work + domain-event dispatch plumbing,
/// even though the audit rows themselves raise no domain events.
/// </remarks>
public sealed class TrafficControlDbContext : BaseDbContext
{
    public TrafficControlDbContext(
        DbContextOptions<TrafficControlDbContext> options,
        IDomainEventDispatcher domainEventDispatcher,
        IIntegrationEventPublisher? integrationEventPublisher,
        ILogger<TrafficControlDbContext> logger)
        : base(options, domainEventDispatcher, integrationEventPublisher, logger)
    {
    }

    /// <summary>The reservation snapshot / audit rows.</summary>
    public DbSet<ReservationAuditRecord> ReservationAudits => Set<ReservationAuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<Event>();
        modelBuilder.Entity<ReservationAuditRecord>(ConfigureAudit);
        base.OnModelCreating(modelBuilder);
    }

    private static void ConfigureAudit(EntityTypeBuilder<ReservationAuditRecord> entity)
    {
        entity.ToTable("ReservationAudits");
        entity.HasKey(a => a.Id);
        entity.Property(a => a.Id).ValueGeneratedOnAdd();

        entity.Property(a => a.ReservationTableId).IsRequired();
        entity.Property(a => a.StateVersion).IsRequired();
        entity.Property(a => a.AgentId).HasMaxLength(128).IsRequired();
        entity.Property(a => a.Action).HasMaxLength(32).IsRequired();
        entity.Property(a => a.LeaseCount).IsRequired();
        entity.Property(a => a.LeasesJson).HasColumnType("jsonb");
        entity.Property(a => a.CreatedAtUtc).IsRequired();

        entity.HasIndex(a => a.ReservationTableId).HasDatabaseName("IX_ReservationAudits_ReservationTableId");
        entity.HasIndex(a => a.CreatedAtUtc).HasDatabaseName("IX_ReservationAudits_CreatedAtUtc");
    }
}
