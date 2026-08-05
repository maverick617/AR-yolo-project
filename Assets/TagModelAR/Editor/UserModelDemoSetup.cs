using System;
using GLTFast;
using TagModelAR;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;

namespace TagModelAREditor
{
    /// <summary>Creates and validates the project's single minimal AR scene.</summary>
    public static class UserModelDemoSetup
    {
        private const string ScenePath = "Assets/Scenes/UserModelAR.unity";

        [MenuItem("Tools/Tag Model AR/Rebuild Demo Scene")]
        public static void RebuildDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);

            GameObject sessionObject = new GameObject("AR Session");
            sessionObject.AddComponent<ARSession>();
            sessionObject.AddComponent<ARInputManager>();

            GameObject originObject = new GameObject("XR Origin (AR)");
            XROrigin origin = originObject.AddComponent<XROrigin>();
            GameObject cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(originObject.transform, false);

            GameObject cameraObject = new GameObject("AR Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(cameraOffset.transform, false);
            Camera arCamera = cameraObject.AddComponent<Camera>();
            arCamera.clearFlags = CameraClearFlags.SolidColor;
            arCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            arCamera.nearClipPlane = 0.05f;
            arCamera.farClipPlane = 20f;
            cameraObject.AddComponent<AudioListener>();
            ARCameraManager cameraManager =
                cameraObject.AddComponent<ARCameraManager>();
            cameraObject.AddComponent<ARCameraBackground>();
            cameraObject.AddComponent<TrackedPoseDriver>();
            cameraObject.AddComponent<UniversalAdditionalCameraData>();

            origin.Camera = arCamera;
            origin.Origin = originObject;
            origin.CameraFloorOffsetObject = cameraOffset;

            GameObject systemObject = new GameObject("Tag Model AR");
            ARCameraFrameProvider frameProvider =
                systemObject.AddComponent<ARCameraFrameProvider>();
            AprilTagPoseSource poseSource =
                systemObject.AddComponent<AprilTagPoseSource>();
            AprilTagDetector detector =
                systemObject.AddComponent<AprilTagDetector>();
            UserModelTagController controller =
                systemObject.AddComponent<UserModelTagController>();

            Assign(frameProvider, "cameraManager", cameraManager);
            Assign(detector, "frameProvider", frameProvider);
            Assign(detector, "poseSource", poseSource);
            Assign(detector, "arCamera", arCamera);
            Assign(detector, "tagSizeMeters", 0.03f);
            Assign(detector, "decimation", 1);
            Assign(detector, "detectionIntervalSeconds", 0.05f);
            Assign(controller, "tagDetector", detector);
            Assign(controller, "poseSource", poseSource);
            Assign(controller, "contentRoot", systemObject.transform);
            Assign(controller, "defaultTagSizeIndex", 1);
            Assign(controller, "modelSizeInTagWidths", 3f);

            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.None;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            lightObject.AddComponent<UniversalAdditionalLightData>();

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientIntensity = 1f;
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };

