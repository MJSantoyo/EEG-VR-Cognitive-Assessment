using System;
using System.Collections.Generic;

namespace IkeaEeg.Experiment
{
    /// <summary>What the participant said about an item.</summary>
    public enum RecognitionResponse
    {
        /// <summary>No response recorded — timed out, aborted, or the phase ended first.</summary>
        None,

        SeenBefore,
        NotSeenBefore,
    }

    /// <summary>
    /// The signal-detection outcome of one response.
    ///
    /// These four are the complete, transparent classification and nothing more. There is
    /// deliberately NO composite score here: no d', no corrected-recognition index, and above
    /// all no proprietary or assumed CNS score. Those are analysis decisions to be made against
    /// a defined methodology, and inventing one now would put an unvalidated number into the
    /// data where it would later be mistaken for a result.
    /// </summary>
    public enum RecognitionOutcome
    {
        /// <summary>No response, so no outcome. Never silently counted as an error.</summary>
        NoResponse,

        /// <summary>Target, answered "seen before".</summary>
        Hit,

        /// <summary>Target, answered "not seen before".</summary>
        Miss,

        /// <summary>Lure, answered "not seen before".</summary>
        CorrectRejection,

        /// <summary>Lure, answered "seen before".</summary>
        FalseAlarm,
    }

    /// <summary>
    /// One recognition trial: the item as presented, and what came back.
    ///
    /// Item IDENTITY and item CLASS travel together through shuffling, so a randomised
    /// presentation order can never detach a word from whether it was a target. That coupling is
    /// the whole reason this is a class rather than two parallel lists.
    /// </summary>
    [Serializable]
    public class RecognitionItem
    {
        /// <summary>The word shown to the participant.</summary>
        public string word = string.Empty;

        /// <summary>Target or Lure. Fixed when the item is built; never inferred later.</summary>
        public RecognitionItemClass itemClass;

        /// <summary>Which phase this item belongs to.</summary>
        public RecognitionPhase phase;

        /// <summary>
        /// Stable identifier of the item independent of presentation order, e.g. TARGET_03 or
        /// IMMEDIATE_LURE_07. Analyses join on this, so it must not encode the shuffled position.
        /// </summary>
        public string itemId = string.Empty;

        /// <summary>0-based position in the RANDOMISED sequence actually presented.</summary>
        public int presentationOrder;

        /// <summary>Unity time of stimulus onset, seconds. 0 until presented.</summary>
        public double onsetTime;

        /// <summary>Unity time the response was registered, seconds. 0 until answered.</summary>
        public double responseTime;

        public RecognitionResponse response = RecognitionResponse.None;

        /// <summary>Milliseconds from stimulus onset to response. Negative when unanswered.</summary>
        public double reactionTimeMs =>
            response == RecognitionResponse.None ? -1d : (responseTime - onsetTime) * 1000d;

        /// <summary>
        /// The signal-detection outcome, DERIVED rather than stored.
        ///
        /// Derived so it can never contradict the class and the response it is supposed to
        /// summarise — there is no code path that can write an outcome inconsistent with them.
        /// </summary>
        public RecognitionOutcome outcome
        {
            get
            {
                if (response == RecognitionResponse.None)
                    return RecognitionOutcome.NoResponse;

                var saidSeen = response == RecognitionResponse.SeenBefore;

                return itemClass == RecognitionItemClass.Target
                    ? (saidSeen ? RecognitionOutcome.Hit : RecognitionOutcome.Miss)
                    : (saidSeen ? RecognitionOutcome.FalseAlarm : RecognitionOutcome.CorrectRejection);
            }
        }

        public string ToNotes()
        {
            return $"item_id={itemId}; class={itemClass.ToString().ToUpperInvariant()}; " +
                   $"phase={phase.ToString().ToUpperInvariant()}; " +
                   $"presentation_order={presentationOrder + 1}; " +
                   $"response={response.ToString().ToUpperInvariant()}; " +
                   $"outcome={outcome.ToString().ToUpperInvariant()}; " +
                   $"rt_ms={reactionTimeMs:F1}";
        }
    }

