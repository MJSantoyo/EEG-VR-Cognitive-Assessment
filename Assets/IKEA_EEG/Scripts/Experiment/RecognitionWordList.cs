using System;
using System.Collections.Generic;
using UnityEngine;

namespace IkeaEeg.Experiment
{
    /// <summary>
    /// What class an item belongs to. Logged for every recognition item, because a response is
    /// only interpretable against what the item actually was.
    /// </summary>
    public enum RecognitionItemClass
    {
        /// <summary>A word that WAS presented during encoding.</summary>
        Target,

        /// <summary>A novel word that was NOT presented during encoding.</summary>
        Lure,
    }

    /// <summary>Which recognition phase an item set belongs to.</summary>
    public enum RecognitionPhase
    {
        Immediate,
        Delayed,
    }

    /// <summary>
    /// Configurable stimulus data for the recognition-memory protocol.
    ///
    /// WHAT THIS IS NOT. It is **not** the CNS Vital Signs word list, and it carries no CNS
    /// stimuli. The official CNS stimulus words have not been established from authorised
    /// methodological documentation, so this asset ships EMPTY or with clearly marked
    /// development placeholders. Nothing here may be described as CNS material, and the
    /// structure deliberately does not encode any CNS scoring formula.
    ///
    /// WHY A ScriptableObject. The experimenter must be able to fill in the real lists later
    /// without touching behavioural logic. Every consumer reads counts and items from this
    /// asset; no coroutine, UI element or logger contains a word or a list length.
    ///
    /// THE THREE LISTS ARE SEPARATE ON PURPOSE:
    ///   targetWords              — presented during encoding, then re-presented as Targets.
    ///   immediateRecognitionLures— novel items for the immediate phase only.
    ///   delayedRecognitionLures  — novel items for the delayed phase only.
    /// A lure reused across both phases would no longer be novel the second time, which would
    /// silently change what a false alarm means. Keeping them apart makes that impossible by
    /// construction rather than by discipline.
    ///
    /// Create via: Assets ▸ Create ▸ IKEA_EEG ▸ Recognition Word List
    /// </summary>
    [CreateAssetMenu(fileName = "RecognitionWordList_Default",
        menuName = "IKEA_EEG/Recognition Word List", order = 2)]
    public class RecognitionWordList : ScriptableObject
    {
        [Header("PROVENANCE — READ THIS FIRST")]
        [Tooltip("OFF for any list that has not been established from authorised methodological " +
                 "documentation. While this is off, the words here are DEVELOPMENT ONLY and no " +
                 "output derived from them may be reported as a validated memory measure.")]
        [SerializeField] bool m_ValidatedForResearch;

        [Tooltip("Where these words came from. While the list is unvalidated this should say so " +
                 "explicitly, e.g. 'DEVELOPMENT ONLY — NOT CNS STIMULI'.")]
        [TextArea(2, 4)]
        [SerializeField] string m_ProvenanceNote =
            "DEVELOPMENT ONLY — NOT CNS STIMULI. Placeholder words for functional testing of " +
            "the recognition architecture. Replace with the authorised list before any data " +
            "collection. These words carry no norms and no clinical meaning.";

        [Tooltip("Language these words are FOR. Encoding and recognition must happen in one " +
                 "language. This prototype iteration is English-only.")]
        [SerializeField] Localization.ExperimentLanguage m_Language =
            Localization.ExperimentLanguage.English;

        [Header("Encoding + recognition stimuli")]
        [Tooltip("Words presented during encoding, in presentation order. These become the " +
                 "Target items in both recognition phases. The protocol calls for 15; the code " +
                 "reads this list's length and never assumes a number.")]
        [SerializeField] List<string> m_TargetWords = new List<string>();

        [Tooltip("Novel words for the IMMEDIATE recognition phase. Never presented during " +
                 "encoding. The CNS-derived structure pairs 15 targets with 15 lures for 30 " +
                 "items, but the count is read from this list.")]
        [SerializeField] List<string> m_ImmediateRecognitionLures = new List<string>();

