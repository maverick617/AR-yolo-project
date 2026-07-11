using UnityEngine;

namespace AR80sRetro
{
    public sealed class CameraFrameSmokeTest : MonoBehaviour
    {
        [SerializeField] private ARCameraFrameProvider frameProvider;
        [SerializeField, Min(0.1f)] private float captureIntervalSeconds = 0.5f;

        private float nextCaptureTime;
        private int successfulFrames;

        private void Reset()
        {
            frameProvider = FindObjectOfType<ARCameraFrameProvider>();
        }

        private void Update()
        {
            if (frameProvider == null || Time.time < nextCaptureTime)
            {
                return;
            }

            nextCaptureTime = Time.time + captureIntervalSeconds;

            if (!frameProvider.TryUpdateFrame())
            {
                return;
            }

            successfulFrames++;
            if (successfulFrames == 1 || successfulFrames % 20 == 0)
            {
                Debug.Log(
                    $"AR camera CPU frame ready: {frameProvider.CameraTexture.width}x" +
                    $"{frameProvider.CameraTexture.height}, count={successfulFrames}",
                    this);
            }
        }
    }
}
