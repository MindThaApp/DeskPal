# Models

## movenet-singlepose-lightning.onnx

The on-device body pose model for "Stretch with Bloop". It runs only on the PC; camera frames are never stored or sent.

- Model: MoveNet SinglePose Lightning (Google), 192 x 192 input, 17 COCO body points.
- Original: https://www.kaggle.com/models/google/movenet (formerly https://tfhub.dev/google/movenet/singlepose/lightning), Apache License 2.0.
- ONNX conversion used here (tf2onnx 1.16.1, opset 11, float32): https://huggingface.co/Xenova/movenet-singlepose-lightning,
  file `onnx/model.onnx` at revision `ed0f314bb7356fd1dbf1e4f52c2d40791bf6534f`, Apache License 2.0.
  Download: https://huggingface.co/Xenova/movenet-singlepose-lightning/resolve/ed0f314bb7356fd1dbf1e4f52c2d40791bf6534f/onnx/model.onnx
- Size: 9,413,268 bytes.
- SHA-256: `1ad4f8d6c2f776a9967db3993c9ca740bc350104f9d37c151dc183fc29a464ad`
- Input: `input`, int32 `[1, 192, 192, 3]`, RGB 0..255. Output: `output_0`, float `[1, 1, 17, 3]`, (y, x, score) per point.
- Licence text: `movenet-LICENSE.txt` (Apache License 2.0). The file is unchanged from the download.
