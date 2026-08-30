# Three-AprilTag Cup AR Replacement Experiment

This project handles a single class: `cup`. YOLO confirms the presence of a cup and estimates its dimensions, while three AprilTags (IDs `0`, `1`, and `2`) rigidly attached to the sides of the same cup provide 6DoF poses. The three Tag poses are transformed into a shared coordinate frame defined by the cup-body center and handle direction, so the system ultimately displays only one retro cup model.

## Approach

```text
AR Camera
  ├─ YOLO cup-like detection ─> 2D box / class / size
  └─ AprilTag 0/1/2 ─────> 6DoF tag pose
                                │
                 known rigid Tag-to-cup transform
                                │
                                v
                    common cup-center pose
                                │
                                v
                      retro cup replacement
```

- YOLO verifies that the target in the camera view is a cup, preventing the model from appearing when only an isolated Tag is visible. For transparent cups, the COCO `cup`, `wine glass`, and `bowl` classes are all normalized into `cup` candidates for this experiment.
- Cup height and body diameter are initialized on the first frame that contains both a valid cup-like bounding box and any valid Tag. The system no longer waits for five frames, and no scan around the cup is required.
- AprilTag tracking provides translation, rotation, tilt, and handoff between Tags. A complete pose can be updated whenever any configured Tag is visible.
- All three Tags share one track, so switching IDs does not create a second cup model.
- If all Tags temporarily disappear, the system uses the YOLO box to correct position while retaining the last rotation for up to approximately `0.8 s`. A 2D box cannot recover unknown rotations caused by the user moving the cup while the Tags are occluded.
- This AprilTag control experiment disables LiDAR depth and AR plane visualization. Metric dimensions are estimated jointly from Tag distance and the YOLO box.

## Physical Setup of the Three Tags

Use IDs `0`, `1`, and `2` from the AprilRobotics `tagStandard41h12` family. The **active black square** of each pattern must have a side length of `1 cm`; the outer white border is not included. The scene's `tagSizeMeters` value must be `0.01`.

Viewed from above, with the physical handle pointing toward 3 o'clock:

```text
                         12 o'clock
                              ID 1 (1 o'clock)

 ID 0 (9 o'clock)       cup-body center       handle (3 o'clock)

                              ID 2 (5 o'clock)
                          6 o'clock
```

Mounting requirements:

1. Place ID `0` at 9 o'clock, directly opposite the handle; ID `1` at 1 o'clock; and ID `2` at 5 o'clock. Do not swap IDs `1` and `2`.
2. Position the centers of all three Tags near the midpoint of the cup body's height and within the same horizontal band.
3. Face each Tag outward along the cup's radius, with the top edge of every pattern pointing toward the rim.
4. Mount the Tags rigidly and keep them as flat as possible. If the cup is noticeably curved, use a small flat holder and enter its thickness in `tagMountStandoffMeters`.
5. Do not use ID `3` or attach a Tag to the bottom of the cup.

The current Tag-to-cup rotation configuration is:

| Tag | Mount position | `tagToObjectRotationEuler` |
|---|---|---|
| 0 | 9 o'clock | `(0, -90, 0)` |
| 1 | 1 o'clock | `(0, 150, 0)` |
| 2 | 5 o'clock | `(0, 30, 0)` |

Keijiro AprilTag's local `+Z` axis points through the back of the pattern toward the cup center, so the cup-center direction for all three mounts is `(0, 0, +1)`. The mount positions, IDs, pattern top-edge directions, and rotations above form a single convention. Changing any one of them introduces a fixed position or handle-angle error.

## Unity Setup and On-Device Testing

