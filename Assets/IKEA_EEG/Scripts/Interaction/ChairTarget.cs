using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace IkeaEeg.Interaction
{
    /// <summary>
    /// One selectable chair in Area B.
    ///
    /// Data-driven: the chair knows only its own <see cref="ChairSpec"/>. It contains no
    /// knowledge of which chair is "the right one" — correctness is decided by
    /// <see cref="ChairSelectionTask"/> comparing specs. Adding a seventh chair or changing
    /// the target requires no change here.
    ///
    /// The chair is a selection target only: it uses <see cref="XRSimpleInteractable"/>,
    /// never XRGrabInteractable, so it can be pointed at and selected but never picked up
    /// or moved. Its Rigidbody-free colliders are static.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChairTarget : MonoBehaviour
    {
        /// <summary>
        /// One pre-built silhouette. All three exist under every chair from the moment the
        /// scene is built; a trial simply shows one of them.
        /// </summary>
        [Serializable]
        public class ShapeVariant
        {
            public ChairShape shape;
            public GameObject root;
            public List<Renderer> renderers = new List<Renderer>();
        }

        [Header("Identity")]
        [Tooltip("Stable id written to the CSV object_id column, e.g. Chair_01.")]
        [SerializeField] string m_ChairId = "Chair_00";

        [Header("Attributes (data-driven)")]
        [SerializeField] ChairSpec m_Spec = new ChairSpec(ChairColor.White, ChairSize.Medium, ChairShape.Solid);

        [Header("Shape variants")]
        [Tooltip("One pre-built body per ChairShape. Exactly one is active at a time; the trial " +
                 "plan decides which. Built and wired by the scene builder.")]
        [SerializeField] List<ShapeVariant> m_ShapeVariants = new List<ShapeVariant>();

        [Header("Wiring")]
        [Tooltip("Renderers whose material is swapped for the highlight. Points at the ACTIVE " +
                 "shape variant's renderers; re-pointed by ApplySpec.")]
        [SerializeField] List<Renderer> m_Renderers = new List<Renderer>();

        [SerializeField] XRSimpleInteractable m_Interactable;

        [Header("Feedback materials")]
        [SerializeField] Material m_BaseMaterial;
        [SerializeField] Material m_HoverMaterial;
        [SerializeField] Material m_SelectedCorrectMaterial;
        [SerializeField] Material m_SelectedIncorrectMaterial;

        bool m_SelectionEnabled;
        bool m_AlreadySelectedThisTrial;
        bool m_Hovering;

        /// <summary>Raised when the participant selects this chair with a controller.</summary>
        public event Action<ChairTarget> chairSelected;

        /// <summary>
        /// Raised the first time the ray enters this chair while selection is open. Purely
        /// diagnostic — the ExperimentManager decides whether to log it.
        /// </summary>
        public event Action<ChairTarget> chairHovered;

        public string chairId => m_ChairId;
        public ChairSpec spec => m_Spec;
        public bool selectionEnabled => m_SelectionEnabled;
        public IReadOnlyList<ShapeVariant> shapeVariants => m_ShapeVariants;

        void Awake()
        {
            if (m_Interactable == null)
                m_Interactable = GetComponent<XRSimpleInteractable>();

            if (m_Renderers.Count == 0)
                m_Renderers.AddRange(GetComponentsInChildren<Renderer>(true));
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

        // ---------------------------------------------------------------------------------
        // Configuration (used by the scene builder and by tests)
        // ---------------------------------------------------------------------------------

        public void Configure(string chairId, ChairSpec spec, Material baseMaterial,
            Material hoverMaterial, Material correctMaterial, Material incorrectMaterial)
        {
            m_ChairId = chairId;
            m_Spec = spec;
            m_BaseMaterial = baseMaterial;
            m_HoverMaterial = hoverMaterial;
            m_SelectedCorrectMaterial = correctMaterial;
            m_SelectedIncorrectMaterial = incorrectMaterial;
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

        public void SetShapeVariants(IEnumerable<ShapeVariant> variants)
        {
            m_ShapeVariants.Clear();
            m_ShapeVariants.AddRange(variants);
        }

        // ---------------------------------------------------------------------------------
        // Per-trial appearance
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Gives this chair the attributes it must show for the current trial.
        ///
        /// WHY IT SWITCHES A PRE-BUILT BODY INSTEAD OF REBUILDING ONE: the XRSimpleInteractable
        /// and its collider list are registered with the XRInteractionManager once, when the
        /// scene loads, and stay registered for the whole session. Destroying and recreating
        /// chair geometry mid-session would churn that registration — which is exactly the
        /// failure mode that once left the chairs pointable-at but unselectable. Toggling which
        /// child body is active changes nothing the interaction system tracks.
        /// </summary>
        /// <param name="spec">Colour, size and shape for this trial.</param>
        /// <param name="baseMaterial">Material for the spec's colour. Becomes the resting look.</param>
        public void ApplySpec(ChairSpec spec, Material baseMaterial)
        {
            m_Spec = spec;

            if (baseMaterial != null)
                m_BaseMaterial = baseMaterial;

            // Show the requested silhouette, hide the others, and point the highlight renderers
            // at whichever body is now visible.
            var matched = false;
            m_Renderers.Clear();

            for (var i = 0; i < m_ShapeVariants.Count; i++)
            {
                var variant = m_ShapeVariants[i];
                if (variant?.root == null)
                    continue;

                var isActive = variant.shape == spec.shape;
                if (variant.root.activeSelf != isActive)
                    variant.root.SetActive(isActive);

                if (!isActive)
                    continue;

                matched = true;
                for (var r = 0; r < variant.renderers.Count; r++)
                {
                    if (variant.renderers[r] != null)
                        m_Renderers.Add(variant.renderers[r]);
                }
            }

            if (!matched)
            {
                // Never leave an invisible chair in the room: a missing variant would silently
                // remove a distractor and change the difficulty of the trial.
                Debug.LogError($"[IKEA_EEG] {m_ChairId} has no '{spec.shape}' shape variant. " +
                               "Re-run IKEA_EEG > Build Experiment Scene.");
                m_Renderers.AddRange(GetComponentsInChildren<Renderer>(true));
            }

            var scale = ChairAttributeVisuals.ToScale(spec.size);
            transform.localScale = new Vector3(scale, scale, scale);

            ApplyMaterial(m_BaseMaterial);
        }

        /// <summary>Moves the chair onto one of Area B's fixed slots for this trial.</summary>
        public void PlaceAtSlot(ChairSlot slot)
        {
            if (slot == null)
                return;

            transform.SetPositionAndRotation(slot.transform.position, slot.transform.rotation);
        }

        // ---------------------------------------------------------------------------------
        // Trial control
        // ---------------------------------------------------------------------------------

        /// <summary>Allows this chair to be selected. Called when the response timer starts.</summary>
        public void EnableSelection(bool value)
        {
            m_SelectionEnabled = value;

            // NOTE: the XRSimpleInteractable is deliberately LEFT ENABLED at all times.
            //
            // This used to toggle m_Interactable.enabled, which unregisters the interactable
            // from the XRInteractionManager on disable and re-registers it on enable. That
            // round trip is what left the chairs pointable-at but unselectable. Gating in
            // software instead keeps registration stable for the whole session, and the
            // guards in OnSelectEntered/OnHoverEntered still mean nothing can be selected
            // before the instruction has finished.
            if (m_Interactable != null && !m_Interactable.enabled)
                m_Interactable.enabled = true;
        }

        /// <summary>
        /// Diagnostic used when selection opens: reports whether this chair is actually in a
        /// state where the controller ray can select it.
        /// </summary>
        public string DescribeSelectability()
        {
            if (m_Interactable == null)
                return $"{m_ChairId}: NO XRSimpleInteractable";

            var colliderCount = m_Interactable.colliders != null ? m_Interactable.colliders.Count : 0;

            return $"{m_ChairId}: interactable_enabled={m_Interactable.enabled}, " +
                   $"gameObject_active={gameObject.activeInHierarchy}, " +
                   $"colliders={colliderCount}, selection_open={m_SelectionEnabled}, " +
                   $"already_selected={m_AlreadySelectedThisTrial}";
        }

        /// <summary>
        /// Full reset to the pre-trial state: original material, selectable again,
        /// duplicate-selection lock cleared.
        /// </summary>
        public void ResetToInitialState()
        {
            m_AlreadySelectedThisTrial = false;
            m_Hovering = false;
            EnableSelection(false);
            ApplyMaterial(m_BaseMaterial);
        }

        /// <summary>Applies the post-selection highlight. Called by the selection task.</summary>
        public void ShowSelectionFeedback(bool isCorrect)
        {
            var mat = isCorrect ? m_SelectedCorrectMaterial : m_SelectedIncorrectMaterial;
            ApplyMaterial(mat != null ? mat : m_BaseMaterial);
        }

        // ---------------------------------------------------------------------------------
        // XRI callbacks
        // ---------------------------------------------------------------------------------

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            // Two independent guards against duplicate selections: the per-chair latch here,
            // and the task-level latch in ChairSelectionTask. Either alone would be enough;
            // both together mean a double trigger-pull can never produce two CHAIR_SELECTED
            // rows in the CSV.
            if (!m_SelectionEnabled || m_AlreadySelectedThisTrial)
                return;

            m_AlreadySelectedThisTrial = true;
            chairSelected?.Invoke(this);
        }

        void OnHoverEntered(HoverEnterEventArgs args)
        {
            if (!m_SelectionEnabled || m_AlreadySelectedThisTrial)
                return;

            m_Hovering = true;
            if (m_HoverMaterial != null)
                ApplyMaterial(m_HoverMaterial);

            chairHovered?.Invoke(this);
        }

        void OnHoverExited(HoverExitEventArgs args)
        {
            if (!m_Hovering || m_AlreadySelectedThisTrial)
                return;

            m_Hovering = false;
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
