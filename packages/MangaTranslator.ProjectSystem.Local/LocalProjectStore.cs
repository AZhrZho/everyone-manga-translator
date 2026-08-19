using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PDFtoImage;

namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalProjectStore(
    LocalPathGrantRegistry grants,
    LocalRecentProjectRegistry recentProjects,
    ILogger<LocalProjectStore> logger) : IProjectStore
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ProjectListItem>> ListRecentAsync(CancellationToken cancellationToken)
    {
        var entries = await recentProjects.ListAsync(cancellationToken);
        var result = new List<ProjectListItem>(entries.Count);
        var unavailableIds = new List<string>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(entry.ProjectRoot)
                || !File.Exists(LocalProjectLayout.GetDatabasePath(entry.ProjectRoot)))
            {
                unavailableIds.Add(entry.ProjectId);
                continue;
            }

            try
            {
                await using var context = ProjectDbContextFactory.Create(entry.ProjectRoot);
                var project = await context.Projects
                    .AsNoTracking()
                    .Include(item => item.Pages)
                    .SingleOrDefaultAsync(item => item.Id == entry.ProjectId, cancellationToken);
                if (project is null)
                {
                    unavailableIds.Add(entry.ProjectId);
                    continue;
                }

                result.Add(new ProjectListItem(
                    project.Id,
                    project.Name,
                    project.SourceKind,
                    project.Pages.Count,
                    entry.ProjectRoot,
                    true,
                    project.CreatedAt,
                    project.UpdatedAt,
                    entry.LastOpenedAt));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Unable to read recent project {ProjectRoot}.", entry.ProjectRoot);
                unavailableIds.Add(entry.ProjectId);
            }
        }

        await recentProjects.RemoveAsync(unavailableIds, cancellationToken);

        return result;
    }

    public async Task RemoveRecentAsync(
        string projectId,
        bool deleteProjectFiles,
        CancellationToken cancellationToken)
    {
        var entry = await recentProjects.FindAsync(projectId, cancellationToken)
            ?? throw new ProjectNotFoundException(projectId);
        if (!deleteProjectFiles || !Directory.Exists(entry.ProjectRoot))
        {
            await recentProjects.RemoveAsync([projectId], cancellationToken);
            return;
        }

        var projectRoot = Path.GetFullPath(entry.ProjectRoot);
        if (string.Equals(projectRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(projectRoot)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝删除文件系统根目录。");
        }

        var databasePath = LocalProjectLayout.GetDatabasePath(projectRoot);
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException("项目目录缺少 project.db，无法确认物理删除目标。");
        }

        await WithProjectLockAsync(projectId, async () =>
        {
            await using (var context = ProjectDbContextFactory.Create(projectRoot))
            {
                var matches = await context.Projects.AsNoTracking()
                    .AnyAsync(item => item.Id == projectId, cancellationToken);
                if (!matches)
                {
                    throw new InvalidOperationException("所选目录中的项目身份与最近项目记录不一致。");
                }
            }

            // Keep the project gate held until the directory is gone so another
            // autosave/read cannot reopen project.db between validation and delete.
            SqliteConnection.ClearAllPools();
            await DeleteProjectDirectoryAsync(projectRoot, cancellationToken);
            await recentProjects.RemoveAsync([projectId], cancellationToken);
            return true;
        }, cancellationToken);
    }

    private static async Task DeleteProjectDirectoryAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(projectRoot, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                // SQLite, antivirus software, or a finishing background read can
                // hold a Windows file handle briefly. Clear local pools again and
                // retry without turning a transient lock into a destructive-error UI.
                SqliteConnection.ClearAllPools();
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    public async Task<ProjectSnapshot> GetAsync(string projectId, CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> CreateAsync(
        CreateProjectCommand command,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(command.Name);
        var destinationParent = grants.Resolve(command.DestinationToken, ProjectLocationKind.ProjectParent);
        var (root, project) = await CreateProjectRootAsync(
            destinationParent,
            name,
            ProjectSourceKind.Empty,
            command.WorkflowMode,
            command.TargetLanguage,
            command.EnabledFontIds,
            command.DefaultFontId,
            command.FallbackFontId,
            cancellationToken);
        await recentProjects.UpsertAsync(root, project, 0, markOpened: true, cancellationToken);
        return ToSnapshot(project, root);
    }

    public async Task<ProjectSnapshot> ImportFolderAsync(
        ImportFolderProjectCommand command,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(command.Name);
        var destinationParent = grants.Resolve(command.DestinationToken, ProjectLocationKind.ProjectParent);
        var sourceRoot = grants.Resolve(command.SourceToken, ProjectLocationKind.ImportFolder);
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetRelativePath(sourceRoot, path), NaturalPathComparer.Instance)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            throw new InvalidOperationException("所选文件夹中没有可导入的漫画图片。");
        }

        var (projectRoot, project) = await CreateProjectRootAsync(
            destinationParent,
            name,
            ProjectSourceKind.Folder,
            command.WorkflowMode,
            command.TargetLanguage,
            command.EnabledFontIds,
            command.DefaultFontId,
            command.FallbackFontId,
            cancellationToken);
        try
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var databaseProject = await context.Projects.SingleAsync(item => item.Id == project.Id, cancellationToken);
            for (var offset = 0; offset < sourceFiles.Length; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFile = sourceFiles[offset];
                var index = offset + 1;
                var extension = Path.GetExtension(sourceFile).ToLowerInvariant();
                var assetKey = $"source/{index:0000}{extension}";
                var targetPath = LocalProjectLayout.ResolveAssetPath(projectRoot, assetKey);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await CopyFileAsync(sourceFile, targetPath, cancellationToken);
                var (width, height) = ImageMetadataReader.Read(targetPath);
                var pageId = $"page-{index:0000}";
                var letteringVersionId = $"version-{Guid.NewGuid():N}";
                databaseProject.Pages.Add(new PageRecord
                {
                    Id = pageId,
                    ProjectId = databaseProject.Id,
                    Index = index,
                    OriginalFileName = Path.GetFileName(sourceFile),
                    SourceAssetKey = assetKey,
                    MediaType = MediaTypeForExtension(extension),
                    Width = width,
                    Height = height,
                    PageType = "unknown",
                    IncludedInWorkflow = true,
                    Revision = 1,
                    LastVersionedRevision = 1,
                    CurrentVersionId = letteringVersionId,
                    CurrentLetteringVersionId = letteringVersionId,
                    CurrentBackgroundVersionId = null,
                    Versions =
                    [
                        new PageVersionRecord
                        {
                            Id = letteringVersionId,
                            PageId = pageId,
                            Sequence = 1,
                            Label = "原稿",
                            Kind = PageVersionKind.Imported,
                            Track = PageVersionTrack.Lettering,
                            IsCurrent = true,
                            SnapshotRevision = 1,
                            SourceAssetKey = assetKey,
                            CreatedAt = DateTimeOffset.UtcNow
                        }
                    ]
                });
            }

            databaseProject.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, databaseProject, sourceFiles.Length, markOpened: true, cancellationToken);
            return ToSnapshot(databaseProject, projectRoot);
        }
        catch
        {
            TryDeleteCreatedProject(projectRoot);
            throw;
        }
    }

    public async Task<ProjectSnapshot> OpenAsync(
        OpenProjectCommand command,
        CancellationToken cancellationToken)
    {
        var projectRoot = grants.Resolve(command.LocationToken, ProjectLocationKind.ExistingProject);
        var manifest = await ProjectManifest.ReadAsync(projectRoot, cancellationToken);
        await using var context = ProjectDbContextFactory.Create(projectRoot);
        var project = await LoadProjectAsync(context, manifest.ProjectId, cancellationToken);
        await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: true, cancellationToken);
        return ToSnapshot(project, projectRoot);
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task<ProjectSnapshot> ImportPdfAsync(
        ImportPdfProjectCommand command,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(command.Name);
        var renderDpi = Math.Clamp(command.RenderDpi, 72, 600);
        var destinationParent = grants.Resolve(command.DestinationToken, ProjectLocationKind.ProjectParent);
        var pdfPath = grants.Resolve(command.SourceToken, ProjectLocationKind.ImportPdf);
        var (projectRoot, project) = await CreateProjectRootAsync(
            destinationParent,
            name,
            ProjectSourceKind.Pdf,
            command.WorkflowMode,
            command.TargetLanguage,
            command.EnabledFontIds,
            command.DefaultFontId,
            command.FallbackFontId,
            cancellationToken);
        try
        {
            var sourcePdfKey = "source/original.pdf";
            var sourcePdfPath = LocalProjectLayout.ResolveAssetPath(projectRoot, sourcePdfKey);
            await CopyFileAsync(pdfPath, sourcePdfPath, cancellationToken);
            var fingerprint = await ComputeSha256Async(sourcePdfPath, cancellationToken);
            await using var pdfStream = new FileStream(sourcePdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var pageCount = Conversion.GetPageCount(pdfStream, leaveOpen: true, password: null);
            if (pageCount <= 0)
            {
                throw new InvalidOperationException("所选 PDF 不包含可导入的页面。");
            }

            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var databaseProject = await context.Projects.SingleAsync(item => item.Id == project.Id, cancellationToken);
            databaseProject.ImportSourceFileName = Path.GetFileName(pdfPath);
            databaseProject.ImportSourceFingerprint = fingerprint;
            databaseProject.PdfRenderDpi = renderDpi;
            var renderOptions = new RenderOptions(Dpi: renderDpi, UseTiling: true);
            for (var offset = 0; offset < pageCount; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = offset + 1;
                var assetKey = $"source/{index:0000}.png";
                var targetPath = LocalProjectLayout.ResolveAssetPath(projectRoot, assetKey);
                await Task.Run(
                    () => Conversion.SavePng(targetPath, pdfStream, offset, leaveOpen: true, password: null, options: renderOptions),
                    cancellationToken);
                var (width, height) = ImageMetadataReader.Read(targetPath);
                var pageId = $"page-{index:0000}";
                var letteringVersionId = $"version-{Guid.NewGuid():N}";
                databaseProject.Pages.Add(new PageRecord
                {
                    Id = pageId,
                    ProjectId = databaseProject.Id,
                    Index = index,
                    OriginalFileName = $"{Path.GetFileName(pdfPath)} · 第 {index} 页",
                    SourceAssetKey = assetKey,
                    MediaType = "image/png",
                    Width = width,
                    Height = height,
                    PageType = "unknown",
                    IncludedInWorkflow = true,
                    Revision = 1,
                    LastVersionedRevision = 1,
                    CurrentVersionId = letteringVersionId,
                    CurrentLetteringVersionId = letteringVersionId,
                    CurrentBackgroundVersionId = null,
                    SourcePageNumber = index,
                    Versions =
                    [
                        new PageVersionRecord
                        {
                            Id = letteringVersionId,
                            PageId = pageId,
                            Sequence = 1,
                            Label = "原稿",
                            Kind = PageVersionKind.Imported,
                            Track = PageVersionTrack.Lettering,
                            IsCurrent = true,
                            SnapshotRevision = 1,
                            SourceAssetKey = assetKey,
                            CreatedAt = DateTimeOffset.UtcNow
                        }
                    ]
                });
            }

            databaseProject.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, databaseProject, pageCount, markOpened: true, cancellationToken);
            return ToSnapshot(databaseProject, projectRoot);
        }
        catch
        {
            TryDeleteCreatedProject(projectRoot);
            throw;
        }
    }

    public async Task<ProjectSnapshot> RenameAsync(
        string projectId,
        RenameProjectCommand command,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(command.Name);
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, command.ExpectedRevision);
            project.Name = name;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await WriteManifestAsync(projectRoot, project, cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> UpdateSettingsAsync(
        string projectId,
        UpdateProjectSettingsCommand command,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, command.ExpectedRevision);
            if (project.WorkflowMode != command.WorkflowMode)
            {
                throw new ArgumentException("The workflow mode is fixed when the project is created and cannot be changed later.");
            }
            project.TargetLanguage = NormalizeTargetLanguage(command.TargetLanguage);
            project.EnabledFontIdsJson = SerializeFontIds(command.EnabledFontIds);
            project.DefaultFontId = NormalizeOptionalId(command.DefaultFontId);
            project.FallbackFontId = NormalizeOptionalId(command.FallbackFontId);
            project.PrecheckBatchSize = NormalizePrecheckBatchSize(command.PrecheckBatchSize);
            project.PrecheckBatchConcurrency = NormalizePrecheckBatchConcurrency(command.PrecheckBatchConcurrency);
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await WriteManifestAsync(projectRoot, project, cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> UpdatePageReviewAsync(
        string projectId,
        string pageId,
        UpdatePageReviewCommand command,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, command.ExpectedRevision);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            page.PageType = NormalizePageType(command.PageType);
            page.IncludedInWorkflow = command.IncludedInWorkflow;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> UpdatePageReviewTagAsync(
        string projectId,
        string pageId,
        UpdatePageReviewTagCommand command,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, command.ExpectedRevision);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            if (!page.IncludedInWorkflow)
            {
                throw new ArgumentException("The review status of an excluded page is locked in the translation workspace.");
            }
            page.ReviewTag = command.ReviewTag;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(
                projectRoot,
                project,
                project.Pages.Count,
                markOpened: false,
                cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<PageLayoutSnapshot> GetPageLayoutAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await context.Projects
                .AsNoTracking()
                .Include(item => item.Pages)
                .ThenInclude(item => item.Versions)
                .SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
                ?? throw new ProjectNotFoundException(projectId);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            var working = await context.ProjectArtifacts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.Name == WorkingLayoutArtifactName(pageId),
                    cancellationToken);
            var current = CurrentVersion(page, PageVersionTrack.Lettering);
            return new PageLayoutSnapshot(
                project.Id,
                page.Id,
                project.Revision,
                page.Revision,
                current?.IsWorkingCopy == true,
                current?.Id,
                working?.Json ?? current?.LayoutJson);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> UpdatePageLayoutAsync(
        string projectId,
        string pageId,
        UpdatePageLayoutCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.LayoutJson))
        {
            throw new ArgumentException("布局 JSON 不能为空。", nameof(command));
        }

        try
        {
            using var document = JsonDocument.Parse(command.LayoutJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("布局 JSON 根节点必须是对象。");
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("布局 JSON 无效。", nameof(command), exception);
        }

        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, command.ExpectedRevision);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            await EnsureWorkingVersionAsync(
                context,
                projectId,
                page,
                PageVersionTrack.Lettering,
                cancellationToken);
            var artifactName = WorkingLayoutArtifactName(pageId);
            var artifact = await context.ProjectArtifacts
                .SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.Name == artifactName,
                    cancellationToken);
            if (artifact is null)
            {
                artifact = new ProjectArtifactRecord
                {
                    Id = $"artifact-{Guid.NewGuid():N}",
                    ProjectId = projectId,
                    Name = artifactName,
                    Json = command.LayoutJson,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                context.ProjectArtifacts.Add(artifact);
            }
            else
            {
                artifact.Json = command.LayoutJson;
                artifact.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(command.CleanAssetKey)
                && !string.IsNullOrWhiteSpace(command.MaskAssetKey))
            {
                await SetDerivedBackgroundAsync(
                    context,
                    projectId,
                    page,
                    command.CleanAssetKey,
                    command.MaskAssetKey,
                    command.CandidateMaskAssetKey,
                    cancellationToken);
            }
            await RemoveArtifactAsync(context, projectId, BackgroundRepairArtifactName(pageId), cancellationToken);
            PageImageEditRenderer.DeleteWorkingAsset(projectRoot, pageId);

            page.Revision++;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> RebasePageBackgroundAsync(
        string projectId,
        string pageId,
        RebasePageBackgroundCommand command,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, command.ExpectedRevision);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            await SetDerivedBackgroundAsync(
                context,
                projectId,
                page,
                command.CleanAssetKey,
                command.MaskAssetKey,
                command.CandidateMaskAssetKey,
                cancellationToken);
            await RemoveArtifactAsync(context, projectId, BackgroundRepairArtifactName(pageId), cancellationToken);
            PageImageEditRenderer.DeleteWorkingAsset(projectRoot, pageId);

            page.Revision++;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<PageImageEditSnapshot> GetPageImageEditAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await context.Projects
                .AsNoTracking()
                .Include(item => item.Pages)
                .ThenInclude(item => item.Versions)
                .SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
                ?? throw new ProjectNotFoundException(projectId);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            var repair = await context.ProjectArtifacts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.Name == BackgroundRepairArtifactName(pageId),
                    cancellationToken);
            if (repair is null)
            {
                repair = await context.ProjectArtifacts
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        item => item.ProjectId == projectId && item.Name == WorkingImageEditArtifactName(pageId),
                        cancellationToken);
            }
            var derived = await GetDerivedBackgroundAsync(context, projectId, page, cancellationToken);
            var operationsJson = PageImageEditRenderer.NormalizeStoredRepairDocument(repair?.Json);
            var renderedAssetKey = PageImageEditRenderer.HasRepairOperations(operationsJson)
                                   && File.Exists(LocalProjectLayout.ResolveAssetPath(
                                       projectRoot,
                                       PageImageEditRenderer.WorkingCleanAssetKey(pageId)))
                ? PageImageEditRenderer.WorkingCleanAssetKey(pageId)
                : derived.CleanAssetKey;

            return new PageImageEditSnapshot(
                project.Id,
                page.Id,
                project.Revision,
                page.Revision,
                false,
                CurrentVersion(page, PageVersionTrack.Lettering)?.Id,
                operationsJson,
                derived.CleanAssetKey,
                renderedAssetKey,
                derived.MaskAssetKey,
                derived.CandidateMaskAssetKey);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> UpdatePageImageEditAsync(
        string projectId,
        string pageId,
        UpdatePageImageEditCommand command,
        CancellationToken cancellationToken)
    {
        ValidateJsonObject(command.OperationsJson, nameof(command), "图像编辑操作 JSON 无效。");
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            if (page.Revision != command.ExpectedPageRevision)
            {
                throw new PageRevisionConflictException(pageId, page.Revision);
            }
            var artifactName = BackgroundRepairArtifactName(pageId);
            var artifact = await context.ProjectArtifacts.SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Name == artifactName,
                cancellationToken);
            var operationsJson = PageImageEditRenderer.NormalizeRepairDocument(command.OperationsJson);
            var derived = await GetDerivedBackgroundAsync(context, projectId, page, cancellationToken);
            if (artifact is null)
            {
                context.ProjectArtifacts.Add(new ProjectArtifactRecord
                {
                    Id = $"artifact-{Guid.NewGuid():N}",
                    ProjectId = projectId,
                    Name = artifactName,
                    Json = operationsJson,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                artifact.Json = operationsJson;
                artifact.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if (PageImageEditRenderer.HasRepairOperations(operationsJson))
            {
                await PageImageEditRenderer.RenderAsync(
                    projectRoot,
                    derived.CleanAssetKey,
                    operationsJson,
                    PageImageEditRenderer.WorkingCleanAssetKey(pageId),
                    cancellationToken);
            }
            else
            {
                PageImageEditRenderer.DeleteWorkingAsset(projectRoot, pageId);
            }

            page.Revision++;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<PageVersionSnapshot>> ListPageVersionsAsync(
        string projectId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var page = await context.Pages
                .AsNoTracking()
                .Include(item => item.Versions)
                .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == pageId, cancellationToken)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            return page.Versions
                .Where(item => item.Track == PageVersionTrack.Lettering)
                .OrderByDescending(item => item.Sequence)
                .Select(ToVersionSnapshot)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> CreatePageVersionAsync(
        string projectId,
        string pageId,
        CreatePageVersionCommand command,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            if (command.Track != PageVersionTrack.Lettering)
            {
                throw new NotSupportedException("底图不再维护独立版本，只支持嵌字版本。");
            }
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, command.ExpectedRevision);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            var current = CurrentVersion(page, PageVersionTrack.Lettering);
            var finalizeWorkingCopy = command.Activate
                && current?.IsWorkingCopy == true
                && command.Kind == PageVersionKind.User;
            if (command.Activate && !finalizeWorkingCopy)
            {
                foreach (var version in page.Versions.Where(version => version.Track == PageVersionTrack.Lettering))
                {
                    version.IsCurrent = false;
                }
            }

            var workingArtifact = await context.ProjectArtifacts.SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Name == WorkingLayoutArtifactName(pageId),
                cancellationToken);
            var versionId = finalizeWorkingCopy ? current!.Id : $"version-{Guid.NewGuid():N}";
            var hasResultAffectingChanges = workingArtifact is not null
                                            || command.LayoutJson is not null
                                            || command.CleanAssetKey is not null;
            var renderedAssetKey = command.RenderedAssetKey
                                   ?? (hasResultAffectingChanges ? null : current?.RenderedAssetKey);
            var versionRecord = finalizeWorkingCopy
                ? current!
                : new PageVersionRecord
                {
                    Id = versionId,
                    PageId = page.Id,
                    Sequence = page.Versions.Count == 0 ? 1 : page.Versions.Max(item => item.Sequence) + 1,
                    ParentVersionId = current?.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Label = string.Empty,
                    Track = PageVersionTrack.Lettering,
                    SourceAssetKey = current?.SourceAssetKey ?? page.SourceAssetKey
                };
            versionRecord.Label = NormalizeVersionLabel(command.Label);
            versionRecord.Kind = command.Kind;
            versionRecord.Track = PageVersionTrack.Lettering;
            versionRecord.IsCurrent = command.Activate;
            versionRecord.IsWorkingCopy = false;
            versionRecord.SnapshotRevision = page.Revision;
            versionRecord.SourceAssetKey = current?.SourceAssetKey ?? page.SourceAssetKey;
            versionRecord.CleanAssetKey = null;
            versionRecord.MaskAssetKey = null;
            versionRecord.RenderedAssetKey = renderedAssetKey;
            versionRecord.LayoutJson = command.LayoutJson ?? workingArtifact?.Json ?? current?.LayoutJson;
            if (!finalizeWorkingCopy)
            {
                page.Versions.Add(versionRecord);
            }
            if (command.Activate && workingArtifact is not null)
            {
                context.ProjectArtifacts.Remove(workingArtifact);
            }
            if (command.Activate)
            {
                page.CurrentLetteringVersionId = versionId;
                page.CurrentVersionId = versionId;
            }
            if (!string.IsNullOrWhiteSpace(command.CleanAssetKey)
                && !string.IsNullOrWhiteSpace(command.MaskAssetKey))
            {
                await SetDerivedBackgroundAsync(
                    context,
                    projectId,
                    page,
                    command.CleanAssetKey,
                    command.MaskAssetKey,
                    command.CandidateMaskAssetKey,
                    cancellationToken);
                await RemoveArtifactAsync(context, projectId, BackgroundRepairArtifactName(pageId), cancellationToken);
                PageImageEditRenderer.DeleteWorkingAsset(projectRoot, pageId);
            }
            page.Revision++;
            page.LastVersionedRevision = page.Revision;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> SwitchPageVersionAsync(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        CancellationToken cancellationToken) => await SwitchPageVersionAsync(
        projectId,
        pageId,
        versionId,
        expectedRevision,
        null,
        null,
        null,
        cancellationToken);

    public async Task<ProjectSnapshot> SwitchPageVersionAsync(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        string? cleanAssetKey,
        string? maskAssetKey,
        string? candidateMaskAssetKey,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);

        // A working copy is a real, recoverable lettering version. Switching away from it must
        // solidify it first instead of deleting the user's edits. The derived background and
        // any manual background repair are deliberately rebuilt/reset for the selected layout.
        var existingVersions = await ListPageVersionsAsync(projectId, pageId, cancellationToken);
        var selectedSnapshot = existingVersions.FirstOrDefault(version => version.VersionId == versionId)
            ?? throw new PageVersionNotFoundException(pageId, versionId);
        if (selectedSnapshot.Track != PageVersionTrack.Lettering)
        {
            throw new NotSupportedException("底图不再维护独立版本，只支持切换嵌字版本。");
        }
        var working = existingVersions.FirstOrDefault(version =>
            version.Track == selectedSnapshot.Track && version.IsCurrent && version.IsWorkingCopy);
        if (working is not null && !string.Equals(working.VersionId, versionId, StringComparison.Ordinal))
        {
            var manualNumber = existingVersions.Count(version =>
                version.Track == selectedSnapshot.Track
                && !version.IsWorkingCopy
                && version.Kind == PageVersionKind.User) + 1;
            var finalized = await CreatePageVersionAsync(
                projectId,
                pageId,
                new CreatePageVersionCommand(
                    $"手动编辑 {manualNumber}",
                    expectedRevision,
                    PageVersionKind.User,
                    Track: selectedSnapshot.Track),
                cancellationToken);
            expectedRevision = finalized.Revision;
        }

        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, expectedRevision);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            var selected = page.Versions.FirstOrDefault(item => item.Id == versionId)
                ?? throw new PageVersionNotFoundException(pageId, versionId);
            var previous = CurrentVersion(page, PageVersionTrack.Lettering);
            if (previous?.IsWorkingCopy == true && previous.Id != selected.Id)
            {
                throw new InvalidOperationException(
                    "当前编辑版本尚未固化，请重试版本切换以保留该版本。 ");
            }
            foreach (var version in page.Versions.Where(version => version.Track == PageVersionTrack.Lettering))
            {
                version.IsCurrent = version.Id == selected.Id;
            }

            page.CurrentLetteringVersionId = selected.Id;
            page.CurrentVersionId = selected.Id;
            page.SourceAssetKey = selected.SourceAssetKey;
            var workingArtifact = await context.ProjectArtifacts
                .SingleOrDefaultAsync(
                    item => item.ProjectId == projectId && item.Name == WorkingLayoutArtifactName(pageId),
                    cancellationToken);
            if (workingArtifact is not null) context.ProjectArtifacts.Remove(workingArtifact);
            await RemoveArtifactAsync(context, projectId, BackgroundRepairArtifactName(pageId), cancellationToken);
            PageImageEditRenderer.DeleteWorkingAsset(projectRoot, pageId);
            if (!string.IsNullOrWhiteSpace(cleanAssetKey)
                && !string.IsNullOrWhiteSpace(maskAssetKey))
            {
                await SetDerivedBackgroundAsync(
                    context,
                    projectId,
                    page,
                    cleanAssetKey,
                    maskAssetKey,
                    candidateMaskAssetKey,
                    cancellationToken);
            }
            page.Revision++;
            page.LastVersionedRevision = page.Revision;
            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    public async Task<ProjectSnapshot> DeletePageVersionAsync(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return await WithProjectLockAsync(projectId, async () =>
        {
            await using var context = ProjectDbContextFactory.Create(projectRoot);
            var project = await LoadProjectAsync(context, projectId, cancellationToken);
            EnsureRevision(project, expectedRevision);
            var page = project.Pages.FirstOrDefault(item => item.Id == pageId)
                ?? throw new ProjectPageNotFoundException(projectId, pageId);
            var version = page.Versions.FirstOrDefault(item => item.Id == versionId)
                ?? throw new PageVersionNotFoundException(pageId, versionId);
            if (version.Track != PageVersionTrack.Lettering)
            {
                throw new NotSupportedException("底图不再维护独立版本，只支持删除嵌字版本。");
            }
            if (page.Versions.Count(item => item.Track == PageVersionTrack.Lettering) <= 1)
            {
                throw new InvalidOperationException("页面至少需要保留一个嵌字版本。");
            }
            var wasCurrent = version.IsCurrent;
            page.Versions.Remove(version);
            if (wasCurrent)
            {
                var workingArtifact = await context.ProjectArtifacts
                    .SingleOrDefaultAsync(
                        item => item.ProjectId == projectId && item.Name == WorkingLayoutArtifactName(pageId),
                        cancellationToken);
                if (workingArtifact is not null) context.ProjectArtifacts.Remove(workingArtifact);
                var replacement = page.Versions
                    .Where(item => item.Track == PageVersionTrack.Lettering)
                    .OrderByDescending(item => item.Sequence)
                    .First();
                replacement.IsCurrent = true;
                page.CurrentLetteringVersionId = replacement.Id;
                page.CurrentVersionId = replacement.Id;
                page.SourceAssetKey = replacement.SourceAssetKey;
                page.Revision++;
                page.LastVersionedRevision = page.Revision;
            }

            project.Revision++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await recentProjects.UpsertAsync(projectRoot, project, project.Pages.Count, markOpened: false, cancellationToken);
            return ToSnapshot(project, projectRoot);
        }, cancellationToken);
    }

    private async Task<(string Root, ProjectRecord Project)> CreateProjectRootAsync(
        string parentDirectory,
        string name,
        ProjectSourceKind sourceKind,
        ProjectWorkflowMode workflowMode,
        string targetLanguage,
        IReadOnlyList<string>? enabledFontIds,
        string? defaultFontId,
        string? fallbackFontId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(parentDirectory);
        var root = CreateUniqueProjectRoot(parentDirectory, name);
        Directory.CreateDirectory(root);
        try
        {
            foreach (var directory in LocalProjectLayout.AssetDirectories)
            {
                Directory.CreateDirectory(Path.Combine(root, directory));
            }

            var now = DateTimeOffset.UtcNow;
            var project = new ProjectRecord
            {
                Id = $"project-{Guid.NewGuid():N}",
                Name = name,
                SourceKind = sourceKind,
                WorkflowMode = workflowMode,
                TargetLanguage = NormalizeTargetLanguage(targetLanguage),
                EnabledFontIdsJson = SerializeFontIds(enabledFontIds),
                DefaultFontId = NormalizeOptionalId(defaultFontId),
                FallbackFontId = NormalizeOptionalId(fallbackFontId),
                PrecheckBatchSize = ProjectPrecheckDefaults.BatchSize,
                PrecheckBatchConcurrency = ProjectPrecheckDefaults.BatchConcurrency,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            await ProjectDbContextFactory.InitializeAsync(root, cancellationToken);
            await using (var context = ProjectDbContextFactory.Create(root))
            {
                context.Projects.Add(project);
                await context.SaveChangesAsync(cancellationToken);
            }

            await new ProjectManifest(
                    "1.2.0",
                    project.Id,
                    project.Name,
                    project.CreatedAt,
                    project.WorkflowMode,
                    project.TargetLanguage,
                    DeserializeFontIds(project.EnabledFontIdsJson),
                    project.DefaultFontId,
                    project.FallbackFontId,
                    project.PrecheckBatchSize,
                    project.PrecheckBatchConcurrency)
                .WriteAsync(root, cancellationToken);
            return (root, project);
        }
        catch
        {
            TryDeleteCreatedProject(root);
            throw;
        }
    }

    private async Task<ProjectRecord> LoadProjectAsync(
        ProjectDbContext context,
        string projectId,
        CancellationToken cancellationToken)
    {
        return await context.Projects
                   .Include(item => item.Pages.OrderBy(page => page.Index))
                   .ThenInclude(item => item.Versions)
                   .SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
               ?? throw new ProjectNotFoundException(projectId);
    }

    private async Task<T> WithProjectLockAsync<T>(
        string projectId,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var gate = _projectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private static ProjectSnapshot ToSnapshot(ProjectRecord project, string projectRoot) => new(
        project.Id,
        project.Name,
        project.SourceKind,
        project.Revision,
        project.AutoSaveEnabled,
        project.WorkflowMode,
        project.TargetLanguage,
        DeserializeFontIds(project.EnabledFontIdsJson),
        project.DefaultFontId,
        project.FallbackFontId,
        projectRoot,
        project.Pages.OrderBy(item => item.Index).Select(page => new ProjectPageSnapshot(
            page.Id,
            page.Index,
            page.OriginalFileName,
            page.Width,
            page.Height,
            page.PageType,
            page.IncludedInWorkflow,
            page.ReviewTag,
            page.SourceAssetKey,
            page.Versions.Any(version =>
                version.Track == PageVersionTrack.Lettering && version.Kind == PageVersionKind.Ai),
            page.Versions.Any(version =>
                version.Track == PageVersionTrack.Lettering && version.IsCurrent && version.IsWorkingCopy),
            page.Revision,
            CurrentVersion(page, PageVersionTrack.Lettering)?.Id,
            CurrentVersion(page, PageVersionTrack.Lettering)?.Id,
            null,
            page.Versions.Count(version => version.Track == PageVersionTrack.Lettering))).ToArray(),
        project.CreatedAt,
        project.UpdatedAt,
        project.PrecheckBatchSize,
        project.PrecheckBatchConcurrency);

    private static PageVersionSnapshot ToVersionSnapshot(PageVersionRecord version) => new(
        version.Id,
        version.PageId,
        version.Sequence,
        version.Label,
        version.Kind,
        version.Track,
        version.ParentVersionId,
        version.IsCurrent,
        version.IsWorkingCopy,
        version.SourceAssetKey,
        version.CleanAssetKey,
        version.MaskAssetKey,
        version.RenderedAssetKey,
        version.LayoutJson,
        version.CreatedAt);

    private static async Task<PageVersionRecord> EnsureWorkingVersionAsync(
        ProjectDbContext context,
        string projectId,
        PageRecord page,
        PageVersionTrack track,
        CancellationToken cancellationToken)
    {
        if (track != PageVersionTrack.Lettering)
        {
            throw new NotSupportedException("底图不再维护独立工作版本。");
        }
        var current = CurrentVersion(page, track);
        if (current?.IsWorkingCopy == true) return current;

        foreach (var version in page.Versions.Where(version => version.Track == track))
        {
            version.IsCurrent = false;
        }
        var versionId = $"version-{Guid.NewGuid():N}";
        var working = new PageVersionRecord
        {
            Id = versionId,
            PageId = page.Id,
            Sequence = page.Versions.Count == 0 ? 1 : page.Versions.Max(item => item.Sequence) + 1,
            Label = "当前编辑",
            Kind = PageVersionKind.User,
            Track = track,
            ParentVersionId = current?.Id,
            IsCurrent = true,
            IsWorkingCopy = true,
            SnapshotRevision = page.Revision,
            SourceAssetKey = current?.SourceAssetKey ?? page.SourceAssetKey,
            CleanAssetKey = null,
            MaskAssetKey = null,
            RenderedAssetKey = current?.RenderedAssetKey,
            LayoutJson = current?.LayoutJson,
            CreatedAt = DateTimeOffset.UtcNow
        };
        page.Versions.Add(working);
        page.CurrentLetteringVersionId = working.Id;
        page.CurrentVersionId = working.Id;

        return working;
    }

    private static PageVersionRecord? CurrentVersion(PageRecord page, PageVersionTrack track)
    {
        var versionId = track == PageVersionTrack.Lettering
            ? page.CurrentLetteringVersionId ?? page.CurrentVersionId
            : page.CurrentBackgroundVersionId;
        return page.Versions.FirstOrDefault(version =>
                   version.Track == track && version.Id == versionId)
               ?? page.Versions.FirstOrDefault(version => version.Track == track && version.IsCurrent);
    }

    private static string WorkingLayoutArtifactName(string pageId) => $"page/{pageId}/working-layout.json";

    private static string DerivedBackgroundArtifactName(string pageId) => $"page/{pageId}/derived-background.json";

    private static string BackgroundRepairArtifactName(string pageId) => $"page/{pageId}/background-repair.json";

    private static string WorkingImageEditArtifactName(string pageId) => $"page/{pageId}/working-image-edit.json";

    private static string VersionImageEditArtifactName(string pageId, string versionId) => $"page/{pageId}/versions/{versionId}/image-edit.json";

    private static async Task UpsertArtifactAsync(
        ProjectDbContext context,
        string projectId,
        string name,
        string json,
        CancellationToken cancellationToken)
    {
        var artifact = await context.ProjectArtifacts.SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Name == name,
            cancellationToken);
        if (artifact is null)
        {
            context.ProjectArtifacts.Add(new ProjectArtifactRecord
            {
                Id = $"artifact-{Guid.NewGuid():N}",
                ProjectId = projectId,
                Name = name,
                Json = json,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            artifact.Json = json;
            artifact.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task RemoveArtifactAsync(
        ProjectDbContext context,
        string projectId,
        string name,
        CancellationToken cancellationToken)
    {
        var artifact = await context.ProjectArtifacts.SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Name == name,
            cancellationToken);
        if (artifact is not null) context.ProjectArtifacts.Remove(artifact);
    }

    private static async Task SetDerivedBackgroundAsync(
        ProjectDbContext context,
        string projectId,
        PageRecord page,
        string cleanAssetKey,
        string maskAssetKey,
        string? candidateMaskAssetKey,
        CancellationToken cancellationToken) => await UpsertArtifactAsync(
        context,
        projectId,
        DerivedBackgroundArtifactName(page.Id),
        JsonSerializer.Serialize(new
        {
            schema_version = "2.0.0",
            clean_asset_key = cleanAssetKey,
            mask_asset_key = maskAssetKey,
            candidate_mask_asset_key = candidateMaskAssetKey ?? maskAssetKey,
            lettering_version_id = CurrentVersion(page, PageVersionTrack.Lettering)?.Id,
            updated_at = DateTimeOffset.UtcNow
        }),
        cancellationToken);

    private static async Task<DerivedBackgroundState> GetDerivedBackgroundAsync(
        ProjectDbContext context,
        string projectId,
        PageRecord page,
        CancellationToken cancellationToken)
    {
        var artifact = await context.ProjectArtifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Name == DerivedBackgroundArtifactName(page.Id),
                cancellationToken);
        if (artifact is not null)
        {
            try
            {
                var state = JsonSerializer.Deserialize<DerivedBackgroundState>(artifact.Json);
                if (state is not null && !string.IsNullOrWhiteSpace(state.CleanAssetKey)) return state;
            }
            catch (JsonException)
            {
                // Fall through to the legacy background version for old or partially written projects.
            }
        }

        var legacy = CurrentVersion(page, PageVersionTrack.Background);
        string? candidateMaskAssetKey = null;
        if (legacy is not null)
        {
            var legacyArtifact = await context.ProjectArtifacts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == projectId
                            && item.Name == VersionImageEditArtifactName(page.Id, legacy.Id),
                    cancellationToken);
            candidateMaskAssetKey = PageImageEditRenderer.ResolveLegacyCandidateMaskAssetKey(legacyArtifact?.Json);
        }
        return new DerivedBackgroundState(
            legacy?.CleanAssetKey ?? legacy?.SourceAssetKey ?? page.SourceAssetKey,
            legacy?.MaskAssetKey,
            candidateMaskAssetKey ?? legacy?.MaskAssetKey);
    }

    private sealed record DerivedBackgroundState(
        [property: System.Text.Json.Serialization.JsonPropertyName("clean_asset_key")] string CleanAssetKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("mask_asset_key")] string? MaskAssetKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("candidate_mask_asset_key")] string? CandidateMaskAssetKey);

    private static void ValidateJsonObject(string json, string parameterName, string message)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException(message, parameterName);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(message);
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(message, parameterName, exception);
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteManifestAsync(string root, ProjectRecord project, CancellationToken cancellationToken) =>
        await new ProjectManifest(
            "1.2.0",
            project.Id,
            project.Name,
            project.CreatedAt,
            project.WorkflowMode,
            project.TargetLanguage,
            DeserializeFontIds(project.EnabledFontIdsJson),
            project.DefaultFontId,
            project.FallbackFontId,
            project.PrecheckBatchSize,
            project.PrecheckBatchConcurrency).WriteAsync(root, cancellationToken);

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("项目名称不能为空。", nameof(name));
        }

        return normalized.Length > 120 ? normalized[..120] : normalized;
    }

    private static string NormalizeTargetLanguage(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "zh-Hans" : normalized[..Math.Min(normalized.Length, 32)];
    }

    private static int NormalizePrecheckBatchSize(int value)
    {
        if (value is < ProjectPrecheckDefaults.MinimumBatchSize or > ProjectPrecheckDefaults.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Precheck batch size must be between {ProjectPrecheckDefaults.MinimumBatchSize} and {ProjectPrecheckDefaults.MaximumBatchSize}.");
        }
        return value;
    }

    private static int NormalizePrecheckBatchConcurrency(int value)
    {
        if (value is < ProjectPrecheckDefaults.MinimumBatchConcurrency or > ProjectPrecheckDefaults.MaximumBatchConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Precheck batch concurrency must be between {ProjectPrecheckDefaults.MinimumBatchConcurrency} and {ProjectPrecheckDefaults.MaximumBatchConcurrency}.");
        }
        return value;
    }

    private static string? NormalizeOptionalId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized[..Math.Min(normalized.Length, 64)];
    }

    private static string SerializeFontIds(IReadOnlyList<string>? values) => JsonSerializer.Serialize(
        (values ?? [])
            .Select(NormalizeOptionalId)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static IReadOnlyList<string> DeserializeFontIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizePageType(string? pageType)
    {
        var normalized = pageType?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static string NormalizeVersionLabel(string? label)
    {
        var normalized = label?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "用户版本" : normalized[..Math.Min(normalized.Length, 120)];
    }

    private static void EnsureRevision(ProjectRecord project, int expectedRevision)
    {
        if (project.Revision != expectedRevision)
        {
            throw new ProjectRevisionConflictException(project.Revision);
        }
    }

    private static string CreateUniqueProjectRoot(string parentDirectory, string name)
    {
        var safeName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
        {
            safeName = "EMT 项目";
        }

        var candidate = Path.Combine(parentDirectory, safeName);
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(parentDirectory, $"{safeName} ({suffix})");
        }

        return Path.GetFullPath(candidate);
    }

    private static void TryDeleteCreatedProject(string projectRoot)
    {
        try
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort; the original exception is more useful to the caller.
        }
    }

    private static string MediaTypeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream"
    };

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;
                    var leftNumber = left[leftStart..leftIndex].TrimStart('0');
                    var rightNumber = right[rightStart..rightIndex].TrimStart('0');
                    var result = leftNumber.Length.CompareTo(rightNumber.Length);
                    if (result == 0) result = string.Compare(leftNumber, rightNumber, StringComparison.Ordinal);
                    if (result != 0) return result;
                    continue;
                }

                var characterResult = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (characterResult != 0) return characterResult;
                leftIndex++;
                rightIndex++;
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}
