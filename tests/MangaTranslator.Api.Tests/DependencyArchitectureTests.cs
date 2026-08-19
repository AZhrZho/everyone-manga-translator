using MangaTranslator.Api.Jobs;

namespace MangaTranslator.Api.Tests;

public sealed class DependencyArchitectureTests
{
    [Fact]
    public void ApiAssemblyReferencesBothSelectableDetectorImplementations()
    {
        var references = typeof(LiveTranslationWorkflow).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MangaTranslator.LocalDetection.Craft", references);
        Assert.Contains("MangaTranslator.LocalDetection.Ctd", references);
    }
}
