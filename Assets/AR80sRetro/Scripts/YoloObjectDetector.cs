using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;

namespace AR80sRetro
{
    public sealed class YoloObjectDetector : MonoBehaviour
    {
        private readonly struct TargetClass
        {
            public TargetClass(int index, string label)
            {
                Index = index;
                Label = label;
            }

            public int Index { get; }
            public string Label { get; }
        }

        private readonly struct Candidate
        {
            public Candidate(
                Rect screenBox,
                Rect modelBox,
                float confidence,
                string label,
                float[] maskCoefficients)
            {
                ScreenBox = screenBox;
                ModelBox = modelBox;
                Confidence = confidence;
                Label = label;
                MaskCoefficients = maskCoefficients;
            }

            public Rect ScreenBox { get; }
            public Rect ModelBox { get; }
            public float Confidence { get; }
            public string Label { get; }
            public float[] MaskCoefficients { get; }
        }

        private const int InputSize = 640;
        private const int CocoClassCount = 80;
        private static readonly TargetClass[] TargetClasses =
        {
            // COCO class IDs emitted by the bundled standard yolov8n model.
            // Labels intentionally match Retro Prefab Library.asset.
            new TargetClass(39, "bottle"),
            new TargetClass(41, "cup"),
            new TargetClass(56, "chair"),
            new TargetClass(57, "couch"),
            new TargetClass(58, "plant"),
            new TargetClass(60, "table"),
            new TargetClass(62, "tv"),
            new TargetClass(67, "phone")
        };

        [Header("Dependencies")]
        [SerializeField] private Unity.InferenceEngine.ModelAsset modelAsset;
        [Tooltip("Optional YOLO segmentation ONNX. When assigned, it replaces the detection-only model and supplies per-object masks.")]
        [SerializeField] private Unity.InferenceEngine.ModelAsset segmentationModelAsset;
        [SerializeField] private ARCameraFrameProvider frameProvider;

        [Header("Inference")]
        [SerializeField] private Unity.InferenceEngine.BackendType backendType = Unity.InferenceEngine.BackendType.GPUCompute;
        [SerializeField, Min(0.1f)] private float inferenceIntervalSeconds = 0.1f;
        [Tooltip("Comma-separated COCO classes to emit. Leave empty to emit every configured replacement class.")]
        [SerializeField] private string targetLabelFilter = "bottle,cup,phone";
        [SerializeField, Range(0.05f, 1f)] private float confidenceThreshold = 0.45f;
        [SerializeField, Range(0.05f, 1f)] private float iouThreshold = 0.45f;
        [SerializeField, Min(1)] private int maxDetections = 12;
        [SerializeField] private bool logDetections = true;
        [SerializeField, Min(1)] private int diagnosticLogInterval = 10;
        [SerializeField, Range(24, 160)] private int segmentationMaskResolution = 64;
        [SerializeField, Range(0.05f, 0.95f)] private float segmentationMaskThreshold = 0.5f;

        public event Action<IReadOnlyList<DetectionResult>> DetectionsReady;

        private readonly List<Candidate> candidates = new List<Candidate>(64);
        private readonly List<Candidate> selectedCandidates = new List<Candidate>(16);
        private readonly List<DetectionResult> detectionResults = new List<DetectionResult>(16);
        private readonly float[] highestTargetConfidences = new float[TargetClasses.Length];
        private readonly HashSet<string> enabledTargetLabels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string cachedTargetLabelFilter;

        private Unity.InferenceEngine.Worker worker;
        private Unity.InferenceEngine.Tensor<float> inputTensor;
        private float nextInferenceTime;
        private bool hasLoggedOutputShape;
        private bool initializationFailed;
        private int inferenceCount;
        private int runtimeOutputCount;
        private bool inferencePending;
        private int inferenceGeneration;

        private void Reset()
        {
            frameProvider = FindObjectOfType<ARCameraFrameProvider>();
        }

        private void OnEnable()
        {
            TryInitialize();
        }

        private void Update()
        {
            if (inferencePending || Time.time < nextInferenceTime)
            {
                return;
            }

            nextInferenceTime = Time.time + inferenceIntervalSeconds;

            if (!TryInitialize() || !frameProvider.TryUpdateFrame())
            {
                return;
            }

            RunInference(frameProvider.CameraTexture);
        }

