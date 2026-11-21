using UnityEngine;
using UnityEngine.InputSystem;

namespace Fablewood
{
    /// <summary>
    /// A simple third-person character controller.
    ///
    /// Controls (keyboard + mouse, gamepad also works):
    ///   WASD / Left Stick      - move (relative to the camera)
    ///   Left Shift / L3        - sprint
    ///   Space / A (South)      - jump
    ///
    /// Uses Unity's built-in CharacterController for collision and the Input System for input.
    /// Movement is done entirely in code (no root motion).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class ThirdPersonController : MonoBehaviour
    {
        // Animator parameter names. The setup tool creates these in the Animator Controller.
        public const string SpeedParam = "Speed";
        public const string GroundedParam = "IsGrounded";
        public const string JumpParam = "Jump";

        [Header("Movement")]
        [Tooltip("Walking speed in meters per second.")]
        public float walkSpeed = 2f;

        [Tooltip("Sprinting speed in meters per second (hold Shift).")]
        public float sprintSpeed = 5.5f;

        [Tooltip("How quickly the character speeds up (meters per second, per second).")]
        public float acceleration = 10f;

        [Tooltip("How quickly the character slows down when you let go of the keys.")]
        public float deceleration = 14f;

        [Tooltip("How long it takes to turn toward the movement direction. Smaller = snappier.")]
        [Range(0.01f, 0.5f)]
        public float rotationSmoothTime = 0.1f;

        [Header("Jumping & Gravity")]
        [Tooltip("How high the character jumps, in meters.")]
        public float jumpHeight = 1.2f;

        [Tooltip("Gravity strength. Negative pulls down. Real-world gravity is -9.81, games often use more.")]
        public float gravity = -20f;

        [Tooltip("Seconds you can still jump after walking off a ledge (makes jumping feel fair).")]
        public float coyoteTime = 0.15f;

        [Header("Ground Detection")]
        [Tooltip("Which layers count as ground.")]
        public LayerMask groundLayers = ~0;

        [Tooltip("Extra distance below the feet that still counts as standing on the ground.")]
        public float groundCheckDistance = 0.15f;

        [Header("References (filled in automatically if left empty)")]
        [Tooltip("The camera used to work out which way is 'forward'. Defaults to the Main Camera.")]
        public Transform cameraTransform;

        [Tooltip("The Animator that plays the character's animations. Defaults to one on this object or its children.")]
        public Animator animator;

        // --- Read-only state, handy for debugging in the Inspector (Debug mode) ---
        public bool IsGrounded { get; private set; }
        public float CurrentSpeed { get; private set; }

        CharacterController controller;
        InputAction moveAction;
        InputAction sprintAction;
        InputAction jumpAction;

        Vector3 moveDirection;      // last direction we moved in (kept while slowing down)
        float verticalVelocity;     // up/down speed (jumping and falling)
        float rotationVelocity;     // used by SmoothDampAngle
        float lastGroundedTime;     // for coyote time
        float lastJumpTime = -10f;
        readonly Collider[] groundHits = new Collider[8];

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (animator != null) animator.applyRootMotion = false; // movement comes from this script

            CreateInputActions();
        }

        void Start()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        void OnEnable()
        {
            moveAction?.Enable();
            sprintAction?.Enable();
            jumpAction?.Enable();
        }

        void OnDisable()
        {
            moveAction?.Disable();
            sprintAction?.Disable();
            jumpAction?.Disable();
        }

        void OnDestroy()
        {
            moveAction?.Dispose();
            sprintAction?.Dispose();
            jumpAction?.Dispose();
        }

        /// <summary>Creates the input bindings in code, so no extra input asset is needed.</summary>
        void CreateInputActions()
        {
            moveAction = new InputAction("Move", InputActionType.Value);
            moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");
            moveAction.AddBinding("<Gamepad>/leftStick");

            sprintAction = new InputAction("Sprint", InputActionType.Button);
            sprintAction.AddBinding("<Keyboard>/leftShift");
            sprintAction.AddBinding("<Gamepad>/leftStickPress");

            jumpAction = new InputAction("Jump", InputActionType.Button);
            jumpAction.AddBinding("<Keyboard>/space");
            jumpAction.AddBinding("<Gamepad>/buttonSouth");
        }

        void Update()
        {
            CheckGround();
            HandleMovement();
            HandleJumpAndGravity();

            // Apply horizontal + vertical movement in one call.
            Vector3 velocity = moveDirection * CurrentSpeed + Vector3.up * verticalVelocity;
            controller.Move(velocity * Time.deltaTime);

            UpdateAnimator();
        }

