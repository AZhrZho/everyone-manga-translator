using Microsoft.ML.OnnxRuntime;

namespace MangaTranslator.LocalDetection;

public sealed class OnnxTextMaskDetector : ITextMaskDetector
{
    private static readonly string[] RequiredOutputs = ["blk", "seg", "det"];

    private readonly InferenceSession _session;
    private readonly OnnxTextMaskDetectorOptions _options;
    private readonly bool _ownsSession;
    private bool _disposed;

    public OnnxTextMaskDetector(
        ReadOnlyMemory<byte> model,
        OnnxTextMaskDetectorOptions? options = null,
        SessionOptions? sessionOptions = null)
    {
        if (model.IsEmpty)
        {
            throw new ArgumentException("Model bytes must not be empty.", nameof(model));
        }

        _options = options ?? new OnnxTextMaskDetectorOptions();
        ValidateOptions(_options);

        var ownsSessionOptions = sessionOptions is null;
        sessionOptions ??= CreateDefaultSessionOptions();
        try
        {
            _session = new InferenceSession(model.ToArray(), sessionOptions);
            _ownsSession = true;
        }
        finally
        {
            if (ownsSessionOptions)
            {
                sessionOptions.Dispose();
            }
        }

        try
        {
            ValidateModelContract(_session, _options.InputSize);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Uses a caller-configured session, allowing the host to select CUDA,
    /// DirectML, CoreML or another execution provider without coupling this
    /// assembly to a platform-specific runtime package.
    /// </summary>
    public OnnxTextMaskDetector(
        InferenceSession session,
        OnnxTextMaskDetectorOptions? options = null,
        bool disposeSession = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new OnnxTextMaskDetectorOptions();
        _ownsSession = disposeSession;
        ValidateOptions(_options);
        ValidateModelContract(_session, _options.InputSize);
    }

    public ValueTask<TextDetectionResult> DetectAsync(
        DetectionInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        using var prepared = ImagePreprocessor.Prepare(input.ImageBytes, _options.InputSize);
        var modelInput = NamedOnnxValue.CreateFromTensor("images", prepared.Tensor);
        using var outputs = _session.Run([modelInput], RequiredOutputs);
        cancellationToken.ThrowIfCancellationRequested();

        var byName = outputs.ToDictionary(output => output.Name, StringComparer.Ordinal);
        var result = DetectionPostProcessor.Process(
            prepared,
            byName["blk"].AsTensor<float>(),
            byName["seg"].AsTensor<float>(),
            byName["det"].AsTensor<float>(),
            _options);
        return ValueTask.FromResult(result);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsSession)
        {
            _session.Dispose();
        }
    }

    private static SessionOptions CreateDefaultSessionOptions()
    {
        return new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
    }

    private static void ValidateOptions(OnnxTextMaskDetectorOptions options)
    {
        if (options.InputSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.InputSize));
        }

        if (options.MaxDetections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxDetections));
        }

        ValidateProbability(options.BlockConfidenceThreshold, nameof(options.BlockConfidenceThreshold));
        ValidateProbability(options.BlockIouThreshold, nameof(options.BlockIouThreshold));
        ValidateProbability(options.LineBitmapThreshold, nameof(options.LineBitmapThreshold));
        ValidateProbability(options.LineScoreThreshold, nameof(options.LineScoreThreshold));
    }

    private static void ValidateProbability(float value, string name)
    {
        if (value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(name, "Thresholds must be in the range 0..1.");
        }
    }

    private static void ValidateModelContract(InferenceSession session, int inputSize)
    {
        if (!session.InputMetadata.TryGetValue("images", out var input))
        {
            throw new InvalidOperationException("The ONNX model does not expose the required 'images' input.");
        }

        var expectedInput = new[] { 1, 3, inputSize, inputSize };
        if (!input.Dimensions.SequenceEqual(expectedInput))
        {
            throw new InvalidOperationException(
                $"The ONNX model input shape is [{string.Join(',', input.Dimensions)}], "
                + $"expected [{string.Join(',', expectedInput)}].");
        }

        var missing = RequiredOutputs
            .Where(name => !session.OutputMetadata.ContainsKey(name))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"The ONNX model is missing outputs: {string.Join(", ", missing)}.");
        }
    }
}
