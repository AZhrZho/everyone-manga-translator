# MangaTranslator Font Manager

This is a standalone, offline-friendly font catalog tool. It keeps a private copy of imported font files and a JSON manifest so other MangaTranslator modules can read the catalog without keeping a process running.

The default store is `resources/font-library` under the nearest `MangaTranslator.slnx` root:

```text
font_catalog.json
fonts/F0001/regular.ttf
fonts/F0001/bold.ttf
fonts/F0001/italic.ttf
samples/font_samples_r0001.png
```

`regular`, `bold`, and `italic` are independent imported faces. If a face is missing, sample generation previews an explicitly labelled synthetic variant; the JSON never records that preview as a real font asset.

## Commands

```powershell
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

& $dotnet run --project tools/MangaTranslator.FontManager.Cli -- init
& $dotnet run --project tools/MangaTranslator.FontManager.Cli -- add `
  --name '测试｜等线' --category 'sans / dialogue' --tags 'dialogue,standard' `
  --regular 'C:\Windows\Fonts\Deng.ttf' `
  --bold 'C:\Windows\Fonts\Dengb.ttf' `
  --source-kind system --source 'Windows Fonts' `
  --license-note 'Local system copy; redistribution rights must be checked before publishing.'
& $dotnet run --project tools/MangaTranslator.FontManager.Cli -- sample --font-size 52
& $dotnet run --project tools/MangaTranslator.FontManager.Cli -- list
& $dotnet run --project tools/MangaTranslator.FontManager.Cli -- validate
& $dotnet run --project tools/MangaTranslator.FontManager.Cli -- remove --id F0001
```

For a TTC/OTC collection, pass the face index with `--regular-index`, `--bold-index`, or `--italic-index`.

For downloaded open-source fonts, use `--license-file OFL.txt`. The license is copied into the managed font directory and its SHA-256 is stored in `font_catalog.json`.

The generated sample uses one fixed pixel size for all style cells. Its `latestSample` block records the source library revision, exact sample text, dimensions, native/synthetic render mode, and missing glyphs so a downstream precheck/model step can audit the choice.

The sample renderer intentionally rejects text/size combinations that would be clipped inside the fixed-size cells. Annotation labels use a stable system CJK UI face when available; the imported candidate face is reserved for the regular/bold/italic sample cells.
