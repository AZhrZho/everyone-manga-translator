using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MangaTranslator.LocalDetection.Ctd;

public sealed class CtdTextMaskDetectorFactory : ITextMaskDetectorFactory
{
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly OnnxTextMaskDetectorOptions _options;

    public CtdTextMaskDetectorFactory(OnnxTextMaskDetectorOptions? options = null)
    {
        _options = options ?? new OnnxTextMaskDetectorOptions();
        var serialized = JsonSerializer.Serialize(_options, FingerprintJsonOptions);
        Descriptor = new TextMaskDetectorDescriptor(
            TextDetectorIds.Ctd,
            "ctd-v1",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant(),
            "comictextdetector.pt.onnx");
    }

    public TextMaskDetectorDescriptor Descriptor { get; }

    public ITextMaskDetector Create(ReadOnlyMemory<byte> model) =>
        new OnnxTextMaskDetector(model, _options);
}
