using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ZdrowieInfo.Server.ApiKeys;
using ZdrowieInfo.Server.Push;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Data;

public sealed class ZdrowieInfoDbContext(DbContextOptions<ZdrowieInfoDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    /// <summary>
    /// Konfiguracja wyszukiwania: <c>simple</c> + <c>unaccent</c> (bez polskich znaków, bez odmiany). Tworzona w migracji
    /// Search; słownik ispell dla polskiego można podmienić później jedną migracją.
    /// </summary>
    public const string SearchConfig = "pl_unaccent";

    public DbSet<Source> Sources => Set<Source>();
    public DbSet<SourceSnapshot> SourceSnapshots => Set<SourceSnapshot>();
    public DbSet<SourceItem> SourceItems => Set<SourceItem>();
    public DbSet<SourceIncident> SourceIncidents => Set<SourceIncident>();
    public DbSet<CheckRun> CheckRuns => Set<CheckRun>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportFinding> ReportFindings => Set<ReportFinding>();
    public DbSet<FindingCitation> FindingCitations => Set<FindingCitation>();
    public DbSet<Updates.ServerUpdateRun> UpdateRuns => Set<Updates.ServerUpdateRun>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<PushSubscriptionRecord> PushSubscriptions => Set<PushSubscriptionRecord>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeReports();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeReports();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Raport dzienny: początek okresu = data (unikalność i <see cref="ReportQueries.WhereKey"/> na tym polegają).</summary>
    private void NormalizeReports()
    {
        foreach (var entry in ChangeTracker.Entries<Report>())
            if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity.Kind == ReportKind.Daily)
                entry.Entity.PeriodStart = entry.Entity.Date;
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppSetting>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasColumnType("jsonb");
        });

        var selectorsComparer = new ValueComparer<SourceSelectors>(
            (a, b) => JsonSerializer.Serialize(a, DefaultSource.Json) == JsonSerializer.Serialize(b, DefaultSource.Json),
            v => JsonSerializer.Serialize(v, DefaultSource.Json).GetHashCode(),
            v => JsonSerializer.Deserialize<SourceSelectors>(JsonSerializer.Serialize(v, DefaultSource.Json), DefaultSource.Json)!);

        builder.Entity<Source>(e =>
        {
            e.ToTable("sources");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Url).HasMaxLength(1000);
            e.Property(x => x.Group).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FetchMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Selectors).HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, DefaultSource.Json),
                    v => JsonSerializer.Deserialize<SourceSelectors>(v, DefaultSource.Json) ?? new SourceSelectors())
                .Metadata.SetValueComparer(selectorsComparer);
        });

        builder.Entity<SourceSnapshot>(e =>
        {
            e.ToTable("source_snapshots");
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CheckRun>().WithMany().HasForeignKey(x => x.CheckRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.SourceId, x.FetchedAt });
            e.Property(x => x.ErrorType).HasConversion<string>().HasMaxLength(30);
        });

        builder.Entity<SourceItem>(e =>
        {
            e.ToTable("source_items");
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceId, x.Key }).IsUnique();
            e.HasIndex(x => x.Url);
            e.HasIndex(x => x.LastChangedAt);
            e.Property(x => x.Key).HasMaxLength(1000);
            e.Property(x => x.Url).HasMaxLength(1000);
            e.Property(x => x.Title).HasMaxLength(1000);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.ReportedAt);
            e.HasOne<Report>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Origin).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
        });

        builder.Entity<SourceIncident>(e =>
        {
            e.ToTable("source_incidents");
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceId, x.StartedAt });
            e.Property(x => x.ErrorType).HasConversion<string>().HasMaxLength(30);
        });

        builder.Entity<CheckRun>(e =>
        {
            e.ToTable("check_runs");
            e.HasIndex(x => x.StartedAt);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.PrimitiveCollection(x => x.Groups).ElementType().HasConversion<string>();
        });

        builder.Entity<Report>(e =>
        {
            e.ToTable("reports");
            e.HasIndex(x => new { x.Kind, x.PeriodStart, x.Date }).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.ShareToken).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ShareToken).HasMaxLength(64);
            e.Ignore(x => x.Key);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Producer).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Body).HasColumnType("jsonb").HasConversion(Json<ReportDocument>(), JsonComparer<ReportDocument>());
            e.Property(x => x.SourcesState).HasColumnType("jsonb").HasConversion(Json<List<SourceStateEntry>>(), JsonComparer<List<SourceStateEntry>>());
            e.Property(x => x.AiUsage).HasColumnType("jsonb").HasConversion(Json<AiUsage?>(), JsonComparer<AiUsage?>());
            e.Property(x => x.Research).HasColumnType("jsonb").HasConversion(Json<VulnerabilityResearch?>(), JsonComparer<VulnerabilityResearch?>());
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SearchVector).HasColumnType("tsvector").HasComputedColumnSql(
                $"setweight(to_tsvector('{SearchConfig}'::regconfig, coalesce(title, '')), 'A') || setweight(to_tsvector('{SearchConfig}'::regconfig, coalesce(summary, '')), 'B')", stored: true);
            e.HasIndex(x => x.SearchVector).HasMethod("GIN");
        });

        builder.Entity<Updates.ServerUpdateRun>(e =>
        {
            e.ToTable("update_runs");
            e.HasIndex(x => x.RequestedAt);
            e.Property(x => x.FromVersion).HasMaxLength(32);
            e.Property(x => x.ToVersion).HasMaxLength(32);
            e.Property(x => x.RequestedBy).HasMaxLength(256);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(2000);
        });

        builder.Entity<ReportFinding>(e =>
        {
            e.ToTable("report_findings");
            e.Property(x => x.Importance).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Headline).HasMaxLength(1000);
            e.HasMany(x => x.Citations).WithOne().HasForeignKey(x => x.FindingId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SearchVector).HasColumnType("tsvector").HasComputedColumnSql(
                $"setweight(to_tsvector('{SearchConfig}'::regconfig, coalesce(headline, '')), 'A') || setweight(to_tsvector('{SearchConfig}'::regconfig, coalesce(body, '') || ' ' || coalesce(action, '')), 'B')", stored: true);
            e.HasIndex(x => x.SearchVector).HasMethod("GIN");
        });

        builder.Entity<FindingCitation>(e =>
        {
            e.ToTable("finding_citations");
            e.Property(x => x.Title).HasMaxLength(1000);
            e.Property(x => x.Url).HasMaxLength(1000);
            e.HasOne<SourceItem>().WithMany().HasForeignKey(x => x.SourceItemId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasIndex(x => x.Prefix).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Prefix).HasMaxLength(32);
            e.Property(x => x.SecretHash).HasMaxLength(128);
        });

        ConfigurePush(builder);
    }

    private static void ConfigurePush(ModelBuilder builder) =>
        builder.Entity<PushSubscriptionRecord>(e =>
        {
            e.ToTable("push_subscriptions");
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.Property(x => x.Endpoint).HasMaxLength(2000);
            e.Property(x => x.P256dh).HasMaxLength(200);
            e.Property(x => x.Auth).HasMaxLength(100);
            e.Property(x => x.Device).HasMaxLength(200);
            e.Property(x => x.Origin).HasMaxLength(300);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<T, string> Json<T>() =>
        new(v => JsonSerializer.Serialize(v, JsonOptions), v => JsonSerializer.Deserialize<T>(v, JsonOptions)!);

    private static ValueComparer<T> JsonComparer<T>() => new(
        (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!);
}
