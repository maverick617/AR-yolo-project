# AR80sRetro: Three-AprilTag Cup Implementation

## Runtime Pipeline

```text
ARCameraFrameProvider
    ├── YoloObjectDetector ───────────── cup bounding box/confidence
    ├── ARDepthFrameProvider ─────────── optional metric depth
    └── KeijiroAprilTagFrameDetector ── raw 6DoF for Tag 0/1/2
                                              │
AprilTagPoseSource <──────────────────────────┘
    │ select the freshest Tag matching the cup box
    v
RetroReplacementManager
    ├── CupDimensionEstimator ── median dimensions from 5 frames
    ├── RetroReplacementRule ─── Tag-to-shared-cup transform
    └── CupModelFitter ───────── body diameter/height/handle direction
```

The current `Retro Prefab Library.asset` contains a single `cup` rule: ID `0` is the primary Tag, and IDs `1/2` are additional Tags. All three Tags are mounted on the sides of the cup body, and each Tag's pose is transformed into the same cup-body center and handle direction. The same `TrackedReplacement` is reused when switching between IDs.

## Three Rigid Mounts

| ID | Position on cup (handle at 3 o'clock) | Pattern top edge | Tag-to-cup Euler angles |
|---|---|---|---|
| 0 | 9 o'clock | Toward the rim | `(0,-90,0)` |
| 1 | 1 o'clock | Toward the rim | `(0,+150,0)` |
| 2 | 5 o'clock | Toward the rim | `(0,+30,0)` |

The centers of all three Tags are near the midpoint of the cup body's height, and their front faces point outward along the radius. The active black square has a side length of `0.01 m`. The Keijiro Tag's local `+Z` axis points from the front of the pattern through its back, so the default is `tagToCupCenterDirection = (0,0,+1)`. The center distance is half the measured cup-body diameter plus the mount standoff.

The model pose is:

```text
cupRotation = tagRotation * configuredTagToCupRotation
cupPosition = tagPosition
            + tagRotation * configuredTagToCupCenterOffset
```

After the full cup box and any Tag are first visible, the dimension estimator collects `cupRegistrationSamples = 5` valid samples and takes their median. Once the dimensions are locked, the Tag can continue to provide the pose independently; a temporary loss of YOLO detection does not immediately destroy the model.

## Key Files

- `Scripts/KeijiroAprilTagFrameDetector.cs`: Detects AprilTags in camera CPU images and publishes world-space poses.
- `Scripts/AprilTagPoseSource.cs`: Caches fresh poses, filters for IDs allowed by the rule, and associates them with the YOLO cup box.
- `Scripts/RetroReplacementRule.cs`: Describes the transforms from the three rigid mounts into the shared cup coordinate frame.
- `Scripts/RetroReplacementManager.cs`: Manages initialization, Tag handoff, short-term fallback, and the single-model lifecycle.
- `Scripts/CupDimensionEstimator.cs`: Combines the YOLO box, depth, and camera projection to estimate metric dimensions.
- `Scripts/CupModelFitter.cs`: Fits the retro cup model to the measured cup height and body diameter.
- `Editor/AR80sRetroYoloSetup.cs`: Validates mount geometry and automatically connects the build scene.

## Scene Setup

In Unity `2022.3.62f3`, run:

```text
Tools > AR 80s Retro > Configure Build Scene (3-Tag Cup)
```

The setup tool will:

- Verify that IDs `0/1/2` all resolve to the same cup coordinate frame and reject ID `3` in the active rule.
- Set `tagSizeMeters = 0.01` and `decimation = 1`.
- Set `compensateFrameRotation = false` and use zero manual rotation correction.
- Connect the AR camera, YOLO, depth, AprilTag, dimension-estimation, and replacement-manager components.
- Disable mock input and camera-render occlusion while retaining depth measurement on the system object.

For on-device steps and troubleshooting, see `CUP_DEMO_SETUP_ZH.md`.
