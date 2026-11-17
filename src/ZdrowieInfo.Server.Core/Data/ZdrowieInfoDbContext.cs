using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ZdrowieInfo.Server.ApiKeys;
using ZdrowieInfo.Server.Reports;
using ZdrowieInfo.Server.Sources;

namespace ZdrowieInfo.Server.Data;

public sealed class ZdrowieInfoDbContext(DbContextOptions<ZdrowieInfoDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<SourceSnapshot> SourceSnapshots => Set<SourceSnapshot>();
    public DbSet<SourceItem> SourceItems => Set<SourceItem>();
    public DbSet<SourceIncident> SourceIncidents => Set<SourceIncident>();
    public DbSet<CheckRun> CheckRuns => Set<CheckRun>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportFinding> ReportFindings => Set<ReportFinding>();
    public DbSet<FindingCitation> FindingCitations => Set<FindingCitation>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

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
            e.HasIndex(x => x.Date).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Producer).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Body).HasColumnType("jsonb").HasConversion(Json<ReportDocument>(), JsonComparer<ReportDocument>());
            e.Property(x => x.SourcesState).HasColumnType("jsonb").HasConversion(Json<List<SourceStateEntry>>(), JsonComparer<List<SourceStateEntry>>());
            e.Property(x => x.AiUsage).HasColumnType("jsonb").HasConversion(Json<AiUsage?>(), JsonComparer<AiUsage?>());
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReportFinding>(e =>
        {
            e.ToTable("report_findings");
            e.Property(x => x.Importance).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Headline).HasMaxLength(1000);
            e.HasMany(x => x.Citations).WithOne().HasForeignKey(x => x.FindingId).OnDelete(DeleteBehavior.Cascade);
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
    }

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