        private bool TryInitialize()
        {
            if (worker != null)
            {
                return true;
            }

            if (initializationFailed)
            {
                return false;
            }

            if ((modelAsset == null && segmentationModelAsset == null)
                || frameProvider == null)
            {
                return false;
            }

            try
            {
                Unity.InferenceEngine.ModelAsset selectedModelAsset = segmentationModelAsset != null
                    ? segmentationModelAsset
                    : modelAsset;
                Unity.InferenceEngine.Model runtimeModel = Unity.InferenceEngine.ModelLoader.Load(selectedModelAsset);
                runtimeOutputCount = runtimeModel.outputs.Count;
                Unity.InferenceEngine.BackendType selectedBackend = backendType;
                if (selectedBackend == Unity.InferenceEngine.BackendType.GPUCompute && !SystemInfo.supportsComputeShaders)
                {
                    selectedBackend = Unity.InferenceEngine.BackendType.CPU;
                    Debug.LogWarning("Compute shaders are unavailable. YOLO is using the CPU backend.", this);
                }

                worker = new Unity.InferenceEngine.Worker(runtimeModel, selectedBackend);
                inputTensor = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 3, InputSize, InputSize));
                Debug.Log(
                    $"YOLO initialized with {selectedBackend} backend; "
                    + $"mode={(segmentationModelAsset != null ? "segmentation" : "detection")}, "
                    + $"outputs={runtimeOutputCount}.",
                    this);
                if (segmentationModelAsset == null)
                {
                    Debug.LogWarning(
                        "YOLO segmentation ONNX is not assigned. XR initialization will use detector boxes plus depth, not pixel masks.",
                        this);
                }
                else if (runtimeOutputCount < 2)
                {
                    Debug.LogWarning(
                        "The assigned segmentation model has fewer than two outputs. This parser expects standard YOLOv8-seg detection and prototype outputs.",
                        this);
                }

