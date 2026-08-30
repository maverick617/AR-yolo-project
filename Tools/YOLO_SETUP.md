# YOLOv8n-seg ONNX

The working COCO segmentation model is already bundled at:

`Assets/AR80sRetro/Models/YOLO/yolov8n-seg.onnx`

Its expected shapes are:

- input: `[1, 3, 640, 640]`
- detection/mask coefficients: `[1, 116, 8400]`
- mask prototypes: `[1, 32, 160, 160]`

Re-export only when the model needs to be replaced. Run the following from the
project root, preferably with the virtual environment outside an iCloud-synced
folder:

```bash
python3 -m venv ~/Library/Caches/arm-maskpose-yolo-venv
~/Library/Caches/arm-maskpose-yolo-venv/bin/python -m pip install ultralytics onnx onnxslim
~/Library/Caches/arm-maskpose-yolo-venv/bin/python Tools/export_yolov8n_seg_onnx.py
```

The script writes `Tools/output/yolov8n-seg.onnx`. Replace the bundled model
only after confirming the two output tensors retain the shapes above.
