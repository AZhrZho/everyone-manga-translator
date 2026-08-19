using System.IO.Compression;
using MangaTranslator.ProjectSystem;
using SkiaSharp;

namespace MangaTranslator.ProjectSystem.Local.Tests;

public sealed class LocalExportServiceTests
{
    [Fact]
    public async Task ZipExportSkipsPagesExcludedFromWorkflow()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = Path.Combine(workspace.Root, "result.zip");
        var grants = new LocalPathGrantRegistry();
        var grant = grants.Register(destination, ProjectLocationKind.ExportZip);
        var results = new RecordingResultService();
        var service = new LocalExportService(grants, new StubProjectStore(Project()), results);

        var exported = await service.ExportAsync(
            "project-1",
            new ExportProjectCommand(grant.Token, ProjectExportFormat.Zip),
            null,
            CancellationToken.None);

        Assert.Equal(1, exported.PageCount);
        Assert.Equal(["page-1"], results.RenderedPageIds);
        using var archive = ZipFile.OpenRead(destination);
        Assert.Single(archive.Entries);
        Assert.StartsWith("0001_", archive.Entries[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledZipExportDoesNotReplaceExistingDestinationOrLeaveTemporaryFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = Path.Combine(workspace.Root, "result.zip");
        await File.WriteAllTextAsync(destination, "existing");
        var grants = new LocalPathGrantRegistry();
        var grant = grants.Register(destination, ProjectLocationKind.ExportZip);
        var service = new LocalExportService(
            grants,
            new StubProjectStore(Project(includeSecondPage: true)),
            new RecordingResultService());
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(
            "project-1",
            new ExportProjectCommand(grant.Token, ProjectExportFormat.Zip),
            (progress, _) =>
            {
                if (progress.CompletedPages == 1) cancellation.Cancel();
                return Task.CompletedTask;
            },
            cancellation.Token));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp"));
    }

    private static ProjectSnapshot Project(bool includeSecondPage = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProjectSnapshot(
            "project-1",
            "Project",
            ProjectSourceKind.Folder,
            1,
            true,
            "C:\\project",
            [
                Page("page-1", 1, true),
                Page("page-2", 2, includeSecondPage)
            ],
            now,
            now);
    }

    private static ProjectPageSnapshot Page(string pageId, int index, bool included) =>
        new(pageId, index, $"page-{index}.png", 16, 16, "story", included, PageReviewTag.Unreviewed, $"source/{pageId}.png", false, false, 0, null, null, null, 0);

    private sealed class RecordingResultService : IProjectResultService
    {
        public List<string> RenderedPageIds { get; } = [];

        public Task<ProjectAsset> RenderPageAsync(string projectId, string pageId, CancellationToken cancellationToken)
        {
            RenderedPageIds.Add(pageId);
            using var bitmap = new SKBitmap(16, 16);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = data.ToArray();
            return Task.FromResult(new ProjectAsset(new MemoryStream(bytes, writable: false), "image/png", $"{pageId}.png", bytes.Length));
        }

        public Task<ProjectAsset> RenderPageAsync(string projectId, string pageId, string backgroundAssetKey, string? layoutJson, CancellationToken cancellationToken) =>
            RenderPageAsync(projectId, pageId, cancellationToken);
    }

    private sealed class StubProjectStore(ProjectSnapshot project) : IProjectStore
    {
        public Task<ProjectSnapshot> GetAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(project);
        public Task<IReadOnlyList<ProjectListItem>> ListRecentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> ImportFolderAsync(ImportFolderProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> ImportPdfAsync(ImportPdfProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> OpenAsync(OpenProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> RenameAsync(string projectId, RenameProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageReviewAsync(string projectId, string pageId, UpdatePageReviewCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageReviewTagAsync(string projectId, string pageId, UpdatePageReviewTagCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PageLayoutSnapshot> GetPageLayoutAsync(string projectId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageLayoutAsync(string projectId, string pageId, UpdatePageLayoutCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PageImageEditSnapshot> GetPageImageEditAsync(string projectId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageImageEditAsync(string projectId, string pageId, UpdatePageImageEditCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PageVersionSnapshot>> ListPageVersionsAsync(string projectId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> CreatePageVersionAsync(string projectId, string pageId, CreatePageVersionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> SwitchPageVersionAsync(string projectId, string pageId, string versionId, int expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> DeletePageVersionAsync(string projectId, string pageId, string versionId, int expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"emt-export-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
