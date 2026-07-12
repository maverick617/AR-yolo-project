using AR80sRetro;
using Unity.Sentis;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;

namespace AR80sRetroEditor
{
    public static class AR80sRetroYoloSetup
    {
        private const string SystemObjectName = "AR80sRetro System";
        private const string ModelPath = "Assets/AR80sRetro/Models/YOLO/yolov8n.onnx";
        private const string PrefabLibraryPath = "Assets/AR80sRetro/Retro Prefab Library.asset";

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

            RetroPrefabLibrary prefabLibrary =
                AssetDatabase.LoadAssetAtPath<RetroPrefabLibrary>(PrefabLibraryPath);
            if (prefabLibrary == null)
            {
                Debug.LogError($"Cannot load prefab library at '{PrefabLibraryPath}'.");
                return;
            }

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

            if (cameraManager == null)
            {
                Debug.LogError("Could not create or find an ARCameraManager.");
                return;
            }

            Undo.RegisterFullObjectHierarchyUndo(systemObject, "Configure AR 80s Retro YOLO");

            ARCameraFrameProvider frameProvider =
                GetOrAddComponent<ARCameraFrameProvider>(systemObject);
            YoloObjectDetector detector = GetOrAddComponent<YoloObjectDetector>(systemObject);
            RetroDetectionPipeline pipeline =
                GetOrAddComponent<RetroDetectionPipeline>(systemObject);
            YoloDetectionOverlay overlay =
                GetOrAddComponent<YoloDetectionOverlay>(systemObject);
            AprilTagPoseSource aprilTagPoseSource =
                GetOrAddComponent<AprilTagPoseSource>(systemObject);
            KeijiroAprilTagFrameDetector aprilTagDetector =
                GetOrAddComponent<KeijiroAprilTagFrameDetector>(systemObject);

            AssignObjectReference(frameProvider, "cameraManager", cameraManager);
            AssignObjectReference(detector, "modelAsset", modelAsset);
            AssignObjectReference(detector, "frameProvider", frameProvider);
            AssignObjectReference(pipeline, "detector", detector);
            AssignObjectReference(pipeline, "replacementManager", replacementManager);
            AssignObjectReference(overlay, "detector", detector);
            AssignObjectReference(replacementManager, "prefabLibrary", prefabLibrary);
            AssignObjectReference(replacementManager, "positionSolver", positionSolver);
            AssignObjectReference(replacementManager, "aprilTagPoseSource", aprilTagPoseSource);
            AssignObjectReference(replacementManager, "arCamera", cameraManager.GetComponent<Camera>());
            AssignObjectReference(replacementManager, "contentRoot", systemObject.transform);
            AssignObjectReference(positionSolver, "raycastManager", raycastManager);
            AssignObjectReference(positionSolver, "arCamera", cameraManager.GetComponent<Camera>());
            AssignObjectReference(aprilTagPoseSource, "arCamera", cameraManager.GetComponent<Camera>());
            AssignObjectReference(aprilTagPoseSource, "trackedImageManager", trackedImageManager);
            AssignObjectReference(aprilTagDetector, "frameProvider", frameProvider);
            AssignObjectReference(aprilTagDetector, "poseSource", aprilTagPoseSource);
            AssignObjectReference(aprilTagDetector, "arCamera", cameraManager.GetComponent<Camera>());

            DisableIfPresent<MockDetectionInput>(systemObject);
            DisableIfPresent<CameraFrameSmokeTest>(systemObject);

            EditorUtility.SetDirty(systemObject);
            EditorSceneManager.MarkSceneDirty(systemObject.scene);
            Selection.activeGameObject = systemObject;

            Debug.Log("YOLO scene configuration completed. Mock input is disabled. Save the scene when Unity asks or before closing the project.");
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
