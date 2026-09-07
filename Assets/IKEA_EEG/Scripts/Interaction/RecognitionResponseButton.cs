using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using IkeaEeg.Experiment;

namespace IkeaEeg.Interaction
{
    /// <summary>
    /// One large "SEEN BEFORE" / "NOT SEEN BEFORE" response target.
    ///
    /// WHY IT MIRRORS <see cref="ChairTarget"/>. The project already selects world objects with
    /// <see cref="XRSimpleInteractable"/> and its <c>selectEntered</c> event, and that path is
    /// verified on hardware. Reusing it means the recognition response needs no new interaction
    /// package, no new input action and no change to the XR rig — the least disruptive reliable
    /// method, as required. A poke/direct-touch interactor would demand the participant reach a
    /// precise depth, which is slower and less comfortable for a task answered thirty times in
    /// a row; ray selection lets them point and pull the trigger from wherever they are sitting.
    ///
    /// SIZE IS A CORRECTNESS PROPERTY HERE, not styling. The target is deliberately large so a
    /// response costs a coarse point rather than fine aiming: reaction time is being measured,
    /// and time spent aiming would be recorded as time spent deciding.
    ///
    /// DUPLICATE INPUT. The button latches after the first accepted selection and ignores every
    /// later one until it is explicitly re-armed for the next item. A double trigger-pull, or
    /// both controllers landing together, must not produce two responses to one word.
    /// </summary>
    [DisallowMultipleComponent]
    public class RecognitionResponseButton : MonoBehaviour
    {
        [Tooltip("Which answer this button represents.")]
        [SerializeField] RecognitionResponse m_Response = RecognitionResponse.SeenBefore;

        [Tooltip("The interactable that receives the controller ray. Found on this object when " +
                 "left empty.")]
        [SerializeField] XRSimpleInteractable m_Interactable;

        [Tooltip("Renderer tinted to show armed / disarmed state.")]
        [SerializeField] Renderer m_Renderer;

        [SerializeField] Color m_ArmedColor = new Color(0.20f, 0.45f, 0.75f);
        [SerializeField] Color m_DisarmedColor = new Color(0.22f, 0.22f, 0.25f);

        /// <summary>Raised on an accepted selection. The manager owns what happens next.</summary>
        public event Action<RecognitionResponse> responseSelected;

        bool m_Armed;

        /// <summary>
        /// Arming was requested but is being HELD BACK until the trigger is released.
        ///
        /// The inter-item pause used to hide this problem: 0.15-0.35 s was usually long enough
        /// for a participant to let go before the next item armed. With the pause removed, the
        /// next item arms in the same breath as the response, so a finger still on the trigger
        /// has to be handled explicitly rather than waited out.
        /// </summary>
        bool m_ArmPending;

        public RecognitionResponse response => m_Response;
        public bool isArmed => m_Armed;

        /// <summary>True while arming is deferred waiting for the input to be released.</summary>
        public bool isArmPending => m_ArmPending;

        /// <summary>True while an interactor is currently selecting this button.</summary>
        public bool isBeingSelected => m_Interactable != null && m_Interactable.isSelected;

        void Awake()
        {
            if (m_Interactable == null)
                m_Interactable = GetComponent<XRSimpleInteractable>();

            if (m_Renderer == null)
                m_Renderer = GetComponentInChildren<Renderer>();

            ApplyTint();
        }

        void OnEnable()
        {
            if (m_Interactable != null)
                m_Interactable.selectEntered.AddListener(OnSelectEntered);
        }

        void OnDisable()
        {
            if (m_Interactable != null)
                m_Interactable.selectEntered.RemoveListener(OnSelectEntered);
        }

        /// <summary>
        /// Allows exactly ONE response until re-armed.
        ///
        /// The interactable itself is left enabled either way — the same decision ChairTarget
        /// documents. Toggling interactables mid-session has produced stuck hover states, so
        /// acceptance is gated in code, where it is visible and testable, rather than by
        /// enabling and disabling XR components.
        /// </summary>
        public void Arm()
        {
            // REQUESTED, not granted. If the participant is still holding the trigger on this
            // button from the previous item, arming now would let one continuous press answer
            // two consecutive words. The word itself is already on screen; only the ACCEPTANCE
            // of a response waits, and only for as long as the finger stays down.
            m_ArmPending = true;
            TryGrantArm();
        }

        public void Disarm()
        {
            m_ArmPending = false;
            m_Armed = false;
            ApplyTint();
        }

        void Update()
        {
            // Cheap: two bools and, at most, one property read per frame, and only while an arm
            // is actually outstanding.
            if (m_ArmPending)
                TryGrantArm();
        }

        /// <summary>
        /// Grants a pending arm once nothing is selecting this button.
        ///
        /// A RELEASE GUARD RATHER THAN A TIMED PAUSE: it blocks exactly the unsafe case — input
        /// still held across the item boundary — and costs nothing at all when the participant
        /// has already let go, which is the normal case. A timer would penalise everyone to
        /// protect against the minority.
        /// </summary>
        void TryGrantArm()
        {
            if (!m_ArmPending)
                return;

            // Still held from the previous item: keep waiting.
            if (m_Interactable != null && m_Interactable.isSelected)
                return;

            m_ArmPending = false;
            m_Armed = true;
            ApplyTint();
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (!m_Armed)
                return;

            // Latch FIRST, then notify: a handler that presents the next item must not be able
            // to re-enter this one through a second selection arriving in the same frame.
            m_Armed = false;
            ApplyTint();

            responseSelected?.Invoke(m_Response);
        }

        void ApplyTint()
        {
            if (m_Renderer == null)
                return;

            // MaterialPropertyBlock rather than .material: touching .material would instantiate
            // a per-renderer copy and leak it, which is what the rest of this project avoids.
            var block = new MaterialPropertyBlock();
            m_Renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", m_Armed ? m_ArmedColor : m_DisarmedColor);
            block.SetColor("_Color", m_Armed ? m_ArmedColor : m_DisarmedColor);
            m_Renderer.SetPropertyBlock(block);
        }

        /// <summary>Builder wiring.</summary>
        public void Configure(RecognitionResponse response, XRSimpleInteractable interactable,
            Renderer renderer)
        {
            m_Response = response;
            m_Interactable = interactable;
            m_Renderer = renderer;
        }
    }
}
