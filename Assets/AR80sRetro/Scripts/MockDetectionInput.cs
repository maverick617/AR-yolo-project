using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AR80sRetro
{
    public sealed class MockDetectionInput : MonoBehaviour
    {
        [SerializeField] private RetroReplacementManager replacementManager;
        [SerializeField] private string label = "cup";
        [SerializeField, Range(0f, 1f)] private float confidence = 0.9f;
        [SerializeField] private Rect normalizedBox = new Rect(0.45f, 0.45f, 0.1f, 0.1f);
#if ENABLE_LEGACY_INPUT_MANAGER
        [SerializeField] private KeyCode emitKey = KeyCode.Space;
#endif

        private readonly List<DetectionResult> detections = new List<DetectionResult>(1);

        private void Reset()
        {
            replacementManager = FindObjectOfType<RetroReplacementManager>();
        }

        private void Update()
        {
            if (replacementManager == null || !IsEmitInputPressed())
            {
                return;
            }

            detections.Clear();
            detections.Add(new DetectionResult(label, confidence, normalizedBox));
            replacementManager.ApplyDetections(detections);
        }

        private bool IsEmitInputPressed()
        {
#if ENABLE_INPUT_SYSTEM
            bool keyboardPressed = Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
            bool screenPressed = Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed;
            return keyboardPressed || screenPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(emitKey);
#else
            return false;
#endif
        }
    }
}