        [Tooltip("Novel words for the DELAYED recognition phase. Must not overlap the immediate " +
                 "lures — a word already seen in the immediate phase is no longer novel. The " +
                 "item-level structure of delayed recognition is NOT yet established, so this " +
                 "list is deliberately independent and may legitimately differ in length.")]
        [SerializeField] List<string> m_DelayedRecognitionLures = new List<string>();

        [Header("Identity (round list + stable concept IDs)")]
        [Tooltip("Which round list this is (A, B, C). Together with the language it is what the " +
                 "run resolver selects on. Empty on the legacy placeholder.")]
        [SerializeField] string m_ListId = string.Empty;

        [Tooltip("Stable concept ID per target word, same order as the targets (e.g. WORD_CANDLE). " +
                 "Identical across languages, so EN/ES/JA responses join on it.")]
        [SerializeField] List<string> m_TargetIds = new List<string>();

        [SerializeField] List<string> m_ImmediateLureIds = new List<string>();

        [SerializeField] List<string> m_DelayedLureIds = new List<string>();

        public bool validatedForResearch => m_ValidatedForResearch;
        public string provenanceNote => m_ProvenanceNote;
        public Localization.ExperimentLanguage language => m_Language;

        public IReadOnlyList<string> targetWords => m_TargetWords;
        public IReadOnlyList<string> immediateRecognitionLures => m_ImmediateRecognitionLures;
        public IReadOnlyList<string> delayedRecognitionLures => m_DelayedRecognitionLures;

        public string listId => m_ListId ?? string.Empty;

        /// <summary>The value logged in the CSV word_set_id column, e.g. B_JA.</summary>
        public string wordSetId =>
            $"{listId}_{Localization.ExperimentLanguages.ToCode(m_Language)}";

        /// <summary>True when every word list carries a stable ID per word.</summary>
        public bool hasStableIds =>
            CountsMatch(m_TargetWords, m_TargetIds) && m_TargetIds.Count > 0 &&
            CountsMatch(m_ImmediateRecognitionLures, m_ImmediateLureIds) &&
            CountsMatch(m_DelayedRecognitionLures, m_DelayedLureIds);

        public IReadOnlyList<string> targetIds => m_TargetIds;

        public IReadOnlyList<string> LureIds(RecognitionPhase phase) =>
            phase == RecognitionPhase.Immediate
                ? (IReadOnlyList<string>)m_ImmediateLureIds
                : m_DelayedLureIds;

        /// <summary>Stable ID of target i, or empty when this list has none.</summary>
        public string TargetId(int index) => IdAt(m_TargetIds, index);

        /// <summary>Stable ID of lure i of a phase, or empty when this list has none.</summary>
        public string LureId(RecognitionPhase phase, int index) =>
            IdAt(phase == RecognitionPhase.Immediate ? m_ImmediateLureIds : m_DelayedLureIds, index);

        static string IdAt(List<string> ids, int index) =>
            ids != null && index >= 0 && index < ids.Count ? ids[index] ?? string.Empty : string.Empty;

        static bool CountsMatch(List<string> words, List<string> ids) =>
            words != null && ids != null && ids.Count == words.Count;

        public int targetCount => m_TargetWords?.Count ?? 0;

        /// <summary>Total items presented in a phase: every target plus that phase's lures.</summary>
        public int ItemCount(RecognitionPhase phase) => targetCount + LureCount(phase);

        public int LureCount(RecognitionPhase phase)
        {
            var lures = phase == RecognitionPhase.Immediate
                ? m_ImmediateRecognitionLures
                : m_DelayedRecognitionLures;

            return lures?.Count ?? 0;
        }

        public IReadOnlyList<string> Lures(RecognitionPhase phase) =>
            phase == RecognitionPhase.Immediate
                ? (IReadOnlyList<string>)m_ImmediateRecognitionLures
                : m_DelayedRecognitionLures;

