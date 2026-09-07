using System;
using System.Collections.Generic;
using UnityEngine;

namespace IkeaEeg.Experiment
{
    /// <summary>One named list of words to be memorised.</summary>
    [Serializable]
    public class WordSet
    {
        [Tooltip("Stable machine-readable id written to the CSV word_set_id column, e.g. SET_A. " +
                 "Never change it for an existing set — analyses join on this value. Rename the " +
                 "display label instead. Left blank it is derived from the label, so word lists " +
                 "authored before ids existed keep working.")]
        public string wordSetId = string.Empty;

        [Tooltip("Label for this set, e.g. 'Set A'. Written to the CSV notes for traceability.")]
        public string setName = "Set A";

        [Tooltip("The language this set is FOR. Encoding and recall must happen in one language, " +
                 "so a set is only ever offered to a session running in its own language. " +
                 "A word list is language-specific DATA, not a translation of another list.")]
        public Localization.ExperimentLanguage language = Localization.ExperimentLanguage.English;

        [Tooltip("OFF for any set that has not been validated as an experimental instrument. " +
                 "A set left off is usable for development but must never be reported as " +
                 "validated verbal-memory testing.")]
        public bool validatedForResearch;

        [Tooltip("Free text describing this set's provenance and validation status.")]
        [TextArea(1, 3)]
        public string validationNote = string.Empty;

        // ---- Provenance -------------------------------------------------------------------
        // APPENDED FIELDS. Existing sets deserialize with these empty, which is exactly right:
        // a set with no recorded provenance is one whose provenance is genuinely unknown, and
        // that is what the data should then say.
        //
        // These exist so a run can be reconstructed item for item. "Spanish words were used"
        // is not reproducible; "items 1-5 of ES_F1_MAIN, in published order" is.

        [Tooltip("Where these words came from, e.g. 'RAVLT-derived Spanish adaptation'. " +
                 "Describes the STIMULI only — it is never a claim about the IKEA_EEG task.")]
        public string wordSource = string.Empty;

        [Tooltip("Stable id of the published list this set was drawn from, e.g. ES_F1_MAIN. " +
                 "Parallel forms are never merged, so this identifies exactly one of them.")]
        public string sourceForm = string.Empty;

        [Tooltip("0-based index of the first word within the source form. With the word count " +
                 "this reproduces the exact subset that was presented.")]
        public int sourceStartIndex;

        [Tooltip("How many words the full source form contains (e.g. 15). Compared with the " +
                 "set's own word count, this is what makes a subset visible as a subset.")]
        public int sourceFormWordCount;

        /// <summary>
        /// True when this set is a SUBSET of a larger published form rather than the whole of
        /// it.
        ///
        /// Derived rather than stored, so it can never contradict the numbers around it. It is
        /// reported on every run because a five-word subset of a fifteen-word instrument
        /// carries none of that instrument's norms, and the data must say so on its own.
        /// </summary>
        public bool IsSubsetOfSourceForm =>
            sourceFormWordCount > 0 && words != null && words.Count < sourceFormWordCount;

        /// <summary>
        /// One line describing exactly what was presented, for the CSV and the summaries.
        ///
        /// Deliberately includes the words themselves: the set id alone is only reproducible
        /// while this project's assets are to hand, and analyses outlive assets.
        /// </summary>
        public string DescribeProvenance()
        {
            var items = words != null ? string.Join("|", words) : string.Empty;

            return $"word_source={(string.IsNullOrEmpty(wordSource) ? "unspecified" : wordSource)}; " +
                   $"source_form={(string.IsNullOrEmpty(sourceForm) ? "unspecified" : sourceForm)}; " +
                   $"source_form_word_count={sourceFormWordCount}; " +
                   $"source_start_index={sourceStartIndex}; " +
                   $"selected_word_count={words?.Count ?? 0}; " +
                   $"is_subset_of_source_form={(IsSubsetOfSourceForm ? "TRUE" : "FALSE")}; " +
                   $"selected_words={items}";
        }

        [Tooltip("Words in presentation order.")]
        public List<string> words = new List<string>();

        [Tooltip("Spoken recording of each word, in the SAME order as 'words'. This is what " +
                 "the participant actually hears — the words are never shown on screen during " +
                 "encoding. Generate with IKEA_EEG > Generate Spoken Word Clips (local TTS), " +
                 "or drop in your own human recordings.")]
        public List<AudioClip> wordClips = new List<AudioClip>();

        public bool IsValid(int expectedCount) => words != null && words.Count == expectedCount;

