namespace MangaTranslator.Dialogue;

public sealed record DialogueProgress(
    string PageId,
    string Action,
    int? Attempt = null,
    int? MaximumAttempts = null,
    string? Detail = null,
    TimeSpan? Elapsed = null,
    string? ErrorCode = null);

public interface IDialogueProgressReporter
{
    void Report(DialogueProgress progress);
}

public sealed class NullDialogueProgressReporter : IDialogueProgressReporter
{
    public static NullDialogueProgressReporter Instance { get; } = new();

    private NullDialogueProgressReporter()
    {
    }

    public void Report(DialogueProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
    }
}
