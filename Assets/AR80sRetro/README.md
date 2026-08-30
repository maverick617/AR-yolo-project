# AR80sRetro: YOLO-seg mask-pose pipeline

This project no longer depends on AprilTag, Vuforia, XRTracker, or an exact CAD
model of the physical object. The active runtime path is:

1. `YoloObjectDetector` runs `yolov8n-seg.onnx` and returns a class, box, and
   per-instance silhouette mask.
2. `MaskDepthPointCloudProvider` samples ARKit environment depth only inside
   that mask. `ARDepthFrameProvider` rejects hand/background depth outliers with
   a median and median-absolute-deviation cluster.
3. `CategoryPoseEstimator` uses stable category rules and physical-size priors.
   A 550 ml bottle is calibrated to about 0.23 x 0.068 m, while a phone is
   calibrated to about 0.16 x 0.075 x 0.009 m. Transparent/glossy targets reject
   LiDAR depth when it conflicts with silhouette-derived distance.
4. `AdaptivePoseFilter` suppresses position, rotation, and scale jitter while
   increasing responsiveness during deliberate motion. A single large depth
   jump must be confirmed by the next detector result; deadbands and per-update
   speed limits prevent background/hand samples from teleporting the model.
5. `MaskBackgroundReconstructionController` sends the detected silhouette to a
   URP reconstruction pass, and `MaskPoseReplacementController` fits the existing
   retro prefab to the measured category-level size at the same pose.

## Open and run

- Unity: `6000.0.76f1`
- Scene: `Assets/Scenes/SampleScene.unity`
- Menu repair/setup command:
  `Tools > AR 80s Retro > Prepare Build Scene (YOLO-seg Mask Pose)`
- Target device: LiDAR-capable iPhone/iPad. Build through Xcode as an iOS AR app.

The first validation target is a 550 ml bottle. The active `bottle` rule uses
`Models/Retro/Retro Beer Bottle.prefab`, generated from the downloaded
`beer_bottle.glb`. The source was converted to OBJ locally so the iOS build does
not need a glTF runtime package. The point-cloud provider processes the best
detection per configured category. Runtime procedural fallback has been removed:
every replacement must have one explicit prefab, preventing duplicate visuals.

Replacement scale is now derived from each prefab's renderer bounds with no
centimeter-scale lower clamp. The old `0.01` minimum was incorrect for source
meshes authored in tens of model units: the beer-bottle mesh needs a scale near
`0.00485` to become 0.23 m tall. Bottle, phone, cup, and pen replacements use
fixed physical dimensions rather than allowing one noisy depth frame to resize
the visible model.

Model attribution is recorded in
`Models/Retro/Beer Bottle LICENSE.txt` (CC BY 4.0).

Additional desktop models are deployed as follows:

- COCO `phone` -> `Retro Nokia Phone.prefab`;
- COCO `cup` -> an explicit `Retro Mug.prefab` generated in the editor from
  built-in meshes and saved as a normal project asset; the incorrect Sports
  Bottle mapping remains removed;
- `Retro Pen.prefab` is included and registered as `pen`, but the bundled
  standard COCO YOLOv8-seg model has no pen class. A custom segmentation model
  with a `pen` output is required before that prefab can be triggered reliably.

The detector currently emits `bottle,cup,phone`. This keeps unrelated furniture
detections out of the depth/reconstruction path while allowing all supported
desktop retro prefabs to run.

Rotation behavior is category-specific: `bottle` is intentionally locked
world-upright. `phone` always presents its configured model front toward the
camera and follows only the mask's stable long-axis roll, because standard COCO
YOLO has no semantic front/back output. Cup and pen axes also retain sign
continuity and an angular rate limit to prevent contour noise and 180-degree
visual flips. The replacement controller interpolates every display frame
between low-frequency detector poses, so a new YOLO result no longer appears as
a visible transform step. Scale changes use a slower path than position changes,
which keeps hand motion responsive without making the model pulse in size.

Performance work includes asynchronous YOLO GPU readback, persistent camera
conversion memory, one LiDAR acquisition per best category, a 256 px removal
mask, bounded mask rasterization, and an 8-step reconstruction shader. iOS is
built without Development mode overhead.

## Important limitation

The reconstruction pass performs fast local image inpainting from visible pixels
around the mask. A single live RGB frame cannot reveal the genuinely hidden
background. Large objects, complex backgrounds, or rapid camera motion may show
blur/stretch artifacts. A later research version can replace this module with a
temporal background mosaic or neural inpainting without changing the mask-pose
interfaces.

The removal mask now subscribes directly to YOLO segmentation rather than waiting
for a successful LiDAR pose. All current instance masks are combined, padded, and
reconstructed after the AR camera background but before opaque virtual models.
ARKit environment depth remains enabled for measurement while its automatic
occlusion rendering is disabled, preventing the physical target depth from being
drawn over the replacement.

The current runtime tracks the best recent instance per COCO label. Supporting
multiple same-class objects requires adding instance association IDs to the
temporal-filter dictionary.

If a transparent hand-held bottle yields too few LiDAR points, the point-cloud
provider now emits a mask-only observation. The category estimator then derives
distance from the known 0.23 m bottle height and current silhouette. Therefore
the replacement continues to follow the YOLO mask instead of freezing at the
last reliable table-top depth pose.
