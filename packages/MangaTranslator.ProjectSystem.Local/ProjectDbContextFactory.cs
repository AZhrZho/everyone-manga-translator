using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MangaTranslator.ProjectSystem.Local;

internal static class ProjectDbContextFactory
{
    public static ProjectDbContext Create(string projectRoot)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = LocalProjectLayout.GetDatabasePath(projectRoot),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // Project directories are user-managed and can be moved or deleted
            // while the desktop backend remains alive. A pooled SQLite handle
            // keeps project.db locked on Windows even after its DbContext has
            // been disposed, and a stale development backend can retain that
            // handle in another process. Prefer deterministic release here.
            Pooling = false,
            ForeignKeys = true
        }.ToString();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var context = new ProjectDbContext(options);
        context.Database.EnsureCreated();
        ApplySchemaMigrations(context);
        return context;
    }

    public static async Task InitializeAsync(string projectRoot, CancellationToken cancellationToken)
    {
        await using var context = Create(projectRoot);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
    }

    private static void ApplySchemaMigrations(ProjectDbContext context)
    {
        context.Database.OpenConnection();
        try
        {
            using var transaction = context.Database.BeginTransaction();
            var projectColumns = ReadColumns(context, "Projects");
            if (!projectColumns.Contains("WorkflowMode"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Projects ADD COLUMN WorkflowMode TEXT NOT NULL DEFAULT 'Expert';");
            }
            if (!projectColumns.Contains("TargetLanguage"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Projects ADD COLUMN TargetLanguage TEXT NOT NULL DEFAULT 'zh-Hans';");
            }
            if (!projectColumns.Contains("EnabledFontIdsJson"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Projects ADD COLUMN EnabledFontIdsJson TEXT NOT NULL DEFAULT '[]';");
            }
            if (!projectColumns.Contains("DefaultFontId"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Projects ADD COLUMN DefaultFontId TEXT NULL;");
            }
            if (!projectColumns.Contains("FallbackFontId"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Projects ADD COLUMN FallbackFontId TEXT NULL;");
            }
            if (!projectColumns.Contains("PrecheckBatchSize"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Projects ADD COLUMN PrecheckBatchSize INTEGER NOT NULL DEFAULT 12;");
            }
            if (!projectColumns.Contains("PrecheckBatchConcurrency"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Projects ADD COLUMN PrecheckBatchConcurrency INTEGER NOT NULL DEFAULT 4;");
            }

            var pageColumns = ReadColumns(context, "Pages");
            if (!pageColumns.Contains("ReviewTag"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Pages ADD COLUMN ReviewTag TEXT NOT NULL DEFAULT 'Unreviewed';");
            }
            if (!pageColumns.Contains("CurrentLetteringVersionId"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Pages ADD COLUMN CurrentLetteringVersionId TEXT NULL;");
            }
            if (!pageColumns.Contains("CurrentBackgroundVersionId"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Pages ADD COLUMN CurrentBackgroundVersionId TEXT NULL;");
            }

            var versionColumns = ReadColumns(context, "PageVersions");
            if (!versionColumns.Contains("IsWorkingCopy"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE PageVersions ADD COLUMN IsWorkingCopy INTEGER NOT NULL DEFAULT 0;");
            }
            if (!versionColumns.Contains("Track"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE PageVersions ADD COLUMN Track TEXT NOT NULL DEFAULT 'Lettering';");
                context.Database.ExecuteSqlRaw(
                    """
                    UPDATE Pages
                    SET CurrentLetteringVersionId = CurrentVersionId,
                        CurrentBackgroundVersionId = NULL;
                    """);
            }

            var jobColumns = ReadColumns(context, "Jobs");
            if (!jobColumns.Contains("SettingsJson"))
            {
                context.Database.ExecuteSqlRaw(
                    "ALTER TABLE Jobs ADD COLUMN SettingsJson TEXT NULL;");
            }
            transaction.Commit();
        }
        finally
        {
            context.Database.CloseConnection();
        }
    }

    private static HashSet<string> ReadColumns(ProjectDbContext context, string tableName)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName}');";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }
}
