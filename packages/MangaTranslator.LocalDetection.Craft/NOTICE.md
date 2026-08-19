# Attribution and model boundary

The CRAFT architecture and the post-processing behavior used as a reference
come from NAVER/Clova AI Research's official CRAFT repository:

- https://github.com/clovaai/CRAFT-pytorch
- Reference commit: `e332dd8b718e291f51b66ff8f9ef2c98ee4474c8`
- Copyright (c) 2019-present NAVER Corp.
- Software license: MIT

The official README links three Google Drive artifacts but does not publish
their byte sizes or checksums:

- General (SynthText, IC13, IC17; English + multilingual): `1Jk4eGD7crsqCCg9C9VjCLkMN3ze8kutZ`
- IC15 (SynthText, IC15; English): `1i2R7UIUqmkUtF0jv_3MXTqmQ_9wuAnLf`
- LinkRefiner (CTW1500): `1XSaFwBkOaFOdtk4Ane3DFyJGPRw6v5bO`

This assembly does not contain or download pretrained weights. Before shipping
a model, the distributor must separately record the source, checksum, weight
license and any relevant training-data terms. A repository code license alone
does not establish those facts for every converted ONNX file.

The August 2026 proof of concept used this third-party ONNX file solely for a
local interoperability smoke check:

- Source repository: https://huggingface.co/KvaytG/craft-mlt-25k-onnx
- Model URL: https://huggingface.co/KvaytG/craft-mlt-25k-onnx/resolve/main/craft.onnx
- File commit: `88ee1e402f4cca66ccd1d1c7f2e29511a79cc6fe`
- Size: 83,179,177 bytes
- SHA-256: `f69ff10b3fd19126738b2ff7322285a59ba92571bb603dfbeb61a0dc03aa536e`

That model is not committed, packaged, or approved for redistribution by this
notice. The third-party repository and its MIT license are useful provenance
signals, but they do not by themselves prove the converted weight's full
license or training-data rights. A release decision requires separate legal
evidence for the exact model artifact.
