using AR80sRetro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;

namespace AR80sRetroEditor
{
    public static class AR80sRetroMaskPoseSetup
    {
        private const string SystemObjectName = "AR80sRetro Mask Pose System";
        private const string SegmentationModelPath =
            "Assets/AR80sRetro/Models/YOLO/yolov8n-seg.onnx";
        private const string PrefabLibraryPath =
            "Assets/AR80sRetro/Retro Prefab Library.asset";
        private const string BuildScenePath = "Assets/Scenes/SampleScene.unity";
        private const string RendererDataPath =
            "Assets/Settings/URP-Performant-Renderer.asset";
        private const string InpaintShaderPath =
            "Assets/AR80sRetro/Shaders/MaskBackgroundReconstruction.shader";
        private const string BottleModelSourcePath =
            "Assets/AR80sRetro/Models/Retro/Beer Bottle.obj";
        private const string BottlePrefabPath =
            "Assets/AR80sRetro/Models/Retro/Retro Beer Bottle.prefab";
        private const string BottleGlassMaterialPath =
            "Assets/AR80sRetro/Models/Retro/Retro Bottle Glass.mat";
        private const string BottleLiquidMaterialPath =
            "Assets/AR80sRetro/Models/Retro/Retro Bottle Liquid.mat";
        private const string BottleCapMaterialPath =
            "Assets/AR80sRetro/Models/Retro/Retro Bottle Cap.mat";
        private const string PhoneModelSourcePath =
            "Assets/AR80sRetro/Models/Retro/Old_Nokia_Phone_Low_Poly.obj";
        private const string PhonePrefabPath =
            "Assets/AR80sRetro/Models/Retro/Retro Nokia Phone.prefab";
        private const string PenModelSourcePath =
            "Assets/AR80sRetro/Models/Retro/Old_Pen.obj";
        private const string PenPrefabPath =
            "Assets/AR80sRetro/Models/Retro/Retro Pen.prefab";
        private const string CupPrefabPath =
            "Assets/AR80sRetro/Models/Retro/Retro Mug.prefab";
        private const string CupBodyMaterialPath =
            "Assets/AR80sRetro/Models/Retro/Retro Mug Body.mat";
        private const string CupRimMaterialPath =
            "Assets/AR80sRetro/Models/Retro/Retro Mug Rim.mat";
        private const string CupInteriorMaterialPath =
            "Assets/AR80sRetro/Models/Retro/Retro Mug Interior.mat";

