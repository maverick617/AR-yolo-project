using System;
using System.IO;
using System.Linq;
using AR80sRetro;
using Unity.Sentis;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using Object = UnityEngine.Object;

namespace AR80sRetroEditor
{
    public static class AR80sRetroYoloSetup
    {
        private const string SystemObjectName = "AR80sRetro System";
        private const string ModelPath = "Assets/AR80sRetro/Models/YOLO/yolov8n.onnx";
        private const string SegmentationModelPath =
            "Assets/AR80sRetro/Models/YOLO/yolov8n-seg.onnx";
        private const string PrefabLibraryPath = "Assets/AR80sRetro/Retro Prefab Library.asset";
        private const string BuildScenePath = "Assets/Scenes/SampleScene.unity";
        private const float CupTagSizeMeters = 0.01f;

        [MenuItem("Tools/AR 80s Retro/Configure Build Scene (3-Tag Cup)")]
        public static void ConfigureBuildScene()
        {
            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.OpenScene(
                BuildScenePath,
                OpenSceneMode.Single);
            ConfigureScene();
            EditorSceneManager.SaveScene(scene);
        }

        [MenuItem("Tools/AR 80s Retro/Build iOS Player (3-Tag Cup)")]
        public static void BuildIosPlayer()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS
                && !EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.iOS,
                    BuildTarget.iOS))
            {
                throw new InvalidOperationException(
                    "Could not switch the Unity Editor to the iOS build target.");
            }

            ConfigureBuildScene();

            string outputPath = Environment.GetEnvironmentVariable(
                "AR80SRETRO_IOS_BUILD_PATH");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = Path.GetFullPath(Path.Combine(
                    Application.dataPath,
                    "..",
                    "Builds",
                    "iOS-AprilTag"));
            }

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new InvalidOperationException(
                    "No enabled scenes were found in Build Settings.");
            }

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.iOS,
                options = BuildOptions.Development
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"iOS build failed with {report.summary.totalErrors} error(s). "
                    + $"See the Unity Console for details. Output: '{outputPath}'.");
            }

            Debug.Log(
                $"Three-tag Cup iOS build succeeded: '{outputPath}' "
                + $"({report.summary.totalSize} bytes).");
        }

        [MenuItem("Tools/AR 80s Retro/Configure YOLO Scene")]
        public static void ConfigureScene()
        {
            GameObject systemObject = GameObject.Find(SystemObjectName);
            if (systemObject == null)
            {
                systemObject = new GameObject(SystemObjectName);
                Undo.RegisterCreatedObjectUndo(systemObject, "Create AR 80s Retro System");
            }

            ModelAsset modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelPath);
            if (modelAsset == null)
            {
                Debug.LogError($"Cannot load Sentis model at '{ModelPath}'.");
                return;
            }

            ModelAsset segmentationModelAsset =
                AssetDatabase.LoadAssetAtPath<ModelAsset>(SegmentationModelPath);
            if (segmentationModelAsset == null)
            {
                Debug.LogWarning(
                    $"Optional cup segmentation model was not found at '{SegmentationModelPath}'. "
                    + "The demo will use the detector box plus metric AprilTag range until a compatible YOLOv8-seg ONNX is imported.");
            }

            RetroPrefabLibrary prefabLibrary =
                AssetDatabase.LoadAssetAtPath<RetroPrefabLibrary>(PrefabLibraryPath);
            if (prefabLibrary == null)
            {
                Debug.LogError($"Cannot load prefab library at '{PrefabLibraryPath}'.");
                return;
            }

            if (!prefabLibrary.TryGetRule("cup", out RetroReplacementRule cupRule)
                || !cupRule.UseStandardCupTagMount
                || !cupRule.IsAprilTagMatch(0, null)
                || !cupRule.IsAprilTagMatch(1, null)
                || !cupRule.IsAprilTagMatch(2, null)
                || cupRule.IsAprilTagMatch(3, null)
                || !string.Equals(cupRule.AprilTagIdSummary, "0/1/2"))
            {
                Debug.LogError(
                    "Cup rule must contain exactly the three side AprilTag IDs 0/1/2.");
                return;
            }

            if (!ValidateCupMountGeometry(cupRule))
            {
                return;
            }

            LogCupModelFitDiagnostic(cupRule);

            ARCameraManager cameraManager = FindOrCreateARCameraManager();
            ARTrackedImageManager trackedImageManager =
                Object.FindObjectOfType<ARTrackedImageManager>();
            RetroReplacementManager replacementManager =
                GetOrAddComponent<RetroReplacementManager>(systemObject);
            ARRaycastPositionSolver positionSolver =
                Object.FindObjectOfType<ARRaycastPositionSolver>();
            if (positionSolver == null)
            {
                positionSolver = GetOrAddComponent<ARRaycastPositionSolver>(systemObject);
            }
            ARRaycastManager raycastManager = Object.FindObjectOfType<ARRaycastManager>();
            ARPlaneManager planeManager = Object.FindObjectOfType<ARPlaneManager>();

            if (cameraManager == null)
            {
                Debug.LogError("Could not create or find an ARCameraManager.");
                return;
            }

            Undo.RegisterFullObjectHierarchyUndo(systemObject, "Configure AR 80s Retro YOLO");

            ARCameraFrameProvider frameProvider =
                GetOrAddComponent<ARCameraFrameProvider>(systemObject);
            ARDepthFrameProvider depthProvider =
                GetOrAddComponent<ARDepthFrameProvider>(systemObject);
            CupRegistrationProfileStore profileStore =
                GetOrAddComponent<CupRegistrationProfileStore>(systemObject);
            CupDimensionEstimator dimensionEstimator =
                GetOrAddComponent<CupDimensionEstimator>(systemObject);
            YoloObjectDetector detector = GetOrAddComponent<YoloObjectDetector>(systemObject);
            RetroDetectionPipeline pipeline =
                GetOrAddComponent<RetroDetectionPipeline>(systemObject);
            YoloDetectionOverlay overlay =
                GetOrAddComponent<YoloDetectionOverlay>(systemObject);
            AprilTagPoseSource aprilTagPoseSource =
                GetOrAddComponent<AprilTagPoseSource>(systemObject);
            KeijiroAprilTagFrameDetector aprilTagDetector =
                GetOrAddComponent<KeijiroAprilTagFrameDetector>(systemObject);
            Camera arCamera = cameraManager.GetComponent<Camera>();
            // Keep depth acquisition on the system object. ARCameraBackground
            // automatically uses an AROcclusionManager on the camera for render
            // occlusion, which would hide a replacement model behind the real cup.
            AROcclusionManager cameraOcclusionManager =
                cameraManager.GetComponent<AROcclusionManager>();
            if (cameraOcclusionManager != null)
            {
                Undo.DestroyObjectImmediate(cameraOcclusionManager);
            }

            AROcclusionManager occlusionManager =
                GetOrAddComponent<AROcclusionManager>(systemObject);

            AssignObjectReference(frameProvider, "cameraManager", cameraManager);
            AssignFloat(frameProvider, "minimumCaptureIntervalSeconds", 0.08f);
            AssignObjectReference(detector, "modelAsset", modelAsset);
            AssignObjectReference(detector, "segmentationModelAsset", segmentationModelAsset);
            AssignObjectReference(detector, "frameProvider", frameProvider);
            AssignFloat(detector, "inferenceIntervalSeconds", 0.25f);
            AssignFloat(detector, "confidenceThreshold", 0.2f);
            AssignBoolean(detector, "logDetections", false);
            AssignInteger(detector, "diagnosticLogInterval", 20);
            AssignBoolean(detector, "logRuntimeEvaluation", true);
            AssignFloat(detector, "evaluationWindowSeconds", 20f);
            AssignObjectReference(pipeline, "detector", detector);
            AssignObjectReference(pipeline, "replacementManager", replacementManager);
            AssignObjectReference(overlay, "detector", detector);
            AssignFloat(overlay, "holdDetectionSeconds", 0.8f);
            AssignObjectReference(replacementManager, "prefabLibrary", prefabLibrary);
            AssignObjectReference(replacementManager, "positionSolver", positionSolver);
            AssignObjectReference(replacementManager, "aprilTagPoseSource", aprilTagPoseSource);
            AssignObjectReference(replacementManager, "cupProfileStore", profileStore);
            AssignObjectReference(replacementManager, "cupDimensionEstimator", dimensionEstimator);
            AssignObjectReference(replacementManager, "arCamera", arCamera);
            AssignObjectReference(replacementManager, "contentRoot", systemObject.transform);
            AssignBoolean(replacementManager, "preferAprilTagPose", true);
            AssignBoolean(replacementManager, "autoConfigureAprilTagTracking", true);
            AssignBoolean(replacementManager, "requireAprilTagPoseForTaggedRules", true);
            AssignBoolean(replacementManager, "cupDemoOnly", true);
            AssignFloat(replacementManager, "rotationFollowSpeed", 6f);
            AssignFloat(replacementManager, "tagHandoverFreshnessSeconds", 0.32f);
            AssignFloat(replacementManager, "renderPositionDeadbandMeters", 0.0025f);
            AssignFloat(replacementManager, "renderRotationDeadbandDegrees", 1.25f);
            AssignFloat(replacementManager, "yoloScreenHeightFill", 0.96f);
            AssignFloat(replacementManager, "maximumYoloScreenScaleCorrection", 10f);
            AssignBoolean(replacementManager, "logRuntimeEvaluation", true);
            AssignFloat(replacementManager, "evaluationWindowSeconds", 20f);
            AssignObjectReference(positionSolver, "raycastManager", raycastManager);
            AssignObjectReference(positionSolver, "arCamera", arCamera);
            AssignObjectReference(positionSolver, "depthProvider", depthProvider);
            AssignObjectReference(depthProvider, "occlusionManager", occlusionManager);
            AssignObjectReference(depthProvider, "cameraManager", cameraManager);
            AssignObjectReference(depthProvider, "arCamera", arCamera);
            AssignObjectReference(dimensionEstimator, "depthProvider", depthProvider);
            AssignObjectReference(dimensionEstimator, "arCamera", arCamera);
            AssignBoolean(dimensionEstimator, "useEnvironmentDepth", false);
            AssignObjectReference(aprilTagPoseSource, "arCamera", arCamera);
            AssignObjectReference(aprilTagPoseSource, "trackedImageManager", trackedImageManager);
            AssignBoolean(aprilTagPoseSource, "filterPublishedTagPoses", true);
            AssignFloat(aprilTagPoseSource, "positionFilterSpeed", 7f);
            AssignFloat(aprilTagPoseSource, "rotationFilterSpeed", 6f);
            AssignFloat(aprilTagPoseSource, "positionDeadbandMeters", 0.0015f);
            AssignFloat(aprilTagPoseSource, "rotationDeadbandDegrees", 0.8f);
            AssignFloat(aprilTagPoseSource, "maximumSingleFramePositionJumpMeters", 0.12f);
            AssignFloat(aprilTagPoseSource, "maximumSingleFrameRotationJumpDegrees", 55f);
            AssignFloat(aprilTagPoseSource, "filterResetGapSeconds", 0.45f);
            AssignBoolean(aprilTagPoseSource, "logRuntimeEvaluation", true);
            AssignFloat(aprilTagPoseSource, "evaluationWindowSeconds", 20f);
            AssignObjectReference(aprilTagDetector, "frameProvider", frameProvider);
            AssignObjectReference(aprilTagDetector, "poseSource", aprilTagPoseSource);
            AssignObjectReference(aprilTagDetector, "arCamera", arCamera);
            AssignFloat(aprilTagDetector, "tagSizeMeters", CupTagSizeMeters);
            AssignFloat(aprilTagDetector, "detectionIntervalSeconds", 0.1f);
            AssignInteger(aprilTagDetector, "decimation", 1);
            AssignBoolean(aprilTagDetector, "logDetectedTags", false);
            AssignBoolean(aprilTagDetector, "logRuntimeEvaluation", true);
            AssignFloat(aprilTagDetector, "evaluationWindowSeconds", 20f);
            // ARCameraFrameProvider already rotates the CPU image into the
            // portrait detector texture. Applying frameRotation again to the
            // returned pose makes an upright side Tag publish a horizontal +Y.
            AssignBoolean(aprilTagDetector, "compensateFrameRotation", false);
            AssignVector3(
                aprilTagDetector,
                "detectorToCameraRotationCorrectionEuler",
                Vector3.zero);

            DisableIfPresent<MockDetectionInput>(systemObject);
            DisableIfPresent<CameraFrameSmokeTest>(systemObject);
            depthProvider.enabled = false;
            occlusionManager.enabled = false;
            if (planeManager != null)
            {
                planeManager.enabled = false;
                EditorUtility.SetDirty(planeManager);
            }
            EditorUtility.SetDirty(depthProvider);
            EditorUtility.SetDirty(occlusionManager);

            EditorUtility.SetDirty(systemObject);
            EditorSceneManager.MarkSceneDirty(systemObject.scene);
            Selection.activeGameObject = systemObject;

            Debug.Log("Three-tag Cup scene configuration completed. Side Tag 0/1/2 share one stabilized cup track with handover hysteresis, per-Tag pose filtering and render deadbands; transparent cup/wine-glass/bowl detections feed the cup rule; LiDAR depth, plane visualization, mock input and render occlusion are disabled. Save the scene before building.");
        }

        private static bool ValidateCupMountGeometry(RetroReplacementRule cupRule)
        {
            int[] tagIds = { 0, 1, 2 };
            Quaternion[] installedTagRotations =
            {
                // Common cup frame: +Y points to the rim and +X points to the handle.
                // Keijiro Tag +Z points through the printed front into the object.
                // The three side Tags are mounted at 9, 1 and 5 o'clock, so their
                // local +Z axes point radially inward.
                Quaternion.LookRotation(Vector3.right, Vector3.up),
                Quaternion.LookRotation(
                    new Vector3(-0.5f, 0f, -0.8660254f),
                    Vector3.up),
                Quaternion.LookRotation(
                    new Vector3(-0.5f, 0f, 0.8660254f),
                    Vector3.up)
            };
            Vector3 measuredSize = new Vector3(0.08f, 0.12f, 0.08f);
            Vector3[] installedTagPositions =
            {
                Vector3.left * 0.04f,
                new Vector3(0.5f, 0f, 0.8660254f) * 0.04f,
                new Vector3(0.5f, 0f, -0.8660254f) * 0.04f
            };

            for (int i = 0; i < tagIds.Length; i++)
            {
                Quaternion recoveredCupRotation = installedTagRotations[i]
                    * cupRule.GetAprilTagToObjectRotation(tagIds[i], null);
                float errorDegrees = Quaternion.Angle(
                    recoveredCupRotation,
                    Quaternion.identity);
                if (errorDegrees > 0.1f)
                {
                    Debug.LogError(
                        $"AprilTag {tagIds[i]} mount does not recover the common cup frame "
                        + $"(rotation error {errorDegrees:F2} degrees). Check the configured "
                        + "mount Euler angles and the documented image-top direction.");
                    return false;
                }

                Vector3 recoveredCupCenter = installedTagPositions[i]
                    + installedTagRotations[i]
                        * cupRule.GetAprilTagToCupCenterOffset(
                            tagIds[i],
                            null,
                            measuredSize);
                float centerErrorMeters = recoveredCupCenter.magnitude;
                if (centerErrorMeters > 0.0001f)
                {
                    Debug.LogError(
                        $"AprilTag {tagIds[i]} mount does not recover the cup center "
                        + $"(position error {centerErrorMeters * 100f:F2} cm). Check "
                        + "whether this mount must use body radius or half cup height.");
                    return false;
                }
            }

            Debug.Log(
                "Cup mount geometry validated: side Tag 0/1/2 all recover the "
                + "same upright, handle-aligned cup frame.");
            return true;
        }

        private static ARCameraManager FindOrCreateARCameraManager()
        {
            ARCameraManager cameraManager = Object.FindObjectOfType<ARCameraManager>();
            if (cameraManager != null)
            {
                EnsureARManagers(cameraManager.GetComponent<Camera>());
                return cameraManager;
            }

            ARSession session = Object.FindObjectOfType<ARSession>();
            if (session == null)
            {
                GameObject sessionObject = new GameObject("AR Session");
                Undo.RegisterCreatedObjectUndo(sessionObject, "Create AR Session");
                session = Undo.AddComponent<ARSession>(sessionObject);
            }

            XROrigin origin = Object.FindObjectOfType<XROrigin>();
            if (origin == null)
            {
                GameObject originObject = new GameObject("XR Origin (AR)");
                Undo.RegisterCreatedObjectUndo(originObject, "Create XR Origin");
                origin = Undo.AddComponent<XROrigin>(originObject);
            }

            GameObject cameraOffset = origin.CameraFloorOffsetObject;
            if (cameraOffset == null)
            {
                cameraOffset = new GameObject("Camera Offset");
                Undo.RegisterCreatedObjectUndo(cameraOffset, "Create Camera Offset");
                cameraOffset.transform.SetParent(origin.transform, false);
                origin.CameraFloorOffsetObject = cameraOffset;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = Object.FindObjectOfType<Camera>();
            }

            if (camera == null)
            {
                GameObject cameraObject = new GameObject("AR Camera");
                Undo.RegisterCreatedObjectUndo(cameraObject, "Create AR Camera");
                cameraObject.transform.SetParent(cameraOffset.transform, false);
                camera = Undo.AddComponent<Camera>(cameraObject);
            }
            else if (!camera.transform.IsChildOf(origin.transform))
            {
                Undo.SetTransformParent(camera.transform, cameraOffset.transform, "Parent Camera To XR Origin");
                camera.transform.localPosition = Vector3.zero;
                camera.transform.localRotation = Quaternion.identity;
            }

            camera.gameObject.name = "AR Camera";
            camera.tag = "MainCamera";
            origin.Camera = camera;

            cameraManager = GetOrAddComponent<ARCameraManager>(camera.gameObject);
            GetOrAddComponent<ARCameraBackground>(camera.gameObject);
            EnsureARManagers(camera);

            EditorUtility.SetDirty(session);
            EditorUtility.SetDirty(origin);
            EditorUtility.SetDirty(camera);
            return cameraManager;
        }

        private static void EnsureARManagers(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            XROrigin origin = Object.FindObjectOfType<XROrigin>();
            if (origin == null)
            {
                return;
            }

            GetOrAddComponent<ARRaycastManager>(origin.gameObject);
            GetOrAddComponent<ARPlaneManager>(origin.gameObject);
        }

        private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
        {
            T component = gameObject.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(gameObject);
        }

        private static void AssignObjectReference(
            Object target,
            string propertyName,
            Object value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            property.objectReferenceValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignFloat(
            Object target,
            string propertyName,
            float value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            serializedObject.FindProperty(propertyName).floatValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignInteger(
            Object target,
            string propertyName,
            int value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            serializedObject.FindProperty(propertyName).intValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignBoolean(
            Object target,
            string propertyName,
            bool value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            serializedObject.FindProperty(propertyName).boolValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignVector3(
            Object target,
            string propertyName,
            Vector3 value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            serializedObject.FindProperty(propertyName).vector3Value = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void LogCupModelFitDiagnostic(RetroReplacementRule cupRule)
        {
            GameObject wrapper = new GameObject("Cup Fit Diagnostic");
            wrapper.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                GameObject visual = Object.Instantiate(
                    cupRule.Prefab,
                    wrapper.transform,
                    false);
                CupModelFitter fitter = wrapper.AddComponent<CupModelFitter>();
                bool fitted = fitter.InitializeAndFit(
                    visual,
                    new Vector3(0.08f, 0.12f, 0.08f),
                    cupRule.FitMeasuredWidthAndHeight,
                    cupRule.MaximumNonUniformScaleRatio);
                Bounds bounds = CalculateRendererBounds(visual);
                Debug.Log(
                    $"Cup fit diagnostic: success={fitted}, "
                    + $"target=(0.08, 0.12, 0.08)m, rendererBounds={bounds.size}.");
            }
            finally
            {
                Object.DestroyImmediate(wrapper);
            }
        }

        private static Bounds CalculateRendererBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (!hasBounds)
                {
                    bounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }

            return bounds;
        }

        private static void DisableIfPresent<T>(GameObject gameObject) where T : Behaviour
        {
            T component = gameObject.GetComponent<T>();
            if (component != null)
            {
                component.enabled = false;
                EditorUtility.SetDirty(component);
            }
        }
    }
}