                return true;
            }
            catch (Exception exception)
            {
                initializationFailed = true;
                Debug.LogException(exception, this);
                return false;
            }
        }

        private void RunInference(Texture2D cameraTexture)
        {
            if (inferencePending)
            {
                return;
            }

            try
            {
                Unity.InferenceEngine.TextureTransform transform = new Unity.InferenceEngine.TextureTransform()
                    .SetDimensions(InputSize, InputSize, 3)
                    .SetTensorLayout(Unity.InferenceEngine.TensorLayout.NCHW)
                    .SetCoordOrigin(Unity.InferenceEngine.CoordOrigin.TopLeft);

                Unity.InferenceEngine.TextureConverter.ToTensor(cameraTexture, inputTensor, transform);
                worker.Schedule(inputTensor);

                Unity.InferenceEngine.Tensor<float> output = worker.PeekOutput() as Unity.InferenceEngine.Tensor<float>;
                if (output == null)
                {
                    Debug.LogError("YOLO output is not a float tensor.", this);
                    return;
                }

                Unity.InferenceEngine.Tensor<float> prototypeOutput = runtimeOutputCount > 1
                    ? worker.PeekOutput(1) as Unity.InferenceEngine.Tensor<float>
                    : null;
                inferencePending = true;
                int generation = inferenceGeneration;
                CompleteInferenceAsync(output, prototypeOutput, generation);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private async void CompleteInferenceAsync(
            Unity.InferenceEngine.Tensor<float> output,
            Unity.InferenceEngine.Tensor<float> prototypes,
            int generation)
        {
            Unity.InferenceEngine.Tensor<float> cpuOutput = null;
            Unity.InferenceEngine.Tensor<float> cpuPrototypes = null;
            try
            {
                // Synchronous ReadbackAndClone stalled the iPhone main thread on
                // every YOLO frame. GPU readback is now awaited without blocking
                // AR rendering or device pose updates.
                output.ReadbackRequest();
                prototypes?.ReadbackRequest();
                while (!output.IsReadbackRequestDone()
                    || (prototypes != null && !prototypes.IsReadbackRequestDone()))
                {
                    await Awaitable.NextFrameAsync();
                }

                cpuOutput = output.ReadbackAndClone();
                cpuPrototypes = prototypes?.ReadbackAndClone();

                if (generation != inferenceGeneration || !isActiveAndEnabled)
                {
                    return;
                }

                LogOutputShapes(cpuOutput, cpuPrototypes);
                ParseTargetDetections(cpuOutput, cpuPrototypes);
                inferenceCount++;
                DetectionsReady?.Invoke(detectionResults);
            }
            catch (Exception exception)
            {
                if (generation == inferenceGeneration)
                {
                    Debug.LogException(exception, this);
                }
            }
            finally
            {
                cpuPrototypes?.Dispose();
                cpuOutput?.Dispose();
                if (generation == inferenceGeneration)
                {
                    inferencePending = false;
                }
            }
        }

        private void LogOutputShapes(
            Unity.InferenceEngine.Tensor<float> detections,
            Unity.InferenceEngine.Tensor<float> prototypes)
        {
            if (hasLoggedOutputShape)
            {
                return;
            }

            hasLoggedOutputShape = true;
            Debug.Log(
                prototypes != null
                    ? $"YOLO output shapes: detections={detections.shape}, masks={prototypes.shape}"
                    : $"YOLO output shape: {detections.shape}",
                this);
        }

        private void ParseTargetDetections(
            Unity.InferenceEngine.Tensor<float> output,
            Unity.InferenceEngine.Tensor<float> maskPrototypes)
        {
            candidates.Clear();
            selectedCandidates.Clear();
            detectionResults.Clear();

            if (output.shape.rank != 3 || output.shape[0] != 1)
            {
                Debug.LogError($"Unsupported YOLO output shape: {output.shape}", this);
                return;
            }

            int dimensionOne = output.shape[1];
            int dimensionTwo = output.shape[2];
            int detectionChannels = CocoClassCount + 4;
            bool channelsFirst = dimensionOne >= detectionChannels
                && dimensionOne <= detectionChannels + 64;
            bool channelsLast = dimensionTwo >= detectionChannels
                && dimensionTwo <= detectionChannels + 64;

            if (!channelsFirst && !channelsLast)
            {
                Debug.LogError(
                    $"Expected a YOLO detection/segmentation output with at least 84 channels, received {output.shape}.",
                    this);
                return;
            }

            int boxCount = channelsFirst ? dimensionTwo : dimensionOne;
            int channelCount = channelsFirst ? dimensionOne : dimensionTwo;
            int maskCoefficientCount = channelCount - detectionChannels;
            ReadOnlySpan<float> values = output.AsReadOnlySpan();
            float highestAnyConfidence = 0f;
            int highestClassIndex = -1;
            Array.Clear(highestTargetConfidences, 0, highestTargetConfidences.Length);

            for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
            {
                float targetConfidence = 0f;
                string targetLabel = string.Empty;

                for (int classIndex = 0; classIndex < CocoClassCount; classIndex++)
                {
                    float classConfidence = ReadOutput(
                        values,
                        channelsFirst,
                        boxCount,
                        channelCount,
                        boxIndex,
                        classIndex + 4);

                    if (classConfidence > highestAnyConfidence)
                    {
                        highestAnyConfidence = classConfidence;
                        highestClassIndex = classIndex;
                    }
                }

                for (int targetIndex = 0; targetIndex < TargetClasses.Length; targetIndex++)
                {
                    TargetClass targetClass = TargetClasses[targetIndex];
                    float confidence = ReadOutput(
                        values,
                        channelsFirst,
                        boxCount,
                        channelCount,
                        boxIndex,
                        targetClass.Index + 4);

                    highestTargetConfidences[targetIndex] = Mathf.Max(
                        highestTargetConfidences[targetIndex],
                        confidence);

                    if (!IsTargetLabelEnabled(targetClass.Label))
                    {
                        continue;
                    }

                    if (confidence > targetConfidence)
                    {
                        targetConfidence = confidence;
                        targetLabel = targetClass.Label;
                    }
                }

                if (targetConfidence < confidenceThreshold)
                {
                    continue;
                }

                float centerX = ReadOutput(values, channelsFirst, boxCount, channelCount, boxIndex, 0);
                float centerY = ReadOutput(values, channelsFirst, boxCount, channelCount, boxIndex, 1);
                float width = ReadOutput(values, channelsFirst, boxCount, channelCount, boxIndex, 2);
                float height = ReadOutput(values, channelsFirst, boxCount, channelCount, boxIndex, 3);

                Rect normalizedBox = Rect.MinMaxRect(
                    Mathf.Clamp01((centerX - width * 0.5f) / InputSize),
                    Mathf.Clamp01((centerY - height * 0.5f) / InputSize),
                    Mathf.Clamp01((centerX + width * 0.5f) / InputSize),
                    Mathf.Clamp01((centerY + height * 0.5f) / InputSize));

                if (normalizedBox.width <= 0f || normalizedBox.height <= 0f)
                {
                    continue;
                }

                Rect screenBox = frameProvider.ImageRectToScreenRect(normalizedBox);
                float[] maskCoefficients = null;
                if (maskCoefficientCount > 0 && maskPrototypes != null)
                {
                    maskCoefficients = new float[maskCoefficientCount];
                    for (int coefficientIndex = 0;
                        coefficientIndex < maskCoefficientCount;
                        coefficientIndex++)
                    {
                        maskCoefficients[coefficientIndex] = ReadOutput(
                            values,
                            channelsFirst,
                            boxCount,
                            channelCount,
                            boxIndex,
                            detectionChannels + coefficientIndex);
                    }
                }

                candidates.Add(new Candidate(
                    screenBox,
                    normalizedBox,
                    targetConfidence,
                    targetLabel,
                    maskCoefficients));
            }

            if (diagnosticLogInterval > 0 && inferenceCount % diagnosticLogInterval == 0)
            {
                Debug.Log(
                    $"YOLO diagnostics: bestClass={highestClassIndex}, bestScore={highestAnyConfidence:F3}, " +
                    $"{FormatTargetScores(highestTargetConfidences)}, " +
                    $"frame={frameProvider.CameraTexture.width}x{frameProvider.CameraTexture.height}",
                    this);
            }

            candidates.Sort((left, right) => right.Confidence.CompareTo(left.Confidence));
            ApplyNonMaximumSuppression();

            int resultCount = Mathf.Min(maxDetections, selectedCandidates.Count);
            for (int i = 0; i < resultCount; i++)
            {
                Candidate candidate = selectedCandidates[i];
                DetectionMask mask = BuildDetectionMask(candidate, maskPrototypes);
                detectionResults.Add(new DetectionResult(
                    candidate.Label,
                    candidate.Confidence,
                    candidate.ScreenBox,
                    mask));
            }

            if (logDetections && detectionResults.Count > 0)
            {
                foreach (DetectionResult result in detectionResults)
                {
                    Debug.Log($"YOLO detect: {result.Label}, confidence={result.Confidence:F2}, box={result.NormalizedBox}", this);
                }
            }
        }

        private bool IsTargetLabelEnabled(string label)
        {
            if (string.IsNullOrWhiteSpace(targetLabelFilter))
            {
                return true;
            }

            if (!string.Equals(
                cachedTargetLabelFilter,
                targetLabelFilter,
                StringComparison.Ordinal))
            {
                cachedTargetLabelFilter = targetLabelFilter;
                enabledTargetLabels.Clear();
                string[] labels = targetLabelFilter.Split(
                    new[] { ',', ';' },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < labels.Length; i++)
                {
                    string candidate = labels[i].Trim();
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        enabledTargetLabels.Add(candidate);
                    }
                }
            }

            return enabledTargetLabels.Count == 0 || enabledTargetLabels.Contains(label);
        }

        private void ApplyNonMaximumSuppression()
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate candidate = candidates[i];
                bool overlapsSelected = false;

                for (int j = 0; j < selectedCandidates.Count; j++)
                {
                    Candidate selected = selectedCandidates[j];
                    if (!string.Equals(candidate.Label, selected.Label, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (CalculateIntersectionOverUnion(
                        candidate.ScreenBox,
                        selected.ScreenBox) > iouThreshold)
                    {
                        overlapsSelected = true;
                        break;
                    }
                }

                if (!overlapsSelected)
                {
                    selectedCandidates.Add(candidate);
                }

                if (selectedCandidates.Count >= maxDetections)
                {
                    break;
                }
            }
        }

        private static float ReadOutput(
            ReadOnlySpan<float> values,
            bool channelsFirst,
            int boxCount,
            int channelCount,
            int boxIndex,
            int channelIndex)
        {
            return channelsFirst
                ? values[channelIndex * boxCount + boxIndex]
                : values[boxIndex * channelCount + channelIndex];
        }

        private DetectionMask BuildDetectionMask(
            Candidate candidate,
            Unity.InferenceEngine.Tensor<float> prototypes)
        {
            float[] coefficients = candidate.MaskCoefficients;
            if (prototypes == null
                || coefficients == null
                || coefficients.Length == 0
                || prototypes.shape.rank != 4
                || prototypes.shape[0] != 1)
            {
                return null;
            }

            bool channelsFirst = prototypes.shape[1] == coefficients.Length;
            bool channelsLast = prototypes.shape[3] == coefficients.Length;
            if (!channelsFirst && !channelsLast)
            {
                return null;
            }

            int prototypeHeight = channelsFirst
                ? prototypes.shape[2]
                : prototypes.shape[1];
            int prototypeWidth = channelsFirst
                ? prototypes.shape[3]
                : prototypes.shape[2];
            int resolution = Mathf.Max(8, segmentationMaskResolution);
            byte[] mask = new byte[resolution * resolution];
            ReadOnlySpan<float> prototypeValues = prototypes.AsReadOnlySpan();

            for (int y = 0; y < resolution; y++)
            {
                float v = (y + 0.5f) / resolution;
                float imageY = Mathf.Lerp(
                    candidate.ModelBox.yMin,
                    candidate.ModelBox.yMax,
                    v);
                int prototypeY = Mathf.Clamp(
                    Mathf.FloorToInt(imageY * prototypeHeight),
                    0,
                    prototypeHeight - 1);

                for (int x = 0; x < resolution; x++)
                {
                    float u = (x + 0.5f) / resolution;
                    float imageX = Mathf.Lerp(
                        candidate.ModelBox.xMin,
                        candidate.ModelBox.xMax,
                        u);
                    int prototypeX = Mathf.Clamp(
                        Mathf.FloorToInt(imageX * prototypeWidth),
                        0,
                        prototypeWidth - 1);
                    float logit = 0f;
                    for (int channel = 0; channel < coefficients.Length; channel++)
                    {
                        int index = channelsFirst
                            ? ((channel * prototypeHeight) + prototypeY)
                                * prototypeWidth + prototypeX
                            : ((prototypeY * prototypeWidth) + prototypeX)
                                * coefficients.Length + channel;
                        logit += coefficients[channel] * prototypeValues[index];
                    }

                    float probability = 1f / (1f + Mathf.Exp(-logit));
                    mask[y * resolution + x] = probability >= segmentationMaskThreshold
                        ? (byte)255
                        : (byte)0;
                }
            }

            return new DetectionMask(
                candidate.ScreenBox,
                resolution,
                resolution,
                mask);
        }

        private static string FormatTargetScores(float[] scores)
        {
            StringBuilder builder = new StringBuilder(128);
            for (int i = 0; i < TargetClasses.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(TargetClasses[i].Label);
                builder.Append("Score=");
                builder.Append(scores[i].ToString("F3"));
            }

            return builder.ToString();
        }

        private static float CalculateIntersectionOverUnion(Rect first, Rect second)
        {
            float intersectionXMin = Mathf.Max(first.xMin, second.xMin);
            float intersectionYMin = Mathf.Max(first.yMin, second.yMin);
            float intersectionXMax = Mathf.Min(first.xMax, second.xMax);
            float intersectionYMax = Mathf.Min(first.yMax, second.yMax);
            float intersectionWidth = Mathf.Max(0f, intersectionXMax - intersectionXMin);
            float intersectionHeight = Mathf.Max(0f, intersectionYMax - intersectionYMin);
            float intersectionArea = intersectionWidth * intersectionHeight;
            float unionArea = first.width * first.height + second.width * second.height - intersectionArea;
            return unionArea > 0f ? intersectionArea / unionArea : 0f;
        }

        private void OnDisable()
        {
            DisposeResources();
        }

        private void OnDestroy()
        {
            DisposeResources();
        }

        private void DisposeResources()
        {
            inferenceGeneration++;
            inferencePending = false;
            worker?.Dispose();
            worker = null;

            inputTensor?.Dispose();
            inputTensor = null;
        }
    }
}
