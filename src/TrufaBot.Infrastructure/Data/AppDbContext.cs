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

    public static void EnsureSchemaUpdated()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "PRAGMA table_info(MediaItems);";
            db.Database.OpenConnection();
            var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    existingCols.Add(reader.GetString(1));
                }
            }

            if (!existingCols.Contains("AIDescription"))
            {
                using var alterCmd = db.Database.GetDbConnection().CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MediaItems ADD COLUMN AIDescription TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }

            if (!existingCols.Contains("AITags"))
            {
                using var alterCmd = db.Database.GetDbConnection().CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MediaItems ADD COLUMN AITags TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }

            if (!existingCols.Contains("AIProcessedAt"))
            {
                using var alterCmd = db.Database.GetDbConnection().CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MediaItems ADD COLUMN AIProcessedAt TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch
        {
        }
    }
}
