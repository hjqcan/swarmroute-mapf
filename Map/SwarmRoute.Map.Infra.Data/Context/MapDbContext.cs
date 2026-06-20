using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.Infra.Data.Core.Context;
using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using System.Text.Json;

namespace SwarmRoute.Map.Infra.Data.Context;

/// <summary>
/// EF Core unit-of-work DbContext for the Map bounded context. Persists the <see cref="Roadmap"/> aggregate
/// and its owned child entities (<see cref="MapSite"/>/<see cref="MapLine"/>/<see cref="MapBlock"/>).
/// </summary>
/// <remarks>
/// Owned child collections are mapped to their own tables; the string-id lists and the <see cref="MapPosition"/>
/// value objects are stored as JSON (<c>jsonb</c>) columns, which is acceptable for these small,
/// read-mostly topology collections.
/// </remarks>
public sealed class MapDbContext : BaseDbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MapDbContext(
        DbContextOptions<MapDbContext> options,
        IDomainEventDispatcher domainEventDispatcher,
        IIntegrationEventPublisher? integrationEventPublisher,
        ILogger<MapDbContext> logger)
        : base(options, domainEventDispatcher, integrationEventPublisher, logger)
    {
    }

    /// <summary>The roadmap aggregates.</summary>
    public DbSet<Roadmap> Roadmaps => Set<Roadmap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<Event>();
        modelBuilder.Entity<Roadmap>(ConfigureRoadmap);
        base.OnModelCreating(modelBuilder);
    }

    private static void ConfigureRoadmap(EntityTypeBuilder<Roadmap> entity)
    {
        entity.ToTable("Roadmaps");
        entity.HasKey(r => r.Id);
        entity.Property(r => r.Id).ValueGeneratedNever();

        entity.Property(r => r.Name).HasMaxLength(256).IsRequired();
        entity.Property(r => r.StateVersion).IsConcurrencyToken();
        entity.Property(r => r.StateChangedAtUtc).HasColumnName("StateChangedAtUtc");

        entity.HasIndex(r => r.Name).IsUnique().HasDatabaseName("IX_Roadmaps_Name");

        entity.OwnsMany(r => r.Sites, ConfigureSite);
        entity.OwnsMany(r => r.Lines, ConfigureLine);
        entity.OwnsMany(r => r.Blocks, ConfigureBlock);

        entity.Navigation(r => r.Sites).UsePropertyAccessMode(PropertyAccessMode.Field);
        entity.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        entity.Navigation(r => r.Blocks).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureSite(OwnedNavigationBuilder<Roadmap, MapSite> sites)
    {
        sites.ToTable("RoadmapSites");
        sites.WithOwner().HasForeignKey("RoadmapId");
        sites.Property<Guid>("Id").ValueGeneratedNever();
        sites.HasKey("Id");

        sites.Property(s => s.SiteId).HasMaxLength(128).IsRequired();
        sites.Property(s => s.SiteType).HasConversion<string>().HasMaxLength(32).IsRequired();
        sites.Property(s => s.SiteRole).HasConversion<string>().HasMaxLength(32).IsRequired().HasDefaultValue(SiteRole.Transit);
        sites.Property(s => s.Enable).IsRequired();

        sites.Property(s => s.Pos).HasColumnName("Pos").HasColumnType("jsonb").HasConversion(PositionConverter, PositionComparer);

        ConfigureStringList(sites.Property(s => s.InterferenceSiteIds), "InterferenceSiteIds");
        ConfigureStringList(sites.Property(s => s.InterferenceLineIds), "InterferenceLineIds");

        sites.HasIndex("RoadmapId", nameof(MapSite.SiteId)).IsUnique().HasDatabaseName("IX_RoadmapSites_RoadmapId_SiteId");
    }

    private static void ConfigureLine(OwnedNavigationBuilder<Roadmap, MapLine> lines)
    {
        lines.ToTable("RoadmapLines");
        lines.WithOwner().HasForeignKey("RoadmapId");
        lines.Property<Guid>("Id").ValueGeneratedNever();
        lines.HasKey("Id");

        lines.Property(l => l.LineId).HasMaxLength(128).IsRequired();
        lines.Property(l => l.StartStationId).HasMaxLength(128).IsRequired();
        lines.Property(l => l.EndStationId).HasMaxLength(128).IsRequired();
        lines.Property(l => l.Distance).IsRequired();
        lines.Property(l => l.LineType).HasConversion<string>().HasMaxLength(32).IsRequired();

        lines.Property(l => l.ControlPos1).HasColumnName("ControlPos1").HasColumnType("jsonb").HasConversion(NullablePositionConverter, NullablePositionComparer);
        lines.Property(l => l.ControlPos2).HasColumnName("ControlPos2").HasColumnType("jsonb").HasConversion(NullablePositionConverter, NullablePositionComparer);

        ConfigureStringList(lines.Property(l => l.InterferenceSiteIds), "InterferenceSiteIds");
        ConfigureStringList(lines.Property(l => l.InterferenceLineIds), "InterferenceLineIds");

        lines.HasIndex("RoadmapId", nameof(MapLine.LineId)).IsUnique().HasDatabaseName("IX_RoadmapLines_RoadmapId_LineId");
    }

    private static void ConfigureBlock(OwnedNavigationBuilder<Roadmap, MapBlock> blocks)
    {
        blocks.ToTable("RoadmapBlocks");
        blocks.WithOwner().HasForeignKey("RoadmapId");
        blocks.Property<Guid>("Id").ValueGeneratedNever();
        blocks.HasKey("Id");

        blocks.Property(b => b.BlockId).HasMaxLength(128).IsRequired();

        blocks.Property(b => b.MinPos).HasColumnName("MinPos").HasColumnType("jsonb").HasConversion(PositionConverter, PositionComparer);
        blocks.Property(b => b.MaxPos).HasColumnName("MaxPos").HasColumnType("jsonb").HasConversion(PositionConverter, PositionComparer);

        ConfigureStringList(blocks.Property(b => b.ContainedSiteIds), "ContainedSiteIds");
        ConfigureStringList(blocks.Property(b => b.ContainedLineIds), "ContainedLineIds");

        blocks.HasIndex("RoadmapId", nameof(MapBlock.BlockId)).IsUnique().HasDatabaseName("IX_RoadmapBlocks_RoadmapId_BlockId");
    }

    private static void ConfigureStringList(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<IReadOnlyCollection<string>> property,
        string columnName)
    {
        property
            .HasColumnName(columnName)
            .HasColumnType("jsonb")
            .HasConversion(StringListConverter, StringListComparer)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    // ----- JSON converters / comparers -----

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IReadOnlyCollection<string>, string> StringListConverter =
        new(
            v => JsonSerializer.Serialize(v ?? Array.Empty<string>(), JsonOptions),
            v => JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>());

    private static readonly ValueComparer<IReadOnlyCollection<string>> StringListComparer =
        new(
            (a, b) => (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>()),
            v => v == null ? 0 : v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
            v => v.ToList());

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<MapPosition, string> PositionConverter =
        new(
            v => JsonSerializer.Serialize(Persistence.PositionJson.From(v ?? MapPosition.Empty), JsonOptions),
            v => (JsonSerializer.Deserialize<Persistence.PositionJson>(v, JsonOptions) ?? Persistence.PositionJson.From(MapPosition.Empty)).ToDomain());

    private static readonly ValueComparer<MapPosition> PositionComparer =
        new(
            (a, b) => Equals(a, b),
            v => v == null ? 0 : v.GetHashCode(),
            v => v == null ? MapPosition.Empty : new MapPosition(v.X, v.Y, v.Angle));

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<MapPosition?, string?> NullablePositionConverter =
        new(
            v => v == null ? null : JsonSerializer.Serialize(Persistence.PositionJson.From(v), JsonOptions),
            v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<Persistence.PositionJson>(v, JsonOptions)!.ToDomain());

    private static readonly ValueComparer<MapPosition?> NullablePositionComparer =
        new(
            (a, b) => Equals(a, b),
            v => v == null ? 0 : v.GetHashCode(),
            v => v == null ? null : new MapPosition(v.X, v.Y, v.Angle));
}
