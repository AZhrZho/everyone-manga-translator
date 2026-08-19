using System.Security.Cryptography;
using MangaTranslator.LocalDetection;

namespace MangaTranslator.Api.Jobs;

public sealed record TextDetectorSelection(
    string DetectorId,
    TextMaskDetectorDescriptor Descriptor,
    string? ModelPath,
    string? ModelSha256)
{
    public bool HasModel => ModelPath is not null && ModelSha256 is not null;

    public string CacheKey
    {
        get
        {
            var normalizedId = TextDetectorIds.Normalize(DetectorId);
            var descriptorIdentity = string.Join(
                '\n',
                Descriptor.DetectorId,
                Descriptor.AlgorithmVersion,
                Descriptor.OptionsFingerprint,
                ModelSha256 ?? "no-model");
            var descriptorHash = Convert.ToHexString(
                    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(descriptorIdentity)))
                .ToLowerInvariant();
            return $"{normalizedId}-{descriptorHash[..16]}";
        }
    }
}

public interface ITextDetectorRuntime
{
    IReadOnlyList<TextMaskDetectorDescriptor> Detectors { get; }

    TextMaskDetectorDescriptor GetDescriptor(string detectorId);

    Task<TextDetectorSelection> ResolveAsync(
        string detectorId,
        bool requireModel,
        CancellationToken cancellationToken);

    ValueTask<TextDetectionResult> DetectAsync(
        TextDetectorSelection selection,
        DetectionInput input,
        CancellationToken cancellationToken = default);

    string DefaultInstallPath(string detectorId);

    string ModelConfigurationHint(string detectorId);
}

public sealed class TextDetectorRuntime(
    IEnumerable<ITextMaskDetectorFactory> factories,
    ITextDetectorModelLocator modelLocator) : ITextDetectorRuntime, IDisposable
{
    private readonly IReadOnlyDictionary<string, DetectorEntry> _entries = factories
        .Select(factory => new DetectorEntry(factory))
        .ToDictionary(entry => entry.Factory.Descriptor.DetectorId, StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IReadOnlyList<TextMaskDetectorDescriptor> Detectors => _entries.Values
        .Select(entry => entry.Factory.Descriptor)
        .OrderBy(descriptor => descriptor.DetectorId, StringComparer.Ordinal)
        .ToArray();

    public TextMaskDetectorDescriptor GetDescriptor(string detectorId) =>
        GetEntry(detectorId).Factory.Descriptor;

    public async Task<TextDetectorSelection> ResolveAsync(
        string detectorId,
        bool requireModel,
        CancellationToken cancellationToken)
    {
        var entry = GetEntry(detectorId);
        var descriptor = entry.Factory.Descriptor;
        var modelPath = modelLocator.TryResolve(descriptor);
        if (modelPath is null)
        {
            if (requireModel)
            {
                throw new InvalidOperationException(
                    $"文字检测与清字引擎 {descriptor.DetectorId} 缺少模型。"
                    + $"请将模型安装到“{modelLocator.DefaultInstallPath(descriptor)}”，"
                    + $"或设置 {modelLocator.EnvironmentVariableName(descriptor)}。");
            }

            return new TextDetectorSelection(descriptor.DetectorId, descriptor, null, null);
        }

        await using var stream = new FileStream(
            modelPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var modelSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        return new TextDetectorSelection(descriptor.DetectorId, descriptor, modelPath, modelSha256);
    }

    public ValueTask<TextDetectionResult> DetectAsync(
        TextDetectorSelection selection,
        DetectionInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = GetEntry(selection.DetectorId);
        if (!Equals(entry.Factory.Descriptor, selection.Descriptor))
        {
            throw new InvalidOperationException(
                $"任务中的检测器快照 {selection.DetectorId} 与当前实现不兼容，请重新创建翻译任务。");
        }

        if (selection.ModelPath is null || selection.ModelSha256 is null)
        {
            throw new InvalidOperationException($"检测器 {selection.DetectorId} 没有可用模型。");
        }

        return entry.Provider.DetectAsync(
            selection.ModelPath,
            selection.ModelSha256,
            input,
            cancellationToken);
    }

    public string DefaultInstallPath(string detectorId) =>
        modelLocator.DefaultInstallPath(GetDescriptor(detectorId));

    public string ModelConfigurationHint(string detectorId) =>
        modelLocator.EnvironmentVariableName(GetDescriptor(detectorId));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _entries.Values)
        {
            entry.Provider.Dispose();
        }
    }

    private DetectorEntry GetEntry(string detectorId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = TextDetectorIds.Normalize(detectorId);
        return _entries.TryGetValue(normalized, out var entry)
            ? entry
            : throw new ArgumentException($"未知的文字检测与清字引擎：{detectorId}", nameof(detectorId));
    }

    private sealed class DetectorEntry
    {
        public DetectorEntry(ITextMaskDetectorFactory factory)
        {
            Factory = factory;
            Provider = new ReusableTextMaskDetectorProvider(factory);
        }

        public ITextMaskDetectorFactory Factory { get; }

        public ReusableTextMaskDetectorProvider Provider { get; }
    }

    private sealed class ReusableTextMaskDetectorProvider(ITextMaskDetectorFactory factory) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CachedDetector? _cached;
        private bool _disposed;

        public async ValueTask<TextDetectionResult> DetectAsync(
            string modelPath,
            string expectedModelSha256,
            DetectionInput input,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var fullPath = Path.GetFullPath(modelPath);
                var modelBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                var actualHash = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
                if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, expectedModelSha256))
                {
                    throw new InvalidOperationException(
                        "The text detector model changed after the translation task was created. Create a new task.");
                }

                var identity = new ModelIdentity(fullPath, expectedModelSha256);
                if (_cached is null || !_cached.Identity.Equals(identity))
                {
                    var detector = factory.Create(modelBytes);
                    _cached?.Detector.Dispose();
                    _cached = new CachedDetector(identity, detector);
                }

                return await _cached.Detector.DetectAsync(input, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _gate.Wait();
            try
            {
                _disposed = true;
                _cached?.Detector.Dispose();
                _cached = null;
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private sealed record CachedDetector(ModelIdentity Identity, ITextMaskDetector Detector);

        private sealed record ModelIdentity(string FullPath, string Sha256)
        {
            public bool Equals(ModelIdentity? other) =>
                other is not null
                && PathComparer.Equals(FullPath, other.FullPath)
                && StringComparer.OrdinalIgnoreCase.Equals(Sha256, other.Sha256);

            public override int GetHashCode() => HashCode.Combine(
                PathComparer.GetHashCode(FullPath),
                StringComparer.OrdinalIgnoreCase.GetHashCode(Sha256));

            private static StringComparer PathComparer => OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }
    }
}
