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
            var existingMediaCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    existingMediaCols.Add(reader.GetString(1));
                }
            }

            if (!existingMediaCols.Contains("AIDescription"))
            {
                using var alterCmd = db.Database.GetDbConnection().CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MediaItems ADD COLUMN AIDescription TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }

            if (!existingMediaCols.Contains("AITags"))
            {
                using var alterCmd = db.Database.GetDbConnection().CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MediaItems ADD COLUMN AITags TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }

            if (!existingMediaCols.Contains("AIProcessedAt"))
            {
                using var alterCmd = db.Database.GetDbConnection().CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MediaItems ADD COLUMN AIProcessedAt TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }

            // Проверяем таблицы People и PersonFaces
            using var tablesCmd = db.Database.GetDbConnection().CreateCommand();
            tablesCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS People (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Category TEXT NOT NULL DEFAULT 'Семья',
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

            // Проверяем наличие колонки Category в People (если таблица уже была создана)
            using var personColsCmd = db.Database.GetDbConnection().CreateCommand();
            personColsCmd.CommandText = "PRAGMA table_info(People);";
            var existingPeopleCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = personColsCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    existingPeopleCols.Add(reader.GetString(1));
                }
            }

            if (!existingPeopleCols.Contains("Category"))
            {
                using var alterPeopleCmd = db.Database.GetDbConnection().CreateCommand();
                alterPeopleCmd.CommandText = "ALTER TABLE People ADD COLUMN Category TEXT NOT NULL DEFAULT 'Семья';";
                alterPeopleCmd.ExecuteNonQuery();
            }
        }
        catch
        {
        }
    }
}
