# AR 80s Retro MVP Scaffold

This folder contains the first Unity-side implementation for the MVP described in the project plan.

## Implemented

- Detection result data contract for YOLO-style output.
- Scriptable replacement rules from detected labels to retro prefabs.
- AR Foundation raycast-based 2D bbox center to AR world pose solver.
- AprilTag/marker pose source that can consume Keijiro AprilTag poses, native plugin poses through `PublishTagPose`, or AR tracked-image poses as a fallback.
- Replacement manager with confidence gating, multi-frame confirmation, smoothing, duplicate suppression, and lost-object grace time.
- Replacement objects can now follow marker-derived 6DoF position and rotation instead of locking their initial rotation.
- Mock input for testing the placement loop before a real detector is connected.
- Detector stub with a throttled inference interval.

## Unity scene setup

1. Create a Unity 2022.3 LTS Android project.
2. Install `AR Foundation` and `ARCore XR Plugin`.
3. Add an `AR Session`, `XR Origin`, `AR Camera`, `AR Plane Manager`, and `AR Raycast Manager`.
4. Add `ARRaycastPositionSolver`, `RetroReplacementManager`, and `RetroDetectionPipeline` to scene objects.
5. Create a `RetroPrefabLibrary` asset and add rules such as `cup -> enamel cup prefab`.
6. For early testing, add `MockDetectionInput` and hold Space in Play Mode while an AR plane is visible.

## Keijiro AprilTag tracking setup

Use this path when the real object must rotate or move and the retro model must follow it precisely.

1. Install `jp.keijiro.apriltag` from the Keijiro scoped registry.
2. Add `AprilTagPoseSource` and `KeijiroAprilTagFrameDetector` to the same scene object as `RetroReplacementManager`.
3. Assign the AR camera, `ARCameraFrameProvider`, and `AprilTagPoseSource` references on `KeijiroAprilTagFrameDetector`.
4. Set `Tag Size Meters` to the real printed tag width, for example `0.05` for a 5 cm tag.
5. In each `RetroReplacementRule`, enable `Use April Tag Pose`.
6. Set `April Tag Id` to the printed tag number.
7. Set `April Tag To Object Offset Meters` so the virtual object's origin lands where the old object should appear relative to the printed tag.
8. Set `April Tag To Object Rotation Euler` and the existing `Rotation Offset Euler` until the old object faces the same direction as the real object.

For a native AprilTag detector later, keep the Unity replacement code unchanged and call:

```csharp
aprilTagPoseSource.PublishTagPose(tagId, new Pose(worldPosition, worldRotation));
```

The manager still uses YOLO detections for class confirmation and falls back to raycast placement when no fresh tag pose is available.

`ARTrackedImageManager` is still supported by `AprilTagPoseSource`, but it is now the fallback path rather than the recommended precise marker tracker.

## Next implementation step

Replace `YoloObjectDetector` with a real detector backend. Keep its output as `DetectionResult` values with normalized top-left-origin bounding boxes so the placement and replacement layers remain unchanged.
