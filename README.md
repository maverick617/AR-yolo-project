# ARm YOLO-seg Mask Pose

This is a standalone new implementation available from the desktop as
`ARm-yoloseg-maskpose`. It does not depend on AprilTag, Vuforia,
FoundationPose, or XRTracker, nor does it require an exact CAD model of the
physical object.

The current runtime pipeline is:

1. YOLOv8n-seg outputs the class, bounding box, and instance mask from an RGB
   image.
2. iPhone/iPad LiDAR depth is sampled only within the mask, while the median and
   median absolute deviation (MAD) are used to reject hand and background points.
3. Depending on the category, axial-symmetry, planar, or world-upright rules
   estimate a category-level position, orientation, and size from the point cloud.
4. Adaptive temporal filtering reduces position, rotation, and size jitter.
5. Visible pixels around the mask provide real-time local background inpainting.
6. An existing retro model is placed at the same position; a built-in procedural
   replacement is used if the original model is missing.

## Opening the project

- Recommended Unity version: `6000.0.76f1`
- In Unity Hub, select `Add > Add project from disk`, then choose the entire
  `/Users/zhiyue/Desktop/ARm-yoloseg-maskpose` folder.
- This desktop entry links to `~/Projects/ARm-yoloseg-maskpose` to prevent iCloud
  from turning the ONNX model and Unity PackageCache into cloud-only placeholder
  files. Do not open the desktop folder named
  `ARm-yoloseg-maskpose-icloud-backup` instead.
- Open the scene at `Assets/Scenes/SampleScene.unity`.
- If the scene is corrupted or components are missing, run:
  `Tools > AR 80s Retro > Prepare Build Scene (YOLO-seg Mask Pose)`
- Target platform: a LiDAR-capable iPhone/iPad. Build the iOS app through Xcode.

## Generated iOS project

The Unity iOS export completed successfully. The project is located at
`Builds/iOS/Unity-iPhone.xcodeproj`.

To continue:

1. Double-click `Unity-iPhone.xcodeproj`.
2. In Xcode, select `Unity-iPhone > Signing & Capabilities`.
3. Select your own Apple Team. The Bundle ID is already set to
   `com.zhiyue.armmaskpose`.
4. Connect a LiDAR-capable iPhone, select the physical device, and click Run.
5. For the first test, use only one 550 ml water bottle in a well-lit environment
   with a clearly textured background.

After installation, the app runs locally on the phone and does not need to
remain connected to the computer. A connection is required only for
installation, log inspection, and rebuilding.

For the initial validation, test only a 550 ml water bottle. The currently
supported COCO category mappings are:
`bottle`, `cup`, `cell phone -> phone`, `tv`, `chair`,
`couch`, `potted plant -> plant`, and `dining table -> table`.

## Current limitations

- This is an approximate category-level pose, not exact CAD-level 6DoF. The
  rotation around the intrinsic axis of rotationally symmetric objects such as
  bottles cannot be determined reliably from their shape.
- A single camera frame cannot reveal the background genuinely hidden by a
  physical object. The current background reconstruction uses real-time local
  inpainting, so complex textures, large objects, or rapid motion may produce
  stretching or blur.
- The system currently retains one best instance per category. Tracking multiple
  objects of the same category simultaneously requires instance-association IDs.
- The Unity Editor has no iPhone LiDAR data, so the final result must be tested
  on a physical device.

Current status: automated Unity scene validation and Unity iOS/IL2CPP project
export have passed. The YOLO frame rate, depth orientation, and actual occlusion
behavior have not yet been validated on a physical device.

See `Assets/AR80sRetro/README.md` for detailed technical documentation and
`Tools/YOLO_SETUP.md` for model export instructions.
