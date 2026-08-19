using MangaTranslator.Api.Fixtures;
using MangaTranslator.Api.BrowserInference;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Api.Projects;
using MangaTranslator.Api.Pipeline;
using MangaTranslator.Api.Storage;
using MangaTranslator.Api.Settings;
using MangaTranslator.Api.Infrastructure;
using MangaTranslator.Contracts;
using MangaTranslator.LocalDetection;
using MangaTranslator.LocalDetection.Craft;
using MangaTranslator.LocalDetection.Ctd;
using MangaTranslator.ProjectSystem;
using MangaTranslator.ProjectSystem.Local;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var appDataPaths = new AppDataPaths();
var fileLoggerProvider = new FileLoggerProvider(appDataPaths.LogsRoot);

builder.Logging.AddProvider(fileLoggerProvider);
var startupLogger = fileLoggerProvider.CreateLogger("MangaTranslator.Api.Startup");
startupLogger.LogInformation(
    "API process starting. Version={Version}; Environment={Environment}; UserDataRoot={UserDataRoot}; LogsRoot={LogsRoot}",
    typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown",
    builder.Environment.EnvironmentName,
    appDataPaths.UserDataRoot,
    appDataPaths.LogsRoot);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host is "localhost" or "127.0.0.1";
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient("workflow-model-provider", client =>
    client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton(appDataPaths);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(appDataPaths.UserDataRoot, "protection-keys")))
    .SetApplicationName("Everyone Manga Translator");
builder.Services.AddSingleton(new LocalProjectSystemOptions(appDataPaths.UserDataRoot));
builder.Services.AddSingleton<LocalPathGrantRegistry>();
builder.Services.AddSingleton<IProjectLocationGrantRegistry>(provider => provider.GetRequiredService<LocalPathGrantRegistry>());
builder.Services.AddSingleton<LocalRecentProjectRegistry>();
builder.Services.AddSingleton<IProjectStore, LocalProjectStore>();
builder.Services.AddSingleton<IProjectAssetStore, LocalProjectAssetStore>();
builder.Services.AddSingleton<IProjectAssetWriter>(provider =>
    (IProjectAssetWriter)provider.GetRequiredService<IProjectAssetStore>());
builder.Services.AddSingleton<IJobStore, LocalJobStore>();
builder.Services.AddSingleton<IProjectArtifactStore, LocalProjectArtifactStore>();
builder.Services.AddSingleton<ProjectResultService>();
builder.Services.AddSingleton<IProjectResultService>(provider => provider.GetRequiredService<ProjectResultService>());
builder.Services.AddSingleton<LayoutAwareCleaningService>();
builder.Services.AddSingleton<IProjectEditLock, LocalProjectEditLock>();
builder.Services.AddSingleton<ManagedLetteringRenderer>();
builder.Services.AddSingleton<IExportService, LocalExportService>();
builder.Services.AddSingleton<FontCatalogService>(provider =>
    new FontCatalogService(
        provider.GetRequiredService<IHostEnvironment>(),
        provider.GetRequiredService<AppDataPaths>()));
builder.Services.AddSingleton<LocalApplicationSettingsStore>();
builder.Services.AddSingleton<IApplicationSettingsStore>(provider =>
    provider.GetRequiredService<LocalApplicationSettingsStore>());
builder.Services.AddSingleton<IWorkflowModelSettingsResolver>(provider =>
    provider.GetRequiredService<LocalApplicationSettingsStore>());
builder.Services.AddSingleton<PrecheckReviewService>();
builder.Services.AddSingleton<TranslationAnnotationService>();
builder.Services.AddSingleton<IPipelineArtifactCatalog, FileSystemPipelineArtifactCatalog>();
builder.Services.AddSingleton<PipelinePrecheckArtifactImporter>();
builder.Services.AddSingleton<IPrecheckArtifactMaterializer, PrecheckArtifactMaterializer>();
builder.Services.AddSingleton<IPrecheckFixtureCatalog, FileSystemPrecheckFixtureCatalog>();
builder.Services.AddSingleton<ReplayPrecheckExecutionSource>();
builder.Services.AddSingleton<CachedPrecheckExecutionSource>();
builder.Services.AddSingleton<ILivePrecheckWorkflow, LivePrecheckWorkflow>();
builder.Services.AddSingleton<LivePrecheckExecutionSource>();
builder.Services.AddSingleton<IPrecheckExecutionSource>(provider => provider.GetRequiredService<ReplayPrecheckExecutionSource>());
builder.Services.AddSingleton<IPrecheckExecutionSource>(provider => provider.GetRequiredService<CachedPrecheckExecutionSource>());
builder.Services.AddSingleton<IPrecheckExecutionSource>(provider => provider.GetRequiredService<LivePrecheckExecutionSource>());
builder.Services.AddSingleton<IJobUpdatePublisher, SignalRJobUpdatePublisher>();
builder.Services.AddSingleton<PrecheckJobService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PrecheckJobService>());
builder.Services.AddSingleton<ITextMaskDetectorFactory, CraftTextMaskDetectorFactory>();
builder.Services.AddSingleton<ITextMaskDetectorFactory, CtdTextMaskDetectorFactory>();
builder.Services.AddSingleton<ITextDetectorModelLocator, TextDetectorModelLocator>();
builder.Services.AddSingleton<ITextDetectorRuntime, TextDetectorRuntime>();
builder.Services.AddSingleton<ProjectOperationGate>();
builder.Services.AddSingleton<BrowserDetectionArtifactService>();
builder.Services.AddSingleton<ILiveTranslationWorkflow, LiveTranslationWorkflow>();
builder.Services.AddSingleton<IQuickTranslationWorkflow, QuickTranslationWorkflow>();
builder.Services.AddSingleton<CachedTranslationExecutionSource>();
builder.Services.AddSingleton<LiveTranslationExecutionSource>();
builder.Services.AddSingleton<ITranslationExecutionSource>(provider =>
    provider.GetRequiredService<CachedTranslationExecutionSource>());
