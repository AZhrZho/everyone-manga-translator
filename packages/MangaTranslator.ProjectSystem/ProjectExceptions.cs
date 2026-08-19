namespace MangaTranslator.ProjectSystem;

public sealed class ProjectNotFoundException(string projectId)
    : Exception($"找不到项目：{projectId}");

public sealed class ProjectRevisionConflictException(int currentRevision)
    : Exception($"项目已经被其他操作更新。当前修订号为 {currentRevision}。")
{
    public int CurrentRevision { get; } = currentRevision;
}

public sealed class PageRevisionConflictException(string pageId, int currentRevision)
    : Exception($"页面 {pageId} 已经被其他操作更新。当前页面修订号为 {currentRevision}。")
{
    public string PageId { get; } = pageId;

    public int CurrentRevision { get; } = currentRevision;
}

public sealed class ProjectLocationException(string message) : Exception(message);

public sealed class ProjectFormatException(string message) : Exception(message);

public sealed class ProjectPageNotFoundException(string projectId, string pageId)
    : Exception($"项目 {projectId} 中不存在页面 {pageId}。");

public sealed class PageVersionNotFoundException(string pageId, string versionId)
    : Exception($"页面 {pageId} 中不存在版本 {versionId}。");