        [MenuItem("Tools/AR 80s Retro/Prepare Build Scene (YOLO-seg Mask Pose)")]
        public static void PrepareBuildScene()
        {
            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.OpenScene(
                BuildScenePath,
                OpenSceneMode.Single);
            PrepareCurrentScene();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        public static void ValidateBuildScene()
        {
            EditorSceneManager.OpenScene(BuildScenePath, OpenSceneMode.Single);
            bool valid = true;
            Unity.InferenceEngine.ModelAsset segmentationModel =
                AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(
                    SegmentationModelPath);
            if (segmentationModel == null)
            {
                Debug.LogError($"Validation failed: cannot import {SegmentationModelPath}.");
                valid = false;
            }

            RetroPrefabLibrary prefabLibrary =
                AssetDatabase.LoadAssetAtPath<RetroPrefabLibrary>(PrefabLibraryPath);
            if (prefabLibrary == null
                || !prefabLibrary.TryGetRule("cup", out RetroReplacementRule cupRule)
                || cupRule.Prefab == null)
            {
                Debug.LogError(
                    "Validation failed: COCO cup has no explicit replacement prefab.");
                valid = false;
            }

            GameObject systemObject = GameObject.Find(SystemObjectName);
            if (systemObject == null)
            {
                Debug.LogError($"Validation failed: scene has no '{SystemObjectName}'.");
                valid = false;
            }
            else
            {
                valid &= RequireComponent<ARCameraFrameProvider>(systemObject);
                valid &= RequireComponent<ARDepthFrameProvider>(systemObject);
                valid &= RequireComponent<YoloObjectDetector>(systemObject);
                valid &= RequireComponent<MaskDepthPointCloudProvider>(systemObject);
                valid &= RequireComponent<CategoryPoseEstimator>(systemObject);
                valid &= RequireComponent<MaskPoseReplacementController>(systemObject);

                YoloObjectDetector detector = systemObject.GetComponent<YoloObjectDetector>();
                if (detector != null)
                {
                    SerializedProperty property = new SerializedObject(detector)
                        .FindProperty("segmentationModelAsset");
                    if (property == null || property.objectReferenceValue == null)
                    {
                        Debug.LogError(
                            "Validation failed: YoloObjectDetector has no segmentation model assigned.");
                        valid = false;
                    }
                }
            }

            Camera arCamera = Camera.main;
            if (arCamera == null
                || arCamera.GetComponent<ARCameraManager>() == null
                || arCamera.GetComponent<AROcclusionManager>() == null
                || arCamera.GetComponent<MaskBackgroundReconstructionController>() == null)
            {
                Debug.LogError(
                    "Validation failed: Main AR Camera is missing camera, LiDAR, or reconstruction components.");
                valid = false;
            }

            UniversalRendererData rendererData =
                AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererDataPath);
            bool hasReconstructionFeature = false;
            if (rendererData != null)
            {
                for (int i = 0; i < rendererData.rendererFeatures.Count; i++)
                {
                    if (rendererData.rendererFeatures[i]
                        is MaskBackgroundReconstructionFeature feature
                        && feature.isActive)
                    {
                        hasReconstructionFeature = true;
                        break;
                    }
                }
            }

            if (!hasReconstructionFeature)
            {
                Debug.LogError(
                    "Validation failed: URP mask background reconstruction feature is not active.");
                valid = false;
            }

            if (!valid)
            {
                throw new System.InvalidOperationException(
                    "YOLO-seg mask-pose scene validation failed. See errors above.");
            }

            Debug.Log(
                "MASKPOSE VALIDATION PASSED: model, scene pipeline, ARKit depth and URP "
                + "mask reconstruction are connected.");
        }

