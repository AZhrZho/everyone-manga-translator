# MangaTranslator.LocalDetection.Craft

This assembly runs a standard CRAFT ONNX text detector and returns the existing
`ITextMaskDetector` contract. It is intentionally independent from the GPL
`MangaTranslator.LocalDetection.Ctd` assembly.

The CRAFT affinity map is used only to group character regions. The cleaning
mask is synthesized from the character-region score map, so inter-character
background is not treated as text.

The host supplies the ONNX model. No model weights are embedded or downloaded
by this assembly.

This project is a non-packable proof of concept. The API reuses one ONNX
session and serializes detector access to bound memory use. Release use still
requires an approved, reproducible model artifact and broader page-quality
evaluation.

For API runs, install the model as `craft.onnx` in the application's models
directory or set `MANGA_TRANSLATOR_CRAFT_MODEL` to the model path. The legacy
`MANGA_TRANSLATOR_DETECTOR_MODEL` variable remains reserved for the GPL
ComicTextDetector engine so the two incompatible ONNX contracts cannot be
mixed accidentally.

## Current validation limits

The current mask is synthesized from the CRAFT character-score heatmap; it
does not yet use source-image color or Lab refinement. This implementation is
therefore an architecture and interoperability baseline, not a
production-ready detector or cleaning path.

For the current proof of concept, a compatible graph has:

- one dynamic float NCHW input with three channels;
- one float score output in NHWC or NCHW form with two channels;
- character and affinity scores at half input resolution.

The official PyTorch network also returns a 32-channel feature tensor for the
optional link refiner. It is not a detector score output and is deliberately
ignored by this assembly. To avoid confusing a dynamic spatial dimension with
the score channel, the ONNX graph must record the score output's two-channel
dimension statically.

## Reproducible smoke check

The real-model smoke test is opt-in because neither the model nor manga pages
belong in the test package:

```powershell
$env:MANGA_TRANSLATOR_CRAFT_MODEL = "$env:TEMP\emt-craft-model\craft.onnx"
$env:MANGA_TRANSLATOR_DETECTOR_TEST_IMAGE = "C:\path\to\page.png"
$env:MANGA_TRANSLATOR_CRAFT_ARTIFACTS = ".artifacts\craft-poc\page"
dotnet test tests\MangaTranslator.LocalDetection.Craft.Tests\MangaTranslator.LocalDetection.Craft.Tests.csproj --filter FullyQualifiedName~CraftRealModelSmokeTests
```

The test writes `input.png`, `actual-mask.png`, `overlay.png`, `clean.png`,
`detection-result.json`, and `summary.json`. These artifacts and the model stay
untracked; the check is technical validation only. The older `CRAFT_SMOKE_*`
variable names remain accepted for local convenience.

See `NOTICE.md` for CRAFT attribution and model-distribution cautions.
