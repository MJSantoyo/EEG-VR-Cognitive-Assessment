using System;
using System.Collections.Generic;
using UnityEngine;
using IkeaEeg.Core;
using IkeaEeg.Experiment;

namespace IkeaEeg.Interaction
{
    /// <summary>Outcome of one chair-selection trial. Passed to the manager and the logger.</summary>
    public struct ChairSelectionResult
    {
        public string chairId;
        public ChairSpec target;
        public ChairSpec selected;
        public bool correct;
        public double responseTimeMs;
        public int attributeMatchCount;
    }

    /// <summary>
    /// Owns the Area B task: which chair is the target, when selection opens, how long the
    /// participant took, and whether they were right.
    ///
    /// Timing contract (this matters for EEG alignment):
    ///   * the response timer starts ONLY in <see cref="BeginSelection"/>, which the
    ///     ExperimentManager calls after the instruction has finished being presented;
    ///   * the timer stops in the very first line of the selection callback, before any
    ///     logging, audio or material work, so feedback cost is not counted as response time.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChairSelectionTask : MonoBehaviour
    {
        [Header("Chairs")]
        [Tooltip("All selectable chairs in Area B. Populated by the scene builder.")]
        [SerializeField] List<ChairTarget> m_Chairs = new List<ChairTarget>();

        [Header("Layout")]
        [Tooltip("The fixed, pre-validated positions chairs are assigned to. Six of them, one " +
                 "per chair. Populated by the scene builder.")]
        [SerializeField] List<ChairSlot> m_Slots = new List<ChairSlot>();

        [Tooltip("One material per ChairColor, indexed by the enum's order. A trial's chair " +
                 "takes the material of the colour its plan assigns it.")]
        [SerializeField] List<Material> m_ColorMaterials = new List<Material>();

        [Header("Target")]
        [Tooltip("The (size, colour, shape) combination the participant must find. Set per " +
                 "trial from the generated plan — do not edit at runtime.")]
        [SerializeField] ChairSpec m_TargetSpec = new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Solid);

        readonly IntervalTimer m_ResponseTimer = new IntervalTimer();

        bool m_SelectionOpen;
        bool m_SelectionMade;

        /// <summary>Raised on the first (and only) chair selection of the current chair trial.</summary>
        public event Action<ChairSelectionResult> selectionMade;

        /// <summary>Raised when the ray first enters a chair while selection is open.</summary>
        public event Action<ChairTarget> chairHovered;

        public IReadOnlyList<ChairTarget> chairs => m_Chairs;
        public IReadOnlyList<ChairSlot> slots => m_Slots;
        public ChairSpec targetSpec => m_TargetSpec;
        public bool selectionOpen => m_SelectionOpen;

        /// <summary>True once a chair has been selected in the current trial.</summary>
        public bool hasSelection => m_SelectionMade;