    /// <summary>
    /// The outcome tallies of ONE recognition phase, as typed data rather than as prose.
    ///
    /// WHY THIS EXISTS. The counts were previously computed as local variables inside
    /// <see cref="RecognitionSequence.Summarise"/> and formatted straight into an event's notes
    /// column. That made them readable in the CSV and unreachable from anywhere else — the
    /// results screen could not show them without re-parsing a string it had itself produced.
    /// Parsing prose back into numbers is how a display and its data start disagreeing.
    ///
    /// WHAT IT IS NOT. There is deliberately no composite here: no d', no corrected-recognition
    /// index, no CNS score, no accuracy judgement. Every member is either a raw count or the
    /// sum of raw counts, and each one can be checked by hand against the item list. Turning
    /// these into a score is an analysis decision to be made against a defined methodology, and
    /// inventing one here would put an unvalidated number where it would later be read as a
    /// result.
    ///
    /// NO RESPONSE IS NOT AN ERROR. It is counted, reported and kept out of both
    /// <see cref="totalCorrect"/> and <see cref="totalIncorrect"/>, exactly as the item loop
    /// already treats it. "Did not answer" and "answered wrongly" are different facts.
    /// </summary>
    [Serializable]
    public class RecognitionPhaseResults
    {
        /// <summary>Which phase these counts describe. Taken from the items themselves.</summary>
        public RecognitionPhase phase;

        public int hits;
        public int misses;
        public int correctRejections;
        public int falseAlarms;

        /// <summary>Items that ended without an answer. NEVER scored as an error.</summary>
        public int noResponse;

        /// <summary>Items presented in this phase.</summary>
        public int itemCount;

        /// <summary>Items that were targets, counted from <see cref="RecognitionItem.itemClass"/>.</summary>
        public int targetCount;

        /// <summary>Items that were lures, counted from the same field.</summary>
        public int lureCount;

        /// <summary>True once a phase has actually run. Distinguishes "0 items" from "no phase".</summary>
        public bool hasData => itemCount > 0;

        /// <summary>Hits + correct rejections. A transparent sum, not a score.</summary>
        public int totalCorrect => hits + correctRejections;

        /// <summary>Misses + false alarms. Unanswered items are NOT included.</summary>
        public int totalIncorrect => misses + falseAlarms;

        /// <summary>Items that received an answer — the denominator of "correct responses".</summary>
        public int answeredCount => itemCount - noResponse;

        public void Reset()
        {
            phase = RecognitionPhase.Immediate;
            hits = misses = correctRejections = falseAlarms = noResponse = 0;
            itemCount = targetCount = lureCount = 0;
        }

        /// <summary>
        /// THE ONE COUNTING IMPLEMENTATION. Clears these counts and recomputes them from the
        /// item list.
        ///
        /// Everything is read from <see cref="RecognitionItem.outcome"/> and
        /// <see cref="RecognitionItem.itemClass"/>, both of which are the existing sources of
        /// truth — <c>outcome</c> is a derived property, so there is no way for a tally to
        /// disagree with the class and response it summarises. This method does NOT reclassify
        /// anything; a second Hit/Miss classifier is exactly what must not exist.
        ///
        /// Both consumers go through here — the event-notes summary and the typed session
        /// aggregate — so the number in the CSV and the number on the panel come from the same
        /// loop.
        /// </summary>
        public RecognitionPhaseResults Recount(IReadOnlyList<RecognitionItem> items)
        {
            Reset();

            if (items == null)
                return this;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];

                if (item == null)
                    continue;

                itemCount++;

                // The phase travels with the items; it is never assumed by the caller.
                if (itemCount == 1)
                    phase = item.phase;

                if (item.itemClass == RecognitionItemClass.Target)
                    targetCount++;
                else
                    lureCount++;

