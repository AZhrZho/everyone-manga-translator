using MangaTranslator.Api.Jobs;
using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;
using PageReviewTag = MangaTranslator.ProjectSystem.PageReviewTag;
using PageVersionKind = MangaTranslator.ProjectSystem.PageVersionKind;
using PageVersionTrack = MangaTranslator.ProjectSystem.PageVersionTrack;
using ProjectSourceKind = MangaTranslator.ProjectSystem.ProjectSourceKind;
using System.Text.Json;

namespace MangaTranslator.Api.Tests;

public sealed class TranslationPageRunnerTests
{
    [Theory]
    [InlineData("{\"needs_priority_review\":true,\"segments\":[]}", true)]
    [InlineData("{\"needs_priority_review\":false,\"segments\":[{\"needs_priority_review\":true}]}", true)]
    [InlineData("{\"segments\":[{\"needs_priority_review\":false}]}", false)]
    public void CachedLayoutPriorityFlagIsDetected(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, CachedTranslationExecutionSource.NeedsPriorityReview(document.RootElement));
    }

    [Theory]
    [InlineData(true, PageReviewTag.Priority)]
    [InlineData(false, PageReviewTag.Unreviewed)]
    public async Task AiPriorityFlagUpdatesPageReviewTag(
        bool needsPriorityReview,
        PageReviewTag expectedTag)
    {
        var store = new RecordingProjectStore();
        var runner = new TranslationPageRunner(store, new UnlockedProjectEditLock(), new ProjectOperationGate());
        var source = new SingleResultSource(new TranslationPageExecutionResult(
            "{\"segments\":[]}",
            "clean/page.png",
            "mask/page.png",
            "rendered/page.png",
            "done",
            "detail",
            needsPriorityReview));

        await runner.RunAsync(
            source,
            new TranslationPageExecutionContext(
                "translation-12345678",
                store.Project.ProjectId,
                "run-1",
                1,
                (_, _, _) => Task.CompletedTask),
            CancellationToken.None);

        Assert.Equal(expectedTag, store.Project.Pages.Single().ReviewTag);
        Assert.Equal([PageVersionTrack.Lettering], store.CreatedTracks);
        Assert.Equal("clean/page.png", store.CreatedCleanAssetKeys.Single());
        Assert.All(store.CreatedKinds, kind => Assert.Equal(PageVersionKind.Ai, kind));
    }

    [Fact]
    public async Task AiRerunCreatesLetteringVersionAndRebasesDerivedBackground()
    {
        var store = new RecordingProjectStore();
        var runner = new TranslationPageRunner(store, new UnlockedProjectEditLock(), new ProjectOperationGate());
        var source = new SingleResultSource(new TranslationPageExecutionResult(
            "{\"segments\":[]}",
            "clean/new-ai.png",
            "mask/new-ai.png",
            "rendered/new-ai.png",
            "done",
            "detail",
            false));

        await runner.RunAsync(
            source,
            new TranslationPageExecutionContext(
                "translation-protect-background",
                store.Project.ProjectId,
                "run-2",
                1,
                (_, _, _) => Task.CompletedTask),
            CancellationToken.None);

        Assert.Equal([true], store.CreatedActivations);
        Assert.Equal("clean/new-ai.png", store.CreatedCleanAssetKeys.Single());
        var versions = await store.ListPageVersionsAsync(store.Project.ProjectId, "page-0001", CancellationToken.None);
        Assert.Contains(versions, version =>
            version.Track == PageVersionTrack.Lettering
            && version.Kind == PageVersionKind.Ai
            && version.IsCurrent);
    }

    [Fact]
    public async Task CandidateMaskIsForwardedToDerivedBackgroundState()
    {
        var store = new RecordingProjectStore();
        var runner = new TranslationPageRunner(store, new UnlockedProjectEditLock(), new ProjectOperationGate());
        var source = new SingleResultSource(new TranslationPageExecutionResult(
            "{\"segments\":[]}",
            "clean/page.png",
            "mask/effective.png",
            "rendered/page.png",
            "done",
            "detail",
            CandidateMaskAssetKey: "mask/candidate.png"));

        await runner.RunAsync(
            source,
            new TranslationPageExecutionContext(
                "translation-candidate-mask",
                store.Project.ProjectId,
                "run-3",
                1,
                (_, _, _) => Task.CompletedTask),
            CancellationToken.None);

        Assert.Equal("mask/candidate.png", store.CreatedCandidateMaskAssetKeys.Single());
    }

    [Fact]
    public async Task AiRerunPublishesLatestDerivedBackground()
    {
        var store = new RecordingProjectStore();
        var runner = new TranslationPageRunner(
            store,
            new UnlockedProjectEditLock(),
            new ProjectOperationGate());

        await runner.RunAsync(
            new TranslationPageExecutionContext(
                "translation-equivalent-background",
                store.Project.ProjectId,
                "run-equivalent",
                1,
                (_, _, _) => Task.CompletedTask),
            new TranslationPageExecutionResult(
                "{\"segments\":[]}",
                "clean/rerun.png",
                "mask/rerun.png",
                "rendered/rerun.png",
                "done",
                "detail",
                CandidateMaskAssetKey: "mask/rerun-candidate.png"),
            CancellationToken.None);

        Assert.Equal([PageVersionTrack.Lettering], store.CreatedTracks);
        Assert.Equal("clean/rerun.png", store.CreatedCleanAssetKeys.Single());
        var imageEdit = await store.GetPageImageEditAsync(
            store.Project.ProjectId,
            "page-0001",
            CancellationToken.None);
        Assert.Equal("clean/rerun.png", imageEdit.DerivedCleanAssetKey);
        Assert.Equal("mask/rerun.png", imageEdit.EffectiveMaskAssetKey);
        Assert.Equal("mask/rerun-candidate.png", imageEdit.CandidateMaskAssetKey);
    }

    [Fact]
    public async Task ChangedCleaningOutputStillOnlyCreatesLetteringVersion()
    {
        var store = new RecordingProjectStore();
        var runner = new TranslationPageRunner(
            store,
            new UnlockedProjectEditLock(),
            new ProjectOperationGate());

        await runner.RunAsync(
            new TranslationPageExecutionContext(
                "translation-changed-background",
                store.Project.ProjectId,
                "run-changed",
                1,
                (_, _, _) => Task.CompletedTask),
            new TranslationPageExecutionResult(
                "{\"segments\":[]}",
                "clean/rerun.png",
                "mask/rerun.png",
                "rendered/rerun.png",
                "done",
                "detail",
                CandidateMaskAssetKey: "mask/rerun-candidate.png"),
            CancellationToken.None);

        Assert.Equal([PageVersionTrack.Lettering], store.CreatedTracks);
        Assert.Equal("clean/rerun.png", store.CreatedCleanAssetKeys.Single());
    }

    [Fact]
    public async Task ExportLockAcquiredDuringInferencePreventsCommit()
    {
        var store = new RecordingProjectStore();
        var editLock = new RecordingProjectEditLock();
        var runner = new TranslationPageRunner(store, editLock, new ProjectOperationGate());
        var source = new LockingResultSource(
            editLock,
            store.Project.ProjectId,
            new TranslationPageExecutionResult(
                "{\"segments\":[]}",
                "clean/page.png",
                "mask/page.png",
                "rendered/page.png",
                "done",
                "detail"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            source,
            new TranslationPageExecutionContext(
                "translation-export-race",
                store.Project.ProjectId,
                "run-4",
                1,
                (_, _, _) => Task.CompletedTask),
            CancellationToken.None));

        Assert.Empty(store.CreatedTracks);
        editLock.Release();
    }

    private sealed class SingleResultSource(TranslationPageExecutionResult result) : ITranslationExecutionSource
    {
        public ExecutionMode Mode => ExecutionMode.Live;
        public Task<TranslationExecutionPlan> CreateAsync(StartTranslationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TranslationPageExecutionResult> ExecutePageAsync(TranslationPageExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class LockingResultSource(
        RecordingProjectEditLock editLock,
        string projectId,
        TranslationPageExecutionResult result) : ITranslationExecutionSource
    {
        public ExecutionMode Mode => ExecutionMode.Live;
        public Task<TranslationExecutionPlan> CreateAsync(StartTranslationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TranslationPageExecutionResult> ExecutePageAsync(TranslationPageExecutionContext context, CancellationToken cancellationToken)
        {
            _ = editLock.AcquireExport(projectId);
            return Task.FromResult(result);
        }
    }

    private sealed class UnlockedProjectEditLock : IProjectEditLock
    {
        public bool IsLocked(string projectId) => false;
        public IDisposable AcquireExport(string projectId) => throw new NotSupportedException();
    }

    private sealed class RecordingProjectEditLock : IProjectEditLock
    {
        private bool locked;

        public bool IsLocked(string projectId) => locked;

        public IDisposable AcquireExport(string projectId)
        {
            locked = true;
            return new ReleaseLease(this);
        }

        public void Release() => locked = false;

        private sealed class ReleaseLease(RecordingProjectEditLock owner) : IDisposable
        {
            public void Dispose() => owner.Release();
        }
    }

    private sealed class RecordingProjectStore : IProjectStore
    {
        private ProjectSnapshot project = CreateProject();
        private readonly List<PageVersionSnapshot> versions =
        [
            new("imported-lettering", "page-0001", 1, "Imported lettering", PageVersionKind.Imported, PageVersionTrack.Lettering, null, true, false, "source/page.png", null, null, null, null, DateTimeOffset.UtcNow)
        ];

        public ProjectSnapshot Project => project;
        public List<PageVersionKind> CreatedKinds { get; } = [];
        public List<PageVersionTrack> CreatedTracks { get; } = [];
        public List<bool> CreatedActivations { get; } = [];
        public List<string?> CreatedCandidateMaskAssetKeys { get; } = [];
        public List<string?> CreatedCleanAssetKeys { get; } = [];
        private string derivedCleanAssetKey = "source/page.png";
        private string? derivedMaskAssetKey;
        private string? candidateMaskAssetKey;

        public Task<ProjectSnapshot> GetAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(project);
        public Task<IReadOnlyList<PageVersionSnapshot>> ListPageVersionsAsync(string projectId, string pageId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PageVersionSnapshot>>(versions.ToArray());

        public Task<ProjectSnapshot> CreatePageVersionAsync(string projectId, string pageId, CreatePageVersionCommand command, CancellationToken cancellationToken)
        {
            Assert.Equal(project.Revision, command.ExpectedRevision);
            CreatedKinds.Add(command.Kind);
            CreatedTracks.Add(command.Track);
            CreatedActivations.Add(command.Activate);
            CreatedCandidateMaskAssetKeys.Add(command.CandidateMaskAssetKey);
            CreatedCleanAssetKeys.Add(command.CleanAssetKey);
            if (!string.IsNullOrWhiteSpace(command.CleanAssetKey)
                && !string.IsNullOrWhiteSpace(command.MaskAssetKey))
            {
                derivedCleanAssetKey = command.CleanAssetKey;
                derivedMaskAssetKey = command.MaskAssetKey;
                candidateMaskAssetKey = command.CandidateMaskAssetKey;
            }
            var versionId = $"version-{versions.Count + 1}";
            if (command.Activate)
            {
                for (var index = 0; index < versions.Count; index++)
                {
                    if (versions[index].Track == command.Track)
                    {
                        versions[index] = versions[index] with { IsCurrent = false };
                    }
                }
            }
            var parentVersionId = versions.Last(version => version.Track == command.Track).VersionId;
            versions.Add(new PageVersionSnapshot(versionId, pageId, versions.Count + 1, command.Label, command.Kind, command.Track, parentVersionId, command.Activate, false, "source/page.png", command.CleanAssetKey, command.MaskAssetKey, command.RenderedAssetKey, command.LayoutJson, DateTimeOffset.UtcNow));
            var currentPage = project.Pages.Single();
            var page = currentPage with
            {
                CurrentVersionId = command.Activate && command.Track == PageVersionTrack.Lettering ? versionId : currentPage.CurrentVersionId,
                CurrentLetteringVersionId = command.Activate && command.Track == PageVersionTrack.Lettering ? versionId : currentPage.CurrentLetteringVersionId,
                VersionCount = versions.Count,
                Revision = currentPage.Revision + 1,
                HasUnversionedUserChanges = false
            };
            project = project with { Revision = project.Revision + 1, Pages = [page], UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(project);
        }

        public Task<ProjectSnapshot> UpdatePageReviewTagAsync(string projectId, string pageId, UpdatePageReviewTagCommand command, CancellationToken cancellationToken)
        {
            Assert.Equal(project.Revision, command.ExpectedRevision);
            var page = project.Pages.Single() with { ReviewTag = command.ReviewTag };
            project = project with { Revision = project.Revision + 1, Pages = [page], UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(project);
        }

        public Task<IReadOnlyList<ProjectListItem>> ListRecentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> ImportFolderAsync(ImportFolderProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> ImportPdfAsync(ImportPdfProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> OpenAsync(OpenProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> RenameAsync(string projectId, RenameProjectCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageReviewAsync(string projectId, string pageId, UpdatePageReviewCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PageLayoutSnapshot> GetPageLayoutAsync(string projectId, string pageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> UpdatePageLayoutAsync(string projectId, string pageId, UpdatePageLayoutCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> RebasePageBackgroundAsync(string projectId, string pageId, RebasePageBackgroundCommand command, CancellationToken cancellationToken)
        {
            Assert.Equal(project.Revision, command.ExpectedRevision);
            var page = project.Pages.Single() with { Revision = project.Pages.Single().Revision + 1 };
            project = project with { Revision = project.Revision + 1, Pages = [page], UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(project);
        }
        public Task<PageImageEditSnapshot> GetPageImageEditAsync(string projectId, string pageId, CancellationToken cancellationToken)
        {
            var page = project.Pages.Single();
            return Task.FromResult(new PageImageEditSnapshot(
                projectId,
                pageId,
                project.Revision,
                page.Revision,
                false,
                page.CurrentLetteringVersionId,
                null,
                derivedCleanAssetKey,
                derivedCleanAssetKey,
                derivedMaskAssetKey,
                candidateMaskAssetKey ?? derivedMaskAssetKey));
        }
        public Task<ProjectSnapshot> UpdatePageImageEditAsync(string projectId, string pageId, UpdatePageImageEditCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> SwitchPageVersionAsync(string projectId, string pageId, string versionId, int expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectSnapshot> DeletePageVersionAsync(string projectId, string pageId, string versionId, int expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static ProjectSnapshot CreateProject()
        {
            var now = DateTimeOffset.UtcNow;
            return new ProjectSnapshot(
                "project-1",
                "Project",
                ProjectSourceKind.Folder,
                1,
                true,
                "C:\\project",
                [new ProjectPageSnapshot("page-0001", 1, "page.png", 100, 140, "story", true, PageReviewTag.Unreviewed, "source/page.png", false, false, 1, "imported-lettering", "imported-lettering", null, 1)],
                now,
                now);
        }
    }

}
