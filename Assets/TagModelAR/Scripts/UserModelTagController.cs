using System;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using UnityEngine.Rendering;

namespace TagModelAR
{
    /// <summary>
    /// Lets the user import one GLB and attaches it to the first visible tag.
    /// The imported model is normalized once, then follows that tag's 6DoF pose.
    /// </summary>
    public sealed class UserModelTagController : MonoBehaviour
    {
        private const string SavedTagSizeKey = "tag-model-ar.tag-size-mm.v1";
        private const string SavedModelFolder = "UserModel";
        private const string SavedModelFilename = "current.glb";

        [SerializeField] private AprilTagDetector tagDetector;
        [SerializeField] private AprilTagPoseSource poseSource;
        [SerializeField] private Transform contentRoot;

        [Header("Physical size")]
        [SerializeField] private float[] tagSizePresetsMillimeters =
            { 20f, 30f, 50f, 80f, 100f };
        [SerializeField, Min(0)] private int defaultTagSizeIndex = 1;
        [Tooltip("The imported model's longest edge in Tag widths.")]
        [SerializeField, Range(0.25f, 10f)] private float modelSizeInTagWidths = 3f;

        [Header("Tracking")]
        [SerializeField, Min(0f)] private float surfaceOffsetMeters = 0.001f;
        [SerializeField, Min(0.005f)] private float followPositionSeconds = 0.035f;
        [SerializeField, Min(1f)] private float followRotationSpeed = 24f;

        [Header("Import")]
        [SerializeField, Range(1, 500)] private int maximumFileSizeMegabytes = 100;
        [SerializeField] private bool loadSavedModelOnStart = true;

        private GltfImport currentImport;
        private GameObject currentAnchor;
        private Transform normalizationRoot;
        private Bounds originalModelBounds;
        private bool hasOriginalBounds;
        private int boundTagId = -1;
        private bool hasAnchorPose;
        private Vector3 followVelocity;
        private int selectedTagSizeIndex;
        private int loadVersion;
        private bool isLoading;
        private bool pickerOpen;
        private string currentFilename;
        private string message = "Import a .glb model to begin";

        private GUIStyle panelStyle;
        private GUIStyle titleStyle;
        private GUIStyle labelStyle;
        private GUIStyle buttonStyle;
        private int styledScreenHeight = -1;

        public float SelectedTagSizeMillimeters =>
            tagSizePresetsMillimeters[selectedTagSizeIndex];
        public bool HasLoadedModel => currentAnchor != null;
        public bool IsLoading => isLoading;
        public int BoundTagId => boundTagId;
        public string CurrentFilename => currentFilename;

        private string SavedModelPath => Path.Combine(
            Application.persistentDataPath,
            SavedModelFolder,
            SavedModelFilename);

        private void Reset()
        {
            ResolveReferences();
        }

        private void Awake()
        {
            ResolveReferences();
            ValidatePresets();
            float savedSize = PlayerPrefs.GetFloat(
                SavedTagSizeKey,
                tagSizePresetsMillimeters[defaultTagSizeIndex]);
            selectedTagSizeIndex = FindNearestTagSize(savedSize);
            ApplyTagSize(false);
        }

        private void Start()
        {
            if (loadSavedModelOnStart && File.Exists(SavedModelPath))
            {
                _ = ImportModelFromPathAsync(SavedModelPath, false);
            }
        }

        private void ResolveReferences()
        {
            if (tagDetector == null)
            {
                tagDetector = FindObjectOfType<AprilTagDetector>();
            }

            if (poseSource == null)
            {
                poseSource = FindObjectOfType<AprilTagPoseSource>();
            }

            if (contentRoot == null)
            {
                contentRoot = transform;
            }
        }

        private void ValidatePresets()
        {
            if (tagSizePresetsMillimeters == null
                || tagSizePresetsMillimeters.Length == 0)
            {
                tagSizePresetsMillimeters =
                    new[] { 20f, 30f, 50f, 80f, 100f };
            }

            for (int i = 0; i < tagSizePresetsMillimeters.Length; i++)
            {
                tagSizePresetsMillimeters[i] = Mathf.Max(
                    5f,
                    tagSizePresetsMillimeters[i]);
            }

            defaultTagSizeIndex = Mathf.Clamp(
                defaultTagSizeIndex,
                0,
                tagSizePresetsMillimeters.Length - 1);
        }

        private int FindNearestTagSize(float millimeters)
        {
            int bestIndex = 0;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < tagSizePresetsMillimeters.Length; i++)
            {
                float distance = Mathf.Abs(
                    tagSizePresetsMillimeters[i] - millimeters);
                if (distance < bestDistance)
                {
                    bestIndex = i;
                    bestDistance = distance;
                }
            }

            return bestIndex;
        }

