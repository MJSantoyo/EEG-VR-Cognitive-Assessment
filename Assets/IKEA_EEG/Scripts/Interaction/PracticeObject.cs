using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace IkeaEeg.Interaction
{
    /// <summary>
    /// A selectable practice object in Area 0 (VR familiarization).
    ///
    /// WHAT IT IS FOR: letting a first-time VR participant discover that pointing at a thing
    /// and squeezing a trigger selects it, before any of that matters. Nothing here is scored.
    ///
    /// WHAT IT IS DELIBERATELY NOT:
    ///   * not a chair — it never uses ChairTarget, and Area 0 contains no chairs, so nothing
    ///     in the practice room can prime the Area B search;
    ///   * not a trial — selections raise a diagnostic event and nothing else; they never reach
    ///     chair accuracy, response-time averages or memory scoring;
    ///   * not grabbable — like the chairs it uses XRSimpleInteractable with no Rigidbody, so
    ///     it can be pointed at and selected but never picked up or moved;
    ///   * not haptic — no impulse is ever played from here.
    ///
    /// It deliberately mirrors ChairTarget's interaction contract (software-gated selection,
    /// interactable left registered for the whole session) so that what the participant learns
    /// in Area 0 is exactly what Area B expects of them.
    /// </summary>
    [DisallowMultipleComponent]
    public class PracticeObject : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Stable id written to the CSV object_id column, e.g. Practice_Blue.")]
        [SerializeField] string m_ObjectId = "Practice_Object";

        [Tooltip("THE colour of this object. The single source of truth: the canonical logged " +
                 "name, the participant-facing localized label, the spoken prompt and the " +
                 "correctness comparison are all derived from this one value.")]
        [SerializeField] ChairColor m_Color = ChairColor.Red;

        [Header("Wiring")]
        [SerializeField] List<Renderer> m_Renderers = new List<Renderer>();
        [SerializeField] XRSimpleInteractable m_Interactable;

        [Header("Feedback materials (visual only — no haptics, by design)")]
        [SerializeField] Material m_BaseMaterial;
        [SerializeField] Material m_HoverMaterial;
        [SerializeField] Material m_SelectedMaterial;

        [Tooltip("Applied to the MOST RECENTLY selected object while it is the active one.")]
        [SerializeField] Material m_SuccessMaterial;

        [Header("Active highlight animation")]
        [Tooltip("How far the object grows and shrinks while it is the active selection.")]
        [Range(0f, 0.5f)]
        [SerializeField] float m_PulseAmplitude = 0.16f;

        [Tooltip("Pulses per second while active.")]
        [Range(0.2f, 4f)]
        [SerializeField] float m_PulseSpeed = 1.4f;

        bool m_Hovering;
        int m_SelectionCount;
        bool m_Active;
        Vector3 m_BaseScale = Vector3.one;
        bool m_BaseScaleCaptured;
        float m_PulsePhase;

        /// <summary>Raised on every practice selection. Diagnostic only.</summary>
        public event Action<PracticeObject> practiceSelected;

        public string objectId => m_ObjectId;

        /// <summary>
        /// THE colour. Everything about this object's colour is derived from it, so no two
        /// derivations can disagree.
        /// </summary>
        public ChairColor color => m_Color;

        /// <summary>
        /// CANONICAL colour name, e.g. "BLUE". Logged and used as the narration key; never
        /// shown to the participant.
        ///
        /// DERIVED, not stored. It used to be a serialized string that the scene builder set
        /// alongside the enum — which is exactly how the spoken prompt and the written prompt
        /// came to name different colours.
        /// </summary>
        public string colorName => PracticeColors.CanonicalName(m_Color);

        /// <summary>The colour name in the language in force — what the participant reads.</summary>
        public string LocalizedColorName() =>
            Localization.ExperimentLocalization.PracticeColorName(m_Color);

        /// <summary>How many times this object has been selected in the current Area 0 visit.</summary>
        public int selectionCount => m_SelectionCount;

        /// <summary>
        /// True once this object has been selected while it WAS the requested target.
        ///
        /// This is a RECORD, not a look: it is what the readiness rule consults. The visual
        /// highlight follows the most recent selection instead, so hitting the target and then
        /// exploring another object does not leave two objects looking "chosen".
        /// </summary>
        public bool succeeded { get; private set; }

        /// <summary>True while this is the most recently selected object.</summary>
        public bool isActive => m_Active;

        /// <summary>Records that this object was the requested target when it was selected.</summary>
        public void MarkSuccess()
        {
            succeeded = true;
        }

        /// <summary>
        /// Makes this the active selection, or clears it.
        ///
        /// EXACTLY ONE object is active at a time — the manager clears the others — so the
        /// participant always has one unambiguous "this is what you just picked". The active
        /// object takes the highlight material and breathes gently; everything else sits at its
        /// resting colour and size.
        ///
        /// The pulse animates the TRANSFORM, not the material. The four objects share the chair
        /// colour materials, so animating a material here would also animate the chairs in
        /// Area B.
        /// </summary>
        public void SetActiveHighlight(bool active)
        {
            CaptureBaseScale();
            m_Active = active;

            if (active)
            {
                m_PulsePhase = 0f;
                ApplyMaterial(m_SuccessMaterial != null ? m_SuccessMaterial : m_SelectedMaterial);
            }
            else
            {
                transform.localScale = m_BaseScale;
                ApplyMaterial(m_BaseMaterial);
            }
        }

        void Update()
        {
            if (!m_Active)
                return;

            m_PulsePhase += Time.deltaTime * m_PulseSpeed;

            var pulse = 1f + Mathf.Sin(m_PulsePhase * Mathf.PI * 2f) * m_PulseAmplitude;
            transform.localScale = m_BaseScale * pulse;
        }

        void Awake()
        {
            if (m_Interactable == null)
                m_Interactable = GetComponent<XRSimpleInteractable>();

            if (m_Renderers.Count == 0)
                m_Renderers.AddRange(GetComponentsInChildren<Renderer>(true));

            CaptureBaseScale();
        }

        /// <summary>
        /// Records the resting size once, so the pulse always has something correct to return
        /// to.
        ///
        /// Called from Awake at run time AND from every entry point that can change the scale,
        /// because Awake does not run in the Editor outside Play Mode — and a default of
        /// Vector3.one would then be written over the authored size, inflating a 0.30 m sphere
        /// to 1 m the first time the highlight was cleared.
        /// </summary>
        void CaptureBaseScale()
        {
            if (m_BaseScaleCaptured)
                return;

            m_BaseScale = transform.localScale;
            m_BaseScaleCaptured = true;
        }

        void OnEnable()
        {
            if (m_Interactable == null)
                return;

            m_Interactable.selectEntered.AddListener(OnSelectEntered);
            m_Interactable.hoverEntered.AddListener(OnHoverEntered);
            m_Interactable.hoverExited.AddListener(OnHoverExited);
        }

        void OnDisable()
        {
            if (m_Interactable == null)
                return;

            m_Interactable.selectEntered.RemoveListener(OnSelectEntered);
            m_Interactable.hoverEntered.RemoveListener(OnHoverEntered);
            m_Interactable.hoverExited.RemoveListener(OnHoverExited);
        }

        /// <summary>
        /// Wires the object up. Called by the scene builder only.
        ///
        /// The colour is REQUIRED and has no default. It used to be the last optional parameter
        /// of a seven-argument call, and the builder simply stopped short of passing it: all four
        /// practice objects silently took the default ChairColor.Red, so the panel always read
        /// "RED" while the narration asked for whatever the object really was. A parameter that
        /// must be supplied cannot be forgotten.
        /// </summary>
        public void Configure(string objectId, ChairColor color, Material baseMaterial,
            Material hoverMaterial, Material selectedMaterial, Material successMaterial)
        {
            m_Color = color;
            m_ObjectId = objectId;
            m_BaseMaterial = baseMaterial;
            m_HoverMaterial = hoverMaterial;
            m_SelectedMaterial = selectedMaterial;
            m_SuccessMaterial = successMaterial;
        }

        public void SetRenderers(IEnumerable<Renderer> renderers)
        {
            m_Renderers.Clear();
            m_Renderers.AddRange(renderers);
        }

        public void SetInteractable(XRSimpleInteractable interactable)
        {
            m_Interactable = interactable;
        }

        /// <summary>Clears the practice state. Called when Area 0 is (re)entered.</summary>
        public void ResetPractice()
        {
            m_SelectionCount = 0;
            m_Hovering = false;
            succeeded = false;

            CaptureBaseScale();
            SetActiveHighlight(false);
        }

        // ---------------------------------------------------------------------------------
        // XRI callbacks
        // ---------------------------------------------------------------------------------

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            // Unlike a chair, a practice object may be selected as many times as the
            // participant likes — repetition is the point. Each physical press still produces
            // exactly one event: selectEntered fires once per select, and the dual-trigger
            // mapping routes both triggers through a SINGLE XRI select action (see the scene
            // builder), so holding both cannot produce two.
            m_SelectionCount++;

            // The manager decides whether this was the requested object and applies the
            // success or neutral look; this class does not know what the target is.
            practiceSelected?.Invoke(this);
        }

        void OnHoverEntered(HoverEnterEventArgs args)
        {
            m_Hovering = true;

            // The active object keeps its highlight under the pointer: what the participant
            // just picked should not appear to change when they look elsewhere.
            if (m_Active)
                return;

            if (m_HoverMaterial != null)
                ApplyMaterial(m_HoverMaterial);
        }

        void OnHoverExited(HoverExitEventArgs args)
        {
            if (!m_Hovering)
                return;

            m_Hovering = false;

            if (m_Active)
                return;

            ApplyMaterial(m_BaseMaterial);
        }

        void ApplyMaterial(Material material)
        {
            if (material == null)
                return;

            for (var i = 0; i < m_Renderers.Count; i++)
            {
                if (m_Renderers[i] != null)
                    m_Renderers[i].sharedMaterial = material;
            }
        }
    }
}
