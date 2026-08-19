using System.Text.Json;
using System.Text.Json.Serialization;
using MangaTranslator.Api.Jobs;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.BrowserInference;

internal static class BrowserInferenceEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static IEndpointRouteBuilder MapBrowserInferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/projects/{projectId}/pages/{pageId}/browser-detection",
                SaveBrowserDetection)
            .DisableAntiforgery();
        return endpoints;
    }

    private static async Task<IResult> SaveBrowserDetection(
        string projectId,
        string pageId,
        HttpRequest request,
        BrowserDetectionArtifactService service,
        PrecheckJobService precheckJobs,
        TranslationJobService translationJobs,
        IProjectEditLock editLock,
        ProjectOperationGate operationGate,
        HttpContext context)
    {
        using var operation = await operationGate.EnterAsync(
            projectId,
            context.RequestAborted);
        if (editLock.IsLocked(projectId))
        {
            return Results.Conflict(new { detail = "The project is locked for export." });
        }
        if (translationJobs.HasActiveForProject(projectId))
        {
            return Results.Conflict(new
            {
                detail = "Browser detection must be saved before starting a translation task."
            });
        }
        if (precheckJobs.HasActiveForProject(projectId))
        {
            return Results.Conflict(new
            {
                detail = "Browser detection cannot be saved while project preprocessing is running."
            });
        }
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new
            {
                detail = "Expected multipart/form-data with metadata and mask fields."
            });
        }

        try
        {
            if (request.ContentLength is > BrowserDetectionArtifactService.MaximumRequestBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var form = await request.ReadFormAsync(context.RequestAborted);
            var metadataJson = form["metadata"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                return Results.BadRequest(new { detail = "The metadata form field is required." });
            }
            var mask = form.Files.GetFile("mask");
            if (mask is null)
            {
                return Results.BadRequest(new { detail = "The mask form file is required." });
            }
            if (!string.Equals(mask.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { detail = "The mask file must use image/png." });
            }
            if (mask.Length > BrowserDetectionArtifactService.MaximumMaskBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var metadata = JsonSerializer.Deserialize<BrowserDetectionUploadMetadata>(
                               metadataJson,
                               JsonOptions)
                           ?? throw new BrowserDetectionValidationException(
                               "The metadata form field is empty.");
            await using var maskStream = mask.OpenReadStream();
            return Results.Ok(await service.SaveAsync(
                projectId,
                pageId,
                metadata,
                maskStream,
                context.RequestAborted));
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { detail = $"Invalid metadata JSON: {exception.Message}" });
        }
        catch (BrowserDetectionConflictException exception)
        {
            return Results.Conflict(new { detail = exception.Message });
        }
        catch (BrowserDetectionValidationException exception)
        {
            return Results.UnprocessableEntity(new { detail = exception.Message });
        }
        catch (ProjectNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ProjectPageNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
