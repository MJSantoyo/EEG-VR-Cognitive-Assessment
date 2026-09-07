using System;
using System.Collections.Generic;
using IkeaEeg.Core;
using IkeaEeg.Interaction;

namespace IkeaEeg.Experiment
{
    /// <summary>
    /// Builds one chair trial from (session seed, trial index, difficulty).
    ///
    /// GUARANTEES — these are the properties the behavioural data depends on, so they are
    /// enforced here and re-checked by <see cref="ChairTrialPlan.ValidateUnambiguous"/> before
    /// any plan is handed to the scene:
    ///
    ///   1. EXACTLY ONE chair satisfies all three target attributes. A trial that cannot be
    ///      built to this standard is refused, never silently approximated.
    ///   2. Every chair in the room shows a different attribute combination.
    ///   3. The distractor similarity profile of the requested difficulty is respected exactly.
    ///   4. The same (seed, trial index, difficulty, chair ids, profile) always produces the
    ///      identical plan — same target, same layout, same slot arrangement.
    ///   5. Where the attribute space allows it, the target avoids repeating the previous
    ///      trial's chair identity and attribute combination.
    ///
    /// Nothing here touches the scene or Unity's global state: it is pure data in, pure data
    /// out, which is what makes it testable and reproducible offline.
    /// </summary>
    public static class ChairTrialGenerator
    {
        /// <summary>How many times to redraw when trying to avoid an immediate repeat.</summary>
        const int k_AvoidRepeatAttempts = 24;

        static readonly ChairColor[] k_Colors = (ChairColor[])Enum.GetValues(typeof(ChairColor));
        static readonly ChairSize[] k_Sizes = (ChairSize[])Enum.GetValues(typeof(ChairSize));
        static readonly ChairShape[] k_Shapes = (ChairShape[])Enum.GetValues(typeof(ChairShape));

        /// <summary>Inputs that fully determine a trial. Everything is explicit on purpose.</summary>
        public struct Request
        {
            /// <summary>Session-level seed. The only thing that has to be recorded to reproduce a run.</summary>
            public long sessionSeed;

            /// <summary>1-based trial index within the Area B block.</summary>
            public int trialIndex;

            /// <summary>How many chair trials this run contains.</summary>
            public int trialCount;

            public DifficultyLevel difficulty;

            /// <summary>Distractor similarity rules for this difficulty.</summary>
            public DifficultyProfile profile;

            /// <summary>Stable chair identities available in the room, e.g. Chair_01..Chair_06.</summary>
            public IReadOnlyList<string> chairIds;

            /// <summary>How many fixed slots the room offers. Must equal the chair count.</summary>
            public int slotCount;

            /// <summary>Target of the previous trial, used to avoid an immediate repeat. May be null.</summary>
            public ChairTrialPlan previousTrial;
        }