        void CheckGround()
        {
            // Right after jumping, ignore the ground so we don't "stick" to it.
            if (Time.time < lastJumpTime + 0.2f)
            {
                IsGrounded = false;
                return;
            }

            // Look for colliders in a small sphere at the bottom of the capsule.
            float radius = controller.radius * 0.9f;
            Vector3 bottom = transform.TransformPoint(controller.center)
                             + Vector3.down * (controller.height * 0.5f * transform.lossyScale.y - radius * transform.lossyScale.y);
            Vector3 checkPoint = bottom + Vector3.down * groundCheckDistance;

            bool hitGround = false;
            int count = Physics.OverlapSphereNonAlloc(checkPoint, radius * transform.lossyScale.y,
                groundHits, groundLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (groundHits[i] != controller) { hitGround = true; break; }
            }

            IsGrounded = controller.isGrounded || hitGround;
            if (IsGrounded) lastGroundedTime = Time.time;
        }

        void HandleMovement()
        {
            Vector2 input = moveAction.ReadValue<Vector2>();
            if (input.sqrMagnitude > 1f) input.Normalize();
            bool hasInput = input.sqrMagnitude > 0.01f;

            float targetSpeed = 0f;

            if (hasInput)
            {
                // Turn input into a world direction relative to where the camera is looking.
                Vector3 camForward = Vector3.forward;
                Vector3 camRight = Vector3.right;
                if (cameraTransform != null)
                {
                    camForward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                    camRight = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
                }
                Vector3 desired = (camForward * input.y + camRight * input.x).normalized;

                // Smoothly rotate the character to face the movement direction.
                float targetAngle = Mathf.Atan2(desired.x, desired.z) * Mathf.Rad2Deg;
                float angle = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetAngle,
                    ref rotationVelocity, rotationSmoothTime);
                transform.rotation = Quaternion.Euler(0f, angle, 0f);

                // Walk the way the character is FACING, not straight toward the input direction.
                // This stops the character sliding sideways while it turns, so it turns on the
                // spot (around its feet) and then walks off.
                moveDirection = transform.forward;

                // Work out the target speed. Analog sticks give partial speed.
                float maxSpeed = sprintAction.IsPressed() ? sprintSpeed : walkSpeed;
                targetSpeed = maxSpeed * input.magnitude;

                // Slow down during sharp turns: full speed when facing the right way,
                // almost stopped when facing the opposite way.
                float facing = Vector3.Dot(transform.forward, desired); // 1 = same way, -1 = opposite
                targetSpeed *= Mathf.Clamp01((facing + 0.2f) / 1.2f);
            }

            // Smoothly speed up or slow down.
            float rate = targetSpeed > CurrentSpeed ? acceleration : deceleration;
            CurrentSpeed = Mathf.MoveTowards(CurrentSpeed, targetSpeed, rate * Time.deltaTime);
        }

        void HandleJumpAndGravity()
        {
            if (IsGrounded && verticalVelocity < 0f)
            {
                // A small downward push keeps the character glued to slopes and steps.
                verticalVelocity = -2f;
            }

            bool canJump = Time.time - lastGroundedTime <= coyoteTime && Time.time > lastJumpTime + 0.3f;
            if (jumpAction.WasPressedThisFrame() && canJump)
            {
                // Physics formula: velocity needed to reach a given height.
                verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                lastJumpTime = Time.time;
                IsGrounded = false;
                if (animator != null) animator.SetTrigger(JumpParam);
            }

            verticalVelocity += gravity * Time.deltaTime;
        }

        void UpdateAnimator()
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;

            // Damping (0.1s) smooths out the blend between idle, walk and run.
            animator.SetFloat(SpeedParam, CurrentSpeed, 0.1f, Time.deltaTime);

            // Treat very short moments in the air (like stepping down a stair) as still grounded,
            // so the fall animation doesn't flicker. After a jump we report "not grounded" right away.
            bool justJumped = Time.time < lastJumpTime + 0.2f;
            bool groundedForAnimation = IsGrounded || (!justJumped && Time.time - lastGroundedTime < coyoteTime && lastJumpTime < lastGroundedTime);
            animator.SetBool(GroundedParam, groundedForAnimation);
        }

        void OnDrawGizmosSelected()
        {
            var cc = GetComponent<CharacterController>();
            if (cc == null) return;
            float radius = cc.radius * 0.9f * transform.lossyScale.y;
            Vector3 bottom = transform.TransformPoint(cc.center)
                             + Vector3.down * (cc.height * 0.5f * transform.lossyScale.y - radius);
            Gizmos.color = IsGrounded ? Color.green : Color.red;
            Gizmos.DrawWireSphere(bottom + Vector3.down * groundCheckDistance, radius);
        }
    }
}