            IncludeRuntimeGltfShaders();
            EnableGltfSafeMode();
            EnsureXrManagersAutoStart();
            AssetDatabase.SaveAssets();
            ValidateDemoScene();
            Selection.activeGameObject = systemObject;
            Debug.Log("Tag Model AR demo scene rebuilt successfully.");
        }

        public static void RebuildDemoSceneBatch()
        {
            RebuildDemoScene();
        }

        [MenuItem("Tools/Tag Model AR/Validate Demo")]
        public static void ValidateDemoScene()
        {
            if (!System.IO.File.Exists(ScenePath))
            {
                throw new InvalidOperationException("Demo scene does not exist.");
            }

            Scene scene = EditorSceneManager.OpenScene(
                ScenePath,
                OpenSceneMode.Single);
            ARSession session = UnityEngine.Object.FindObjectOfType<ARSession>();
            ARCameraManager camera =
                UnityEngine.Object.FindObjectOfType<ARCameraManager>();
            ARCameraFrameProvider frames =
                UnityEngine.Object.FindObjectOfType<ARCameraFrameProvider>();
            AprilTagPoseSource poses =
                UnityEngine.Object.FindObjectOfType<AprilTagPoseSource>();
            AprilTagDetector detector =
                UnityEngine.Object.FindObjectOfType<AprilTagDetector>();
            UserModelTagController controller =
                UnityEngine.Object.FindObjectOfType<UserModelTagController>();

            if (!scene.IsValid()
                || session == null
                || camera == null
                || frames == null
                || poses == null
                || detector == null
                || controller == null)
            {
                throw new InvalidOperationException(
                    "Demo scene is missing a required AR component.");
            }

            ValidateXrManagers();
            ValidateModelFitting();
            Debug.Log(
                "Tag Model AR validation passed: AR camera, AprilTag detector, "
                + "GLB controller and metric model fitting are present.");
        }

        public static void ValidateDemoSceneBatch()
        {
            ValidateDemoScene();
        }

        /// <summary>
        /// CI smoke test for the actual runtime importer. Set
        /// TAG_MODEL_AR_TEST_GLB to a local GLB and run this execute method
        /// without -quit; the method exits Unity after its async work finishes.
        /// </summary>
        public static async void ValidateRuntimeGlbBatch()
        {
            int exitCode = 0;
            GameObject root = null;
            GltfImport importer = null;
            try
            {
                string path = Environment.GetEnvironmentVariable(
                    "TAG_MODEL_AR_TEST_GLB");
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    throw new InvalidOperationException(
                        "TAG_MODEL_AR_TEST_GLB must name an existing GLB file.");
                }

                importer = new GltfImport(
                    deferAgent: new UninterruptedDeferAgent());
                bool loaded = await importer.LoadFile(
                    path,
                    null,
                    new ImportSettings
                    {
                        AnimationMethod = AnimationMethod.None,
                        GenerateMipMaps = true
                    });
                root = new GameObject("Runtime GLB Validation");
                bool instantiated = loaded
                    && await importer.InstantiateMainSceneAsync(root.transform);
                if (!instantiated
                    || !UserModelTagController.TryCalculateLocalBounds(
                        root.transform,
                        out Bounds bounds))
                {
                    throw new InvalidOperationException(
                        "Runtime GLB load or mesh instantiation failed.");
                }

                float longest = Mathf.Max(
                    bounds.size.x,
                    bounds.size.y,
                    bounds.size.z);
                Debug.Log(
                    $"Runtime GLB validation passed: {path}, "
                    + $"source longest edge={longest:F4} units.");
            }
            catch (Exception exception)
            {
                exitCode = 1;
                Debug.LogException(exception);
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }

                importer?.Dispose();
                EditorApplication.Exit(exitCode);
            }
        }

        private static void ValidateModelFitting()
        {
            GameObject anchor = new GameObject("Fit Validation");
            anchor.hideFlags = HideFlags.HideAndDontSave;
            GameObject normalization = new GameObject("Normalization");
            normalization.transform.SetParent(anchor.transform, false);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(normalization.transform, false);
            cube.transform.localScale = new Vector3(2f, 1f, 0.5f);
            try
            {
                if (!UserModelTagController.TryCalculateLocalBounds(
                    normalization.transform,
                    out Bounds bounds))
                {
                    throw new InvalidOperationException(
                        "Model bounds calculation failed.");
                }

                const float targetSize = 0.09f;
                UserModelTagController.ApplyModelScale(
                    normalization.transform,
                    bounds,
                    targetSize);
                float renderedLongest = Mathf.Max(
                    cube.GetComponent<Renderer>().bounds.size.x,
                    cube.GetComponent<Renderer>().bounds.size.y,
                    cube.GetComponent<Renderer>().bounds.size.z);
                if (Mathf.Abs(renderedLongest - targetSize) > 0.001f)
                {
                    throw new InvalidOperationException(
                        $"Metric model fitting failed ({renderedLongest:F4} m)." );
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(anchor);
            }
        }

        private static void IncludeRuntimeGltfShaders()
        {
            UnityEngine.Object[] graphicsSettings =
                AssetDatabase.LoadAllAssetsAtPath(
                    "ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettings.Length == 0)
            {
                return;
            }

            string[] shaderPaths =
            {
                "Packages/com.unity.cloud.gltfast/Runtime/Shader/"
                    + "glTF-pbrMetallicRoughness.shadergraph",
                "Packages/com.unity.cloud.gltfast/Runtime/Shader/"
                    + "glTF-pbrSpecularGlossiness.shadergraph",
                "Packages/com.unity.cloud.gltfast/Runtime/Shader/"
                    + "glTF-unlit.shadergraph",
                "Packages/com.unity.cloud.gltfast/Runtime/Shader/URP/"
                    + "glTF-pbrMetallicRoughness-Clearcoat.shadergraph"
            };

            var serialized = new SerializedObject(graphicsSettings[0]);
            SerializedProperty shaders = serialized.FindProperty(
                "m_AlwaysIncludedShaders");
            if (shaders == null)
            {
                return;
            }

            foreach (string path in shaderPaths)
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null || ContainsGuid(shaders, path))
                {
                    continue;
                }

                int index = shaders.arraySize;
                shaders.InsertArrayElementAtIndex(index);
                shaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool ContainsGuid(
            SerializedProperty array,
            string assetPath)
        {
            string expectedGuid = AssetDatabase.AssetPathToGUID(assetPath);
            for (int i = 0; i < array.arraySize; i++)
            {
                UnityEngine.Object value = array
                    .GetArrayElementAtIndex(i)
                    .objectReferenceValue;
                if (value != null
                    && AssetDatabase.AssetPathToGUID(
                        AssetDatabase.GetAssetPath(value)) == expectedGuid)
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnableGltfSafeMode()
        {
            NamedBuildTarget[] targets =
            {
                NamedBuildTarget.Android,
                NamedBuildTarget.iOS,
                NamedBuildTarget.Standalone
            };
            foreach (NamedBuildTarget target in targets)
            {
                string defines = PlayerSettings.GetScriptingDefineSymbols(target);
                string[] values = defines.Split(
                    new[] { ';' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (Array.IndexOf(values, "GLTFAST_SAFE") >= 0)
                {
                    continue;
                }

                PlayerSettings.SetScriptingDefineSymbols(
                    target,
                    string.IsNullOrEmpty(defines)
                        ? "GLTFAST_SAFE"
                        : defines + ";GLTFAST_SAFE");
            }
        }

        private static void EnsureXrManagersAutoStart()
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(
                "Assets/XR/XRGeneralSettings.asset");
            foreach (UnityEngine.Object asset in assets)
            {
                if (asset is not XRManagerSettings manager)
                {
                    continue;
                }

                manager.automaticLoading = true;
                manager.automaticRunning = true;
                EditorUtility.SetDirty(manager);
            }
        }

        private static void ValidateXrManagers()
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(
                "Assets/XR/XRGeneralSettings.asset");
            int mobileManagers = 0;
            foreach (UnityEngine.Object asset in assets)
            {
                if (asset is not XRManagerSettings manager
                    || (manager.name != "Android Providers"
                        && manager.name != "iPhone Providers"))
                {
                    continue;
                }

                mobileManagers++;
                if (!manager.automaticLoading
                    || !manager.automaticRunning
                    || manager.activeLoaders.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"{manager.name} must auto-start a configured AR loader.");
                }
            }

            if (mobileManagers != 2)
            {
                throw new InvalidOperationException(
                    "Android and iPhone XR managers must both be configured.");
            }
        }

        private static void Assign(
            UnityEngine.Object target,
            string propertyName,
            UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(
            UnityEngine.Object target,
            string propertyName,
            float value)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            property.floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(
            UnityEngine.Object target,
            string propertyName,
            int value)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            property.intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
