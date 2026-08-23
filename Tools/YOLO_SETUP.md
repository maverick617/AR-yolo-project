# YOLOv8n ONNX preparation

Run these commands from the `ARm` directory:

```powershell
py -m venv .venv-yolo
.\.venv-yolo\Scripts\python.exe -m pip install ultralytics onnx onnxslim
.\.venv-yolo\Scripts\python.exe Tools\export_yolov8n_onnx.py
```

The generated model is:

`Tools/output/yolov8n.onnx`

Copy or drag it into:

`Assets/AR80sRetro/Models/YOLO/yolov8n.onnx`

This is the pretrained COCO detection model. It already includes the `cup`
class, so custom training is not required for the first prototype.