        public void SelectTagSize(int index)
        {
            ValidatePresets();
            int nextIndex = Mathf.Clamp(
                index,
                0,
                tagSizePresetsMillimeters.Length - 1);
            if (nextIndex == selectedTagSizeIndex)
            {
                return;
            }

            selectedTagSizeIndex = nextIndex;
            PlayerPrefs.SetFloat(
                SavedTagSizeKey,
                SelectedTagSizeMillimeters);
            PlayerPrefs.Save();
            ApplyTagSize(true);
        }

        private void ApplyTagSize(bool resetTracking)
        {
            float tagSizeMeters = SelectedTagSizeMillimeters * 0.001f;
            tagDetector?.SetTagSizeMeters(tagSizeMeters);
            if (hasOriginalBounds && normalizationRoot != null)
            {
                ApplyModelScale(
                    normalizationRoot,
                    originalModelBounds,
                    tagSizeMeters * modelSizeInTagWidths);
            }

            if (resetTracking)
            {
                RebindTag();
            }
        }

        public void OpenModelPicker()
        {
            if (pickerOpen || isLoading)
            {
                return;
            }

            pickerOpen = true;
            message = "Choose a binary glTF (.glb) file";
            // No platform filter: Android and iOS document providers handle
            // custom .glb types inconsistently. The selected file is validated.
            NativeFilePicker.PickFile(path =>
            {
                pickerOpen = false;
                if (string.IsNullOrWhiteSpace(path))
                {
                    message = currentAnchor == null
                        ? "Import cancelled"
                        : "Keeping the current model";
                    return;
                }

                _ = ImportModelFromPathAsync(path, true);
            });
        }

        /// <summary>
        /// Imports a GLB without opening the native picker. Other open-source
        /// front ends can call this API with their own local file path.
        /// </summary>
        public async Task ImportModelFromPathAsync(
            string path,
            bool saveAfterSuccess = false)
        {
            int version = ++loadVersion;
            if (!TryValidateGlb(path, out string validationError))
            {
                message = validationError;
                return;
            }

            isLoading = true;
            message = $"Loading {Path.GetFileName(path)}...";
            var stagedImport = new GltfImport();
            GameObject stagedAnchor = new GameObject("User Model Anchor");
            stagedAnchor.transform.SetParent(contentRoot, false);
            stagedAnchor.SetActive(false);
            GameObject stagedNormalization = new GameObject("Normalized Model");
            stagedNormalization.transform.SetParent(stagedAnchor.transform, false);

            try
            {
                var settings = new ImportSettings
                {
                    AnimationMethod = AnimationMethod.None,
                    GenerateMipMaps = true
                };
                bool loaded = await stagedImport.LoadFile(
                    path,
                    null,
                    settings);
                bool instantiated = loaded
                    && await stagedImport.InstantiateMainSceneAsync(
                        stagedNormalization.transform);

                if (version != loadVersion)
                {
                    Destroy(stagedAnchor);
                    stagedImport.Dispose();
                    return;
                }

                if (!instantiated
                    || !TryCalculateLocalBounds(
                        stagedNormalization.transform,
                        out Bounds modelBounds))
                {
                    Destroy(stagedAnchor);
                    stagedImport.Dispose();
                    message = "GLB has no renderable 3D mesh";
                    return;
                }

                ApplyModelScale(
                    stagedNormalization.transform,
                    modelBounds,
                    SelectedTagSizeMillimeters * 0.001f
                        * modelSizeInTagWidths);
                DisableModelShadows(stagedAnchor);

                DestroyCurrentModel();
                currentImport = stagedImport;
                currentAnchor = stagedAnchor;
                normalizationRoot = stagedNormalization.transform;
                originalModelBounds = modelBounds;
                hasOriginalBounds = true;
                currentFilename = Path.GetFileName(path);
                boundTagId = -1;
                hasAnchorPose = false;
                followVelocity = Vector3.zero;
                currentAnchor.SetActive(false);

                if (saveAfterSuccess)
                {
                    TrySaveModel(path);
                }

                message = "Model ready — show an AprilTag";
            }
            catch (Exception exception)
            {
                if (version == loadVersion)
                {
                    message = $"Import failed: {exception.Message}";
                }

                Destroy(stagedAnchor);
                stagedImport.Dispose();
            }
            finally
            {
                if (version == loadVersion)
                {
                    isLoading = false;
                }
            }
        }