        /// <summary>
        /// The id actually written to the CSV: the explicit one when set, otherwise one derived
        /// from the display label so no session is ever logged with a blank word_set_id.
        /// </summary>
        public string EffectiveId()
        {
            if (!string.IsNullOrWhiteSpace(wordSetId))
                return wordSetId.Trim();

            if (string.IsNullOrWhiteSpace(setName))
                return "SET_UNNAMED";

            var sb = new System.Text.StringBuilder(setName.Length);
            foreach (var c in setName.Trim().ToUpperInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');

            return sb.ToString().Trim('_');
        }

        /// <summary>
        /// Full structural check of one set: the exact word count, an id, five ordered words
        /// with no blanks or duplicates, and one non-null clip per word in the SAME order.
        ///
        /// Word ORDER is part of the stimulus, not an implementation detail: serial-position
        /// effects are the whole point of a five-word list, so a set whose clips are out of
        /// step with its words would silently invalidate the recall data.
        /// </summary>
        public bool ValidateStructure(int expectedCount, out string problem)
        {
            var id = EffectiveId();

            if (string.IsNullOrWhiteSpace(id))
            {
                problem = $"set '{setName}' has no usable word_set_id";
                return false;
            }

            if (words == null || words.Count != expectedCount)
            {
                problem = $"set '{id}' has {words?.Count ?? 0} words; expected {expectedCount}";
                return false;
            }

            for (var i = 0; i < words.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(words[i]))
                {
                    problem = $"set '{id}' word {i + 1} is blank";
                    return false;
                }
            }

            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words)
            {
                if (!distinct.Add(word.Trim()))
                {
                    problem = $"set '{id}' repeats the word '{word}'";
                    return false;
                }
            }

            if (wordClips == null || wordClips.Count < words.Count)
            {
                problem = $"set '{id}' has {wordClips?.Count ?? 0} clips for " +
                          $"{words.Count} words";
                return false;
            }