        /// <summary>
        /// Structural validation. Returns false with a specific reason rather than a bool alone,
        /// because a malformed stimulus list must be diagnosable before a participant is in the
        /// headset, not after the session.
        ///
        /// Checks the things that would silently corrupt the measurement: an empty or duplicated
        /// item, and any overlap BETWEEN classes. A word that is both a target and a lure makes
        /// its own response unclassifiable — it is simultaneously a hit and a false alarm.
        /// </summary>
        public bool Validate(RecognitionPhase phase, out string problem)
        {
            if (m_TargetWords == null || m_TargetWords.Count == 0)
            {
                problem = "no target words configured";
                return false;
            }

            var lures = Lures(phase);

            if (lures == null || lures.Count == 0)
            {
                problem = $"no {phase} recognition lures configured";
                return false;
            }

            // IDs are optional (legacy placeholder, self-test fixtures), but when present they
            // must pair one-to-one with the words or an item would be logged under another's ID.
            if (m_TargetIds != null && m_TargetIds.Count > 0 &&
                m_TargetIds.Count != m_TargetWords.Count)
            {
                problem = $"{m_TargetIds.Count} target IDs for {m_TargetWords.Count} target words";
                return false;
            }

            var lureIds = LureIds(phase);

            if (lureIds != null && lureIds.Count > 0 && lureIds.Count != lures.Count)
            {
                problem = $"{lureIds.Count} {phase} lure IDs for {lures.Count} {phase} lures";
                return false;
            }

            if (!NoBlanksOrDuplicates(m_TargetWords, "target", out problem))
                return false;

            if (!NoBlanksOrDuplicates(lures, $"{phase} lure", out problem))
                return false;

            // The overlap that makes a response meaningless.
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var word in m_TargetWords)
                targets.Add(word.Trim());

            foreach (var lure in lures)
            {
                if (targets.Contains(lure.Trim()))
                {
                    problem = $"'{lure}' is both a target and a {phase} lure; a response to it " +
                              "would be simultaneously a hit and a false alarm";
                    return false;
                }
            }

            // A delayed lure that already appeared in the immediate phase is not novel.
            if (phase == RecognitionPhase.Delayed && m_ImmediateRecognitionLures != null)
            {
                var immediate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var word in m_ImmediateRecognitionLures)
                    immediate.Add(word.Trim());

                foreach (var lure in lures)
                {
                    if (immediate.Contains(lure.Trim()))
                    {
                        problem = $"'{lure}' is used as both an immediate and a delayed lure; " +
                                  "it is no longer novel by the delayed phase";
                        return false;
                    }
                }
            }

            problem = string.Empty;
            return true;
        }

        static bool NoBlanksOrDuplicates(IReadOnlyList<string> words, string label,
            out string problem)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < words.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(words[i]))
                {
                    problem = $"{label} {i + 1} is blank";
                    return false;
                }

                if (!seen.Add(words[i].Trim()))
                {
                    problem = $"{label} list repeats '{words[i]}'";
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// One line of provenance for the CSV and the session summary.
        ///
        /// Always states the validation status, so a file produced from placeholder words says
        /// so on its own without needing this project to hand.
        /// </summary>
        public string DescribeProvenance(RecognitionPhase phase)
        {
            return $"validated_for_research={(m_ValidatedForResearch ? "TRUE" : "FALSE")}; " +
                   $"list_id={listId}; " +
                   $"language={m_Language}; " +
                   $"target_count={targetCount}; " +
                   $"{phase.ToString().ToLowerInvariant()}_lure_count={LureCount(phase)}; " +
                   $"item_count={ItemCount(phase)}; " +
                   $"provenance={m_ProvenanceNote.Replace('\n', ' ')}";
        }

        /// <summary>Editor/builder wiring. Never called at run time.</summary>
        public void SetLists(List<string> targets, List<string> immediateLures,
            List<string> delayedLures, bool validated, string provenanceNote)
        {
            m_TargetWords = targets;
            m_ImmediateRecognitionLures = immediateLures;
            m_DelayedRecognitionLures = delayedLures;
            m_ValidatedForResearch = validated;
            m_ProvenanceNote = provenanceNote;
        }

        /// <summary>Editor/importer wiring for a round list. Never called at run time.</summary>
        public void SetIdentity(string listId, Localization.ExperimentLanguage language,
            List<string> targetIds, List<string> immediateLureIds, List<string> delayedLureIds)
        {
            m_ListId = listId;
            m_Language = language;
            m_TargetIds = targetIds;
            m_ImmediateLureIds = immediateLureIds;
            m_DelayedLureIds = delayedLureIds;
        }
    }
}
