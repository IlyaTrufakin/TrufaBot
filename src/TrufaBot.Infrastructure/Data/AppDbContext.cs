using Microsoft.EntityFrameworkCore;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Common;

namespace TrufaBot.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<StorageSource> StorageSources => Set<StorageSource>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserFolderPermission> UserFolderPermissions => Set<UserFolderPermission>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<ClassificationCategory> ClassificationCategories => Set<ClassificationCategory>();
    public DbSet<MediaClassification> MediaClassifications => Set<MediaClassification>();
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    public AppDbContext() { }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            AppPaths.EnsureDirectoriesCreated();
            optionsBuilder.UseSqlite($"Data Source={AppPaths.DatabasePath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.TelegramUserId)
            .IsUnique();

        modelBuilder.Entity<MediaItem>()
            .HasIndex(m => new { m.StorageSourceId, m.RelativePath })
            .IsUnique();

        modelBuilder.Entity<MediaItem>()
            .HasIndex(m => m.ClassificationStatus);

        modelBuilder.Entity<AuditLogEntry>()
            .HasIndex(a => a.Timestamp);
    }
}
