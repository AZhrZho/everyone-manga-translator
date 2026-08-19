namespace MangaTranslator.ProjectSystem;

public enum ProjectExportFormat
{
    Folder,
    Zip,
    Pdf
}

public enum ProjectExportResolution
{
    Original,
    Half,
    Quarter
}

public sealed record ExportProjectCommand(
    string? DestinationToken,
    ProjectExportFormat Format,
    bool IncludeBackground = true,
    bool IncludeLettering = true,
    ProjectExportResolution Resolution = ProjectExportResolution.Original);

public sealed record ExportResult(
    ProjectExportFormat Format,
    string DestinationPath,
    int PageCount);

public sealed record ExportProgress(
    int CompletedPages,
    int TotalPages,
    string Stage,
    string Message);

public interface IProjectEditLock
{
    bool IsLocked(string projectId);

    IDisposable AcquireExport(string projectId);
}

public interface IExportService
{
    Task<ExportResult> ExportAsync(
        string projectId,
        ExportProjectCommand command,
        Func<ExportProgress, CancellationToken, Task>? reportAsync,
        CancellationToken cancellationToken);
}
