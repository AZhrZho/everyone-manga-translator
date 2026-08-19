using MangaTranslator.Api.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MangaTranslator.Api.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void WritesStructuredEntriesAndExceptionsToTheConfiguredLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "emt-file-logger", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new FileLoggerProvider(root);
            var logger = provider.CreateLogger("Test.Category");
            var exception = new InvalidOperationException("sample failure");

            logger.Log(
                LogLevel.Error,
                new EventId(42, "Sample"),
                "Could not start API.",
                exception,
                static (state, _) => state);

            var logPath = Path.Combine(root, "api.log");
            var content = File.ReadAllText(logPath);
            Assert.Contains("[Error] Test.Category (42/Sample)", content);
            Assert.Contains("Could not start API.", content);
            Assert.Contains("System.InvalidOperationException: sample failure", content);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
