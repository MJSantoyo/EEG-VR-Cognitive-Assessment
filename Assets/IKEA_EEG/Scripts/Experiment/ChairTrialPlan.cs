using System.Collections.Generic;
using System.Text;
using IkeaEeg.Interaction;

namespace IkeaEeg.Experiment
{
    /// <summary>What one chair occupies during one trial: which identity, where, looking how.</summary>
    public struct ChairSlotAssignment
    {
        /// <summary>Index into the room's fixed slot layout (0..5).</summary>
        public int slotIndex;

        /// <summary>Stable chair identity, e.g. "Chair_04". Written to the CSV object_id column.</summary>
        public string chairId;

        /// <summary>The attributes this chair shows during this trial.</summary>
        public ChairSpec spec;

        /// <summary>How many of the target's three attributes this chair shares (0..3).</summary>
        public int sharedWithTarget;
    }

    /// <summary>
    /// The complete, reproducible specification of ONE chair-selection trial.
    ///
    /// A plan is pure data produced by <see cref="ChairTrialGenerator"/> from (seed, trial
    /// index, difficulty). Nothing in it depends on the scene, so the whole stimulus sequence
    /// of a session can be regenerated — and checked — offline, without Unity.
    /// </summary>
    public class ChairTrialPlan
    {
        /// <summary>1-based index within the Area B block.</summary>
        public int trialIndex;

        /// <summary>How many chair trials the run contains. Constant across a run.</summary>
        public int trialCount;

        public DifficultyLevel difficulty;

        /// <summary>The (colour, size, shape) the participant is asked to find.</summary>
        public ChairSpec target;

        /// <summary>Identity of the one chair that satisfies all three target attributes.</summary>
        public string targetChairId = string.Empty;

        /// <summary>Slot the target occupies this trial.</summary>
        public int targetSlotIndex;

        /// <summary>One entry per slot, ordered by slot index.</summary>
        public ChairSlotAssignment[] assignments = System.Array.Empty<ChairSlotAssignment>();

        /// <summary>Seed of the per-trial random stream, for the record.</summary>
        public long trialSeed;

        /// <summary>
        /// The invariant the whole task rests on: exactly one chair matches all three target
        /// attributes. Checked by the generator on every plan it produces, and again by the
        /// self test across thousands of seeds.
        /// </summary>
        public bool ValidateUnambiguous(out string problem)
        {
            if (assignments == null || assignments.Length == 0)
            {
                problem = "plan has no chair assignments";
                return false;
            }

            var exactMatches = 0;
            var ids = new HashSet<string>();
            var specs = new HashSet<ChairSpec>();
            var slots = new HashSet<int>();

            foreach (var a in assignments)
            {
                if (a.spec.Matches(target))
                    exactMatches++;

                if (!ids.Add(a.chairId))
                {
                    problem = $"chair id '{a.chairId}' is used twice";
                    return false;
                }

                if (!specs.Add(a.spec))
                {
                    problem = $"two chairs share the attribute combination {a.spec}";
                    return false;
                }

                if (!slots.Add(a.slotIndex))
                {
                    problem = $"slot {a.slotIndex} is occupied twice";
                    return false;
                }
            }

            if (exactMatches != 1)
            {
                problem = $"{exactMatches} chairs match the target {target} (expected exactly 1)";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>LOW / MEDIUM / HIGH, the exact string written to the CSV difficulty column.</summary>
        public string DifficultyLabelUpper() => difficulty.ToString().ToUpperInvariant();

        /// <summary>
        /// Everything the participant is actually presented with — target, difficulty, and which
        /// chair shows which attributes in which slot — and nothing else.
        ///
        /// Deliberately EXCLUDES <see cref="trialCount"/>: the number of trials in the run is a
        /// property of the run, not of this trial's stimulus. Comparing signatures is how the
        /// tests state "shortening the run did not change trial 1".
        /// </summary>
        public string StimulusSignature()
        {
            var sb = new StringBuilder();
            sb.Append(trialIndex).Append('|').Append(difficulty).Append('|').Append(target)
              .Append('|').Append(targetChairId).Append('@').Append(targetSlotIndex);

            foreach (var a in assignments)
                sb.Append("|s").Append(a.slotIndex).Append(':').Append(a.chairId)
                  .Append('=').Append(a.spec);

            return sb.ToString();
        }

        /// <summary>Compact description for the Console and the CSV notes column.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"trial {trialIndex}/{trialCount} [{difficulty.ToString().ToUpperInvariant()}] " +
                      $"target={target} on {targetChairId}@slot{targetSlotIndex}; layout=");

            for (var i = 0; i < assignments.Length; i++)
            {
                if (i > 0)
                    sb.Append(' ');

                var a = assignments[i];
                sb.Append($"slot{a.slotIndex}:{a.chairId}={a.spec}({a.sharedWithTarget}/3)");
            }

            return sb.ToString();
        }
    }
}