builder.Services.AddSingleton<ITranslationExecutionSource>(provider =>
    provider.GetRequiredService<LiveTranslationExecutionSource>());
builder.Services.AddSingleton<ITranslationPageRunner, TranslationPageRunner>();
builder.Services.AddSingleton<TranslationJobService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TranslationJobService>());
builder.Services.AddSingleton<ExportJobService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ExportJobService>());

WebApplication app;
try
{
    app = builder.Build();
}
catch (Exception exception)
{
    startupLogger.LogCritical(exception, "API host failed while building the application.");
    throw;
}
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/system/info", (IHostEnvironment environment, AppDataPaths paths) =>
{
    var hostKind = string.Equals(
        Environment.GetEnvironmentVariable("EMT_HOST_KIND"),
        "desktop",
        StringComparison.OrdinalIgnoreCase)
        ? HostKind.Desktop
        : HostKind.Web;

    return new SystemInfoResponse(
        "EMT Studio",
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.1",
        hostKind,
        Environment.Version.ToString(),
        paths.DataRoot);
});

app.MapProjectEndpoints();
app.MapBrowserInferenceEndpoints();
app.MapSettingsEndpoints();

app.MapGet("/api/jobs/active", async (IJobStore jobs, HttpContext context) =>
    Results.Ok(await jobs.ListActiveAsync(context.RequestAborted)));

app.MapGet("/api/projects/demo", () => DemoProject.Create());
app.MapGet("/api/precheck/fixtures", (IPrecheckFixtureCatalog catalog) =>
    catalog.List().Select(fixture => new PrecheckFixtureSummaryResponse(
        fixture.RunId,
        fixture.PageCount,
        fixture.BatchCount,
        fixture.AvailableArtifacts,
        fixture.LastModifiedAt,
        string.Equals(fixture.RunId, catalog.GetDefault().RunId, StringComparison.OrdinalIgnoreCase))));

