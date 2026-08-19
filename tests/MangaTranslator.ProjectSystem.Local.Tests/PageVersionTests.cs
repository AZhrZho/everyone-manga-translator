using MangaTranslator.ProjectSystem;
using MangaTranslator.ProjectSystem.Local;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MangaTranslator.ProjectSystem.Local.Tests;

public sealed class PageVersionTests
{
    private const string InitialLayout = """
        {"schema_version":"1.0.0","segments":[]}
        """;
    private const string EditedLayout = """
        {"schema_version":"1.0.0","segments":[{"segment_id":"manual"}]}
        """;

    [Fact]
    public async Task ProjectPrecheckBatchSettingsArePersisted()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, _) = await harness.ImportSinglePageAsync();

        project = await harness.Store.UpdateSettingsAsync(
            project.ProjectId,
            new UpdateProjectSettingsCommand(
                project.WorkflowMode,
                project.TargetLanguage,
                project.EnabledFontIds,
                project.DefaultFontId,
                project.Revision,
                PrecheckBatchSize: 6,
                PrecheckBatchConcurrency: 2),
            CancellationToken.None);
        var restored = await harness.Store.GetAsync(project.ProjectId, CancellationToken.None);

        Assert.Equal(6, restored.PrecheckBatchSize);
        Assert.Equal(2, restored.PrecheckBatchConcurrency);
    }

    [Fact]
    public async Task ProjectPrecheckBatchSettingsRejectOutOfRangeValues()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, _) = await harness.ImportSinglePageAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.UpdateSettingsAsync(
            project.ProjectId,
            new UpdateProjectSettingsCommand(
                project.WorkflowMode,
                project.TargetLanguage,
                project.EnabledFontIds,
                project.DefaultFontId,
                project.Revision,
                PrecheckBatchSize: 0,
                PrecheckBatchConcurrency: 2),
            CancellationToken.None));
    }

    [Fact]
    public async Task ImportedPageStartsWithOnlyLetteringVersion()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();

        var versions = await harness.Store.ListPageVersionsAsync(project.ProjectId, page.PageId, CancellationToken.None);

        var imported = Assert.Single(versions);
        Assert.Equal(PageVersionTrack.Lettering, imported.Track);
        Assert.True(imported.IsCurrent);
        Assert.Null(page.CurrentBackgroundVersionId);
        Assert.Equal(1, page.VersionCount);
        Assert.False(page.HasAiResult);
    }

    [Fact]
    public async Task AiLetteringCommitAtomicallyPublishesDerivedBackground()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        const string cleanKey = "clean/page-ai.png";
        const string maskKey = "masks/page-ai.png";
        const string candidateMaskKey = "workflow-cache/page-0001/mask.png";
        await WritePngAsync(Path.Combine(project.LocationLabel, cleanKey), SKColors.White);
        await WritePngAsync(Path.Combine(project.LocationLabel, maskKey), SKColors.Black);
        await WritePngAsync(Path.Combine(project.LocationLabel, candidateMaskKey), SKColors.White);

        project = await harness.Store.CreatePageVersionAsync(
            project.ProjectId,
            page.PageId,
            new CreatePageVersionCommand(
                "AI result",
                project.Revision,
                PageVersionKind.Ai,
                LayoutJson: InitialLayout,
                CleanAssetKey: cleanKey,
                MaskAssetKey: maskKey,
                RenderedAssetKey: "rendered/page-ai.png",
                CandidateMaskAssetKey: candidateMaskKey),
            CancellationToken.None);

        var currentPage = Assert.Single(project.Pages);
        var edit = await harness.Store.GetPageImageEditAsync(project.ProjectId, page.PageId, CancellationToken.None);
        Assert.True(currentPage.HasAiResult);
        Assert.Equal(2, currentPage.VersionCount);
        Assert.Null(currentPage.CurrentBackgroundVersionId);
        Assert.Equal(cleanKey, edit.DerivedCleanAssetKey);
        Assert.Equal(cleanKey, edit.RenderedAssetKey);
        Assert.Equal(maskKey, edit.EffectiveMaskAssetKey);
        Assert.Equal(candidateMaskKey, edit.CandidateMaskAssetKey);
        Assert.False(edit.HasUnversionedUserChanges);
    }

    [Fact]
    public async Task SmartCleanupCreatesNoPageVersionAndUsesPageRevision()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        project = await PublishDerivedBackgroundAsync(harness, project, page.PageId, SKColors.LightGray);
        var before = await harness.Store.GetPageImageEditAsync(project.ProjectId, page.PageId, CancellationToken.None);
        var versionCount = project.Pages.Single().VersionCount;

        project = await harness.Store.RenameAsync(
            project.ProjectId,
            new RenameProjectCommand("Renamed while editing", project.Revision),
            CancellationToken.None);
        project = await harness.Store.UpdatePageImageEditAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageImageEditCommand(InpaintDocument("stroke-1"), before.PageRevision),
            CancellationToken.None);

        var edit = await harness.Store.GetPageImageEditAsync(project.ProjectId, page.PageId, CancellationToken.None);
        Assert.Equal(versionCount, project.Pages.Single().VersionCount);
        Assert.Contains("inpaint", edit.OperationsJson);
        Assert.Equal(PageImageEditRenderer.WorkingCleanAssetKey(page.PageId), edit.RenderedAssetKey);
        Assert.False(edit.HasUnversionedUserChanges);
    }

    [Fact]
    public async Task SmartCleanupRejectsLegacyTools()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        project = await PublishDerivedBackgroundAsync(harness, project, page.PageId, SKColors.White);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.UpdatePageImageEditAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageImageEditCommand(
                """{"schema_version":"2.0.0","operations":[{"tool":"restore","size_px":10,"points":[{"x":10,"y":10}]}]}""",
                project.Pages.Single().Revision),
            CancellationToken.None));
    }

    [Fact]
    public async Task LayoutUpdatePublishesNewBackgroundAndResetsRepairs()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        project = await PublishDerivedBackgroundAsync(harness, project, page.PageId, SKColors.Red);
        project = await harness.Store.UpdatePageImageEditAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageImageEditCommand(InpaintDocument("old-repair"), project.Pages.Single().Revision),
            CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(project.LocationLabel, PageImageEditRenderer.WorkingCleanAssetKey(page.PageId))));

        const string newCleanKey = "derived/page-0001/layout-clean.png";
        const string newMaskKey = "derived/page-0001/layout-mask.png";
        const string candidateMaskKey = "workflow-cache/page-0001/candidate.png";
        await WritePngAsync(Path.Combine(project.LocationLabel, newCleanKey), SKColors.Blue);
        await WritePngAsync(Path.Combine(project.LocationLabel, newMaskKey), SKColors.Black);
        await WritePngAsync(Path.Combine(project.LocationLabel, candidateMaskKey), SKColors.White);
        project = await harness.Store.UpdatePageLayoutAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageLayoutCommand(
                EditedLayout,
                project.Revision,
                newCleanKey,
                newMaskKey,
                candidateMaskKey),
            CancellationToken.None);

        var edit = await harness.Store.GetPageImageEditAsync(project.ProjectId, page.PageId, CancellationToken.None);
        Assert.Equal(newCleanKey, edit.DerivedCleanAssetKey);
        Assert.Equal(newCleanKey, edit.RenderedAssetKey);
        Assert.Equal(newMaskKey, edit.EffectiveMaskAssetKey);
        Assert.Equal(candidateMaskKey, edit.CandidateMaskAssetKey);
        Assert.DoesNotContain("old-repair", edit.OperationsJson);
        Assert.False(File.Exists(Path.Combine(project.LocationLabel, PageImageEditRenderer.WorkingCleanAssetKey(page.PageId))));
    }

    [Fact]
    public async Task RebaseBackgroundResetsRepairsWithoutCreatingVersion()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        project = await PublishDerivedBackgroundAsync(harness, project, page.PageId, SKColors.Red);
        var versionCount = project.Pages.Single().VersionCount;
        project = await harness.Store.UpdatePageImageEditAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageImageEditCommand(InpaintDocument("repair"), project.Pages.Single().Revision),
            CancellationToken.None);

        const string nextCleanKey = "derived/page-0001/next-clean.png";
        const string nextMaskKey = "derived/page-0001/next-mask.png";
        await WritePngAsync(Path.Combine(project.LocationLabel, nextCleanKey), SKColors.Green);
        await WritePngAsync(Path.Combine(project.LocationLabel, nextMaskKey), SKColors.Black);
        project = await harness.Store.RebasePageBackgroundAsync(
            project.ProjectId,
            page.PageId,
            new RebasePageBackgroundCommand(nextCleanKey, nextMaskKey, project.Revision),
            CancellationToken.None);

        var edit = await harness.Store.GetPageImageEditAsync(project.ProjectId, page.PageId, CancellationToken.None);
        Assert.Equal(versionCount, project.Pages.Single().VersionCount);
        Assert.Equal(nextCleanKey, edit.RenderedAssetKey);
        Assert.DoesNotContain("repair", edit.OperationsJson);
    }

    [Fact]
    public async Task LetteringVersionSwitchResetsRepairsAndAcceptsPreparedBackground()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        var importedVersionId = page.CurrentLetteringVersionId!;
        project = await PublishDerivedBackgroundAsync(harness, project, page.PageId, SKColors.Red);
        project = await harness.Store.UpdatePageImageEditAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageImageEditCommand(InpaintDocument("repair"), project.Pages.Single().Revision),
            CancellationToken.None);

        const string sourceCleanKey = "derived/page-0001/source-clean.png";
        const string sourceMaskKey = "derived/page-0001/source-mask.png";
        await WritePngAsync(Path.Combine(project.LocationLabel, sourceCleanKey), SKColors.White);
        await WritePngAsync(Path.Combine(project.LocationLabel, sourceMaskKey), SKColors.Black);
        project = await harness.Store.SwitchPageVersionAsync(
            project.ProjectId,
            page.PageId,
            importedVersionId,
            project.Revision,
            sourceCleanKey,
            sourceMaskKey,
            sourceMaskKey,
            CancellationToken.None);

        var edit = await harness.Store.GetPageImageEditAsync(project.ProjectId, page.PageId, CancellationToken.None);
        Assert.Equal(importedVersionId, project.Pages.Single().CurrentLetteringVersionId);
        Assert.Equal(sourceCleanKey, edit.RenderedAssetKey);
        Assert.DoesNotContain("repair", edit.OperationsJson);
    }

    [Fact]
    public async Task FirstLayoutEditCreatesOneWorkingVersionAndLaterEditsReuseIt()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();

        project = await harness.Store.UpdatePageLayoutAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageLayoutCommand(EditedLayout, project.Revision),
            CancellationToken.None);
        var first = await harness.Store.ListPageVersionsAsync(project.ProjectId, page.PageId, CancellationToken.None);
        var working = Assert.Single(first, version => version.IsWorkingCopy);

        project = await harness.Store.UpdatePageLayoutAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageLayoutCommand(InitialLayout, project.Revision),
            CancellationToken.None);
        var second = await harness.Store.ListPageVersionsAsync(project.ProjectId, page.PageId, CancellationToken.None);

        Assert.Equal(working.VersionId, Assert.Single(second, version => version.IsWorkingCopy).VersionId);
        Assert.Equal(2, second.Count);
        Assert.True(project.Pages.Single().HasUnversionedUserChanges);
    }

    [Fact]
    public async Task CreatingUserVersionFinalizesWorkingLetteringVersionInPlace()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        project = await harness.Store.UpdatePageLayoutAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageLayoutCommand(EditedLayout, project.Revision),
            CancellationToken.None);
        var before = await harness.Store.ListPageVersionsAsync(project.ProjectId, page.PageId, CancellationToken.None);
        var workingId = Assert.Single(before, version => version.IsWorkingCopy).VersionId;

        project = await harness.Store.CreatePageVersionAsync(
            project.ProjectId,
            page.PageId,
            new CreatePageVersionCommand("Manual lettering", project.Revision),
            CancellationToken.None);
        var after = await harness.Store.ListPageVersionsAsync(project.ProjectId, page.PageId, CancellationToken.None);

        var current = Assert.Single(after, version => version.IsCurrent);
        Assert.Equal(workingId, current.VersionId);
        Assert.False(current.IsWorkingCopy);
        Assert.Equal(EditedLayout, current.LayoutJson);
        Assert.False(project.Pages.Single().HasUnversionedUserChanges);
    }

    [Fact]
    public async Task ReviewTagDoesNotCreateVersion()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        var before = page.VersionCount;

        project = await harness.Store.UpdatePageReviewTagAsync(
            project.ProjectId,
            page.PageId,
            new UpdatePageReviewTagCommand(PageReviewTag.Priority, project.Revision),
            CancellationToken.None);

        Assert.Equal(before, project.Pages.Single().VersionCount);
        Assert.Equal(PageReviewTag.Priority, project.Pages.Single().ReviewTag);
    }

    [Fact]
    public async Task LegacySingleTrackDatabaseMigrationDoesNotCreateBackgroundVersions()
    {
        using var harness = await ProjectStoreHarness.CreateAsync();
        var (project, page) = await harness.ImportSinglePageAsync();
        var databasePath = Path.Combine(project.LocationLabel, "project.db");
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE PageVersions DROP COLUMN Track;";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        project = await harness.Store.GetAsync(project.ProjectId, CancellationToken.None);
        var versions = await harness.Store.ListPageVersionsAsync(project.ProjectId, page.PageId, CancellationToken.None);

        Assert.Single(versions);
        Assert.All(versions, version => Assert.Equal(PageVersionTrack.Lettering, version.Track));
        Assert.Null(project.Pages.Single().CurrentBackgroundVersionId);
    }

    private static async Task<ProjectSnapshot> PublishDerivedBackgroundAsync(
        ProjectStoreHarness harness,
        ProjectSnapshot project,
        string pageId,
        SKColor cleanColor)
    {
        var cleanKey = $"derived/{pageId}/{Guid.NewGuid():N}-clean.png";
        var maskKey = $"derived/{pageId}/{Guid.NewGuid():N}-mask.png";
        await WritePngAsync(Path.Combine(project.LocationLabel, cleanKey), cleanColor);
        await WritePngAsync(Path.Combine(project.LocationLabel, maskKey), SKColors.Black);
        return await harness.Store.CreatePageVersionAsync(
            project.ProjectId,
            pageId,
            new CreatePageVersionCommand(
                "AI result",
                project.Revision,
                PageVersionKind.Ai,
                LayoutJson: InitialLayout,
                CleanAssetKey: cleanKey,
                MaskAssetKey: maskKey,
                RenderedAssetKey: "rendered/page.png",
                CandidateMaskAssetKey: maskKey),
            CancellationToken.None);
    }

    private static string InpaintDocument(string operationId) => $$"""
        {"schema_version":"2.0.0","operations":[{"operation_id":"{{operationId}}","tool":"inpaint","size_px":12,"hardness":50,"spacing":18,"points":[{"x":16,"y":16}]}]}
        """;

    private static MemoryStream CreatePngStream(SKColor color)
    {
        using var bitmap = new SKBitmap(32, 32);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Unable to encode test image.");
        return new MemoryStream(data.ToArray(), writable: false);
    }

    private static async Task WritePngAsync(string path, SKColor color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var image = CreatePngStream(color);
        await using var output = File.Create(path);
        await image.CopyToAsync(output, CancellationToken.None);
    }

    private sealed class ProjectStoreHarness : IDisposable
    {
        private readonly string root;
        private readonly LocalPathGrantRegistry grants;

        private ProjectStoreHarness(string root, LocalPathGrantRegistry grants, LocalProjectStore store)
        {
            this.root = root;
            this.grants = grants;
            Store = store;
        }

        public LocalProjectStore Store { get; }

        public static Task<ProjectStoreHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"emt-project-store-tests-{Guid.NewGuid():N}");
            var userDataRoot = Path.Combine(root, "user-data");
            Directory.CreateDirectory(userDataRoot);
            var grants = new LocalPathGrantRegistry();
            var recentProjects = new LocalRecentProjectRegistry(new LocalProjectSystemOptions(userDataRoot));
            var store = new LocalProjectStore(grants, recentProjects, NullLogger<LocalProjectStore>.Instance);
            return Task.FromResult(new ProjectStoreHarness(root, grants, store));
        }

        public async Task<(ProjectSnapshot Project, ProjectPageSnapshot Page)> ImportSinglePageAsync()
        {
            var sourceRoot = Path.Combine(root, "source");
            var projectParent = Path.Combine(root, "projects");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(projectParent);
            await using (var image = CreatePngStream(SKColors.White))
            await using (var output = File.Create(Path.Combine(sourceRoot, "page-001.png")))
            {
                await image.CopyToAsync(output, CancellationToken.None);
            }

            var sourceGrant = grants.Register(sourceRoot, ProjectLocationKind.ImportFolder);
            var destinationGrant = grants.Register(projectParent, ProjectLocationKind.ProjectParent);
            var project = await Store.ImportFolderAsync(
                new ImportFolderProjectCommand("Version test", destinationGrant.Token, sourceGrant.Token),
                CancellationToken.None);
            return (project, project.Pages.Single());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