        private bool TryValidateGlb(string path, out string error)
        {
            error = null;
            if (!File.Exists(path))
            {
                error = "The selected file is unavailable";
                return false;
            }

            // Validate the binary signature instead of the path suffix. On some
            // Android document providers Native File Picker copies the selected
            // GLB to an extensionless temporary path.
            var info = new FileInfo(path);
            long maximumBytes = maximumFileSizeMegabytes * 1024L * 1024L;
            if (info.Length < 12 || info.Length > maximumBytes)
            {
                error = $"GLB must be between 12 bytes and "
                    + $"{maximumFileSizeMegabytes} MB";
                return false;
            }

            byte[] header = new byte[12];
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, header.Length) != header.Length)
                {
                    error = "Cannot read the GLB header";
                    return false;
                }
            }

            bool validMagic = header[0] == (byte)'g'
                && header[1] == (byte)'l'
                && header[2] == (byte)'T'
                && header[3] == (byte)'F';
            uint glbVersion = BitConverter.ToUInt32(header, 4);
            uint declaredLength = BitConverter.ToUInt32(header, 8);
            if (!validMagic
                || glbVersion != 2
                || declaredLength != info.Length)
            {
                error = "Only binary glTF 2.0 (.glb) is supported";
                return false;
            }

            return true;
        }

        private void TrySaveModel(string sourcePath)
        {
            try
            {
                string folder = Path.GetDirectoryName(SavedModelPath);
                Directory.CreateDirectory(folder);
                if (string.Equals(
                    Path.GetFullPath(sourcePath),
                    Path.GetFullPath(SavedModelPath),
                    StringComparison.Ordinal))
                {
                    return;
                }

                string temporaryPath = SavedModelPath + ".tmp";
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                File.Copy(sourcePath, temporaryPath);
                if (File.Exists(SavedModelPath))
                {
                    File.Delete(SavedModelPath);
                }

                File.Move(temporaryPath, SavedModelPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"Model loaded but could not be saved: {exception.Message}",
                    this);
            }
        }

        public void RebindTag()
        {
            boundTagId = -1;
            hasAnchorPose = false;
            followVelocity = Vector3.zero;
            poseSource?.Clear();
            if (currentAnchor != null)
            {
                currentAnchor.SetActive(false);
                message = "Show the Tag you want to bind";
            }
        }

        private void Update()
        {
            if (currentAnchor == null || poseSource == null)
            {
                return;
            }

            Pose tagPose;
            if (boundTagId < 0)
            {
                if (!poseSource.TryGetNewestTagPose(
                    out tagPose,
                    out boundTagId,
                    out _,
                    out _))
                {
                    currentAnchor.SetActive(false);
                    return;
                }

                hasAnchorPose = false;
                message = $"Tracking Tag {boundTagId}";
            }
            else if (!poseSource.TryGetTagPose(
                boundTagId,
                out tagPose,
                out _,
                out _))
            {
                currentAnchor.SetActive(false);
                hasAnchorPose = false;
                followVelocity = Vector3.zero;
                message = $"Tag {boundTagId} lost — point the camera at it";
                return;
            }

            Pose target = BuildModelPose(tagPose);
            if (!hasAnchorPose)
            {
                currentAnchor.transform.SetPositionAndRotation(
                    target.position,
                    target.rotation);
                hasAnchorPose = true;
            }
            else
            {
                currentAnchor.transform.position = Vector3.SmoothDamp(
                    currentAnchor.transform.position,
                    target.position,
                    ref followVelocity,
                    followPositionSeconds);
                float blend = 1f - Mathf.Exp(
                    -followRotationSpeed * Time.deltaTime);
                currentAnchor.transform.rotation = Quaternion.Slerp(
                    currentAnchor.transform.rotation,
                    target.rotation,
                    blend);
            }

            currentAnchor.SetActive(true);
            message = $"Tracking Tag {boundTagId}";
        }

        private Pose BuildModelPose(Pose tagPose)
        {
            Vector3 surfaceNormal = -(tagPose.rotation * Vector3.forward);
            Vector3 tagTop = tagPose.rotation * Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(tagTop, surfaceNormal);
            return new Pose(
                tagPose.position + surfaceNormal * surfaceOffsetMeters,
                rotation);
        }

        public static bool TryCalculateLocalBounds(
            Transform root,
            out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter filter = meshFilters[i];
                if (filter.sharedMesh != null)
                {
                    EncapsulateBounds(
                        root,
                        filter.transform,
                        filter.sharedMesh.bounds,
                        ref bounds,
                        ref hasBounds);
                }
            }

            SkinnedMeshRenderer[] skinnedRenderers =
                root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                EncapsulateBounds(
                    root,
                    skinnedRenderers[i].transform,
                    skinnedRenderers[i].localBounds,
                    ref bounds,
                    ref hasBounds);
            }

            return hasBounds
                && Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z)
                    > 0.000001f;
        }

        private static void EncapsulateBounds(
            Transform root,
            Transform source,
            Bounds sourceBounds,
            ref Bounds result,
            ref bool hasBounds)
        {
            Vector3 minimum = sourceBounds.min;
            Vector3 maximum = sourceBounds.max;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localPoint = new Vector3(
                    (corner & 1) == 0 ? minimum.x : maximum.x,
                    (corner & 2) == 0 ? minimum.y : maximum.y,
                    (corner & 4) == 0 ? minimum.z : maximum.z);
                Vector3 rootPoint = root.InverseTransformPoint(
                    source.TransformPoint(localPoint));
                if (hasBounds)
                {
                    result.Encapsulate(rootPoint);
                }
                else
                {
                    result = new Bounds(rootPoint, Vector3.zero);
                    hasBounds = true;
                }
            }
        }

        public static void ApplyModelScale(
            Transform root,
            Bounds unscaledBounds,
            float targetLongestEdgeMeters)
        {
            float longestEdge = Mathf.Max(
                unscaledBounds.size.x,
                unscaledBounds.size.y,
                unscaledBounds.size.z);
            float scale = Mathf.Max(0.0001f, targetLongestEdgeMeters)
                / Mathf.Max(0.000001f, longestEdge);
            root.localScale = Vector3.one * scale;
            root.localPosition = new Vector3(
                -unscaledBounds.center.x * scale,
                -unscaledBounds.min.y * scale,
                -unscaledBounds.center.z * scale);
        }

        private static void DisableModelShadows(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].shadowCastingMode = ShadowCastingMode.Off;
                renderers[i].receiveShadows = false;
            }
        }

        private void DestroyCurrentModel()
        {
            if (currentAnchor != null)
            {
                Destroy(currentAnchor);
            }

            currentImport?.Dispose();
            currentImport = null;
            currentAnchor = null;
            normalizationRoot = null;
            hasOriginalBounds = false;
        }

        private void OnDestroy()
        {
            loadVersion++;
            DestroyCurrentModel();
        }

        private void EnsureGuiStyles()
        {
            if (panelStyle != null && styledScreenHeight == Screen.height)
            {
                return;
            }

            styledScreenHeight = Screen.height;
            int fontSize = Mathf.Clamp(
                Mathf.RoundToInt(Screen.height * 0.021f),
                14,
                26);
            panelStyle = new GUIStyle(GUI.skin.box);
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize + 2,
                fontStyle = FontStyle.Bold
            };
            titleStyle.normal.textColor = Color.white;
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = true
            };
            labelStyle.normal.textColor = Color.white;
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter
            };
        }

        private void OnGUI()
        {
            EnsureGuiStyles();
            Rect safe = Screen.safeArea;
            float width = Mathf.Min(
                720f,
                Mathf.Max(280f, safe.width - 24f));
            float x = safe.x + (safe.width - width) * 0.5f;
            float y = Screen.height - safe.yMax + 12f;
            float height = 240f;
            GUI.Box(new Rect(x, y, width, height), GUIContent.none, panelStyle);

            GUI.Label(
                new Rect(x + 12f, y + 8f, width - 24f, 32f),
                "TAG MODEL AR",
                titleStyle);
            string modelLabel = string.IsNullOrEmpty(currentFilename)
                ? "Model: none"
                : $"Model: {currentFilename}";
            GUI.Label(
                new Rect(x + 12f, y + 39f, width - 24f, 48f),
                $"{modelLabel}\n{message}",
                labelStyle);

            float half = (width - 30f) * 0.5f;
            bool previousEnabled = GUI.enabled;
            GUI.enabled = !pickerOpen && !isLoading;
            if (GUI.Button(
                new Rect(x + 12f, y + 88f, half, 48f),
                "IMPORT .GLB / 导入模型",
                buttonStyle))
            {
                OpenModelPicker();
            }

            GUI.enabled = currentAnchor != null && !isLoading;
            if (GUI.Button(
                new Rect(x + 18f + half, y + 88f, half, 48f),
                "REBIND TAG / 重新绑定",
                buttonStyle))
            {
                RebindTag();
            }

            GUI.enabled = previousEnabled;
            GUI.Label(
                new Rect(x + 12f, y + 142f, width - 24f, 28f),
                $"TAG SIZE / 实际黑框边长   "
                    + $"MODEL MAX = {modelSizeInTagWidths:F1}× TAG",
                labelStyle);

            int count = tagSizePresetsMillimeters.Length;
            float gap = 6f;
            float buttonWidth = (width - 24f - gap * (count - 1)) / count;
            Color previousColor = GUI.backgroundColor;
            for (int i = 0; i < count; i++)
            {
                GUI.backgroundColor = i == selectedTagSizeIndex
                    ? new Color(0.2f, 0.8f, 1f)
                    : Color.white;
                if (GUI.Button(
                    new Rect(
                        x + 12f + i * (buttonWidth + gap),
                        y + 178f,
                        buttonWidth,
                        48f),
                    $"{tagSizePresetsMillimeters[i]:F0} mm",
                    buttonStyle))
                {
                    SelectTagSize(i);
                }
            }

            GUI.backgroundColor = previousColor;
        }
    }
}
