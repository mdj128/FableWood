using UnityEngine;
using UnityEngine.InputSystem;

namespace Fablewood
{
    /// <summary>
    /// A simple orbit camera that follows a target (the player) from behind.
    ///
    ///   Mouse / Right Stick  - orbit around the character
    ///   Mouse Scroll         - zoom in and out
    ///   Escape               - unlock the mouse cursor (click the Game view to lock it again)
    ///
    /// Put this on your camera and drag the character into "Target".
    /// The camera pulls in closer if a wall gets between it and the character.
    /// </summary>
    public class ThirdPersonCamera : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The character to follow.")]
        public Transform target;

        [Tooltip("The point on the character the camera looks at, measured up from the character's feet.")]
        public Vector3 targetOffset = new Vector3(0f, 1.5f, 0f);

        [Header("Orbit")]
        [Tooltip("How far behind the character the camera sits.")]
        public float distance = 4f;
        public float minDistance = 1.5f;
        public float maxDistance = 8f;

        [Tooltip("Mouse look speed.")]
        public float mouseSensitivity = 0.15f;

        [Tooltip("Gamepad right stick look speed (degrees per second).")]
        public float gamepadSensitivity = 150f;

        public bool invertY = false;

        [Tooltip("Lowest the camera can look (negative = looking up from below).")]
        public float minPitch = -30f;

        [Tooltip("Highest the camera can look (positive = looking down from above).")]
        public float maxPitch = 70f;

        [Header("Smoothing")]
        [Tooltip("How smoothly the camera follows the character. Smaller = tighter.")]
        public float followSmoothTime = 0.05f;

        [Header("Collision")]
        [Tooltip("Stops the camera from going through walls.")]
        public bool avoidObstacles = true;
        public LayerMask obstacleLayers = ~0;
        public float collisionRadius = 0.25f;

        [Header("Cursor")]
        [Tooltip("Hide and lock the mouse cursor while playing.")]
        public bool lockCursor = true;

        float yaw;
        float pitch = 15f;
        float currentDistance;
        Vector3 smoothedFocus;
        Vector3 focusVelocity;
        readonly RaycastHit[] hits = new RaycastHit[16];

        InputAction lookMouseAction;
        InputAction lookStickAction;
        InputAction zoomAction;

        void Awake()
        {
            lookMouseAction = new InputAction("LookMouse", InputActionType.Value, "<Mouse>/delta");
            lookStickAction = new InputAction("LookStick", InputActionType.Value, "<Gamepad>/rightStick");
            zoomAction = new InputAction("Zoom", InputActionType.Value, "<Mouse>/scroll/y");
        }

        void OnEnable()
        {
            lookMouseAction.Enable();
            lookStickAction.Enable();
            zoomAction.Enable();
        }

        void OnDisable()
        {
            lookMouseAction.Disable();
            lookStickAction.Disable();
            zoomAction.Disable();
        }

        void OnDestroy()
        {
            lookMouseAction.Dispose();
            lookStickAction.Dispose();
            zoomAction.Dispose();
        }

        void Start()
        {
            // Start looking the same way the camera already faces.
            Vector3 angles = transform.eulerAngles;
            yaw = angles.y;
            pitch = Mathf.Clamp(NormalizeAngle(angles.x), minPitch, maxPitch);
            currentDistance = distance;
            if (target != null) smoothedFocus = target.position + targetOffset;

            if (lockCursor) SetCursorLocked(true);
        }

        void Update()
        {
            if (!lockCursor) return;

            // Escape frees the cursor; clicking locks it again.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                SetCursorLocked(false);
            else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                SetCursorLocked(true);
        }

        // LateUpdate runs after the character has moved, so the camera never lags a frame behind.
        void LateUpdate()
        {
            if (target == null) return;

            // --- Read look input ---
            bool canLook = !lockCursor || Cursor.lockState == CursorLockMode.Locked;
            if (canLook)
            {
                Vector2 look = lookMouseAction.ReadValue<Vector2>() * mouseSensitivity;
                look += lookStickAction.ReadValue<Vector2>() * gamepadSensitivity * Time.deltaTime;

                yaw += look.x;
                pitch += invertY ? look.y : -look.y;
                pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            }

            // --- Zoom with the scroll wheel ---
            float scroll = zoomAction.ReadValue<float>();
            if (Mathf.Abs(scroll) > 0.01f)
                distance = Mathf.Clamp(distance - Mathf.Sign(scroll) * 0.5f, minDistance, maxDistance);

            // --- Position the camera ---
            Vector3 focus = target.position + targetOffset;
            smoothedFocus = Vector3.SmoothDamp(smoothedFocus, focus, ref focusVelocity, followSmoothTime);

            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 direction = rotation * Vector3.back;

            float wantedDistance = distance;
            if (avoidObstacles) wantedDistance = GetUnblockedDistance(smoothedFocus, direction, distance);

            // Move in quickly when blocked, ease back out when clear.
            float speed = wantedDistance < currentDistance ? 30f : 5f;
            currentDistance = Mathf.Lerp(currentDistance, wantedDistance, 1f - Mathf.Exp(-speed * Time.deltaTime));

            transform.SetPositionAndRotation(smoothedFocus + direction * currentDistance, rotation);
        }

        /// <summary>Returns how far the camera can go before hitting something (ignoring the character itself).</summary>
        float GetUnblockedDistance(Vector3 from, Vector3 direction, float maxDist)
        {
            int count = Physics.SphereCastNonAlloc(from, collisionRadius, direction, hits, maxDist,
                obstacleLayers, QueryTriggerInteraction.Ignore);

            float closest = maxDist;
            for (int i = 0; i < count; i++)
            {
                if (hits[i].transform.IsChildOf(target)) continue; // don't collide with the player
                if (hits[i].distance > 0f && hits[i].distance < closest) closest = hits[i].distance;
            }
            return Mathf.Max(closest, 0.3f);
        }

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            return angle > 180f ? angle - 360f : angle;
        }
    }
}
