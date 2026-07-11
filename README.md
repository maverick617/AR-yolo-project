# ARm

Android AR prototype that detects real-world objects with YOLO and places
1980s-style replacement assets using AR Foundation and ARCore.

## Environment

- Unity 2022.3.62f3
- Universal Render Pipeline 14
- AR Foundation / ARCore XR Plugin 5.2
- Unity Sentis 2.1.3
- Android minimum SDK 30

## Current Prototype

- ARCore plane detection
- YOLOv8n `cup` detection
- Camera-image to screen-coordinate calibration
- Bounding-box anchor to AR plane raycast
- Dynamic prefab scale estimation
- Retro cup replacement asset

## Getting Started

1. Clone the repository.
2. Open the repository root in Unity Hub with Unity 2022.3.62f3.
3. Wait for Package Manager and asset imports to finish.
4. Open `Assets/Scenes/SampleScene.unity`.
5. Build and run on an ARCore-compatible Android device.

Generated Unity directories such as `Library`, `Temp`, `Logs`, and
`UserSettings` are intentionally not tracked.
