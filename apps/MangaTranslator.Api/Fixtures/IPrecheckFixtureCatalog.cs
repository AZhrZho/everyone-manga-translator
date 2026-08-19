namespace MangaTranslator.Api.Fixtures;

public interface IPrecheckFixtureCatalog
{
    string RootPath { get; }

    IReadOnlyList<PrecheckFixtureDescriptor> List();

    PrecheckFixtureDescriptor GetDefault();

    bool TryGet(string runId, out PrecheckFixtureDescriptor? fixture);
}
