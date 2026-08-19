namespace MangaTranslator.LocalDetection.Craft;

/// <summary>
/// Lazily creates one CRAFT detector for the configured model and reuses its
/// ONNX session across pages. The caller supplies the already-computed model
/// hash used by workflow cache validation, avoiding another 83 MB file hash.
/// </summary>
public sealed class CraftTextMaskDetectorProvider : IDisposable
{
    private readonly ITextMaskDetectorFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedDetector? _cached;
    private bool _disposed;
    private int _disposeStarted;

    public CraftTextMaskDetectorProvider(ITextMaskDetectorFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async ValueTask<TextDetectionResult> DetectAsync(
        string modelPath,
        string modelSha256,
        DetectionInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSha256);
        ArgumentNullException.ThrowIfNull(input);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposeStarted != 0, this);
            var identity = new ModelIdentity(Path.GetFullPath(modelPath), modelSha256);
            if (_cached is null || !_cached.Identity.Equals(identity))
            {
                var modelBytes = await File.ReadAllBytesAsync(modelPath, cancellationToken);
                var detector = _factory.Create(modelBytes);
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
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
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

    private sealed record ModelIdentity
    {
        public ModelIdentity(string fullPath, string sha256)
        {
            FullPath = fullPath;
            Sha256 = sha256;
        }

        public string FullPath { get; }

        public string Sha256 { get; }

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
