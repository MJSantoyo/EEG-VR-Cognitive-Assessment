using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace IkeaEeg.XR
{
    /// <summary>
    /// DEVELOPMENT AID. Lets the left mouse button select the world objects a Quest controller
    /// would normally point at, so the whole protocol can be walked through on the desktop
    /// without putting the headset on.
    ///
    /// THIS IS NOT PARTICIPANT FUNCTIONALITY and it changes nothing about how the experiment
    /// behaves. It adds one more way to reach the SAME selection path; it does not replace,
    /// wrap, disable or reconfigure any XR component.
    ///
    /// ---------------------------------------------------------------------------------------
    /// WHY THIS COMPONENT EXISTS AT ALL — and why it is small
    ///
    /// The project has TWO interaction surfaces, and only one of them already worked with a
    /// mouse:
    ///
    ///   * uGUI buttons (START, READY, RESTART, END, Recenter, the language screen) go through
    ///     the EventSystem. XRUIInputModule already has enableMouseInput = true and every world
    ///     canvas the builder creates already carries a GraphicRaycaster alongside its
    ///     TrackedDeviceGraphicRaycaster. Those buttons are therefore ALREADY clickable in the
    ///     Game view, and this component deliberately does nothing about them — adding a second
    ///     path to something that works is how double activations get invented.
    ///
    ///   * The 3D targets — the recognition SEEN BEFORE / NOT SEEN BEFORE pair, the six chairs
    ///     and the Area 0 practice objects — are XRSimpleInteractable, NOT uGUI. They are driven
    ///     by XR interactors and are unreachable from the EventSystem at any mouse setting. That
    ///     gap is the whole reason for this file.
    ///
    /// HOW IT STAYS HONEST. It raises <c>selectEntered</c> on the interactable it hit — the very
    /// UnityEvent the XR ray raises. Every listener downstream is the production one:
    /// RecognitionResponseButton still latches and still refuses a second press, ChairTarget
    /// still reports through ChairSelectionTask, the response timer still stops where it always
    /// did. There is no parallel "desktop" code path to drift out of step, because the only
    /// thing this class contributes is the raycast.
    /// ---------------------------------------------------------------------------------------
    /// </summary>
    [DisallowMultipleComponent]
    public class DesktopMouseInteraction : MonoBehaviour
    {
        [Tooltip("Master switch. OFF means the mouse does nothing to 3D targets at all.\n\n" +
                 "This is a DEVELOPMENT aid. It has no effect in a headset session — nobody is " +
                 "holding a mouse — but it is left switchable so a build intended for a " +
                 "participant can have it off explicitly rather than by circumstance.")]
        [SerializeField] bool m_EnableMouseSelection = true;

        [Tooltip("Only act in the Editor and in desktop standalone players. A device build " +
                 "ignores this component entirely.")]
        [SerializeField] bool m_DesktopOnly = true;

        [Tooltip("How far the click ray reaches, in metres. Matches the controller ray's own " +
                 "10 m range so the mouse cannot select something the controller could not.")]
        [SerializeField] float m_MaxDistance = 10f;

        [Tooltip("Camera the click ray is cast from. Falls back to Camera.main, which in this " +
                 "scene is the XR rig's head camera.")]
        [SerializeField] Camera m_Camera;

        /// <summary>How many selections this component has raised. Read by the self test.</summary>
        public int selectionsRaised { get; private set; }

        /// <summary>The last interactable it selected, for diagnostics.</summary>
        public string lastSelected { get; private set; } = string.Empty;

        /// <summary>True when this build/platform lets the component act.</summary>
        public bool isActiveOnThisPlatform
        {
            get
            {
                if (!m_EnableMouseSelection)
                    return false;

                if (!m_DesktopOnly)
                    return true;

                return Application.isEditor ||
                       Application.platform == RuntimePlatform.WindowsPlayer ||
                       Application.platform == RuntimePlatform.OSXPlayer ||
                       Application.platform == RuntimePlatform.LinuxPlayer;
            }
        }

        void Update()
        {
            if (!isActiveOnThisPlatform)
                return;

            var mouse = Mouse.current;

            if (mouse == null)
                return;

            // RISING EDGE ONLY. wasPressedThisFrame is true for exactly one frame per press, so
            // a held button cannot repeat and one click cannot become two selections. This is
            // the same guarantee the recognition buttons' own input-release guard provides for
            // the trigger, arrived at from the other direction.
            if (!mouse.leftButton.wasPressedThisFrame)
                return;

            // A click that landed on a uGUI button has already been handled by the EventSystem.
            // Without this, one click on START could ALSO select whatever 3D object happened to
            // be behind the panel.
            if (EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            var camera = m_Camera != null ? m_Camera : Camera.main;

            if (camera == null)
                return;

            var ray = camera.ScreenPointToRay(mouse.position.ReadValue());

            if (!Physics.Raycast(ray, out var hit, m_MaxDistance))
                return;

            // The interactable may be on the hit collider or on a parent: the chairs are
            // multi-part objects whose colliders sit on child renderers.
            var interactable = hit.collider.GetComponentInParent<XRSimpleInteractable>();

            if (interactable == null || !interactable.isActiveAndEnabled)
                return;

            // THE ONE LINE THAT MATTERS. Raising the interactable's own selectEntered event puts
            // this click on exactly the path a controller trigger uses. Nothing downstream can
            // tell the difference, which is the point — a desktop test that exercised different
            // code would not be a test of the experiment.
            //
            // The args carry no interactor: no listener in this project reads one, and inventing
            // a fake interactor would be claiming something about the input that is not true.
            interactable.selectEntered.Invoke(
                new UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs());

            selectionsRaised++;
            lastSelected = interactable.gameObject.name;
        }

        /// <summary>Builder wiring.</summary>
        public void Configure(bool enableMouseSelection, bool desktopOnly, Camera camera)
        {
            m_EnableMouseSelection = enableMouseSelection;
            m_DesktopOnly = desktopOnly;
            m_Camera = camera;
        }
    }
}
