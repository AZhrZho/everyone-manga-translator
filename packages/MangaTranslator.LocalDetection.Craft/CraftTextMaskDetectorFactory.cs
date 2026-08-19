using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MangaTranslator.LocalDetection.Craft;

public sealed class CraftTextMaskDetectorFactory : ITextMaskDetectorFactory
{
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly CraftTextMaskDetectorOptions _options;

    public CraftTextMaskDetectorFactory(CraftTextMaskDetectorOptions? options = null)
    {
        _options = options ?? new CraftTextMaskDetectorOptions();
        _options.Validate();
        Descriptor = new TextMaskDetectorDescriptor(
            TextDetectorIds.Craft,
            "craft-onnx-clean-mask-v2",
            Convert.ToHexString(SHA256.HashData(CreateFingerprintMaterial(_options))).ToLowerInvariant(),
            "craft.onnx");
    }

    public TextMaskDetectorDescriptor Descriptor { get; }

    public ITextMaskDetector Create(ReadOnlyMemory<byte> model) =>
        new CraftTextMaskDetector(model, _options);

    private static byte[] CreateFingerprintMaterial(CraftTextMaskDetectorOptions options)
    {
        var serialized = JsonSerializer.Serialize(options, FingerprintJsonOptions);
        return Encoding.UTF8.GetBytes("craft-onnx-clean-mask-v2\n" + serialized);
    }
}
