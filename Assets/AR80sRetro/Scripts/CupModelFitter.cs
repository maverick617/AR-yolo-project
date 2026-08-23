using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Fits the current retro cup asset to a measured metric envelope and recenters
    /// its visual geometry around a logical cup-center tracking transform.
    /// </summary>
    public sealed class CupModelFitter : MonoBehaviour
    {
        private Transform visualRoot;
        private Vector3 originalVisualScale;
        private readonly List<Vector3> vertexBuffer = new List<Vector3>(32768);
        private readonly List<float> horizontalXSamples = new List<float>(32768);
        private readonly List<float> horizontalZSamples = new List<float>(32768);
        private Vector3 lastMeasuredSizeMeters;
        private bool lastFitWidthAndHeight;
        private float lastMaximumNonUniformScaleRatio;
        private bool hasSuccessfulFit;
        private bool initialized;

        public bool InitializeAndFit(
            GameObject visual,
            Vector3 measuredSizeMeters,
            bool fitWidthAndHeight,
            float maximumNonUniformScaleRatio)
        {
            if (visual == null)
            {
                return false;
            }

            visualRoot = visual.transform;
            originalVisualScale = visualRoot.localScale;
            initialized = true;
            return Fit(
                measuredSizeMeters,
                fitWidthAndHeight,
                maximumNonUniformScaleRatio);
        }

        public bool Fit(
            Vector3 measuredSizeMeters,
            bool fitWidthAndHeight,
            float maximumNonUniformScaleRatio)
        {
            if (!initialized || visualRoot == null)
            {
                return false;
            }

            if (hasSuccessfulFit
                && (lastMeasuredSizeMeters - measuredSizeMeters).sqrMagnitude < 0.00000001f
                && lastFitWidthAndHeight == fitWidthAndHeight
                && Mathf.Approximately(
                    lastMaximumNonUniformScaleRatio,
                    maximumNonUniformScaleRatio))
            {
                return true;
            }

            visualRoot.localScale = originalVisualScale;
            visualRoot.localPosition = Vector3.zero;
            if (!TryGetMeshBoundsInLocalSpace(transform, visualRoot.gameObject, out Bounds bounds)
                || bounds.size.x <= 0.0001f
                || bounds.size.y <= 0.0001f
                || bounds.size.z <= 0.0001f)
            {
                return false;
            }

            float heightFactor = measuredSizeMeters.y / bounds.size.y;
            // Z stores the registered cup-body diameter. X may include the
            // physical handle envelope, so fitting the continuous model against
            // X would shrink its body whenever the handle is prominent.
            float horizontalTarget = measuredSizeMeters.z > 0.01f
                ? measuredSizeMeters.z
                : measuredSizeMeters.x;
            float modelHorizontal = Mathf.Min(bounds.size.x, bounds.size.z);
            if (TryGetRobustBodyHorizontalMetrics(
                transform,
                visualRoot.gameObject,
                out _,
                out float robustBodyDiameter)
                && robustBodyDiameter > 0.0001f)
            {
                modelHorizontal = robustBodyDiameter;
            }

            float horizontalFactor = horizontalTarget / modelHorizontal;
            Vector3 factor = fitWidthAndHeight
                ? new Vector3(horizontalFactor, heightFactor, horizontalFactor)
                : Vector3.one * heightFactor;

            float ratioLimit = Mathf.Max(1f, maximumNonUniformScaleRatio);
            float minimumFactor = Mathf.Max(0.0001f, Mathf.Min(factor.x, factor.y, factor.z));
            float maximumFactor = Mathf.Max(factor.x, factor.y, factor.z);
            if (maximumFactor / minimumFactor > ratioLimit)
            {
                float geometricMean = Mathf.Sqrt(Mathf.Max(0.0001f, horizontalFactor * heightFactor));
                float halfRatio = Mathf.Sqrt(ratioLimit);
                factor.x = Mathf.Clamp(
                    factor.x,
                    geometricMean / halfRatio,
                    geometricMean * halfRatio);
                factor.y = Mathf.Clamp(
                    factor.y,
                    geometricMean / halfRatio,
                    geometricMean * halfRatio);
                factor.z = factor.x;
            }

            visualRoot.localScale = Vector3.Scale(originalVisualScale, factor);
            if (!TryGetMeshBoundsInLocalSpace(transform, visualRoot.gameObject, out Bounds fittedBounds))
            {
                return false;
            }

            // Center Y from the full envelope, but use a robust vertex median for
            // X/Z. The full bounds center is pulled toward the handle and would
            // move the cylindrical cup body away from the registered physical center.
            Vector3 visualCenter = fittedBounds.center;
            if (TryGetRobustBodyHorizontalMetrics(
                transform,
                visualRoot.gameObject,
                out Vector2 horizontalCenter,
                out _))
            {
                visualCenter.x = horizontalCenter.x;
                visualCenter.z = horizontalCenter.y;
            }

            visualRoot.localPosition -= visualCenter;
            lastMeasuredSizeMeters = measuredSizeMeters;
            lastFitWidthAndHeight = fitWidthAndHeight;
            lastMaximumNonUniformScaleRatio = maximumNonUniformScaleRatio;
            hasSuccessfulFit = true;
            return true;
        }

        private bool TryGetRobustBodyHorizontalMetrics(
            Transform reference,
            GameObject visual,
            out Vector2 center,
            out float bodyDiameter)
        {
            center = default;
            bodyDiameter = 0f;
            horizontalXSamples.Clear();
            horizontalZSamples.Clear();

            MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || !mesh.isReadable)
                {
                    continue;
                }

                vertexBuffer.Clear();
                mesh.GetVertices(vertexBuffer);
                for (int vertexIndex = 0; vertexIndex < vertexBuffer.Count; vertexIndex++)
                {
                    Vector3 point = reference.InverseTransformPoint(
                        filter.transform.TransformPoint(vertexBuffer[vertexIndex]));
                    horizontalXSamples.Add(point.x);
                    horizontalZSamples.Add(point.z);
                }
            }

            if (horizontalXSamples.Count < 16)
            {
                return false;
            }

            horizontalXSamples.Sort();
            horizontalZSamples.Sort();
            int middle = horizontalXSamples.Count / 2;
            center = new Vector2(
                horizontalXSamples[middle],
                horizontalZSamples[middle]);
            float xExtent = Percentile(horizontalXSamples, 0.97f)
                - Percentile(horizontalXSamples, 0.03f);
            float zExtent = Percentile(horizontalZSamples, 0.97f)
                - Percentile(horizontalZSamples, 0.03f);
            bodyDiameter = Mathf.Min(xExtent, zExtent);
            return true;
        }

        private static float Percentile(List<float> sortedValues, float percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0f;
            }

            float index = Mathf.Clamp01(percentile) * (sortedValues.Count - 1);
            int lower = Mathf.FloorToInt(index);
            int upper = Mathf.Min(lower + 1, sortedValues.Count - 1);
            return Mathf.Lerp(sortedValues[lower], sortedValues[upper], index - lower);
        }

        private static bool TryGetMeshBoundsInLocalSpace(
            Transform reference,
            GameObject visual,
            out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                EncapsulateTransformedBounds(
                    reference,
                    filter.transform,
                    filter.sharedMesh.bounds,
                    ref bounds,
                    ref hasBounds);
            }

            SkinnedMeshRenderer[] skinned = visual.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (int i = 0; i < skinned.Length; i++)
            {
                EncapsulateTransformedBounds(
                    reference,
                    skinned[i].transform,
                    skinned[i].localBounds,
                    ref bounds,
                    ref hasBounds);
            }

            return hasBounds;
        }

        private static void EncapsulateTransformedBounds(
            Transform reference,
            Transform source,
            Bounds sourceBounds,
            ref Bounds result,
            ref bool hasBounds)
        {
            Vector3 min = sourceBounds.min;
            Vector3 max = sourceBounds.max;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localCorner = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                Vector3 point = reference.InverseTransformPoint(source.TransformPoint(localCorner));
                if (!hasBounds)
                {
                    result = new Bounds(point, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    result.Encapsulate(point);
                }
            }
        }
    }
}
