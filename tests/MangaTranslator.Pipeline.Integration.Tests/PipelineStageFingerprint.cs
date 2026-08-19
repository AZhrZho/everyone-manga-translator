using System.Security.Cryptography;
using System.Text.Json;

namespace MangaTranslator.Pipeline.Integration.Tests;

internal sealed record PipelineStageFingerprint(
    string Stage,
    string ContractVersion,
    string Sha256,
    Dictionary<string, string> Inputs)
{
    public static PipelineStageFingerprint Create(
        string stage,
        string contractVersion,
        IEnumerable<KeyValuePair<string, string>> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersion);
        ArgumentNullException.ThrowIfNull(inputs);

        var orderedInputs = inputs
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var material = JsonSerializer.SerializeToUtf8Bytes(new
        {
            stage,
            contractVersion,
            inputs = orderedInputs
        });
        var sha256 = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        return new PipelineStageFingerprint(stage, contractVersion, sha256, orderedInputs);
    }

    public bool Matches(PipelineStageFingerprint? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(Stage, other.Stage)
        && StringComparer.Ordinal.Equals(ContractVersion, other.ContractVersion)
        && StringComparer.Ordinal.Equals(Sha256, other.Sha256);
}
