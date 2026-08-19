# MangaTranslator.LocalDetection.Ctd

Cross-platform .NET 10/C# 14 port of Comic-Text-Detector (CTD).

The assembly only performs text-line detection and in-memory text Mask
generation. It does not perform OCR, translation, inpainting, rendering,
model downloading or filesystem output.

The host must reference the OpenCvSharp native runtime package matching its
target RID, such as `OpenCvSharp4.runtime.win`,
`OpenCvSharp4.official.runtime.linux-x64` or
`OpenCvSharp4.runtime.osx.arm64`.

```csharp
using MangaTranslator.LocalDetection;
using LatestCtd = MangaTranslator.LocalDetection.Ctd;

using ITextMaskDetector detector = new LatestCtd.OnnxTextMaskDetector(modelBytes);
TextDetectionResult result = await detector.DetectAsync(
    new DetectionInput(imageBytes, "image/png"),
    cancellationToken);
```

The implementation is based on the CTD path from
`zyddnys/manga-image-translator@95227a2bb0fd306cd4f0c104d57284026f991b3a`
and is licensed under GPL-3.0-only. See `NOTICE.md` for attribution and scope.

## Review artifacts from the xUnit host

The detector assembly never writes files. For manual review, the real-model
xUnit smoke test can optionally let the test host export its API result by
setting these environment variables:

```text
MANGA_TRANSLATOR_DETECTOR_MODEL=<comictextdetector.pt.onnx>
MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE=<input image>
MANGA_TRANSLATOR_LATEST_EXPECTED_MASK=<optional upstream reference Mask>
MANGA_TRANSLATOR_DETECTOR_ARTIFACTS=<output directory>
```

The output directory contains the normalized input, actual Mask, detection
overlay, JSON API result and summary. When a reference Mask is configured, it
also contains the upstream Mask and a color-coded difference image: white is
shared, red is C# only, blue is upstream only, and black is absent from both.

The smoke test is deliberately opt-in. Supply your own license-safe input with
`MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE`; neither source images nor generated
review artifacts are committed to this repository.