        public static bool TryGenerate(Request request, out ChairTrialPlan plan, out string problem)
        {
            plan = null;

            if (request.chairIds == null || request.chairIds.Count < 2)
            {
                problem = "at least two chair identities are required";
                return false;
            }

            var chairCount = request.chairIds.Count;

            if (request.slotCount != chairCount)
            {
                problem = $"{request.slotCount} slot(s) for {chairCount} chair(s); they must match";
                return false;
            }

            if (request.profile == null)
            {
                problem = $"no difficulty profile supplied for {request.difficulty}";
                return false;
            }

            if (!request.profile.Validate(chairCount, out problem))
                return false;

            // Per-trial stream derived from the session seed. Trial 3 is the same trial whether
            // the run has 3 trials or 30 — see DeterministicRandom.Derive.
            var trialSeed = DeriveTrialSeed(request.sessionSeed, request.trialIndex);
            var rng = new DeterministicRandom(trialSeed);

            var previousTargetChairId = request.previousTrial?.targetChairId ?? string.Empty;
            var hasPreviousTarget = request.previousTrial != null;
            var previousTargetSpec = request.previousTrial?.target ?? default;

            // ---- 1. Target ---------------------------------------------------------------
            // Redraw a bounded number of times to avoid presenting the identical target twice
            // in a row. Bounded rather than looping forever: with a small attribute space a
            // repeat can be unavoidable, and a stalled generator would be far worse than one.
            var target = DrawSpec(rng);
            for (var attempt = 0; attempt < k_AvoidRepeatAttempts &&
                                  hasPreviousTarget && target.Matches(previousTargetSpec); attempt++)
            {
                target = DrawSpec(rng);
            }

            // ---- 2. Distractors ------------------------------------------------------------
            var specs = new List<ChairSpec>(chairCount) { target };
            var used = new HashSet<ChairSpec> { target };

            foreach (var sharedCount in request.profile.distractorSharedAttributes)
            {
                var candidates = EnumerateSpecsSharing(target, sharedCount, used);

                if (candidates.Count == 0)
                {
                    problem = $"{request.difficulty}: the attribute space has no unused chair " +
                              $"sharing exactly {sharedCount} attribute(s) with {target}. " +
                              "Reduce how many distractors demand that similarity.";
                    return false;
                }

                var chosen = candidates[rng.NextInt(candidates.Count)];
                specs.Add(chosen);
                used.Add(chosen);
            }

            // ---- 3. Which identity shows which attributes, and which slot it stands in -----
            //
            // Two independent permutations, both from the same seeded stream:
            //   * specToChair — the target's attribute combination lands on a different chair
            //     identity from trial to trial, so "the answer" is never tied to one object;
            //   * chairToSlot — the identities are redistributed over the six fixed positions,
            //     so the target's LOCATION also changes between trials.
            //
            // Positions themselves are never randomised: the slots are fixed, validated
            // geometry (see ChairSlotLayout). Only the assignment to them is.
            var chairOrder = new List<int>(chairCount);
            for (var i = 0; i < chairCount; i++)
                chairOrder.Add(i);

            var slotOrder = new List<int>(chairCount);
            for (var i = 0; i < chairCount; i++)
                slotOrder.Add(i);

            var assignments = new ChairSlotAssignment[chairCount];
            var targetChairId = string.Empty;
            var targetSlot = 0;

            for (var attempt = 0; attempt < k_AvoidRepeatAttempts; attempt++)
            {
                rng.Shuffle(chairOrder);
                rng.Shuffle(slotOrder);

                // specs[0] is the target, so the identity holding the target is the one
                // chairOrder maps position 0 to.
                targetChairId = request.chairIds[chairOrder[0]];

                var repeatsPreviousChair = hasPreviousTarget &&
                                           targetChairId == previousTargetChairId;

                if (!repeatsPreviousChair)
                    break;

                // Otherwise redraw. If every attempt collides (only possible with very few
                // chairs) the last draw is used — a repeat is acceptable, a hang is not.
            }

            for (var i = 0; i < chairCount; i++)
            {
                var chairId = request.chairIds[chairOrder[i]];
                var slotIndex = slotOrder[i];
                var spec = specs[i];

                assignments[slotIndex] = new ChairSlotAssignment
                {
                    slotIndex = slotIndex,
                    chairId = chairId,
                    spec = spec,
                    sharedWithTarget = spec.MatchCount(target),
                };

                if (i == 0)
                {
                    targetChairId = chairId;
                    targetSlot = slotIndex;
                }
            }

            plan = new ChairTrialPlan
            {
                trialIndex = request.trialIndex,
                trialCount = request.trialCount,
                difficulty = request.difficulty,
                target = target,
                targetChairId = targetChairId,
                targetSlotIndex = targetSlot,
                assignments = assignments,
                trialSeed = trialSeed,
            };

            // Belt and braces: the plan is re-validated independently of how it was built, so a
            // future change to the construction above cannot quietly produce an ambiguous trial.
            if (!plan.ValidateUnambiguous(out problem))
            {
                plan = null;
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// Generates the whole Area B block in one call. Used by the runtime, by the researcher
        /// preview and by the reproducibility tests — all three therefore exercise identical code.
        /// </summary>
        public static bool TryGenerateBlock(long sessionSeed, IReadOnlyList<DifficultyLevel> sequence,
            IReadOnlyList<DifficultyProfile> profiles, IReadOnlyList<string> chairIds, int slotCount,
            out List<ChairTrialPlan> plans, out string problem)
        {
            plans = new List<ChairTrialPlan>();

            if (sequence == null || sequence.Count == 0)
            {
                problem = "the difficulty sequence is empty";
                return false;
            }

            ChairTrialPlan previous = null;

            for (var i = 0; i < sequence.Count; i++)
            {
                var difficulty = sequence[i];
                var profile = FindProfile(profiles, difficulty);

                var request = new Request
                {
                    sessionSeed = sessionSeed,
                    trialIndex = i + 1,
                    trialCount = sequence.Count,
                    difficulty = difficulty,
                    profile = profile,
                    chairIds = chairIds,
                    slotCount = slotCount,
                    previousTrial = previous,
                };

                if (!TryGenerate(request, out var plan, out problem))
                {
                    plans = null;
                    return false;
                }

                plans.Add(plan);
                previous = plan;
            }

            problem = string.Empty;
            return true;
        }

        public static DifficultyProfile FindProfile(IReadOnlyList<DifficultyProfile> profiles,
            DifficultyLevel level)
        {
            if (profiles == null)
                return null;

            for (var i = 0; i < profiles.Count; i++)
            {
                if (profiles[i] != null && profiles[i].level == level)
                    return profiles[i];
            }

            return null;
        }

        /// <summary>The per-trial stream seed, exposed so it can be recorded and reproduced.</summary>
        public static long DeriveTrialSeed(long sessionSeed, int trialIndex)
        {
            // Same derivation the generator uses internally; going through DeterministicRandom
            // keeps the mixing function in exactly one place.
            var derived = DeterministicRandom.Derive(sessionSeed, trialIndex);
            return unchecked((long)derived.NextUInt64());
        }

        static ChairSpec DrawSpec(DeterministicRandom rng)
        {
            return new ChairSpec(
                k_Colors[rng.NextInt(k_Colors.Length)],
                k_Sizes[rng.NextInt(k_Sizes.Length)],
                k_Shapes[rng.NextInt(k_Shapes.Length)]);
        }

        /// <summary>
        /// Every attribute combination that shares exactly <paramref name="sharedCount"/>
        /// attributes with the target and has not been used yet.
        ///
        /// The space is 6x3x3 = 54 combinations, so enumerating it exhaustively is both cheap
        /// and exact — no rejection sampling that could fail to terminate on a tight profile.
        /// </summary>
        static List<ChairSpec> EnumerateSpecsSharing(ChairSpec target, int sharedCount,
            HashSet<ChairSpec> used)
        {
            var result = new List<ChairSpec>();

            foreach (var color in k_Colors)
            foreach (var size in k_Sizes)
            foreach (var shape in k_Shapes)
            {
                var candidate = new ChairSpec(color, size, shape);

                if (candidate.MatchCount(target) != sharedCount)
                    continue;

                if (used.Contains(candidate))
                    continue;

                result.Add(candidate);
            }

            return result;
        }
    }
}
