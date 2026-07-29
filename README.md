# Four-AprilTag Cup AR Replacement Demo

This project currently supports `cup` only. YOLO confirms the object class and estimates the cup size. Three AprilTags on the side of the cup (IDs `0`, `1`, and `2`) plus one AprilTag on the bottom (ID `3`) provide identity and 6DoF pose. All four tags are transformed into one common coordinate system: **cup-body center + handle direction**. As a result, one retro cup model can be handed over between tags when the cup rotates around its vertical axis or is flipped so that its bottom faces the camera.

## Why use three side tags and one bottom tag?

- A planar tag becomes physically invisible when it rotates to the back of the cup; software cannot recover a pose from a tag that is not visible.
- With two opposing tags, both can be viewed nearly edge-on at the same time. This is especially unreliable for 1 cm tags.
- With three side tags spaced 120° apart, at least one tag should theoretically be within about 60° of facing the camera for any rotation around the cup axis.
- Bottom tag ID `3` handles large flips where the cup bottom faces the camera. It does not need to be visible together with a side tag.
- You do not need a tag on every face, and multiple tags do not need to be visible simultaneously. Seeing any one configured tag is enough for tracking handover.

AprilTag decoding includes the tag image orientation. The application combines that orientation with the known mounting angle of each ID to recover the real cup-handle direction. Translation, lifting, tilting, and flips after tracking begins are driven by the tag's 6DoF pose.

## Required tag placement

Use AprilRobotics `tagStandard41h12` IDs `0`, `1`, `2`, and `3`. The black square of every tag must have a side length of **1 cm**; do not include the white border. The scene is configured with `tagSizeMeters = 0.01`.

View the cup from above and treat the real handle as the 3 o'clock direction. Place ID `0` opposite the handle, at 9 o'clock:

```text
                     12 o'clock       ID 1 (1 o'clock)

 ID 0 (9 o'clock)    cup center       handle (3 o'clock)

                      6 o'clock       ID 2 (5 o'clock)
```

Requirements:

