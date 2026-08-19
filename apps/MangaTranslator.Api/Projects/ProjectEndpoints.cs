using MangaTranslator.Contracts;
using MangaTranslator.Api.Jobs;
using System.Text.Json;
using ProjectModel = MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Projects;

internal static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/host/local-path-grants", RegisterLocalPath);
        endpoints.MapGet("/api/projects", ListProjects);
        endpoints.MapGet("/api/projects/{projectId}", GetProject);
        endpoints.MapDelete("/api/projects/{projectId}", RemoveProject);
        endpoints.MapPost("/api/projects", CreateProject);
        endpoints.MapPost("/api/projects/open", OpenProject);
        endpoints.MapPost("/api/projects/{projectId}/export", ExportProject);
        endpoints.MapPost("/api/projects/import-folder", ImportFolderProject);
        endpoints.MapPost("/api/projects/import-pdf", ImportPdfProject);
        endpoints.MapPatch("/api/projects/{projectId}", RenameProject);
        endpoints.MapPatch("/api/projects/{projectId}/settings", UpdateProjectSettings);
        endpoints.MapPatch("/api/projects/{projectId}/pages/{pageId}/review", UpdatePageReview);
        endpoints.MapPatch("/api/projects/{projectId}/pages/{pageId}/review-tag", UpdatePageReviewTag);
        endpoints.MapGet("/api/projects/{projectId}/pages/{pageId}/layout", GetPageLayout);
        endpoints.MapPatch("/api/projects/{projectId}/pages/{pageId}/layout", UpdatePageLayout);
        endpoints.MapPost("/api/projects/{projectId}/pages/{pageId}/text-clips/render", RenderTextClip);
        endpoints.MapGet("/api/projects/{projectId}/pages/{pageId}/image-edit", GetPageImageEdit);
        endpoints.MapPatch("/api/projects/{projectId}/pages/{pageId}/image-edit", UpdatePageImageEdit);
        endpoints.MapGet("/api/projects/{projectId}/pages/{pageId}/original", GetOriginalAsset);
        endpoints.MapGet("/api/projects/{projectId}/pages/{pageId}/clean", GetCleanAsset);
        endpoints.MapGet("/api/projects/{projectId}/pages/{pageId}/result", GetResultAsset);
        endpoints.MapGet("/api/projects/{projectId}/pages/{pageId}/versions", ListPageVersions);
        endpoints.MapPost("/api/projects/{projectId}/pages/{pageId}/versions", CreatePageVersion);
        endpoints.MapPost("/api/projects/{projectId}/pages/{pageId}/versions/{versionId}/activate", ActivatePageVersion);
        endpoints.MapDelete("/api/projects/{projectId}/pages/{pageId}/versions/{versionId}", DeletePageVersion);
        endpoints.MapGet("/api/projects/{projectId}/precheck-review", GetPrecheckReview);
        endpoints.MapPatch("/api/projects/{projectId}/precheck/{artifactName}", UpdatePrecheckItem);
        endpoints.MapGet("/api/projects/{projectId}/translation-annotations", GetTranslationAnnotations);
        endpoints.MapPatch("/api/projects/{projectId}/translation-annotations", UpdateTranslationAnnotations);
        return endpoints;
    }

    private static IResult RegisterLocalPath(
        RegisterLocalPathRequest request,
        ProjectModel.IProjectLocationGrantRegistry locations)
    {
        if (!IsDesktopHost())
        {
            return Results.NotFound();
        }

        try
        {
            var grant = locations.Register(
                request.Path,
                Enum.Parse<ProjectModel.ProjectLocationKind>(request.Kind.ToString()));
            return Results.Ok(new ProjectLocationGrantResponse(
                grant.Token,
                Enum.Parse<ProjectLocationKind>(grant.Kind.ToString()),
                grant.DisplayName,
                grant.ExpiresAt));
        }
        catch (Exception exception) when (exception is ArgumentException or ProjectModel.ProjectLocationException)
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> ListProjects(
        ProjectModel.IProjectStore projects,
        HttpContext context) =>
        Results.Ok((await projects.ListRecentAsync(context.RequestAborted)).Select(item => item.ToResponse()));

    private static async Task<IResult> GetProject(
        string projectId,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            return Results.Ok((await projects.GetAsync(projectId, context.RequestAborted)).ToResponse());
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> RemoveProject(
        string projectId,
        bool deleteFiles,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            await projects.RemoveRecentAsync(projectId, deleteFiles, context.RequestAborted);
            return Results.NoContent();
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> CreateProject(
        CreateProjectRequest request,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            var project = await projects.CreateAsync(
                new ProjectModel.CreateProjectCommand(
                    request.Name,
                    request.DestinationToken,
                    Enum.Parse<ProjectModel.ProjectWorkflowMode>(request.WorkflowMode.ToString()),
                    request.TargetLanguage,
                    request.EnabledFontIds,
                    request.DefaultFontId,
                    request.FallbackFontId),
                context.RequestAborted);
            return Results.Created($"/api/projects/{Uri.EscapeDataString(project.ProjectId)}", project.ToResponse());
        }
        catch (Exception exception) when (IsClientProjectException(exception))
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> OpenProject(
        OpenProjectRequest request,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            var project = await projects.OpenAsync(
                new ProjectModel.OpenProjectCommand(request.LocationToken),
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (Exception exception) when (IsClientProjectException(exception))
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> ExportProject(
        string projectId,
        ExportProjectRequest request,
        ProjectModel.IExportService exports,
        ProjectModel.IProjectEditLock editLock,
        ProjectOperationGate operationGate,
        HttpContext context)
    {
        try
        {
            using var operation = await operationGate.EnterAsync(
                projectId,
                context.RequestAborted);
            using var lease = editLock.AcquireExport(projectId);
            var result = await exports.ExportAsync(
                projectId,
                new ProjectModel.ExportProjectCommand(
                    request.DestinationToken,
                    Enum.Parse<ProjectModel.ProjectExportFormat>(request.Format.ToString()),
                    request.IncludeBackground,
                    request.IncludeLettering,
                    Enum.Parse<ProjectModel.ProjectExportResolution>(request.Resolution.ToString())),
                null,
                context.RequestAborted);
            return Results.Ok(new ExportProjectResponse(
                Enum.Parse<ProjectExportFormat>(result.Format.ToString()),
                result.DestinationPath,
                result.PageCount));
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectLocationException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.BadRequest(exception);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(exception);
        }
    }

    private static async Task<IResult> ImportFolderProject(
        ImportFolderProjectRequest request,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            var project = await projects.ImportFolderAsync(
                new ProjectModel.ImportFolderProjectCommand(
                    request.Name,
                    request.DestinationToken,
                    request.SourceToken,
                    Enum.Parse<ProjectModel.ProjectWorkflowMode>(request.WorkflowMode.ToString()),
                    request.TargetLanguage,
                    request.EnabledFontIds,
                    request.DefaultFontId,
                    request.FallbackFontId),
                context.RequestAborted);
            return Results.Created($"/api/projects/{Uri.EscapeDataString(project.ProjectId)}", project.ToResponse());
        }
        catch (Exception exception) when (IsClientProjectException(exception))
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> ImportPdfProject(
        ImportPdfProjectRequest request,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            var project = await projects.ImportPdfAsync(
                new ProjectModel.ImportPdfProjectCommand(
                    request.Name,
                    request.DestinationToken,
                    request.SourceToken,
                    request.RenderDpi,
                    Enum.Parse<ProjectModel.ProjectWorkflowMode>(request.WorkflowMode.ToString()),
                    request.TargetLanguage,
                    request.EnabledFontIds,
                    request.DefaultFontId,
                    request.FallbackFontId),
                context.RequestAborted);
            return Results.Created($"/api/projects/{Uri.EscapeDataString(project.ProjectId)}", project.ToResponse());
        }
        catch (Exception exception) when (IsClientProjectException(exception))
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> RenameProject(
        string projectId,
        RenameProjectRequest request,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await projects.RenameAsync(
                projectId,
                new ProjectModel.RenameProjectCommand(request.Name, request.ExpectedRevision),
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception);
        }
        catch (NotSupportedException exception)
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> UpdateProjectSettings(
        string projectId,
        UpdateProjectSettingsRequest request,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await projects.UpdateSettingsAsync(
                projectId,
                new ProjectModel.UpdateProjectSettingsCommand(
                    Enum.Parse<ProjectModel.ProjectWorkflowMode>(request.WorkflowMode.ToString()),
                    request.TargetLanguage,
                    request.EnabledFontIds,
                    request.DefaultFontId,
                    request.ExpectedRevision,
                    request.FallbackFontId,
                    request.PrecheckBatchSize,
                    request.PrecheckBatchConcurrency),
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> UpdatePageReview(
        string projectId,
        string pageId,
        UpdatePageReviewRequest request,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await projects.UpdatePageReviewAsync(
                projectId,
                pageId,
                new ProjectModel.UpdatePageReviewCommand(
                    request.PageType,
                    request.IncludedInWorkflow,
                    request.ExpectedRevision),
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdatePageReviewTag(
        string projectId,
        string pageId,
        UpdatePageReviewTagRequest request,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await projects.UpdatePageReviewTagAsync(
                projectId,
                pageId,
                new ProjectModel.UpdatePageReviewTagCommand(
                    Enum.Parse<ProjectModel.PageReviewTag>(request.ReviewTag.ToString()),
                    request.ExpectedRevision),
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> GetOriginalAsset(
        string projectId,
        string pageId,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectAssetStore assets,
        HttpContext context)
    {
        try
        {
            var project = await projects.GetAsync(projectId, context.RequestAborted);
            var page = project.Pages.FirstOrDefault(item => item.PageId == pageId)
                ?? throw new ProjectModel.ProjectPageNotFoundException(projectId, pageId);
            var asset = await assets.OpenReadAsync(projectId, page.SourceAssetKey, context.RequestAborted);
            return Results.File(
                asset.Content,
                asset.MediaType,
                asset.FileName,
                enableRangeProcessing: true);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException or FileNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> GetCleanAsset(
        string projectId,
        string pageId,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectAssetStore assets,
        HttpContext context)
    {
        try
        {
            var snapshot = await projects.GetPageImageEditAsync(projectId, pageId, context.RequestAborted);
            var asset = await assets.OpenReadAsync(projectId, snapshot.RenderedAssetKey, context.RequestAborted);
            return Results.File(asset.Content, asset.MediaType, asset.FileName, enableRangeProcessing: true);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException or FileNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> GetResultAsset(
        string projectId,
        string pageId,
        ProjectModel.IProjectResultService results,
        ILogger<ProjectResultService> logger,
        HttpContext context)
    {
        try
        {
            var asset = await results.RenderPageAsync(projectId, pageId, context.RequestAborted);
            return Results.File(asset.Content, asset.MediaType, asset.FileName, enableRangeProcessing: false);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException
                                           or ProjectModel.ProjectPageNotFoundException
                                           or FileNotFoundException)
        {
            logger.LogWarning(
                exception,
                "Result preview was not found for project {ProjectId}, page {PageId}",
                projectId,
                pageId);
            return Results.NotFound();
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or JsonException)
        {
            logger.LogError(
                exception,
                "Result preview rendering failed for project {ProjectId}, page {PageId}",
                projectId,
                pageId);
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetPageLayout(
        string projectId,
        string pageId,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            return Results.Ok((await projects.GetPageLayoutAsync(projectId, pageId, context.RequestAborted)).ToResponse());
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdatePageLayout(
        string projectId,
        string pageId,
        UpdatePageLayoutRequest request,
        ProjectModel.IProjectStore projects,
        LayoutAwareCleaningService layoutCleaning,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var rebuilt = await layoutCleaning.PrepareCurrentBackgroundAsync(
                projectId,
                pageId,
                request.LayoutJson,
                request.ExpectedRevision,
                context.RequestAborted);
            var project = await projects.UpdatePageLayoutAsync(
                projectId,
                pageId,
                new ProjectModel.UpdatePageLayoutCommand(
                    request.LayoutJson,
                    request.ExpectedRevision,
                    rebuilt?.CleanAssetKey,
                    rebuilt?.MaskAssetKey,
                    rebuilt?.CandidateMaskAssetKey),
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Results.Conflict(exception);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(exception);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> RenderTextClip(
        string projectId,
        string pageId,
        RenderTextClipRequest request,
        ProjectResultService results,
        HttpContext context)
    {
        try
        {
            var clip = await results.RenderTextClipAsync(
                projectId,
                pageId,
                request.LayoutJson,
                request.SegmentId,
                context.RequestAborted);
            context.Response.Headers["X-EMT-Clip-Segment"] = clip.SegmentId;
            context.Response.Headers["X-EMT-Clip-Width"] = clip.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-EMT-Clip-Height"] = clip.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-EMT-Clip-Fingerprint"] = clip.Fingerprint;
            return Results.File(clip.PngData, "image/png", enableRangeProcessing: false);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetPageImageEdit(
        string projectId,
        string pageId,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            return Results.Ok((await projects.GetPageImageEditAsync(projectId, pageId, context.RequestAborted)).ToResponse());
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdatePageImageEdit(
        string projectId,
        string pageId,
        UpdatePageImageEditRequest request,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await projects.UpdatePageImageEditAsync(
                projectId,
                pageId,
                new ProjectModel.UpdatePageImageEditCommand(request.OperationsJson, request.ExpectedPageRevision),
                context.RequestAborted);
            var imageEdit = await projects.GetPageImageEditAsync(projectId, pageId, context.RequestAborted);
            return Results.Ok(new UpdatePageImageEditResponse(project.ToResponse(), imageEdit.ToResponse()));
        }
        catch (ProjectModel.PageRevisionConflictException exception)
        {
            return Results.Conflict(new
            {
                detail = exception.Message,
                pageId = exception.PageId,
                currentPageRevision = exception.CurrentRevision
            });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ListPageVersions(
        string projectId,
        string pageId,
        ProjectModel.IProjectStore projects,
        HttpContext context)
    {
        try
        {
            return Results.Ok((await projects.ListPageVersionsAsync(projectId, pageId, context.RequestAborted))
                .Select(item => item.ToResponse()));
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> CreatePageVersion(
        string projectId,
        string pageId,
        CreatePageVersionRequest request,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await projects.CreatePageVersionAsync(
                projectId,
                pageId,
                new ProjectModel.CreatePageVersionCommand(
                    request.Label,
                    request.ExpectedRevision,
                    Enum.Parse<ProjectModel.PageVersionKind>(request.Kind.ToString()),
                    Track: Enum.Parse<ProjectModel.PageVersionTrack>(request.Track.ToString())),
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ActivatePageVersion(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        ProjectModel.IProjectStore projects,
        LayoutAwareCleaningService layoutCleaning,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var selected = (await projects.ListPageVersionsAsync(projectId, pageId, context.RequestAborted))
                .FirstOrDefault(item => item.VersionId == versionId)
                ?? throw new ProjectModel.PageVersionNotFoundException(pageId, versionId);
            var rebuilt = selected.Track == ProjectModel.PageVersionTrack.Lettering
                ? await layoutCleaning.PrepareCurrentBackgroundAsync(
                    projectId,
                    pageId,
                    selected.LayoutJson ?? "{\"segments\":[]}",
                    expectedRevision,
                    context.RequestAborted)
                : null;
            var project = await projects.SwitchPageVersionAsync(
                projectId,
                pageId,
                versionId,
                expectedRevision,
                rebuilt?.CleanAssetKey,
                rebuilt?.MaskAssetKey,
                rebuilt?.CandidateMaskAssetKey,
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException or ProjectModel.PageVersionNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> DeletePageVersion(
        string projectId,
        string pageId,
        string versionId,
        int expectedRevision,
        ProjectModel.IProjectStore projects,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await projects.DeletePageVersionAsync(
                projectId,
                pageId,
                versionId,
                expectedRevision,
                context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (Exception exception) when (exception is ProjectModel.ProjectNotFoundException or ProjectModel.ProjectPageNotFoundException or ProjectModel.PageVersionNotFoundException)
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception);
        }
    }

    private static async Task<IResult> GetPrecheckReview(
        string projectId,
        PrecheckReviewService review,
        HttpContext context)
    {
        try
        {
            return Results.Ok(await review.GetAsync(projectId, context.RequestAborted));
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> GetTranslationAnnotations(
        string projectId,
        TranslationAnnotationService annotations,
        HttpContext context)
    {
        try
        {
            return Results.Ok(await annotations.GetAsync(projectId, context.RequestAborted));
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdateTranslationAnnotations(
        string projectId,
        UpdateTranslationAnnotationsRequest request,
        TranslationAnnotationService annotations,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            return Results.Ok(await annotations.UpdateAsync(projectId, request, context.RequestAborted));
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdatePrecheckItem(
        string projectId,
        string artifactName,
        UpdatePrecheckItemRequest request,
        PrecheckReviewService review,
        ProjectModel.IProjectEditLock editLock,
        HttpContext context)
    {
        if (editLock.IsLocked(projectId)) return EditingLocked();
        try
        {
            var project = await review.UpdateItemAsync(projectId, artifactName, request, context.RequestAborted);
            return Results.Ok(project.ToResponse());
        }
        catch (ProjectModel.ProjectRevisionConflictException exception)
        {
            return Conflict(exception);
        }
        catch (ProjectModel.ProjectNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ProjectModel.ProjectFormatException exception)
        {
            return BadRequest(exception);
        }
    }

    private static bool IsDesktopHost() => string.Equals(
        Environment.GetEnvironmentVariable("EMT_HOST_KIND"),
        "desktop",
        StringComparison.OrdinalIgnoreCase);

    private static bool IsClientProjectException(Exception exception) => exception is
        ArgumentException or InvalidOperationException or IOException or
        ProjectModel.ProjectLocationException or ProjectModel.ProjectFormatException;

    private static IResult BadRequest(Exception exception) => Results.BadRequest(new { detail = exception.Message });

    private static IResult EditingLocked() =>
        Results.Conflict(new { detail = "项目正在导出，编辑已暂时锁定。" });

    private static IResult Conflict(ProjectModel.ProjectRevisionConflictException exception) =>
        Results.Conflict(new { detail = exception.Message, currentRevision = exception.CurrentRevision });
}