                switch (item.outcome)
                {
                    case RecognitionOutcome.Hit: hits++; break;
                    case RecognitionOutcome.Miss: misses++; break;
                    case RecognitionOutcome.CorrectRejection: correctRejections++; break;
                    case RecognitionOutcome.FalseAlarm: falseAlarms++; break;
                    default: noResponse++; break;
                }
            }

            return this;
        }

        /// <summary>Copies another phase's counts into this instance.</summary>
        public void CopyFrom(RecognitionPhaseResults other)
        {
            if (other == null)
            {
                Reset();
                return;
            }

            phase = other.phase;
            hits = other.hits;
            misses = other.misses;
            correctRejections = other.correctRejections;
            falseAlarms = other.falseAlarms;
            noResponse = other.noResponse;
            itemCount = other.itemCount;
            targetCount = other.targetCount;
            lureCount = other.lureCount;
        }

        /// <summary>
        /// The exact string that has always gone into the *_RECOGNITION_END event's notes.
        ///
        /// The format is reproduced character for character on purpose: it is already present in
        /// collected CSVs, and changing it would silently break any script that reads it.
        /// </summary>
        public string ToEventNotes()
        {
            return $"hits={hits}; misses={misses}; correct_rejections={correctRejections}; " +
                   $"false_alarms={falseAlarms}; no_response={noResponse}; " +
                   $"items={itemCount}";
        }
    }

    /// <summary>
    /// Builds and summarises a randomised recognition sequence.
    ///
    /// Kept separate from <see cref="ExperimentManager"/> so the sequence can be built and
    /// asserted in a self-test without a scene, a headset or a coroutine.
    /// </summary>
    public static class RecognitionSequence
    {
        /// <summary>
        /// Interleaves every target with that phase's lures and shuffles the result.
        ///
        /// SEEDED. The seed is taken from the caller's deterministic random source so a session
        /// can be reproduced exactly — the presentation order is data, not an accident, and an
        /// unreproducible order would make a serial-position effect impossible to check later.
        ///
        /// Item ids are assigned BEFORE the shuffle, from each word's position in its own source
        /// list, so an id always identifies the same word however the order comes out.
        /// </summary>
        public static List<RecognitionItem> Build(RecognitionWordList list, RecognitionPhase phase,
            int seed)
        {
            var items = new List<RecognitionItem>();

            if (list == null)
                return items;

            var targets = list.targetWords;

            for (var i = 0; i < targets.Count; i++)
            {
                items.Add(new RecognitionItem
                {
                    word = targets[i],
                    itemClass = RecognitionItemClass.Target,
                    phase = phase,
                    itemId = $"TARGET_{i + 1:00}",
                });
            }

            var lures = list.Lures(phase);
            var lurePrefix = phase == RecognitionPhase.Immediate ? "IMM_LURE" : "DEL_LURE";

            for (var i = 0; i < lures.Count; i++)
            {
                items.Add(new RecognitionItem
                {
                    word = lures[i],
                    itemClass = RecognitionItemClass.Lure,
                    phase = phase,
                    itemId = $"{lurePrefix}_{i + 1:00}",
                });
            }

            // Fisher-Yates against a seeded generator: uniform, and reproducible from the seed.
            var random = new System.Random(seed);

            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }

            for (var i = 0; i < items.Count; i++)
                items[i].presentationOrder = i;

            return items;
        }

        /// <summary>
        /// The typed tallies of one phase. Transparent counts only — no derived index, no
        /// composite.
        ///
        /// This is the single entry point for aggregating a phase; both the event notes and the
        /// session-level results go through it, so the two can never disagree.
        /// </summary>
        public static RecognitionPhaseResults Tally(IReadOnlyList<RecognitionItem> items)
        {
            return new RecognitionPhaseResults().Recount(items);
        }

        /// <summary>
        /// Counts of each outcome, formatted for the event notes column.
        ///
        /// Kept as the name and the exact output it always had — it is now a thin wrapper over
        /// <see cref="Tally"/> rather than a second counting loop.
        /// </summary>
        public static string Summarise(IReadOnlyList<RecognitionItem> items)
        {
            return Tally(items).ToEventNotes();
        }
    }
}
