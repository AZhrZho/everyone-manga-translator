# Runtime attribution

The browser inference proof of concept uses ONNX Runtime Web:

- Package: `onnxruntime-web`
- Version: `1.27.0`
- Project: https://github.com/microsoft/onnxruntime
- Copyright: Microsoft Corporation
- License: MIT

The runtime package and the neural-network model are separate distribution
boundaries. This module does not bundle a CRAFT model. See
`packages/MangaTranslator.LocalDetection.Craft/NOTICE.md` for CRAFT attribution and
the exact model-weight provenance caveat used during local validation.
