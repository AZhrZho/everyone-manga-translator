using System.IO.Compression;
using MangaTranslator.ProjectSystem;
using SkiaSharp;

namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalExportService(
    LocalPathGrantRegistry grants,
    IProjectStore projects,
    IProjectResultService results) : IExportService
{
    public async Task<ExportResult> ExportAsync(
        string projectId,
        ExportProjectCommand command,
        Func<ExportProgress, CancellationToken, Task>? reportAsync,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        if (!command.IncludeBackground && !command.IncludeLettering)
        {
            throw new InvalidOperationException("至少选择导出底图或嵌字中的一项。");
        }
        var expectedKind = command.Format switch
        {
            ProjectExportFormat.Folder => ProjectLocationKind.ExportFolder,
            ProjectExportFormat.Zip => ProjectLocationKind.ExportZip,
            ProjectExportFormat.Pdf => ProjectLocationKind.ExportPdf,
            _ => throw new ArgumentOutOfRangeException(nameof(command.Format))
        };
        var destination = string.IsNullOrWhiteSpace(command.DestinationToken)
            ? DefaultDestination(project, command.Format)
            : grants.Resolve(command.DestinationToken, expectedKind);
        var pages = project.Pages
            .Where(item => item.IncludedInWorkflow)
            .OrderBy(item => item.Index)
            .ToArray();
        if (pages.Length == 0)
        {
            throw new InvalidOperationException("项目中没有加入工作流的页面可供导出。");
        }
        return command.Format switch
        {
            ProjectExportFormat.Folder => await ExportFolderAsync(project, pages, destination, command, reportAsync, cancellationToken),
            ProjectExportFormat.Zip => await ExportZipAsync(project, pages, destination, command, reportAsync, cancellationToken),
            ProjectExportFormat.Pdf => await ExportPdfAsync(project, pages, destination, command, reportAsync, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Format))
        };
    }

    private async Task<ExportResult> ExportFolderAsync(
        ProjectSnapshot project,
        IReadOnlyList<ProjectPageSnapshot> pages,
        string destination,
        ExportProjectCommand command,
        Func<ExportProgress, CancellationToken, Task>? reportAsync,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var stagingDirectory = Path.Combine(destination, $".emt-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            for (var index = 0; index < pages.Count; index++)
            {
                var page = pages[index];
                await using var asset = await RenderExportPageAsync(project.ProjectId, page.PageId, command, cancellationToken);
                var output = Path.Combine(stagingDirectory, ResultFileName(page));
                await CopyToFileAsync(asset.Content, output, cancellationToken);
                await ReportAsync(reportAsync, index + 1, pages.Count, "render", $"已渲染第 {page.Index} 页。", cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var stagedFile in Directory.EnumerateFiles(stagingDirectory))
            {
                File.Move(stagedFile, Path.Combine(destination, Path.GetFileName(stagedFile)), overwrite: true);
            }

            return new ExportResult(ProjectExportFormat.Folder, destination, pages.Count);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private async Task<ExportResult> ExportZipAsync(
        ProjectSnapshot project,
        IReadOnlyList<ProjectPageSnapshot> pages,
        string destination,
        ExportProjectCommand command,
        Func<ExportProgress, CancellationToken, Task>? reportAsync,
        CancellationToken cancellationToken)
    {
        EnsureDestinationParent(destination);
        var temporaryPath = TemporaryOutputPath(destination);
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                for (var index = 0; index < pages.Count; index++)
                {
                    var page = pages[index];
                    await using var asset = await RenderExportPageAsync(project.ProjectId, page.PageId, command, cancellationToken);
                    var entry = archive.CreateEntry(ResultFileName(page), CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await asset.Content.CopyToAsync(entryStream, cancellationToken);
                    await ReportAsync(reportAsync, index + 1, pages.Count, "archive", $"已打包第 {page.Index} 页。", cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            return new ExportResult(ProjectExportFormat.Zip, destination, pages.Count);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task<ExportResult> ExportPdfAsync(
        ProjectSnapshot project,
        IReadOnlyList<ProjectPageSnapshot> pages,
        string destination,
        ExportProjectCommand command,
        Func<ExportProgress, CancellationToken, Task>? reportAsync,
        CancellationToken cancellationToken)
    {
        EnsureDestinationParent(destination);
        var temporaryPath = TemporaryOutputPath(destination);
        try
        {
            using (var document = SKDocument.CreatePdf(temporaryPath))
            {
                for (var index = 0; index < pages.Count; index++)
                {
                    var page = pages[index];
                    await using var asset = await RenderExportPageAsync(project.ProjectId, page.PageId, command, cancellationToken);
                    await using var buffer = new MemoryStream();
                    await asset.Content.CopyToAsync(buffer, cancellationToken);
                    buffer.Position = 0;
                    using var bitmap = SKBitmap.Decode(buffer);
                    if (bitmap is null) throw new InvalidOperationException($"无法读取页面 {page.Index} 的图像。");
                    using var canvas = document.BeginPage(bitmap.Width, bitmap.Height);
                    canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default, null);
                    document.EndPage();
                    await ReportAsync(reportAsync, index + 1, pages.Count, "pdf", $"已写入 PDF 第 {page.Index} 页。", cancellationToken);
                }

                document.Close();
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            return new ExportResult(ProjectExportFormat.Pdf, destination, pages.Count);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ResultFileName(ProjectPageSnapshot page) =>
        $"{page.Index:0000}_{Path.GetFileNameWithoutExtension(page.FileName)}.png";

    private async Task<ProjectAsset> RenderExportPageAsync(
        string projectId,
        string pageId,
        ExportProjectCommand command,
        CancellationToken cancellationToken)
    {
        var source = command switch
        {
            { IncludeBackground: true, IncludeLettering: true } => await results.RenderPageAsync(projectId, pageId, cancellationToken),
            { IncludeBackground: true } => await results.RenderBackgroundAsync(projectId, pageId, cancellationToken),
            { IncludeLettering: true } => await results.RenderLetteringAsync(projectId, pageId, cancellationToken),
            _ => throw new InvalidOperationException("至少选择导出底图或嵌字中的一项。")
        };
        if (command.Resolution == ProjectExportResolution.Original) return source;
        await using (source)
        {
            await using var buffer = new MemoryStream();
            await source.Content.CopyToAsync(buffer, cancellationToken);
            using var bitmap = SKBitmap.Decode(buffer.ToArray())
                ?? throw new InvalidOperationException("无法读取待导出的页面图像。");
            var divisor = command.Resolution == ProjectExportResolution.Half ? 2 : 4;
            var width = Math.Max(1, bitmap.Width / divisor);
            var height = Math.Max(1, bitmap.Height / divisor);
            using var scaled = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(scaled))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            using var image = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("无法编码缩放后的导出图像。");
            var data = encoded.ToArray();
            return new ProjectAsset(new MemoryStream(data, writable: false), "image/png", source.FileName, data.LongLength);
        }
    }

    private static string DefaultDestination(ProjectSnapshot project, ProjectExportFormat format)
    {
        var language = string.Concat(project.TargetLanguage.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var basePath = Path.Combine(project.LocationLabel, "output", string.IsNullOrWhiteSpace(language) ? "translated" : language);
        return format switch
        {
            ProjectExportFormat.Folder => basePath,
            ProjectExportFormat.Zip => basePath + ".zip",
            ProjectExportFormat.Pdf => basePath + ".pdf",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static async Task CopyToFileAsync(Stream source, string destination, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await source.CopyToAsync(output, cancellationToken);
    }

    private static string TemporaryOutputPath(string destination) =>
        $"{destination}.{Guid.NewGuid():N}.tmp";

    private static void EnsureDestinationParent(string destination)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
    }

    private static Task ReportAsync(
        Func<ExportProgress, CancellationToken, Task>? reportAsync,
        int completedPages,
        int totalPages,
        string stage,
        string message,
        CancellationToken cancellationToken) =>
        reportAsync?.Invoke(
            new ExportProgress(completedPages, totalPages, stage, message),
            cancellationToken) ?? Task.CompletedTask;
}