1. Open the project in Unity `2022.3.62f3`.
2. Open the only build scene: `Assets/Scenes/SampleScene.unity`.
3. Run `Tools > AR 80s Retro > Configure Build Scene (3-Tag Cup)`. This command validates the geometry of the three mounts, connects the camera/YOLO/AprilTag components, disables LiDAR and AR plane visualization, and saves the scene.
4. Confirm that Build Settings contains only `Assets/Scenes/SampleScene.unity`.
5. Run `Tools > AR 80s Retro > Build iOS Player (3-Tag Cup)`. By default, the Xcode project is generated in `Builds/iOS-AprilTag`; you may also continue to use Unity's standard iOS build workflow.
6. Use Xcode to install the app on an ARKit-compatible iPhone. Start in portrait orientation and test at a distance of approximately `15–30 cm`.
7. Initially, keep the entire cup and ID `0` in view and hold still for about `1–2 s`. After the status changes to `TRACKING CUP`, slowly rotate the cup toward ID `1` and ID `2`.

Core dependencies:

- AR Foundation / ARKit `5.2.0`
- Unity Sentis `2.1.3`
- `jp.keijiro.apriltag` `1.0.3`
- `Assets/AR80sRetro/Models/YOLO/yolov8n.onnx`

## On-Screen Status

- `WAITING: show cup + AprilTag 0/1/2`: The system has not yet received both a cup detection and a valid Tag.
- `YOLO RUNNING ... cup-like=...`: A persistent diagnostic line on the phone showing the inference count, cup-like score, threshold, and current best COCO class.
- `APRILTAG FOUND - YOLO NO CUP (SEE SCORE BELOW)`: Tag tracking is working, but the current cup-like score has not reached the threshold.
- `CUP FOUND - WAITING FOR APRILTAG 0/1/2`: YOLO can see the cup, but none of the three Tags has a fresh pose.
- `CUP + TAG FOUND - INITIALIZING REPLACEMENT`: The first valid joint observation is creating the replacement model.
- `TRACKING CUP (APRILTAG n)`: Tracking is active; `n` is the ID currently in use.
- `APRILTAG HIDDEN - SHORT POSITION FALLBACK`: The system briefly corrects position while retaining the last rotation.
- `CUP LOST - SHOW CUP + APRILTAG 0/1/2`: The loss grace period has expired, so the cup and any one of the Tags must become visible again.

## Suggested Experiments

With the printed Tag size, lighting, and phone held constant, test the following separately:

1. **Yaw handoff**: Starting from ID `0`, rotate the cup through `0–360°` about its axis. Record position/angle jumps and dropped frames when switching from ID `0→1→2`.
2. **Hand occlusion**: Occlude one and then two Tags, recording stability while one Tag remains visible. Then fully occlude all three Tags and measure the fallback duration.
3. **Distance and viewing angle**: Record detection rate and pose jitter at `15/20/25/30 cm` and at head-on, `30°`, `45°`, and `60°` oblique views.
4. **Fast motion**: Compare loss rates while stationary, rotating slowly, and rotating quickly, with particular attention to motion blur on the 1 cm Tags.

Before each experiment, keep ID `0` and the full cup bounding box visible together. Wait until the screen shows `TRACKING CUP` before beginning the occlusion or rotation test.

## Known Limitations

- The three side Tags primarily cover rotation around the cup's vertical axis. Because there is no Tag on the bottom, all three side Tags may be invisible when the bottom faces the camera directly; continuous 6DoF tracking cannot be guaranteed in this orientation.
- The 1 cm Tags are sensitive to focus distance, reflections, print quality, and motion blur. No vision algorithm can recover a pose from a Tag while it is completely covered by a hand.
- The current `yolov8n.onnx` is a detection model, not a segmentation model. It cannot erase the real cup at the pixel level; complete replacement still requires segmentation and background inpainting.
- The current cup FBX is a continuous mesh. The system can match the cup-body center, height, diameter, and handle direction, but it cannot independently adjust the real handle's opening or projection length.

For detailed on-device status information and troubleshooting, see [CUP_DEMO_SETUP_ZH.md](Assets/AR80sRetro/CUP_DEMO_SETUP_ZH.md).
