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
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonFace> PersonFaces => Set<PersonFace>();
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

        modelBuilder.Entity<PersonFace>()
            .HasIndex(p => p.PersonId);

        modelBuilder.Entity<PersonFace>()
            .HasIndex(p => p.MediaItemId);

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

            // Создаем таблицы People и PersonFaces если они еще не существуют
            using var tablesCmd = db.Database.GetDbConnection().CreateCommand();
            tablesCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS People (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Notes TEXT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS PersonFaces (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PersonId INTEGER NULL REFERENCES People(Id) ON DELETE SET NULL,
                    MediaItemId INTEGER NOT NULL REFERENCES MediaItems(Id) ON DELETE CASCADE,
                    BoxX REAL NOT NULL,
                    BoxY REAL NOT NULL,
                    BoxWidth REAL NOT NULL,
                    BoxHeight REAL NOT NULL,
                    Embedding TEXT NULL,
                    Confidence REAL NOT NULL,
                    DetectedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_PersonFaces_PersonId ON PersonFaces(PersonId);
                CREATE INDEX IF NOT EXISTS IX_PersonFaces_MediaItemId ON PersonFaces(MediaItemId);
            ";
            tablesCmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }
}
