# Three-Sided AprilTag Cup Demo: On-Device Testing and Troubleshooting

## Pre-Test Checklist

- Use Unity `2022.3.62f3` and the scene `Assets/Scenes/SampleScene.unity`.
- Run `Tools > AR 80s Retro > Configure Build Scene (3-Tag Cup)`, then rebuild the on-device app.
- Use only IDs `0`, `1`, and `2` from `tagStandard41h12`. The active black square must have a side length of `1 cm`, with `tagSizeMeters = 0.01`.
- Viewed from above with the handle at 3 o'clock, place ID `0` at 9 o'clock, ID `1` at 1 o'clock, and ID `2` at 5 o'clock.
- Point the top edge of every Tag toward the cup rim, face every Tag outward, and position each center near the midpoint of the cup body's height. Do not swap IDs `1` and `2`.
- Do not attach a Tag to the bottom of the cup or use ID `3`.
- Start testing in portrait orientation at `15–30 cm`, with even lighting and no reflections.

## Standard Startup Procedure

1. Face ID `0` toward the camera while keeping the entire cup inside the YOLO box.
2. Briefly hold the phone and cup still while waiting for the first valid joint YOLO + Tag observation.
3. Pick up the cup only after `TRACKING CUP (APRILTAG 0)` appears.
4. Slowly rotate the cup about its axis so ID `1` and then ID `2` can take over.

## On-Screen Status

### `WAITING: show cup + AprilTag 0/1/2`

The system has not yet obtained both a valid `cup` detection and any configured Tag. Check the persistent `YOLO RUNNING` diagnostic line on the phone, and make sure at least one complete, sharply focused Tag is visible.

### `APRILTAG FOUND - YOLO NO CUP (SEE SCORE BELOW)`

The Tag pipeline is working, but YOLO has not reached the cup-like threshold. The diagnostic line below shows the `cup-like` score, required threshold, and current best class. For transparent cups, `cup`, `wine glass`, and `bowl` are all treated as cup candidates.

### `CUP FOUND - WAITING FOR APRILTAG 0/1/2`

YOLO has found the cup, but none of the three Tags has a fresh pose. Check that:

- The Tag family is `tagStandard41h12`.
- The ID is `0`, `1`, or `2`.
- The active black square is exactly `1 cm` wide.
- The Tag is flat, sharply focused, and not occluded by a hand.
- The latest app has been rebuilt and installed.

### `CUP + TAG FOUND - INITIALIZING REPLACEMENT`

The system is initializing the replacement model from the first valid cup-like box and Tag distance. There is no need to wait for five frames or scan around the cup.

### `TRACKING CUP (APRILTAG n)`

The model has been created, and `n` identifies the Tag currently providing the pose. Switching IDs continues to use the same cup model and should not create a second instance.

### `APRILTAG HIDDEN - SHORT POSITION FALLBACK`

All three Tags are temporarily invisible. While the YOLO box remains valid, the system corrects position and retains the last rotation for up to approximately `0.8 s`. Full 6DoF tracking resumes when any Tag becomes visible again.

### `CUP LOST - SHOW CUP + APRILTAG 0/1/2`

Both the cup and all three Tags have exceeded the loss grace period. Bring the full cup and any one Tag back into view.

## 360° Axial-Rotation Acceptance Test

1. Initialize with ID `0` and confirm that the physical and virtual handles are on the same side.
2. Slowly rotate the cup about its vertical axis. Tracking should hand off from ID `0` to ID `1` or ID `2`.
3. Continue through a full `360°` rotation. The model should remain a single instance, with no obvious jump in position or handle direction during handoff.
4. Repeat three times, recording dropped frames and position/angle jumps at each handoff.

At the least favorable viewing angle, the three 1 cm Tags may still be seen at an oblique angle of approximately `60°`. If all Tags are lost over part of the rotation, first reduce the distance, increase lighting to support a faster shutter, improve print quality, and verify that the Tags are spaced approximately 120° apart.

## Incorrect Model Position or Orientation

1. **The virtual handle always has a fixed angular offset:** Verify the clock positions and IDs, especially that IDs `1/2` were not swapped.
2. **The model is upside down or lying sideways:** Confirm that the top edge of all three patterns points toward the cup rim. `compensateFrameRotation` must be disabled, and `detectorToCameraRotationCorrectionEuler` must be `(0,0,0)`.
3. **The model center is vertically offset:** Place all three Tags at the same height and align them as closely as possible with the midpoint of the cup body's height.
4. **The model has a fixed tilt:** The Tag fronts must face outward along the radius and must not be mounted crooked on the curved surface.
5. **A flat holder is used:** Enter the gap between the Tag plane and the cup surface in the corresponding `tagMountStandoffMeters` value.
6. **The model size is incorrect:** Run the three-Tag scene setup again, keep the full cup box and one Tag visible together during initialization, and confirm that `yoloScreenHeightFill = 0.96`.

The default Tag-to-cup rotations are ID `0` `(0,-90,0)`, ID `1` `(0,+150,0)`, and ID `2` `(0,+30,0)`. The local cup-center direction for all three is `(0,0,+1)`.

## Physical Limitations

- Three side-mounted Tags work well for rotation about the cup axis and ordinary tilting. When the bottom faces the camera, however, no Tag may be visible, so continuous tracking through a complete flip cannot be guaranteed.
- When all three Tags are occluded, the 2D YOLO box and phone IMU cannot determine how the user's hand rotates the cup; the system can only retain the last pose briefly.
- Detection-only YOLO does not erase pixels belonging to the real cup. Pixel-level replacement requires segmentation and background inpainting.
- The current cup FBX is a continuous mesh and can only be fit as a whole to the cup-body dimensions and handle direction.
