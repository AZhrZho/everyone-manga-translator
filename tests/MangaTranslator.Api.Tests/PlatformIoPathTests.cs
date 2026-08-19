using System.Security.Cryptography;
using MangaTranslator.Api.Infrastructure;

namespace MangaTranslator.Api.Tests;

public sealed class PlatformIoPathTests
{
    [Fact]
    public async Task ReadsHashesAndEnumeratesFilesBeyondLegacyWindowsMaxPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"emt-long-path-{Guid.NewGuid():N}");
        var current = root;
        try
        {
            PlatformIoPath.CreateDirectory(root);
            while (Path.Combine(current, "artifact.json").Length <= 280)
            {
                current = Path.Combine(current, $"segment-{Guid.NewGuid():N}");
                PlatformIoPath.CreateDirectory(current);
            }

            var path = Path.Combine(current, "artifact.json");
            const string expected = "{\"regions\":[]}";
            await File.WriteAllTextAsync(PlatformIoPath.NormalizeForIo(path), expected);

            Assert.True(path.Length > 260);
            Assert.True(PlatformIoPath.FileExists(path));
            Assert.Equal(expected, await PlatformIoPath.ReadAllTextAsync(path, CancellationToken.None));
            Assert.Contains(
                path,
                PlatformIoPath.EnumerateFiles(current, "*.json", SearchOption.TopDirectoryOnly),
                StringComparer.OrdinalIgnoreCase);

            await using var stream = PlatformIoPath.OpenRead(
                path,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, CancellationToken.None));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected))),
                hash);
        }
        finally
        {
            if (PlatformIoPath.DirectoryExists(root))
            {
                Directory.Delete(PlatformIoPath.NormalizeForIo(root), recursive: true);
            }
        }
    }
}
