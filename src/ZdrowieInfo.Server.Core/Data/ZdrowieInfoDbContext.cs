using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ZdrowieInfo.Server.Data;

public sealed class ZdrowieInfoDbContext(DbContextOptions<ZdrowieInfoDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

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
    }
}
