using Microsoft.EntityFrameworkCore;

namespace MangaTranslator.ProjectSystem.Local;

internal sealed class ProjectDbContext(DbContextOptions<ProjectDbContext> options) : DbContext(options)
{
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<PageRecord> Pages => Set<PageRecord>();
    public DbSet<PageVersionRecord> PageVersions => Set<PageVersionRecord>();
    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<JobEventRecord> JobEvents => Set<JobEventRecord>();
    public DbSet<ProjectArtifactRecord> ProjectArtifacts => Set<ProjectArtifactRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var project = modelBuilder.Entity<ProjectRecord>();
        project.ToTable("Projects");
        project.HasKey(item => item.Id);
        project.Property(item => item.Name).HasMaxLength(120);
        project.Property(item => item.SourceKind).HasConversion<string>().HasMaxLength(24);
        project.Property(item => item.WorkflowMode).HasConversion<string>().HasMaxLength(24);
        project.Property(item => item.TargetLanguage).HasMaxLength(32);
        project.Property(item => item.EnabledFontIdsJson).HasMaxLength(12000);
        project.Property(item => item.DefaultFontId).HasMaxLength(64);
        project.Property(item => item.FallbackFontId).HasMaxLength(64);
        project.Property(item => item.ImportSourceFileName).HasMaxLength(512);
        project.Property(item => item.ImportSourceFingerprint).HasMaxLength(128);
        project.HasMany(item => item.Pages)
            .WithOne(item => item.Project)
            .HasForeignKey(item => item.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        var page = modelBuilder.Entity<PageRecord>();
        page.ToTable("Pages");
        page.HasKey(item => item.Id);
        page.HasIndex(item => new { item.ProjectId, item.Index }).IsUnique();
        page.Property(item => item.OriginalFileName).HasMaxLength(512);
        page.Property(item => item.SourceAssetKey).HasMaxLength(512);
        page.Property(item => item.MediaType).HasMaxLength(128);
        page.Property(item => item.PageType).HasMaxLength(64);
        page.Property(item => item.ReviewTag).HasConversion<string>().HasMaxLength(24);
        page.HasMany(item => item.Versions)
            .WithOne(item => item.Page)
            .HasForeignKey(item => item.PageId)
            .OnDelete(DeleteBehavior.Cascade);

        var version = modelBuilder.Entity<PageVersionRecord>();
        version.ToTable("PageVersions");
        version.HasKey(item => item.Id);
        version.HasIndex(item => new { item.PageId, item.Sequence }).IsUnique();
        version.Property(item => item.Kind).HasConversion<string>().HasMaxLength(24);
        version.Property(item => item.Track).HasConversion<string>().HasMaxLength(24);
        version.Property(item => item.Label).HasMaxLength(120);
        version.Property(item => item.SourceAssetKey).HasMaxLength(512);
        version.Property(item => item.CleanAssetKey).HasMaxLength(512);
        version.Property(item => item.MaskAssetKey).HasMaxLength(512);
        version.Property(item => item.RenderedAssetKey).HasMaxLength(512);

        var job = modelBuilder.Entity<JobRecord>();
        job.ToTable("Jobs");
        job.HasKey(item => item.Id);
        job.HasIndex(item => new { item.ProjectId, item.UpdatedAt });
        job.Property(item => item.Kind).HasMaxLength(64);
        job.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        job.Property(item => item.ExecutionMode).HasMaxLength(32);
        job.Property(item => item.FixtureRunId).HasMaxLength(256);
        job.Property(item => item.SettingsJson).HasMaxLength(4000);
        job.HasMany(item => item.Events)
            .WithOne(item => item.Job)
            .HasForeignKey(item => item.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        var jobEvent = modelBuilder.Entity<JobEventRecord>();
        jobEvent.ToTable("JobEvents");
        jobEvent.HasKey(item => item.Id);
        jobEvent.HasIndex(item => new { item.JobId, item.Sequence }).IsUnique();
        jobEvent.Property(item => item.Stage).HasMaxLength(64);
        jobEvent.Property(item => item.Title).HasMaxLength(240);
        jobEvent.Property(item => item.RequestSummary).HasMaxLength(4000);
        jobEvent.Property(item => item.ResponseSummary).HasMaxLength(4000);
        jobEvent.Property(item => item.Detail).HasMaxLength(12000);
        jobEvent.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        jobEvent.Property(item => item.ParallelGroup).HasMaxLength(128);
        jobEvent.Property(item => item.PageIndicesJson).HasMaxLength(4000);

        var artifact = modelBuilder.Entity<ProjectArtifactRecord>();
        artifact.ToTable("ProjectArtifacts");
        artifact.HasKey(item => item.Id);
        artifact.HasIndex(item => new { item.ProjectId, item.Name }).IsUnique();
        artifact.Property(item => item.Name).HasMaxLength(128);
    }
}

internal sealed class ProjectRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public ProjectSourceKind SourceKind { get; set; }
    public int Revision { get; set; }
    public bool AutoSaveEnabled { get; set; } = true;
    public ProjectWorkflowMode WorkflowMode { get; set; } = ProjectWorkflowMode.Expert;
    public string TargetLanguage { get; set; } = "zh-Hans";
    public string EnabledFontIdsJson { get; set; } = "[]";
    public string? DefaultFontId { get; set; }
    public string? FallbackFontId { get; set; }
    public int PrecheckBatchSize { get; set; } = ProjectPrecheckDefaults.BatchSize;
    public int PrecheckBatchConcurrency { get; set; } = ProjectPrecheckDefaults.BatchConcurrency;
    public string? ImportSourceFileName { get; set; }
    public string? ImportSourceFingerprint { get; set; }
    public int? PdfRenderDpi { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<PageRecord> Pages { get; set; } = [];
}

internal sealed class PageRecord
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public ProjectRecord? Project { get; set; }
    public int Index { get; set; }
    public required string OriginalFileName { get; set; }
    public required string SourceAssetKey { get; set; }
    public required string MediaType { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string PageType { get; set; } = "unknown";
    public bool IncludedInWorkflow { get; set; } = true;
    public PageReviewTag ReviewTag { get; set; } = PageReviewTag.Unreviewed;
    public int Revision { get; set; } = 1;
    public int LastVersionedRevision { get; set; } = 1;
    public string? CurrentVersionId { get; set; }
    public string? CurrentLetteringVersionId { get; set; }
    public string? CurrentBackgroundVersionId { get; set; }
    public int? SourcePageNumber { get; set; }
    public List<PageVersionRecord> Versions { get; set; } = [];
}

internal sealed class PageVersionRecord
{
    public required string Id { get; set; }
    public required string PageId { get; set; }
    public PageRecord? Page { get; set; }
    public int Sequence { get; set; }
    public required string Label { get; set; }
    public PageVersionKind Kind { get; set; }
    public PageVersionTrack Track { get; set; }
    public string? ParentVersionId { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsWorkingCopy { get; set; }
    public int SnapshotRevision { get; set; }
    public required string SourceAssetKey { get; set; }
    public string? CleanAssetKey { get; set; }
    public string? MaskAssetKey { get; set; }
    public string? RenderedAssetKey { get; set; }
    public string? LayoutJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class JobRecord
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Kind { get; set; }
    public PersistentJobStatus Status { get; set; }
    public required string ExecutionMode { get; set; }
    public string? FixtureRunId { get; set; }
    public int ProgressPercent { get; set; }
    public string? SettingsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<JobEventRecord> Events { get; set; } = [];
}

internal sealed class JobEventRecord
{
    public required string Id { get; set; }
    public required string EventId { get; set; }
    public required string JobId { get; set; }
    public JobRecord? Job { get; set; }
    public int Sequence { get; set; }
    public required string Stage { get; set; }
    public required string Title { get; set; }
    public required string RequestSummary { get; set; }
    public string? ResponseSummary { get; set; }
    public string? Detail { get; set; }
    public PersistentProcessEventStatus Status { get; set; }
    public string? ParallelGroup { get; set; }
    public string PageIndicesJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ProjectArtifactRecord
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Name { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