app.MapPost("/api/jobs/precheck", async (
    StartPrecheckRequest request,
    PrecheckJobService jobs,
    IProjectEditLock editLock,
    ProjectOperationGate operationGate,
    HttpContext context) =>
{
    try
    {
        using var operation = await operationGate.EnterAsync(
            request.ProjectId,
            context.RequestAborted);
        if (editLock.IsLocked(request.ProjectId))
        {
            return Results.Conflict(new { detail = "项目正在导出，不能启动预处理。" });
        }
        return Results.Accepted(value: await jobs.StartAsync(request, context.RequestAborted));
    }
    catch (NotSupportedException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status501NotImplemented);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
});
app.MapPost("/api/jobs/translation", async (
    StartTranslationRequest request,
    TranslationJobService jobs,
    IProjectStore projects,
    IProjectEditLock editLock,
    ProjectOperationGate operationGate,
    HttpContext context) =>
{
    try
    {
        using var operation = await operationGate.EnterAsync(
            request.ProjectId,
            context.RequestAborted);
        if (editLock.IsLocked(request.ProjectId))
        {
            return Results.Conflict(new { detail = "项目正在导出，不能启动翻译。" });
        }
        if (request.PageIndices is { Count: > 0 })
        {
            var project = await projects.GetAsync(request.ProjectId, context.RequestAborted);
            var requested = request.PageIndices.ToHashSet();
            var excluded = project.Pages
                .Where(page => requested.Contains(page.Index) && !page.IncludedInWorkflow)
                .Select(page => page.Index)
                .OrderBy(index => index)
                .ToArray();
            if (excluded.Length > 0)
            {
                return Results.UnprocessableEntity(new
                {
                    detail = $"已排除的页面不能运行翻译：{string.Join("、", excluded)}。"
                });
            }
        }
        return Results.Accepted(value: await jobs.StartAsync(request, context.RequestAborted));
    }
    catch (NotSupportedException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status501NotImplemented);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
});
app.MapPost("/api/jobs/export", async (
    StartExportRequest request,
    ExportJobService jobs,
    HttpContext context) =>
{
    try
    {
        return Results.Accepted(value: await jobs.StartAsync(request, context.RequestAborted));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});
app.MapGet("/api/jobs/{jobId}", (string jobId, PrecheckJobService precheck, TranslationJobService translation, ExportJobService export) =>
    precheck.TryGet(jobId, out var precheckSnapshot)
        ? Results.Ok(precheckSnapshot)
        : translation.TryGet(jobId, out var translationSnapshot)
            ? Results.Ok(translationSnapshot)
            : export.TryGet(jobId, out var exportSnapshot) ? Results.Ok(exportSnapshot) : Results.NotFound());
app.MapGet("/api/projects/{projectId}/jobs", async (string projectId, PrecheckJobService jobs, HttpContext context) =>
    Results.Ok(await jobs.ListForProjectAsync(projectId, context.RequestAborted)));
app.MapGet("/api/projects/{projectId}/translation-jobs", async (string projectId, TranslationJobService jobs, HttpContext context) =>
    Results.Ok(await jobs.ListForProjectAsync(projectId, context.RequestAborted)));
app.MapGet("/api/projects/{projectId}/export-jobs", async (string projectId, ExportJobService jobs, HttpContext context) =>
    Results.Ok(await jobs.ListForProjectAsync(projectId, context.RequestAborted)));
app.MapDelete("/api/projects/{projectId}/jobs/finished", async (
    string projectId,
    IJobStore jobs,
    HttpContext context) => Results.Ok(new
{
    deletedCount = await jobs.DeleteFinishedForProjectAsync(projectId, context.RequestAborted)
}));
app.MapPost("/api/jobs/{jobId}/stop", async (string jobId, PrecheckJobService precheck, TranslationJobService translation, ExportJobService export, HttpContext context) =>
{
    var snapshot = await precheck.StopAsync(jobId, context.RequestAborted)
                   ?? await translation.StopAsync(jobId, context.RequestAborted)
                   ?? await export.StopAsync(jobId, context.RequestAborted);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});
app.MapPost("/api/jobs/{jobId}/resume", async (
    string jobId,
    PrecheckJobService precheck,
    TranslationJobService translation,
    IJobStore jobStore,
    IProjectEditLock editLock,
    ProjectOperationGate operationGate,
    HttpContext context) =>
{
    var persisted = await jobStore.FindAsync(jobId, context.RequestAborted);
    var projectId = persisted?.ProjectId;
    if (projectId is null)
    {
        if (precheck.TryGet(jobId, out var precheckSnapshot)) projectId = precheckSnapshot!.ProjectId;
        else if (translation.TryGet(jobId, out var translationSnapshot)) projectId = translationSnapshot!.ProjectId;
    }
    if (projectId is null) return Results.NotFound();

    using var operation = await operationGate.EnterAsync(projectId, context.RequestAborted);
    if (editLock.IsLocked(projectId))
    {
        return Results.Conflict(new { detail = "The project is locked for export." });
    }
    var snapshot = await precheck.ResumeAsync(jobId, context.RequestAborted)
                   ?? await translation.ResumeAsync(jobId, context.RequestAborted);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});
app.MapPost("/api/jobs/{jobId}/retry", async (
    string jobId,
    PrecheckJobService precheck,
    IJobStore jobStore,
    IProjectEditLock editLock,
    ProjectOperationGate operationGate,
    HttpContext context) =>
{
    var persisted = await jobStore.FindAsync(jobId, context.RequestAborted);
    var projectId = persisted?.ProjectId;
    if (projectId is null && precheck.TryGet(jobId, out var snapshot))
    {
        projectId = snapshot!.ProjectId;
    }
    if (projectId is null) return Results.NotFound();

    using var operation = await operationGate.EnterAsync(projectId, context.RequestAborted);
    if (editLock.IsLocked(projectId))
    {
        return Results.Conflict(new { detail = "The project is locked for export." });
    }
    var retried = await precheck.RetryAsync(jobId, context.RequestAborted);
    return retried is null ? Results.NotFound() : Results.Ok(retried);
});

app.MapHub<JobEventsHub>("/hubs/jobs");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");

try
{
    await app.StartAsync();
    var addresses = app.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses;
    var apiAddress = addresses?
        .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
        .FirstOrDefault(uri => uri is not null && uri.Scheme == Uri.UriSchemeHttp)
        ?? throw new InvalidOperationException("API started without an HTTP listening address.");
    Console.Out.WriteLine($"EMT_API_READY={apiAddress.GetLeftPart(UriPartial.Authority)}");
    await Console.Out.FlushAsync();
    await app.WaitForShutdownAsync();
}
catch (Exception exception)
{
    startupLogger.LogCritical(exception, "API host failed during startup or shutdown.");
    throw;
}

internal static class DemoProject
{
    public static ProjectSummaryResponse Create()
    {
        var pages = Enumerable.Range(1, 8)
            .Select(index => new PageSummaryResponse(
                $"page-{index:000}",
                index,
                $"{index:000}.png",
                index switch
                {
                    1 => "cover",
                    7 => "illustration",
                    _ => "manga"
                },
                index is not 1 and not 7))
            .ToArray();

        return new ProjectSummaryResponse("demo-night-store", "夜色便利店", pages.Length, pages);
    }
}
