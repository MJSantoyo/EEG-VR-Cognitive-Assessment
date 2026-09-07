using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IkeaEeg.XR
{
    /// <summary>
    /// A hidden developer control for inspecting one area without replaying the whole
    /// experiment.
    ///
    /// THIS IS NOT PARTICIPANT FUNCTIONALITY. It exists so that checking Area C in the headset
    /// does not cost a full encoding-plus-recall run. Every consequence of using it is recorded:
    /// opening it is logged, and jumping areas marks the run DEVELOPER_INTERRUPTED so a
    /// developer-affected run can never be mistaken for a clean participant run.
    ///
    /// THE GESTURE: both thumbstick CLICKS held together for ~2 seconds. Chosen because
    ///   * a participant has no reason to press a thumbstick at all — locomotion is disabled
    ///     and every interaction in the experiment is point-and-trigger;
    ///   * it needs both hands simultaneously and sustained, so it cannot be brushed by
    ///     accident;
    ///   * it uses neither trigger, so it can never collide with Select.
    ///
    /// INPUT SAFETY: the two actions are created HERE, in code, at run time. The shared XRI
    /// input asset, the Input System configuration and the controller prefabs are untouched —
    /// this component only listens.
    /// </summary>
    [DisallowMultipleComponent]
    public class DeveloperNavigation : MonoBehaviour
    {
        [Header("Gesture")]
        [Tooltip("How long BOTH thumbstick clicks must be held together to open the menu.")]
        [Range(0.5f, 5f)]
        [SerializeField] float m_HoldSeconds = 2f;

        // NOT "m_Enabled": MonoBehaviour already serializes a field of that name (the
        // component's own enabled checkbox), and declaring a second one makes Unity log a
        // serialization warning on every serialize. The self test checks for exactly this.
        [Tooltip("Master switch. Off means the gesture does nothing at all.")]
        [SerializeField] bool m_GestureEnabled = true;

        [Header("Panel")]
        [Tooltip("The developer panel root. Inactive except while the menu is open.")]
        [SerializeField] GameObject m_Panel;

        [Tooltip("Distance in front of the camera at which the panel appears.")]
        [SerializeField] float m_PanelDistance = 1.2f;

        InputAction m_LeftThumbstickClick;
        InputAction m_RightThumbstickClick;

        float m_HeldSeconds;
        bool m_Open;

        /// <summary>Raised when the gesture completes. The manager logs and reacts.</summary>
        public event Action menuOpened;

        /// <summary>Raised when the menu closes by any route.</summary>
        public event Action menuClosed;

        /// <summary>True while the developer panel is on screen.</summary>
        public bool isOpen => m_Open;

        /// <summary>How long the gesture must be held. Exposed for the self test.</summary>
        public float holdSeconds => m_HoldSeconds;

        void Awake()
        {
            // Bound to the standard XR controller layout, so this works with whatever
            // controller profile OpenXR has active without naming a device.
            m_LeftThumbstickClick = new InputAction("IKEA_DevNav_LeftThumbstickClick",
                InputActionType.Button, "<XRController>{LeftHand}/thumbstickClicked");

            m_RightThumbstickClick = new InputAction("IKEA_DevNav_RightThumbstickClick",
                InputActionType.Button, "<XRController>{RightHand}/thumbstickClicked");

            // A second binding per hand: some runtimes surface the click as primary2DAxisClick.
            m_LeftThumbstickClick.AddBinding("<XRController>{LeftHand}/primary2DAxisClick");
            m_RightThumbstickClick.AddBinding("<XRController>{RightHand}/primary2DAxisClick");

            if (m_Panel != null)
                m_Panel.SetActive(false);
        }

        void OnEnable()
        {
            m_LeftThumbstickClick?.Enable();
            m_RightThumbstickClick?.Enable();
        }

        void OnDisable()
        {
            m_LeftThumbstickClick?.Disable();
            m_RightThumbstickClick?.Disable();
            m_HeldSeconds = 0f;
        }

        void OnDestroy()
        {
            m_LeftThumbstickClick?.Dispose();
            m_RightThumbstickClick?.Dispose();
        }

        void Update()
        {
            if (!m_GestureEnabled || m_Open)
                return;

            var bothHeld = IsPressed(m_LeftThumbstickClick) && IsPressed(m_RightThumbstickClick);

            if (!bothHeld)
            {
                // Releasing either stick restarts the hold: a brief accidental double-press
                // can never accumulate towards the threshold.
                m_HeldSeconds = 0f;
                return;
            }

            m_HeldSeconds += Time.unscaledDeltaTime;

            if (m_HeldSeconds >= m_HoldSeconds)
            {
                m_HeldSeconds = 0f;
                Open();
            }
        }

        static bool IsPressed(InputAction action)
        {
            return action != null && action.enabled && action.IsPressed();
        }

        /// <summary>Opens the panel and places it in front of the participant's view.</summary>
        public void Open()
        {
            if (m_Open)
                return;

            m_Open = true;

            if (m_Panel != null)
            {
                PositionInFrontOfCamera();
                m_Panel.SetActive(true);
            }

            Debug.LogWarning("[IKEA_EEG] DEVELOPER NAVIGATION OPENED. This is a developer tool: " +
                             "any area jump made from here invalidates the current run as a " +
                             "participant protocol run.");

            menuOpened?.Invoke();
        }

        public void Close()
        {
            if (!m_Open)
                return;

            m_Open = false;
            m_HeldSeconds = 0f;

            if (m_Panel != null)
                m_Panel.SetActive(false);

            menuClosed?.Invoke();
        }

        /// <summary>
        /// Moves the panel in front of the camera. The panel is a free-standing object in the
        /// scene — nothing is parented to the XR rig, so the rig hierarchy is never modified.
        /// </summary>
        void PositionInFrontOfCamera()
        {
            var camera = Camera.main;
            if (camera == null || m_Panel == null)
                return;

            var cameraTransform = camera.transform;
            var forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up);

            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;

            forward.Normalize();

            m_Panel.transform.position = cameraTransform.position + forward * m_PanelDistance;

            // A world-space canvas reads from its local -Z side, so its forward points AWAY
            // from the viewer — the same rule the built panels follow.
            m_Panel.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        public void SetPanel(GameObject panel)
        {
            m_Panel = panel;

            if (m_Panel != null)
                m_Panel.SetActive(false);
        }
    }
}
