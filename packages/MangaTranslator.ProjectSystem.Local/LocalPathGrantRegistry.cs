using System.Collections.Concurrent;

namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalPathGrantRegistry : IProjectLocationGrantRegistry
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, GrantEntry> _grants = new(StringComparer.Ordinal);

    public ProjectLocationGrant Register(string path, ProjectLocationKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Validate(fullPath, kind);

        RemoveExpired();
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(GrantLifetime);
        _grants[token] = new GrantEntry(fullPath, kind, expiresAt);
        return new ProjectLocationGrant(token, kind, CreateDisplayName(fullPath), expiresAt);
    }

    public string Resolve(string token, ProjectLocationKind expectedKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!_grants.TryGetValue(token, out var grant)
            || grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _grants.TryRemove(token, out _);
            throw new ProjectLocationException("所选位置已过期，请重新选择。");
        }

        if (grant.Kind != expectedKind)
        {
            throw new ProjectLocationException($"位置用途不匹配：期望 {expectedKind}，实际为 {grant.Kind}。");
        }

        Validate(grant.Path, grant.Kind);
        return grant.Path;
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _grants.Where(item => item.Value.ExpiresAt <= now))
        {
            _grants.TryRemove(item.Key, out _);
        }
    }

    private static void Validate(string path, ProjectLocationKind kind)
    {
        switch (kind)
        {
            case ProjectLocationKind.ProjectParent:
            case ProjectLocationKind.ImportFolder:
                if (!Directory.Exists(path))
                {
                    throw new ProjectLocationException($"找不到文件夹：{path}");
                }
                break;
            case ProjectLocationKind.ExistingProject:
                if (!Directory.Exists(path)
                    || !File.Exists(Path.Combine(path, LocalProjectLayout.ManifestFileName))
                    || !File.Exists(Path.Combine(path, LocalProjectLayout.DatabaseFileName)))
                {
                    throw new ProjectLocationException("所选文件夹不是有效的 EMT 项目目录。");
                }
                break;
            case ProjectLocationKind.ImportPdf:
                if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ProjectLocationException("找不到所选 PDF 文件。");
                }
                break;
            case ProjectLocationKind.ImportFont:
                if (!File.Exists(path)
                    || Path.GetExtension(path).ToLowerInvariant() is not (".ttf" or ".otf" or ".ttc" or ".woff" or ".woff2"))
                {
                    throw new ProjectLocationException("找不到所选字体文件，或字体格式不受支持。");
                }
                break;
            case ProjectLocationKind.ExportFolder:
                if (!Directory.Exists(path))
                {
                    throw new ProjectLocationException($"找不到导出文件夹：{path}");
                }
                break;
            case ProjectLocationKind.ExportZip:
            case ProjectLocationKind.ExportPdf:
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                {
                    throw new ProjectLocationException($"导出目标所在文件夹不存在：{parent}");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static string CreateDisplayName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private sealed record GrantEntry(string Path, ProjectLocationKind Kind, DateTimeOffset ExpiresAt);
}
