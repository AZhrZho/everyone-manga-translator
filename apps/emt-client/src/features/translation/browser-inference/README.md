# Browser inference proof of concept

This module runs a user-supplied CRAFT ONNX model inside a dedicated browser
worker. It tries WebGPU first and recreates a pure WASM session if WebGPU
initialization or execution fails.

The model is selected with a local file picker. It is not bundled, uploaded,
or exposed through the local API. This matters because runtime licensing and
model-weight provenance are separate review items.

The experiment panel reads the selected page through the existing
original-image endpoint, then previews or downloads its regions and binary
Mask. “Save to project” uploads the result before a translation task starts.
The API validates page, algorithm, options, and region identities,
re-binarizes the Mask, runs the server OpenCV cleaner, writes fingerprint-
addressed Mask and clean-image assets under the workflow cache, and saves both
an immutable detection checkpoint and a stable browser alias. The Browser
CRAFT engine in Live and Quick translation requires these saved artifacts and
never falls back to host-local CRAFT. A running server job never waits for a
browser callback, and saving is rejected while translation or preprocessing is
active.

Browser model files are capped at 512 MiB before they are read into memory.

## Runtime notes

- `onnxruntime-web` is pinned to 1.27.0 (MIT).
- Vite emits the JSEP WASM asset used by the combined WebGPU/WASM build from
  the pinned package into the production asset graph.
- WebGPU is attempted first. Session creation or run failure recreates the
  inference session with the pure WASM provider.
- WASM uses one thread unless the host is cross-origin isolated.
- Worker requests are serialized; a failed Worker is terminated and must be
  explicitly recreated by the UI before loading the model again.
- Switching projects or pages terminates the previous Worker queue and requires
  reloading the local model, so an old long-running WASM request cannot block or
  update the new page.
- Browser bilinear resize is quantized to an 8-bit RGB value before ImageNet
  normalization to match the server OpenCV `CV_8UC3` path more closely.
- The CRAFT model and manga pages remain local and untracked.

## Validation

Browser and .NET output can be compared with the opt-in local harness. Supply
your own license-safe image and model; neither inputs nor generated artifacts
are committed. WebGPU is the preferred execution provider, while WASM remains
a slower compatibility fallback.