        public static void BuildIosDevelopment()
        {
            string[] scenes = System.Array.ConvertAll(
                System.Array.FindAll(
                    EditorBuildSettings.scenes,
                    scene => scene.enabled),
                scene => scene.path);
            if (scenes.Length == 0)
            {
                throw new System.InvalidOperationException(
                    "No enabled scene is present in Build Settings.");
            }

            string buildPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "../Builds/iOS"));
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = buildPath,
                target = BuildTarget.iOS,
                options = BuildOptions.None
            };
            UnityEditor.Build.Reporting.BuildReport report =
                BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new System.InvalidOperationException(
                    $"iOS development build failed: {report.summary.result}.");
            }

            Debug.Log(
                $"MASKPOSE IOS RELEASE BUILD PASSED: {buildPath}, "
                + $"size={report.summary.totalSize} bytes.");
        }

        [MenuItem("Tools/AR 80s Retro/Prepare Current Scene (YOLO-seg Mask Pose)")]
        public static void PrepareCurrentScene()
        {
            Unity.InferenceEngine.ModelAsset segmentationModel =
                AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(
                    SegmentationModelPath);
            RetroPrefabLibrary prefabLibrary =
                AssetDatabase.LoadAssetAtPath<RetroPrefabLibrary>(PrefabLibraryPath);
            GameObject bottlePrefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(BottlePrefabPath);
            GameObject phonePrefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(PhonePrefabPath);
            GameObject penPrefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(PenPrefabPath);
            GameObject cupPrefab = EnsureRetroCupPrefab();
            Shader inpaintShader = AssetDatabase.LoadAssetAtPath<Shader>(InpaintShaderPath);
            if (segmentationModel == null
                || prefabLibrary == null
                || bottlePrefab == null
                || phonePrefab == null
                || penPrefab == null
                || cupPrefab == null
                || inpaintShader == null)
            {
                Debug.LogError(
                    "Mask-pose setup requires yolov8n-seg.onnx, Retro Prefab Library.asset "
                    + "all retro prefabs and MaskBackgroundReconstruction.shader.");
                return;
            }

            AssignLibraryPrefab(prefabLibrary, "bottle", bottlePrefab);
            AssignLibraryPrefab(prefabLibrary, "phone", phonePrefab);
            AssignLibraryPrefab(prefabLibrary, "pen", penPrefab);
            AssignLibraryPrefab(prefabLibrary, "cup", cupPrefab);
            AssignLibraryRotationOffset(prefabLibrary, "phone", Vector3.zero);

            CleanupOldSceneObjectsAndMissingScripts();
            EnsureBackgroundReconstructionRendererFeature(inpaintShader);

            ARCameraManager cameraManager = FindOrCreateARCameraManager(out XROrigin origin);
            if (cameraManager == null || origin == null)
            {
                Debug.LogError("Could not find or create the AR Camera and XR Origin.");
                return;
            }

            Camera arCamera = cameraManager.GetComponent<Camera>();
            AROcclusionManager occlusionManager =
                GetOrAddComponent<AROcclusionManager>(arCamera.gameObject);
            // Keep LiDAR environment-depth CPU images available for metric sizing, but do not
            // let ARKit render the real object's depth over the virtual replacement.
            AssignInteger(occlusionManager, "m_OcclusionPreferenceMode", 2);
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;

            GameObject systemObject = GameObject.Find(SystemObjectName);
            if (systemObject == null)
            {
                systemObject = new GameObject(SystemObjectName);
                Undo.RegisterCreatedObjectUndo(systemObject, "Create mask pose system");
            }

            Undo.RegisterFullObjectHierarchyUndo(systemObject, "Prepare mask pose system");
            ARCameraFrameProvider frameProvider =
                GetOrAddComponent<ARCameraFrameProvider>(systemObject);
            ARDepthFrameProvider depthProvider =
                GetOrAddComponent<ARDepthFrameProvider>(systemObject);
            YoloObjectDetector detector =
                GetOrAddComponent<YoloObjectDetector>(systemObject);
            YoloDetectionOverlay overlay =
                GetOrAddComponent<YoloDetectionOverlay>(systemObject);
            MaskDepthPointCloudProvider cloudProvider =
                GetOrAddComponent<MaskDepthPointCloudProvider>(systemObject);
            CategoryPoseEstimator poseEstimator =
                GetOrAddComponent<CategoryPoseEstimator>(systemObject);
            MaskPoseReplacementController replacementController =
                GetOrAddComponent<MaskPoseReplacementController>(systemObject);

            AssignObjectReference(frameProvider, "cameraManager", cameraManager);
            AssignObjectReference(detector, "modelAsset", null);
            AssignObjectReference(detector, "segmentationModelAsset", segmentationModel);
            AssignObjectReference(detector, "frameProvider", frameProvider);
            AssignBoolean(detector, "logDetections", false);
            AssignFloat(detector, "inferenceIntervalSeconds", 0.16f);
            AssignString(detector, "targetLabelFilter", "bottle,cup,phone");
            AssignInteger(detector, "segmentationMaskResolution", 72);
            AssignObjectReference(overlay, "detector", detector);
            AssignObjectReference(depthProvider, "occlusionManager", occlusionManager);
            AssignObjectReference(depthProvider, "cameraManager", cameraManager);
            AssignObjectReference(depthProvider, "arCamera", arCamera);
            AssignBoolean(depthProvider, "keepAssignedOcclusionManagerInPlace", true);
            AssignBoolean(depthProvider, "requestTemporalSmoothing", false);
            AssignBoolean(depthProvider, "disableEnvironmentOcclusionRendering", true);
            AssignObjectReference(cloudProvider, "detector", detector);
            AssignObjectReference(cloudProvider, "depthProvider", depthProvider);
            AssignString(cloudProvider, "targetLabel", string.Empty);
            AssignInteger(cloudProvider, "horizontalSamples", 12);
            AssignInteger(cloudProvider, "verticalSamples", 16);
            AssignInteger(cloudProvider, "minimumPointCount", 10);
            AssignObjectReference(poseEstimator, "pointCloudProvider", cloudProvider);
            AssignObjectReference(poseEstimator, "arCamera", arCamera);
            AssignFloat(poseEstimator, "minimumPositionCutoff", 4.5f);
            AssignFloat(poseEstimator, "positionVelocityResponse", 24f);
            AssignFloat(poseEstimator, "rotationCutoff", 4.5f);
            AssignFloat(poseEstimator, "rotationVelocityResponse", 0.05f);
            AssignFloat(poseEstimator, "sizeCutoff", 0.45f);
            AssignFloat(poseEstimator, "resetAfterSeconds", 0.6f);
            AssignFloat(poseEstimator, "nominalBottleHeightMeters", 0.23f);
            AssignFloat(poseEstimator, "nominalBottleDiameterMeters", 0.068f);
            AssignFloat(poseEstimator, "bottleBoxScale", 0.94f);
            AssignFloat(poseEstimator, "bottleMeasurementWeight", 0.18f);
            AssignVector3(poseEstimator, "nominalPhoneSizeMeters", new Vector3(0.075f, 0.16f, 0.009f));
            AssignVector3(poseEstimator, "nominalCupSizeMeters", new Vector3(0.085f, 0.105f, 0.085f));
            AssignVector3(poseEstimator, "nominalPenSizeMeters", new Vector3(0.012f, 0.145f, 0.012f));
            AssignFloat(poseEstimator, "planarMeasurementWeight", 0.22f);
            AssignObjectReference(replacementController, "poseEstimator", poseEstimator);
            AssignObjectReference(replacementController, "prefabLibrary", prefabLibrary);
            AssignFloat(replacementController, "hideAfterSeconds", 0.45f);
            AssignBoolean(replacementController, "lockBottleRotation", true);
            AssignFloat(replacementController, "positionFollowSharpness", 18f);
            AssignFloat(replacementController, "rotationFollowSharpness", 14f);
            AssignFloat(replacementController, "scaleFollowSharpness", 8f);
            AssignFloat(replacementController, "maximumVisualSpeed", 1.6f);
            AssignFloat(replacementController, "maximumVisualRotationSpeed", 300f);
            AssignVector3(
                replacementController,
                "bottlePhysicalSizeMeters",
                new Vector3(0.068f, 0.23f, 0.068f));
            AssignVector3(
                replacementController,
                "phonePhysicalSizeMeters",
                new Vector3(0.072f, 0.15f, 0.008f));
            AssignVector3(
                replacementController,
                "cupPhysicalSizeMeters",
                new Vector3(0.12f, 0.105f, 0.085f));
            AssignVector3(
                replacementController,
                "penPhysicalSizeMeters",
                new Vector3(0.012f, 0.145f, 0.012f));

            Transform replacementRoot = systemObject.transform.Find("Retro Replacements");
            if (replacementRoot == null)
            {
                GameObject rootObject = new GameObject("Retro Replacements");
                Undo.RegisterCreatedObjectUndo(rootObject, "Create replacement root");
                replacementRoot = rootObject.transform;
                replacementRoot.SetParent(systemObject.transform, false);
            }

            AssignObjectReference(replacementController, "replacementRoot", replacementRoot);

            MaskBackgroundReconstructionController reconstruction =
                GetOrAddComponent<MaskBackgroundReconstructionController>(arCamera.gameObject);
            AssignObjectReference(reconstruction, "arCamera", arCamera);
            AssignObjectReference(reconstruction, "detector", detector);
            AssignInteger(reconstruction, "maskTextureResolution", 256);
            AssignFloat(reconstruction, "inpaintRadiusPixels", 240f);
            AssignFloat(reconstruction, "maskPaddingPixels", 8f);
            AssignFloat(reconstruction, "maskPersistenceSeconds", 0.28f);

            EditorUtility.SetDirty(systemObject);
            EditorUtility.SetDirty(origin);
            EditorUtility.SetDirty(arCamera);
            EditorSceneManager.MarkSceneDirty(systemObject.scene);
            Selection.activeGameObject = systemObject;
            Debug.Log(
                "YOLO-seg + mask depth point cloud + category pose + temporal filter + "
                + "mask reconstruction scene prepared with the retro bottle visual. "
                + "No exact target CAD is used for pose estimation.");
        }

        [MenuItem("Tools/AR 80s Retro/Import Retro Bottle Model")]
        public static void ImportRetroBottleModel()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(BottleModelSourcePath);
            if (source == null)
            {
                throw new System.InvalidOperationException(
                    $"Cannot import the retro bottle source at {BottleModelSourcePath}.");
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "Universal Render Pipeline/Lit shader is unavailable.");
            }

            Material glass = EnsureMaterial(
                BottleGlassMaterialPath,
                shader,
                new Color(0.055f, 0.18f, 0.075f, 1f),
                0.18f,
                0.82f);
            Material liquid = EnsureMaterial(
                BottleLiquidMaterialPath,
                shader,
                new Color(0.62f, 0.26f, 0.035f, 1f),
                0.05f,
                0.55f);
            Material cap = EnsureMaterial(
                BottleCapMaterialPath,
                shader,
                new Color(0.72f, 0.48f, 0.08f, 1f),
                0.65f,
                0.5f);

            GameObject root = new GameObject("Retro Beer Bottle");
            try
            {
                GameObject model = Object.Instantiate(source, root.transform, false);
                model.name = "Model";
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    string rendererName = renderer.name.ToUpperInvariant();
                    Material selected = rendererName.Contains("TAMPA")
                        ? cap
                        : rendererName.Contains("LIQUIDO")
                            || rendererName.Contains("LIQUID")
                            ? liquid
                            : glass;
                    Material[] materials = renderer.sharedMaterials;
                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    {
                        materials[materialIndex] = selected;
                    }

                    renderer.sharedMaterials = materials;
                }

                PrefabUtility.SaveAsPrefabAsset(root, BottlePrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            RetroPrefabLibrary library =
                AssetDatabase.LoadAssetAtPath<RetroPrefabLibrary>(PrefabLibraryPath);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BottlePrefabPath);
            if (library != null && prefab != null)
            {
                AssignLibraryPrefab(library, "bottle", prefab);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"RETRO BOTTLE IMPORT PASSED: {BottlePrefabPath}");
        }

        [MenuItem("Tools/AR 80s Retro/Import All Desktop Retro Models")]
        public static void ImportAdditionalRetroModels()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "Universal Render Pipeline/Lit shader is unavailable.");
            }

            Material phoneBody = EnsureMaterial(
                "Assets/AR80sRetro/Models/Retro/Retro Phone Body.mat",
                shader,
                new Color(0.14f, 0.13f, 0.1f, 1f),
                0.08f,
                0.34f);
            Material phoneSide = EnsureMaterial(
                "Assets/AR80sRetro/Models/Retro/Retro Phone Side.mat",
                shader,
                new Color(0.42f, 0.36f, 0.24f, 1f),
                0.15f,
                0.42f);
            Material phoneScreen = EnsureMaterial(
                "Assets/AR80sRetro/Models/Retro/Retro Phone Screen.mat",
                shader,
                new Color(0.04f, 0.24f, 0.12f, 1f),
                0.02f,
                0.76f);
            GameObject phonePrefab = SaveVisualPrefab(
                PhoneModelSourcePath,
                PhonePrefabPath,
                "Retro Nokia Phone",
                new Vector3(-90f, 0f, 0f),
                phoneBody,
                "SIDE",
                phoneSide,
                "SCREEN",
                phoneScreen);

            Material penMaterial = EnsureMaterial(
                "Assets/AR80sRetro/Models/Retro/Retro Pen Material.mat",
                shader,
                new Color(0.3f, 0.14f, 0.045f, 1f),
                0.72f,
                0.58f);
            GameObject penPrefab = SaveVisualPrefab(
                PenModelSourcePath,
                PenPrefabPath,
                "Retro Pen",
                Vector3.zero,
                penMaterial,
                null,
                null,
                null,
                null);

            RetroPrefabLibrary library =
                AssetDatabase.LoadAssetAtPath<RetroPrefabLibrary>(PrefabLibraryPath);
            if (library == null)
            {
                throw new System.InvalidOperationException(
                    $"Cannot load {PrefabLibraryPath}.");
            }

            AssignLibraryPrefab(library, "phone", phonePrefab);
            AssignLibraryPrefab(library, "pen", penPrefab);
            AssetDatabase.SaveAssets();
            Debug.Log(
                "ADDITIONAL RETRO MODELS IMPORT PASSED: phone and dormant pen prefabs.");
        }

        public static void ImportAllRetroModelsAndPrepareBuildScene()
        {
            ImportRetroBottleModel();
            ImportAdditionalRetroModels();
            PrepareBuildScene();
        }

        public static void ImportRetroBottleAndPrepareBuildScene()
        {
            ImportRetroBottleModel();
            PrepareBuildScene();
        }

        private static GameObject SaveVisualPrefab(
            string sourcePath,
            string prefabPath,
            string displayName,
            Vector3 modelEulerAngles,
            Material primary,
            string secondaryToken,
            Material secondary,
            string accentToken,
            Material accent)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null)
            {
                throw new System.InvalidOperationException(
                    $"Cannot import retro model source at {sourcePath}.");
            }

            GameObject root = new GameObject(displayName);
            try
            {
                GameObject model = Object.Instantiate(source, root.transform, false);
                model.name = "Model";
                model.transform.localRotation = Quaternion.Euler(modelEulerAngles);
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    string rendererName = renderer.name.ToUpperInvariant();
                    Material selected = accent != null
                        && !string.IsNullOrEmpty(accentToken)
                        && rendererName.Contains(accentToken)
                        ? accent
                        : secondary != null
                            && !string.IsNullOrEmpty(secondaryToken)
                            && rendererName.Contains(secondaryToken)
                            ? secondary
                            : primary;
                    Material[] materials = renderer.sharedMaterials;
                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    {
                        materials[materialIndex] = selected;
                    }

                    renderer.sharedMaterials = materials;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new System.InvalidOperationException(
                    $"Failed to save retro prefab at {prefabPath}.");
            }

            return prefab;
        }

        private static GameObject EnsureRetroCupPrefab()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "Universal Render Pipeline/Lit shader is unavailable for the retro mug.");
            }

            Material body = EnsureMaterial(
                CupBodyMaterialPath,
                shader,
                new Color(0.42f, 0.08f, 0.055f, 1f),
                0.12f,
                0.48f);
            Material rim = EnsureMaterial(
                CupRimMaterialPath,
                shader,
                new Color(0.92f, 0.68f, 0.22f, 1f),
                0.18f,
                0.55f);
            Material interior = EnsureMaterial(
                CupInteriorMaterialPath,
                shader,
                new Color(0.055f, 0.025f, 0.015f, 1f),
                0f,
                0.3f);

            GameObject root = new GameObject("Retro Mug");
            try
            {
                AddRetroMugPrimitive(
                    root.transform,
                    PrimitiveType.Cylinder,
                    "Mug Body",
                    new Vector3(0f, 0f, 0f),
                    new Vector3(0.72f, 0.5f, 0.72f),
                    body);
                AddRetroMugPrimitive(
                    root.transform,
                    PrimitiveType.Cylinder,
                    "Mug Rim",
                    new Vector3(0f, 0.49f, 0f),
                    new Vector3(0.78f, 0.025f, 0.78f),
                    rim);
                AddRetroMugPrimitive(
                    root.transform,
                    PrimitiveType.Cylinder,
                    "Dark Interior",
                    new Vector3(0f, 0.518f, 0f),
                    new Vector3(0.67f, 0.008f, 0.67f),
                    interior);
                AddRetroMugPrimitive(
                    root.transform,
                    PrimitiveType.Cube,
                    "Handle Top",
                    new Vector3(0.44f, 0.25f, 0f),
                    new Vector3(0.24f, 0.1f, 0.16f),
                    body);
                AddRetroMugPrimitive(
                    root.transform,
                    PrimitiveType.Cube,
                    "Handle Outer",
                    new Vector3(0.58f, 0.025f, 0f),
                    new Vector3(0.12f, 0.55f, 0.16f),
                    body);
                AddRetroMugPrimitive(
                    root.transform,
                    PrimitiveType.Cube,
                    "Handle Bottom",
                    new Vector3(0.44f, -0.2f, 0f),
                    new Vector3(0.24f, 0.1f, 0.16f),
                    body);
                PrefabUtility.SaveAsPrefabAsset(root, CupPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<GameObject>(CupPrefabPath);
        }

        private static void AddRetroMugPrimitive(
            Transform parent,
            PrimitiveType primitiveType,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Material material)
        {
            GameObject primitive = GameObject.CreatePrimitive(primitiveType);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localPosition = localPosition;
            primitive.transform.localScale = localScale;
            Collider collider = primitive.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            Renderer renderer = primitive.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
            }
        }

        private static Material EnsureMaterial(
            string path,
            Shader shader,
            Color color,
            float metallic,
            float smoothness)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            material.color = color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", metallic);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", smoothness);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        private static void AssignLibraryPrefab(
            RetroPrefabLibrary library,
            string label,
            GameObject prefab)
        {
            SerializedObject serializedObject = new SerializedObject(library);
            SerializedProperty rules = serializedObject.FindProperty("rules");
            if (rules == null || !rules.isArray)
            {
                throw new System.InvalidOperationException(
                    "Retro Prefab Library has no serialized rules array.");
            }

            for (int i = 0; i < rules.arraySize; i++)
            {
                SerializedProperty rule = rules.GetArrayElementAtIndex(i);
                SerializedProperty detectionLabel = rule.FindPropertyRelative("detectionLabel");
                if (detectionLabel == null
                    || !string.Equals(
                        detectionLabel.stringValue,
                        label,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rule.FindPropertyRelative("prefab").objectReferenceValue = prefab;
                rule.FindPropertyRelative("spawnScale").vector3Value = Vector3.one;
                rule.FindPropertyRelative("scaleCalibrationMultiplier").floatValue = 1f;
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(library);
                return;
            }

            throw new System.InvalidOperationException(
                $"Retro Prefab Library has no rule for '{label}'.");
        }

        private static void AssignLibraryRotationOffset(
            RetroPrefabLibrary library,
            string label,
            Vector3 eulerAngles)
        {
            SerializedObject serializedObject = new SerializedObject(library);
            SerializedProperty rules = serializedObject.FindProperty("rules");
            for (int i = 0; rules != null && i < rules.arraySize; i++)
            {
                SerializedProperty rule = rules.GetArrayElementAtIndex(i);
                SerializedProperty detectionLabel = rule.FindPropertyRelative("detectionLabel");
                if (detectionLabel != null
                    && string.Equals(
                        detectionLabel.stringValue,
                        label,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    rule.FindPropertyRelative("rotationOffsetEuler").vector3Value = eulerAngles;
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(library);
                    return;
                }
            }
        }

        private static void CleanupOldSceneObjectsAndMissingScripts()
        {
            GameObject[] sceneObjects = Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < sceneObjects.Length; i++)
            {
                GameObject gameObject = sceneObjects[i];
                if (gameObject != null && gameObject.scene.IsValid())
                {
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(gameObject);
                }
            }
        }

        private static void EnsureBackgroundReconstructionRendererFeature(Shader inpaintShader)
        {
            UniversalRendererData rendererData =
                AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererDataPath);
            if (rendererData == null)
            {
                Debug.LogError($"Could not load URP renderer data at {RendererDataPath}.");
                return;
            }

            for (int i = rendererData.rendererFeatures.Count - 1; i >= 0; i--)
            {
                ScriptableRendererFeature feature = rendererData.rendererFeatures[i];
                if (feature == null)
                {
                    rendererData.rendererFeatures.RemoveAt(i);
                }
            }

            MaskBackgroundReconstructionFeature existing = null;
            for (int i = 0; i < rendererData.rendererFeatures.Count; i++)
            {
                if (rendererData.rendererFeatures[i] is MaskBackgroundReconstructionFeature feature)
                {
                    existing = feature;
                    break;
                }
            }

            if (existing == null)
            {
                existing = ScriptableObject.CreateInstance<MaskBackgroundReconstructionFeature>();
                existing.name = "YOLO Mask Background Reconstruction";
                AssetDatabase.AddObjectToAsset(existing, rendererData);
                rendererData.rendererFeatures.Add(existing);
            }

            existing.InpaintShader = inpaintShader;
            existing.SetActive(true);
            existing.Create();
            EditorUtility.SetDirty(existing);
            EditorUtility.SetDirty(rendererData);
        }

        private static ARCameraManager FindOrCreateARCameraManager(out XROrigin origin)
        {
            origin = Object.FindObjectOfType<XROrigin>();
            ARCameraManager cameraManager = Object.FindObjectOfType<ARCameraManager>();
            ARSession session = Object.FindObjectOfType<ARSession>();
            if (session == null)
            {
                GameObject sessionObject = new GameObject("AR Session");
                Undo.RegisterCreatedObjectUndo(sessionObject, "Create AR Session");
                session = Undo.AddComponent<ARSession>(sessionObject);
            }

            GetOrAddComponent<ARInputManager>(session.gameObject);
            if (origin == null)
            {
                GameObject originObject = new GameObject("XR Origin (AR)");
                Undo.RegisterCreatedObjectUndo(originObject, "Create XR Origin");
                origin = Undo.AddComponent<XROrigin>(originObject);
            }

            Camera camera = cameraManager != null
                ? cameraManager.GetComponent<Camera>()
                : Camera.main != null
                    ? Camera.main
                    : Object.FindObjectOfType<Camera>();
            if (camera == null)
            {
                GameObject cameraObject = new GameObject("AR Camera");
                Undo.RegisterCreatedObjectUndo(cameraObject, "Create AR Camera");
                camera = Undo.AddComponent<Camera>(cameraObject);
            }

            if (!camera.transform.IsChildOf(origin.transform))
            {
                Undo.SetTransformParent(camera.transform, origin.transform, "Parent AR Camera");
                camera.transform.localPosition = Vector3.zero;
                camera.transform.localRotation = Quaternion.identity;
            }

            camera.gameObject.name = "AR Camera";
            camera.tag = "MainCamera";
            origin.Camera = camera;
            cameraManager = GetOrAddComponent<ARCameraManager>(camera.gameObject);
            GetOrAddComponent<ARCameraBackground>(camera.gameObject);
            GetOrAddComponent<TrackedPoseDriver>(camera.gameObject);
            return cameraManager;
        }

        private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
        {
            T component = gameObject.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(gameObject);
        }

        private static bool RequireComponent<T>(GameObject gameObject) where T : Component
        {
            if (gameObject.GetComponent<T>() != null)
            {
                return true;
            }

            Debug.LogError(
                $"Validation failed: '{gameObject.name}' has no {typeof(T).Name} component.");
            return false;
        }

        private static void AssignObjectReference(Object target, string propertyName, Object value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogError($"Missing serialized property '{propertyName}' on {target}.");
                return;
            }

            property.objectReferenceValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignBoolean(Object target, string propertyName, bool value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogError($"Missing serialized property '{propertyName}' on {target}.");
                return;
            }

            property.boolValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignString(Object target, string propertyName, string value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogError($"Missing serialized property '{propertyName}' on {target}.");
                return;
            }

            property.stringValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignFloat(Object target, string propertyName, float value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogError($"Missing serialized property '{propertyName}' on {target}.");
                return;
            }

            property.floatValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignVector3(
            Object target,
            string propertyName,
            Vector3 value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogError($"Missing serialized property '{propertyName}' on {target}.");
                return;
            }

            property.vector3Value = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignInteger(Object target, string propertyName, int value)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                Debug.LogError($"Missing serialized property '{propertyName}' on {target}.");
                return;
            }

            property.intValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
