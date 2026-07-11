using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace AR80sRetro
{
    public sealed class ARRaycastPositionSolver : MonoBehaviour
    {
        private static readonly List<ARRaycastHit> Hits = new List<ARRaycastHit>();

        [SerializeField] private ARRaycastManager raycastManager;
        [SerializeField] private Camera arCamera;
        [SerializeField] private ARDepthFrameProvider depthProvider;
        [SerializeField] private TrackableType trackableTypes = TrackableType.PlaneWithinPolygon;
        [SerializeField] private Vector2 anchorInBoundingBox = new Vector2(0.5f, 0.9f);
        [SerializeField] private bool faceCamera;
        [SerializeField, Range(0f, 1f)] private float depthHorizontalWeight = 0.75f;
        [SerializeField, Min(0.05f)] private float maxDepthPlaneHorizontalDeltaMeters = 0.6f;

        private void Reset()
        {
            raycastManager = FindObjectOfType<ARRaycastManager>();
            depthProvider = FindObjectOfType<ARDepthFrameProvider>();
            arCamera = Camera.main;
        }

        public bool TrySolvePose(DetectionResult detection, out Pose pose)
        {
            return TrySolvePose(detection, anchorInBoundingBox, out pose);
        }

        public bool TrySolvePose(
            DetectionResult detection,
            Vector2 normalizedAnchorInBox,
            out Pose pose)
        {
            pose = default;

            if (raycastManager == null)
            {
                return false;
            }

            Vector2 effectiveAnchor = normalizedAnchorInBox;
            if (effectiveAnchor == Vector2.zero)
            {
                effectiveAnchor = new Vector2(0.5f, 0.9f);
            }

            Vector2 screenPoint = detection.ToScreenPoint(
                Screen.width,
                Screen.height,
                effectiveAnchor);
            if (!raycastManager.Raycast(screenPoint, Hits, trackableTypes))
            {
                return false;
            }

            pose = Hits[0].pose;
            Vector3 planePosition = pose.position;

            if (depthProvider != null
                && depthHorizontalWeight > 0f
                && depthProvider.TrySampleWorldPoint(
                    detection,
                    new Vector2(0.5f, 0.5f),
                    out Vector3 depthWorldPoint,
                    out _,
                    out _))
            {
                Vector2 planeHorizontal = new Vector2(planePosition.x, planePosition.z);
                Vector2 depthHorizontal = new Vector2(depthWorldPoint.x, depthWorldPoint.z);
                float horizontalDelta = Vector2.Distance(planeHorizontal, depthHorizontal);
                float effectiveWeight = horizontalDelta > maxDepthPlaneHorizontalDeltaMeters
                    ? depthHorizontalWeight * 0.35f
                    : depthHorizontalWeight;

                pose.position = new Vector3(
                    Mathf.Lerp(planePosition.x, depthWorldPoint.x, effectiveWeight),
                    planePosition.y,
                    Mathf.Lerp(planePosition.z, depthWorldPoint.z, effectiveWeight));
            }

            if (faceCamera && arCamera != null)
            {
                Vector3 toCamera = arCamera.transform.position - pose.position;
                toCamera.y = 0f;
                if (toCamera.sqrMagnitude > 0.0001f)
                {
                    pose.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
                }
            }

            return true;
        }
    }
}
