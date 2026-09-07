using System;
using System.Collections.Generic;
using UnityEngine;

namespace IkeaEeg.Experiment
{
    /// <summary>
    /// Difficulty of one chair trial.
    ///
    /// IMPORTANT — WHAT DIFFICULTY DOES AND DOES NOT CHANGE:
    /// The TASK is identical at every level. The instruction always names all three dimensions
    /// (COLOR + SIZE + SHAPE) and exactly one chair always satisfies all three. What changes is
    /// only how similar the five distractors are to the target, i.e. how much conjunctive
    /// search the participant has to do.
    ///
    /// Keeping the task constant is what makes the levels comparable: a difference in response
    /// time between LOW and HIGH can be attributed to distractor similarity rather than to the
    /// participant having been asked to do something different.
    /// </summary>
    public enum DifficultyLevel
    {
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// How many of the target's three attributes each distractor is allowed to share, for one
    /// difficulty level.
    ///
    /// One entry per distractor (five entries for a six-chair room). A value of 3 is illegal —
    /// that would be a second correct chair — and is rejected by <see cref="Validate"/>.
    /// </summary>
    [Serializable]
    public class DifficultyProfile
    {
        [Tooltip("The level this profile defines.")]
        public DifficultyLevel level = DifficultyLevel.Low;

        [Tooltip("One entry per DISTRACTOR: how many of the target's three attributes that " +
                 "distractor shares. 0 = nothing in common, 2 = near-match. 3 is illegal.")]
        public int[] distractorSharedAttributes = { 0, 0, 0, 0, 1 };

        [Tooltip("Free text for the protocol write-up. Not used by any logic.")]
        [TextArea(1, 3)]
        public string description = string.Empty;

        public int distractorCount => distractorSharedAttributes?.Length ?? 0;

        /// <summary>
        /// PROTOTYPE DEFAULTS — not a validated clinical protocol.
        ///
        ///   LOW    [0,0,0,0,1] four distractors share nothing with the target, one shares a
        ///                      single attribute. The target is close to a pop-out.
        ///   MEDIUM [0,0,1,1,1] three distractors share exactly one target attribute, so at
        ///                      least one dimension has to be checked against another.
        ///   HIGH   [1,2,2,2,2] four near-matches share TWO of the three target attributes.
        ///                      All three dimensions must be conjoined to find the one chair
        ///                      that matches on all three.
        /// </summary>
        public static List<DifficultyProfile> CreateDefaults()
        {
            return new List<DifficultyProfile>
            {
                new DifficultyProfile
                {
                    level = DifficultyLevel.Low,
                    distractorSharedAttributes = new[] { 0, 0, 0, 0, 1 },
                    description = "PROTOTYPE DEFAULT. Distractors share at most one target " +
                                  "attribute; four share none.",
                },
                new DifficultyProfile
                {
                    level = DifficultyLevel.Medium,
                    distractorSharedAttributes = new[] { 0, 0, 1, 1, 1 },
                    description = "PROTOTYPE DEFAULT. Three distractors share exactly one " +
                                  "target attribute; none is a near-match.",
                },
                new DifficultyProfile
                {
                    level = DifficultyLevel.High,
                    distractorSharedAttributes = new[] { 1, 2, 2, 2, 2 },
                    description = "PROTOTYPE DEFAULT. Four near-matches share two of the three " +
                                  "target attributes; exactly one chair matches all three.",
                },
            };
        }

        /// <summary>
        /// Checks the profile can produce a solvable, unambiguous trial for a room of
        /// <paramref name="chairCount"/> chairs.
        /// </summary>
        public bool Validate(int chairCount, out string problem)
        {
            if (distractorSharedAttributes == null || distractorSharedAttributes.Length == 0)
            {
                problem = $"{level}: no distractor rules defined.";
                return false;
            }

            if (distractorSharedAttributes.Length != chairCount - 1)
            {
                problem = $"{level}: {distractorSharedAttributes.Length} distractor rule(s) for " +
                          $"a room of {chairCount} chairs; expected {chairCount - 1}.";
                return false;
            }

            for (var i = 0; i < distractorSharedAttributes.Length; i++)
            {
                var shared = distractorSharedAttributes[i];

                if (shared < 0 || shared > 2)
                {
                    problem = $"{level}: distractor {i + 1} is set to share {shared} attributes. " +
                              "Only 0, 1 or 2 are legal — 3 would be a second correct chair.";
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }
    }
}