        /// <summary>Chair identities in a stable order. This is what the generator plans against.</summary>
        public List<string> GetChairIds()
        {
            var ids = new List<string>(m_Chairs.Count);
            foreach (var chair in m_Chairs)
            {
                if (chair != null)
                    ids.Add(chair.chairId);
            }

            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        void Awake()
        {
            if (m_Chairs.Count == 0)
                m_Chairs.AddRange(GetComponentsInChildren<ChairTarget>(true));
        }

        void OnEnable()
        {
            foreach (var chair in m_Chairs)
            {
                if (chair == null)
                    continue;

                chair.chairSelected += OnChairSelected;
                chair.chairHovered += OnChairHovered;
            }
        }

        void OnDisable()
        {
            foreach (var chair in m_Chairs)
            {
                if (chair == null)
                    continue;

                chair.chairSelected -= OnChairSelected;
                chair.chairHovered -= OnChairHovered;
            }
        }

        // ---------------------------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------------------------

        public void SetChairs(IEnumerable<ChairTarget> chairs)
        {
            foreach (var chair in m_Chairs)
            {
                if (chair == null)
                    continue;

                chair.chairSelected -= OnChairSelected;
                chair.chairHovered -= OnChairHovered;
            }

            m_Chairs.Clear();
            m_Chairs.AddRange(chairs);

            foreach (var chair in m_Chairs)
            {
                if (chair == null)
                    continue;

                chair.chairSelected += OnChairSelected;
                chair.chairHovered += OnChairHovered;
            }
        }

        public void SetSlots(IEnumerable<ChairSlot> slots)
        {
            m_Slots.Clear();
            m_Slots.AddRange(slots);
        }

        public void SetColorMaterials(IEnumerable<Material> materialsByColorIndex)
        {
            m_ColorMaterials.Clear();
            m_ColorMaterials.AddRange(materialsByColorIndex);
        }

        public void SetTarget(ChairSpec target)
        {
            m_TargetSpec = target;
        }

        public ChairTarget FindChair(string chairId)
        {
            foreach (var chair in m_Chairs)
            {
                if (chair != null && chair.chairId == chairId)
                    return chair;
            }

            return null;
        }

        public ChairSlot FindSlot(int slotIndex)
        {
            foreach (var slot in m_Slots)
            {
                if (slot != null && slot.slotIndex == slotIndex)
                    return slot;
            }

            return null;
        }

        public Material GetColorMaterial(ChairColor color)
        {
            var index = (int)color;
            return index >= 0 && index < m_ColorMaterials.Count ? m_ColorMaterials[index] : null;
        }

        // ---------------------------------------------------------------------------------
        // Per-trial layout
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Applies one generated trial to the room: each chair identity is moved to its slot
        /// and given the attributes the plan assigns it, and the target is set from the SAME
        /// plan — so the instruction text, the room and the correctness test can never disagree.
        ///
        /// Returns false and changes nothing about the task state if the plan cannot be applied.
        /// </summary>
        public bool ApplyTrialPlan(ChairTrialPlan plan, out string problem)
        {
            if (plan == null || plan.assignments == null || plan.assignments.Length == 0)
            {
                problem = "no trial plan";
                return false;
            }

            if (!plan.ValidateUnambiguous(out problem))
                return false;

            // Everything is resolved BEFORE anything is moved, so a plan referring to a missing
            // chair or slot cannot leave the room half-rearranged.
            var resolved = new List<(ChairTarget chair, ChairSlot slot, ChairSpec spec)>(
                plan.assignments.Length);

            foreach (var assignment in plan.assignments)
            {
                var chair = FindChair(assignment.chairId);
                if (chair == null)
                {
                    problem = $"no chair with id '{assignment.chairId}' in the scene";
                    return false;
                }

                var slot = FindSlot(assignment.slotIndex);
                if (slot == null)
                {
                    problem = $"no slot with index {assignment.slotIndex} in the scene";
                    return false;
                }

                resolved.Add((chair, slot, assignment.spec));
            }

            foreach (var (chair, slot, spec) in resolved)
            {
                chair.PlaceAtSlot(slot);
                chair.ApplySpec(spec, GetColorMaterial(spec.color));
            }

            SetTarget(plan.target);
            ResetTask();

            if (!ValidateTargetIsUnique(out var matches))
            {
                problem = $"after applying the plan, {matches} chairs match the target " +
                          $"{plan.target} (expected exactly 1)";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// Human-readable instruction, e.g. "Select the LARGE, BLUE, MODERN chair."
        /// Built from the target spec so instruction text and correctness can never disagree.
        /// </summary>
        public string BuildInstructionText()
        {
            return $"Select the {m_TargetSpec.ToInstructionString()} chair.";
        }

        /// <summary>
        /// The participant-facing target panel: a short header and the three attributes on
        /// three lines. Built from the target spec, so what is displayed and what counts as
        /// correct can never disagree.
        /// </summary>
        public string BuildTargetText(string header)
        {
            var heading = string.IsNullOrWhiteSpace(header)
                ? Localization.ExperimentLocalization.Get(Localization.LocKeys.ChairTargetHeader)
                : header;

            // The three attributes come from the localization service, so the participant reads
            // them in their own language while the INTERNAL enum values — and therefore the CSV
            // and the seeded generator — are untouched.
            var lines = Localization.ExperimentLocalization.TargetLines(m_TargetSpec);

            return $"{heading}\n\n<b>{lines}</b>";
        }

        /// <summary>
        /// True when exactly one chair in the room matches the target. The scene builder and
        /// the manager call this at start-up so a mis-specified target is caught immediately
        /// instead of silently producing uninterpretable data.
        /// </summary>
        public bool ValidateTargetIsUnique(out int matchingChairCount)
        {
            matchingChairCount = 0;
            foreach (var chair in m_Chairs)
            {
                if (chair != null && chair.spec.Matches(m_TargetSpec))
                    matchingChairCount++;
            }

            return matchingChairCount == 1;
        }

        // ---------------------------------------------------------------------------------
        // Trial control
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Opens selection and starts the response timer. Called by the ExperimentManager
        /// only after CHAIR_INSTRUCTION_COMPLETE.
        /// </summary>
        public void BeginSelection()
        {
            // FIRST LINE, deliberately: the response clock starts before any other work in this
            // method, so enabling six chairs and printing the diagnostic below cannot shave
            // milliseconds off a participant's measured response time. The mirror of the stop
            // rule in OnChairSelected, which stops the clock before doing anything else.
            m_ResponseTimer.Restart();

            m_SelectionOpen = true;
            m_SelectionMade = false;

            foreach (var chair in m_Chairs)
            {
                if (chair != null)
                    chair.EnableSelection(true);
            }

            // Report the state of every chair the moment selection opens. If a chair turns out
            // to be unselectable in the headset, this line in the Console says which link in
            // the chain is broken rather than leaving it to trial and error.
            var report = new System.Text.StringBuilder();
            report.AppendLine($"[IKEA_EEG] Chair selection OPEN. Target: {m_TargetSpec}. " +
                              $"{m_Chairs.Count} chair(s):");

            foreach (var chair in m_Chairs)
            {
                if (chair != null)
                    report.AppendLine("    " + chair.DescribeSelectability());
            }

            Debug.Log(report.ToString().TrimEnd());
        }

        /// <summary>Closes selection without producing a result (used by reset / abort).</summary>
        public void CloseSelection()
        {
            m_SelectionOpen = false;
            m_ResponseTimer.Stop();

            foreach (var chair in m_Chairs)
            {
                if (chair != null)
                    chair.EnableSelection(false);
            }
        }

        /// <summary>Restores every chair to its original material and clears the latches.</summary>
        public void ResetTask()
        {
            m_SelectionOpen = false;
            m_SelectionMade = false;
            m_ResponseTimer.Reset();

            foreach (var chair in m_Chairs)
            {
                if (chair != null)
                    chair.ResetToInitialState();
            }
        }

        // ---------------------------------------------------------------------------------
        // Selection
        // ---------------------------------------------------------------------------------

        void OnChairHovered(ChairTarget chair)
        {
            if (!m_SelectionOpen || m_SelectionMade || chair == null)
                return;

            chairHovered?.Invoke(chair);
        }

        void OnChairSelected(ChairTarget chair)
        {
            // Stop the clock FIRST. Everything below costs time we must not attribute to
            // the participant.
            m_ResponseTimer.Stop();

            if (!m_SelectionOpen || m_SelectionMade || chair == null)
                return;

            m_SelectionMade = true;
            m_SelectionOpen = false;

            // Lock every chair immediately so a second trigger pull cannot register.
            foreach (var other in m_Chairs)
            {
                if (other != null)
                    other.EnableSelection(false);
            }

            var isCorrect = chair.spec.Matches(m_TargetSpec);
            chair.ShowSelectionFeedback(isCorrect);

            var result = new ChairSelectionResult
            {
                chairId = chair.chairId,
                target = m_TargetSpec,
                selected = chair.spec,
                correct = isCorrect,
                responseTimeMs = m_ResponseTimer.ElapsedMilliseconds(),
                attributeMatchCount = chair.spec.MatchCount(m_TargetSpec),
            };

            selectionMade?.Invoke(result);
        }
    }
}
