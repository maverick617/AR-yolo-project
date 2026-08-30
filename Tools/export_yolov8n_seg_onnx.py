from pathlib import Path

from ultralytics import YOLO


def main() -> None:
    output_dir = Path(__file__).resolve().parent / "output"
    output_dir.mkdir(parents=True, exist_ok=True)

    model = YOLO("yolov8n-seg.pt")
    exported_path = Path(
        model.export(
            format="onnx",
            imgsz=640,
            batch=1,
            dynamic=False,
            simplify=True,
            opset=12,
            nms=False,
        )
    )

    destination = output_dir / "yolov8n-seg.onnx"
    destination.write_bytes(exported_path.read_bytes())
    print(f"Exported segmentation model: {destination}")


if __name__ == "__main__":
    main()