            for (var i = 0; i < words.Count; i++)
            {
                var clip = wordClips[i];

                if (clip == null)
                {
                    problem = $"set '{id}' has no clip for word {i + 1} ('{words[i]}')";
                    return false;
                }

                if (clip.samples <= 0 || clip.length <= 0f)
                {
                    problem = $"set '{id}' clip for word {i + 1} ('{words[i]}') is empty " +
                              $"({clip.name}: {clip.samples} samples)";
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>Spoken clip for a word index, or null if none is assigned.</summary>
        public AudioClip GetClip(int wordIndex)
        {
            if (wordClips == null || wordIndex < 0 || wordIndex >= wordClips.Count)
                return null;

            return wordClips[wordIndex];
        }

        /// <summary>True when every word has a spoken recording.</summary>
        public bool HasAllClips()
        {
            if (words == null || wordClips == null || wordClips.Count < words.Count)
                return false;

            for (var i = 0; i < words.Count; i++)
            {
                if (wordClips[i] == null)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Configurable word lists for the verbal memory task.
    ///
    /// The word list is data, NOT code: nothing in the UI or the state machine hard-codes a
    /// word. Swapping in a validated clinical list, adding parallel sets for repeated
    /// sessions, or translating the whole thing is an edit to this asset only.
    ///
    /// Create via: Assets ▸ Create ▸ IKEA_EEG ▸ Word List Definition
    /// </summary>
    [CreateAssetMenu(fileName = "WordList_Default",
        menuName = "IKEA_EEG/Word List Definition", order = 1)]
    public class WordListDefinition : ScriptableObject
    {
        [Tooltip("How many words each set must contain. The protocol uses five.")]
        [SerializeField] int m_WordsPerSet = 5;

        [Tooltip("One or more parallel word sets. The experiment uses one set per trial.")]
        [SerializeField] List<WordSet> m_Sets = new List<WordSet>();

        public int wordsPerSet => m_WordsPerSet;
        public int setCount => m_Sets.Count;
        public IReadOnlyList<WordSet> sets => m_Sets;

        /// <summary>Returns the words of the requested set, clamped to a valid index.</summary>
        public IReadOnlyList<string> GetWords(int setIndex)
        {
            if (m_Sets.Count == 0)
                return Array.Empty<string>();

            var index = Mathf.Clamp(setIndex, 0, m_Sets.Count - 1);
            return m_Sets[index].words;
        }

        public string GetSetName(int setIndex)
        {
            if (m_Sets.Count == 0)
                return string.Empty;

            var index = Mathf.Clamp(setIndex, 0, m_Sets.Count - 1);
            return m_Sets[index].setName;
        }

        /// <summary>Stable id of the set, for the CSV word_set_id column.</summary>
        public string GetSetId(int setIndex)
        {
            if (m_Sets.Count == 0)
                return string.Empty;

            var index = Mathf.Clamp(setIndex, 0, m_Sets.Count - 1);
            return m_Sets[index].EffectiveId();
        }

        /// <summary>
        /// Structural validation of EVERY set: five ordered words, five usable clips, no nulls,
        /// no duplicate ids. Run at start-up so a malformed list is caught before a participant
        /// is in the headset rather than after the session.
        /// </summary>
        public bool ValidateAllSets(out string problem)
        {
            if (m_Sets.Count == 0)
            {
                problem = "word list contains no sets";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < m_Sets.Count; i++)
            {
                if (!m_Sets[i].ValidateStructure(m_WordsPerSet, out problem))
                    return false;

                if (!ids.Add(m_Sets[i].EffectiveId()))
                {
                    problem = $"two sets share the id '{m_Sets[i].EffectiveId()}'";
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// The spoken recording for one word of a set. This is the stimulus the participant
        /// receives — the words are never displayed during encoding.
        /// </summary>
        public AudioClip GetWordClip(int setIndex, int wordIndex)
        {
            if (m_Sets.Count == 0)
                return null;

            var index = Mathf.Clamp(setIndex, 0, m_Sets.Count - 1);
            return m_Sets[index].GetClip(wordIndex);
        }

        public WordSet GetSet(int setIndex)
        {
            if (m_Sets.Count == 0)
                return null;

            return m_Sets[Mathf.Clamp(setIndex, 0, m_Sets.Count - 1)];
        }

        /// <summary>
        /// Indices of the sets belonging to one language.
        ///
        /// THE RULE THIS ENFORCES: a session presents word sets in its OWN language or it does
        /// not run. Encoding in one language and recall in another is not a degraded experiment,
        /// it is a different one — so there is deliberately no fallback path here. A caller that
        /// gets an empty list must stop, not substitute English.
        /// </summary>
        public List<int> GetSetIndicesForLanguage(Localization.ExperimentLanguage language)
        {
            var indices = new List<int>();

            for (var i = 0; i < m_Sets.Count; i++)
            {
                if (m_Sets[i] != null && m_Sets[i].language == language)
                    indices.Add(i);
            }

            return indices;
        }

        /// <summary>
        /// The set this session should use, or -1 when the language has none.
        ///
        /// <paramref name="preferredIndex"/> is honoured only if it belongs to the language;
        /// otherwise the first set of that language is used. -1 means the run must be blocked.
        /// </summary>
        public int ResolveSetIndexForLanguage(Localization.ExperimentLanguage language,
            int preferredIndex)
        {
            var candidates = GetSetIndicesForLanguage(language);

            if (candidates.Count == 0)
                return -1;

            return candidates.Contains(preferredIndex) ? preferredIndex : candidates[0];
        }

        /// <summary>Checks every set has exactly <see cref="wordsPerSet"/> words.</summary>
        public bool Validate(out string problem)
        {
            if (m_Sets.Count == 0)
            {
                problem = "Word list contains no sets.";
                return false;
            }

            for (var i = 0; i < m_Sets.Count; i++)
            {
                if (!m_Sets[i].IsValid(m_WordsPerSet))
                {
                    problem = $"Set '{m_Sets[i].setName}' (index {i}) has " +
                              $"{m_Sets[i].words?.Count ?? 0} words; expected {m_WordsPerSet}.";
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// Checks the set that will actually be used has a spoken recording for every word.
        /// Separate from <see cref="Validate"/> because a missing clip is a stimulus-delivery
        /// failure, not a malformed list — the participant would simply hear nothing.
        /// </summary>
        public bool ValidateClips(int setIndex, out string problem)
        {
            var set = GetSet(setIndex);
            if (set == null)
            {
                problem = "no word set at the configured index";
                return false;
            }

            var missing = new List<string>();
            for (var i = 0; i < set.words.Count; i++)
            {
                if (set.GetClip(i) == null)
                    missing.Add($"[{i + 1}] {set.words[i]}");
            }

            if (missing.Count > 0)
            {
                problem = $"set '{set.setName}' has no spoken clip for: {string.Join(", ", missing)}. " +
                          "Run IKEA_EEG > Generate Spoken Word Clips (local TTS).";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>Used by the scene builder to populate the example set on first creation.</summary>
        public void SetSets(List<WordSet> sets, int wordsPerSet)
        {
            m_Sets = sets;
            m_WordsPerSet = wordsPerSet;
        }
    }
}
