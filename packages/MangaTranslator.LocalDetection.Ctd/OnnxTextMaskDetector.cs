using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Ctd;

/// <summary>
/// Comic-Text-Detector (CTD) port. It returns text-line
/// geometry and an in-memory refined text mask; it does not perform OCR.
/// </summary>
public sealed class OnnxTextMaskDetector : ITextMaskDetector
{
    private static readonly string[] RequiredOutputs = ["seg", "det"];

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

    public async ValueTask<TextDetectionResult> DetectAsync(
        DetectionInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        using var decoded = DetectionImage.Decode(input.ImageBytes, _options.MinimumImageSize);
        var maps = _options.EnableRearrangedInference
            ? RearrangedInference.TryInfer(
                decoded.Working,
                _options.InputSize,
                _options.RearrangedScaleThreshold,
                _options.RearrangedAspectRatioThreshold,
                tile => RunModel(tile, cancellationToken))
            : null;

        maps ??= RunDirect(decoded.Working, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var result = CtdPostProcessor.Process(
            decoded.Working,
            decoded.Original,
            decoded.HasBorder,
            maps,
            _options,
            cancellationToken);
        await Task.CompletedTask;
        return result;
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

    private ModelInferenceMaps RunDirect(Mat imageBgr, CancellationToken cancellationToken)
    {
        using var prepared = ImagePreprocessor.PrepareDirect(imageBgr, _options.InputSize);
        var maps = RunModel(prepared.Image, cancellationToken);
        return CropMaps(
            maps,
            prepared.ContentWidth,
            prepared.ContentHeight,
            prepared.ContentWidth,
            prepared.ContentHeight);
    }

    private ModelInferenceMaps RunModel(Mat modelImage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tensor = ImagePreprocessor.ToTensor(modelImage);
        var modelInput = NamedOnnxValue.CreateFromTensor("images", tensor);
        using var outputs = _session.Run([modelInput], RequiredOutputs);

        var outputMap = outputs.ToDictionary(output => output.Name, StringComparer.Ordinal);
        var lines = ReadMap(outputMap["det"].AsTensor<float>(), expectedChannels: 2, "det");
        var mask = ReadMap(outputMap["seg"].AsTensor<float>(), expectedChannels: 1, "seg");
        return new ModelInferenceMaps(
            lines.Values,
            lines.Channels,
            lines.Height,
            lines.Width,
            mask.Values,
            mask.Height,
            mask.Width);
    }

    private static ChannelMap ReadMap(Tensor<float> tensor, int expectedChannels, string name)
    {
        var dims = tensor.Dimensions;
        if (dims.Length != 4 || dims[0] != 1 || dims[1] != expectedChannels || dims[2] <= 0 || dims[3] <= 0)
        {
            throw new InvalidOperationException($"The ONNX '{name}' output has an unexpected shape.");
        }

        return new ChannelMap(tensor.ToArray(), dims[1], dims[2], dims[3]);
    }

    private static ModelInferenceMaps CropMaps(
        ModelInferenceMaps maps,
        int lineWidth,
        int lineHeight,
        int maskWidth,
        int maskHeight)
    {
        var lines = Crop(maps.Lines, maps.LineChannels, maps.LineHeight, maps.LineWidth, lineWidth, lineHeight);
        var mask = Crop(maps.Mask, 1, maps.MaskHeight, maps.MaskWidth, maskWidth, maskHeight);
        return new ModelInferenceMaps(lines, maps.LineChannels, lineHeight, lineWidth, mask, maskHeight, maskWidth);
    }

    private static float[] Crop(float[] source, int channels, int sourceHeight, int sourceWidth, int width, int height)
    {
        width = Math.Clamp(width, 1, sourceWidth);
        height = Math.Clamp(height, 1, sourceHeight);
        var result = new float[checked(channels * width * height)];
        for (var channel = 0; channel < channels; channel++)
        {
            for (var y = 0; y < height; y++)
            {
                Array.Copy(
                    source,
                    channel * sourceHeight * sourceWidth + y * sourceWidth,
                    result,
                    channel * height * width + y * width,
                    width);
            }
        }

        return result;
    }

    private static SessionOptions CreateDefaultSessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
    };

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

        if (options.MinimumImageSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MinimumImageSize));
        }

        if (options.RearrangedScaleThreshold <= 0 || options.RearrangedAspectRatioThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RearrangedScaleThreshold));
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

        var dimensions = input.Dimensions;
        var expected = new[] { 1, 3, inputSize, inputSize };
        if (dimensions.Length != expected.Length
            || dimensions.Zip(expected).Any(pair => pair.First > 0 && pair.First != pair.Second))
        {
            throw new InvalidOperationException(
                $"The ONNX model input shape is [{string.Join(',', dimensions)}], expected [{string.Join(',', expected)}].");
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

    private sealed record ChannelMap(float[] Values, int Channels, int Height, int Width);
}
