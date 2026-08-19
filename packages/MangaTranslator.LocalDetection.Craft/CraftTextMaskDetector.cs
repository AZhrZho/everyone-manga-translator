using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaTranslator.LocalDetection.Craft;

/// <summary>
/// Runs a standard CRAFT ONNX graph and converts its character-region and
/// affinity maps into the application's region and binary-mask contract.
/// </summary>
public sealed class CraftTextMaskDetector : ITextMaskDetector
{
    private readonly InferenceSession _session;
    private readonly CraftTextMaskDetectorOptions _options;
    private readonly bool _ownsSession;
    private readonly string _inputName;
    private readonly string _scoreOutputName;
    private bool _disposed;

    public CraftTextMaskDetector(
        ReadOnlyMemory<byte> model,
        CraftTextMaskDetectorOptions? options = null,
        SessionOptions? sessionOptions = null)
    {
        if (model.IsEmpty)
        {
            throw new ArgumentException("Model bytes must not be empty.", nameof(model));
        }

        _options = options ?? new CraftTextMaskDetectorOptions();
        _options.Validate();
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
            (_inputName, _scoreOutputName) = ValidateModelContract(_session);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    internal CraftTextMaskDetector(
        InferenceSession session,
        CraftTextMaskDetectorOptions? options = null,
        bool disposeSession = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new CraftTextMaskDetectorOptions();
        _options.Validate();
        _ownsSession = disposeSession;
        (_inputName, _scoreOutputName) = ValidateModelContract(_session);
    }

    public ValueTask<TextDetectionResult> DetectAsync(
        DetectionInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        using var prepared = CraftModelInput.Prepare(input.ImageBytes, _options);
        var modelInput = NamedOnnxValue.CreateFromTensor(_inputName, prepared.Tensor);
        using var outputs = _session.Run([modelInput], [_scoreOutputName]);
        cancellationToken.ThrowIfCancellationRequested();
        var scores = outputs.Single().AsTensor<float>();
        var result = CraftPostProcessor.Process(prepared, scores, _options, cancellationToken);
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

    private static SessionOptions CreateDefaultSessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
    };

    private static (string InputName, string ScoreOutputName) ValidateModelContract(InferenceSession session)
    {
        var compatibleInputs = session.InputMetadata
            .Where(item => item.Value.ElementType == typeof(float)
                           && item.Value.Dimensions.Length == 4
                           && item.Value.Dimensions[1] == 3)
            .Select(item => item.Key)
            .ToArray();
        if (compatibleInputs.Length != 1)
        {
            throw new InvalidOperationException(
                "The CRAFT ONNX graph must expose exactly one float NCHW input with three channels.");
        }

        var compatibleOutputs = session.OutputMetadata
            .Where(item => item.Value.ElementType == typeof(float)
                           && item.Value.Dimensions.Length == 4
                           && (item.Value.Dimensions[1] == 2
                               || item.Value.Dimensions[3] == 2))
            .Select(item => item.Key)
            .ToArray();
        if (compatibleOutputs.Length != 1)
        {
            throw new InvalidOperationException(
                "The CRAFT ONNX graph must expose exactly one four-dimensional score output with a statically declared two-channel dimension.");
        }

        return (compatibleInputs[0], compatibleOutputs[0]);
    }
}
