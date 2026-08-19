# Browser CRAFT algorithm layer

This directory is dependency-free TypeScript. A worker can run the complete
algorithm with three calls:

```ts
const prepared = prepareCraftInput(imageData)
const input = new ort.Tensor('float32', prepared.data, prepared.dimensions)
const outputs = await session.run({ [inputName]: input })
const result = postprocessCraftOutput(outputs[scoreName], prepared)
```

The worker also has a convenience overload,
`await prepareCraftInput(encodedArrayBuffer, mediaType)`, and a compatibility
adapter `processCraftOutput(prepared, scoreData, scoreDims)` that returns the
browser-inference wire contract directly.

`prepareCraftInput` applies the server CRAFT defaults: 1.5 magnification,
1280-pixel cap, multiple-of-32 black padding, and NCHW layout. It performs
bilinear resize, saturates/rounds each interpolated RGB channel to the byte
value produced by an OpenCV `CV_8UC3` destination, and only then applies
ImageNet normalization. `postprocessCraftOutput` accepts both NHWC and NCHW
two-channel score tensors, crops canvas padding, performs 4-connected
text/affinity grouping, emits minimum-area region boxes, and builds a
page-sized 0/255 mask.

Mask semantics intentionally match the server implementation: affinity/link
scores may join text islands into one region, but link-only pixels never enter
the mask. Candidate glyph islands are labeled with 8-connectivity. The mask is
resized score-map -> unpadded content -> source page with bilinear filtering,
then thresholded at 30. When saved to a project, the API decodes the PNG and
independently re-binarizes luminance at `> 30` before cleaning and checkpoint
generation.

Known parity limits:

- Browser `ImageData` is already color-managed/decoded and ignores alpha in
  this layer, while OpenCV decodes source bytes directly. Transparent or
  color-profiled images can therefore differ before inference.
- Resize output is explicitly byte-quantized before normalization. OpenCV may
  still use optimized/fixed-point interpolation internally, so a small number
  of pixels can differ by one byte.
- The rotating-convex-hull minimum-area rectangle is geometrically equivalent
  to OpenCV `minAreaRect`, but tied rectangles can select a different angle or
  starting corner. Page-space bounds and mask semantics remain stable.
