# Upstream attribution and license

`MangaTranslator.LocalDetection.Ctd` is a C# port of the
comic text detector and mask-refinement path from:

- Project: `zyddnys/manga-image-translator`
- Repository: <https://github.com/zyddnys/manga-image-translator>
- Baseline commit: `95227a2bb0fd306cd4f0c104d57284026f991b3a`
- Baseline date: 2026-07-20
- Upstream license: GNU General Public License v3.0
- License text: <https://github.com/zyddnys/manga-image-translator/blob/95227a2bb0fd306cd4f0c104d57284026f991b3a/LICENSE>

The principal upstream files used as behavioral references are:

- `manga_translator/detection/ctd.py`
- `manga_translator/detection/common.py`
- `manga_translator/detection/ctd_utils/textmask.py`
- `manga_translator/detection/ctd_utils/utils/db_utils.py`
- `manga_translator/detection/ctd_utils/utils/imgproc_utils.py`
- `manga_translator/utils/generic.py`

The port replaces Python, PyTorch/OpenCV DNN, NumPy, PyClipper and Shapely
runtime dependencies with .NET 10, ONNX Runtime and OpenCvSharp. It also
exposes an in-memory .NET API and does not copy upstream OCR, translation,
inpainting, rendering or filesystem orchestration code.

Windows release builds acquire the upstream ONNX artifact directly from the
`beta-0.3` release instead of storing it in this repository:

- Model URL: <https://github.com/zyddnys/manga-image-translator/releases/download/beta-0.3/comictextdetector.pt.onnx>
- Size: 94,669,756 bytes
- SHA-256: `1a86ace74961413cbd650002e7bb4dcec4980ffa21b2f19b86933372071d718f`

The pinned source and checksum are also recorded in
`eng/release-models.json` and verified before packaging.

This notice does not change the upstream GPL obligations. Applications that
distribute this assembly should obtain appropriate legal guidance for their
distribution model.