1. Place ID `0` at 9 o'clock, directly opposite the handle.
2. Place ID `1` at 1 o'clock and ID `2` at 5 o'clock. Do not swap IDs `1` and `2`.
3. Keep all three side tags in the same horizontal band around the middle height of the cup. Their fronts must face radially outward.
4. The top edge of every side-tag image must point toward the cup rim. Do not mix upright and upside-down tags.
5. Attach ID `3` flat to the cup bottom with its front facing outward; it therefore faces down when the cup is upright. The top edge of the ID `3` image must point toward the handle (the 3 o'clock direction).
6. Tags must be fixed rigidly and lie flat. If the cup curvature is noticeable, use a small flat mounting pad. If the tag plane has a measurable gap from the cup surface, set the corresponding `tagMountStandoffMeters` value as well.

The default side Tag-to-cup rotations are `-90°` for ID `0`, `+150°` for ID `1`, and `+30°` for ID `2`. Bottom ID `3` uses the 3D rotation `(0°, +90°, +90°)` and is positioned from the bottom to the cup center using half of the measured cup height, rather than the cup radius. In the Keijiro detector's convention, local Tag `+Z` points from the printed front into the back of the tag; all four tags therefore use the cup-center direction `(0, 0, +1)`. These parameters, the physical tag locations, and the image directions above form one inseparable convention.

### Tag orientation and the physical limit of full occlusion

Tag image direction still matters. The AprilTag detector recovers the full 3D orientation of the printed pattern, but the application must know the fixed relation between that image direction and the real cup handle. Although side IDs `0`, `1`, and `2` use different configuration values, they all recover the same cup pose when attached at the clock positions above with their top edges facing the rim. Switching between them should not make the virtual model jump or flip.

`ARCameraFrameProvider` already rotates the camera CPU image into a portrait detector texture. Therefore, `KeijiroAprilTagFrameDetector.compensateFrameRotation` must be disabled. Applying another 90° compensation to the detected pose turns the local top of an upright side tag sideways on screen, making the virtual cup lie horizontally. This does not mean the real tag needs to be mounted sideways.

The normal of bottom tag ID `3` determines which way the cup bottom faces, but it cannot independently determine the handle angle around the cup axis. Its image top must therefore point to the handle. Rotating ID `3` by 90° in place on the bottom causes approximately a 90° virtual-handle direction error. The camera does not have to keep seeing the same tag, but at least one of the four tags must be visible whenever a person continues rotating the cup and the application must obtain a new real pose.

When all tags are occluded, the current YOLO model provides only a 2D cup box and the phone IMU knows only the phone's own movement. Neither can observe a hand-driven rotation of the cup. The application can briefly use YOLO/depth to correct position while keeping the last orientation, but cannot reliably infer an arbitrary flip that occurs while all tags are hidden. Long-term tag-free 6DoF tracking requires a cup pose/feature tracker, an ARKit reference-object scan (on supported platforms), or an IMU attached to the cup. Physics alone cannot infer an unknown hand motion.

## No camera sweep is required

This version no longer asks you to move the phone around the cup, collect several viewpoints, and save a profile:

1. For initial sizing, show the full cup and one side tag (ID `0`, `1`, or `2`) together in portrait view. A distance of about `15–30 cm` is recommended. Bottom ID `3` can take over flip tracking after sizing is locked, but a bottom view cannot determine the cup height by itself.
2. Keep the phone and cup still for about `1–2 seconds`. The application collects five YOLO cup boxes from the same ordinary viewpoint and uses their median. This is background size stabilization; it does not require changing viewpoints.
3. Once `TRACKING CUP (APRILTAG n)` appears, you can pick up, move, and rotate the cup.
4. The median of those five frames is locked as the height and body diameter of this physical cup. This prevents the model from visually breathing as the visible handle changes the detection box during rotation. These are not fixed preset dimensions. When changing to a different physical cup, restart tracking or run `Clear Saved Cup Registrations` to measure again.

Typical status messages are:

- `WAITING: show cup + AprilTag 0/1/2/3`
- `AUTO SIZING CUP: 1/5 ... 4/5 - HOLD STILL` (the fifth valid sample enters `TRACKING` directly)
- `SHOW SIDE APRILTAG 0/1/2 TO SIZE CUP` (only bottom ID `3` is visible before size initialization)
- `TRACKING CUP (APRILTAG 0/1/2/3) H=...cm D=...cm`

## First device test

1. Open the project with Unity `2022.3.62f3`.
2. Use only the build scene: `Assets/Scenes/SampleScene.unity`.
3. If Unity reports that files were modified outside the editor, choose **Reload from Disk**.
4. Run `Tools > AR 80s Retro > Configure Build Scene (4-Tag Cup)`. The command saves the scene. Then rebuild the device app so it contains the latest code and scene configuration.
5. When upgrading from an old 5 cm-tag or single-tag build, run `Clear Saved Cup Registrations` once from the `RetroReplacementManager` component menu. The current four-tag path does not read the old single-tag profile.
6. Test in portrait first. A 1 cm tag is small, so avoid glare, motion blur, fuzzy printing, and finger occlusion.
7. Start tracking with ID `0` facing the camera. Then rotate around the vertical axis to IDs `1` and `2`, and finally flip the cup to bottom ID `3`. Every handover should keep exactly one model, and the handle direction should not jump.

For detailed status messages and troubleshooting, see the [Cup Demo device-test guide (Chinese)](Assets/AR80sRetro/CUP_DEMO_SETUP_ZH.md).

## Current accuracy limits

- The repository's `yolov8n.onnx` is a detector, not a segmentation model. Final model height on screen is based on the median of five YOLO cup boxes and is locked only when the error is within `4%`. Environment depth only converts the pixel envelope into metres. If environment depth is unavailable, the tag distance is used only as a perspective-depth fallback because a single 2D box cannot produce a centimetre measurement; the tag no longer supplies the cup width or height envelope. Importing a compatible `yolov8n-seg.onnx` can improve cup-contour measurement.
- The four tags solve continuous side visibility, bottom-up flips, and handle orientation. They do not automatically measure individual tag-placement errors. Accurate alignment first depends on following the placement, orientation, and height conventions above.
- The current retro-cup FBX is one continuous mesh containing the cup body and handle. The application can align body center, height, diameter, and handle direction, but cannot independently change the handle opening or handle reach. Cups with very different shapes need a parameterized prefab with separate `Body` and `Handle` parts.
- Once tracking is established, the model continues to follow while any tag remains fresh even if YOLO temporarily misses the cup at a large angle. If the cup starts upside down and YOLO cannot recognize it at all, the system will not classify the object from the tag alone.
- This is virtual-model overlay, not pixel-perfect removal and background reconstruction of the real cup. Without segmentation and inpainting, edges of the real cup may remain visible.

## Environment

- Unity 2022.3.62f3
- Universal Render Pipeline 14
- AR Foundation / ARCore / ARKit 5.2
- Unity Sentis 2.1.3
- Android minimum SDK 30
