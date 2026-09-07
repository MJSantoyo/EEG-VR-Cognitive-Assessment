using System.Collections.Generic;

namespace IkeaEeg.Core
{
    /// <summary>
    /// The experiment's ONLY source of randomness.
    ///
    /// WHY NOT UnityEngine.Random: it is a global, shared, engine-owned generator. Anything
    /// else in the project (a particle system, an editor tool, a package) can advance it, its
    /// algorithm is not contractually stable across Unity versions, and its state cannot be
    /// carried in the data file. A session that cannot be reproduced from its seed is not a
    /// reproducible experiment.
    ///
    /// This is SplitMix64 — small, well-tested, and fully specified by these few lines, so the
    /// same seed produces the same session on any platform and any Unity version, today and in
    /// five years. The offline analysis can reimplement it in ten lines of Python if it ever
    /// needs to regenerate a session's stimuli.
    /// </summary>
    public sealed class DeterministicRandom
    {
        const ulong k_Gamma = 0x9E3779B97F4A7C15UL;

        ulong m_State;

        public DeterministicRandom(long seed)
        {
            unchecked
            {
                // Seed 0 is a legal input but a poor state; mix it so it behaves like any other.
                m_State = (ulong)seed ^ k_Gamma;
            }
        }

        /// <summary>
        /// Creates an independent generator for a sub-part of the session (e.g. one trial).
        ///
        /// Deriving per-trial streams from (session seed, stream id) instead of drawing them
        /// sequentially from one generator means trial 3 is identical whether the run has 3
        /// trials or 30 — changing <c>chairTrialsPerRun</c> does not reshuffle the earlier
        /// trials, so two runs from the same seed stay comparable.
        /// </summary>
        public static DeterministicRandom Derive(long sessionSeed, int streamId)
        {
            unchecked
            {
                var mixed = Mix((ulong)sessionSeed + k_Gamma * (ulong)(streamId + 1));
                return new DeterministicRandom((long)mixed);
            }
        }

        static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public ulong NextUInt64()
        {
            unchecked
            {
                m_State += k_Gamma;
                return Mix(m_State);
            }
        }

        /// <summary>Uniform integer in [0, maxExclusive). Rejection-sampled, so unbiased.</summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 1)
                return 0;

            unchecked
            {
                var bound = (ulong)maxExclusive;
                var limit = ulong.MaxValue - (ulong.MaxValue % bound) - 1;

                ulong value;
                do
                {
                    value = NextUInt64();
                }
                while (value > limit);

                return (int)(value % bound);
            }
        }

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            return minInclusive + NextInt(maxExclusive - minInclusive);
        }

        public double NextDouble()
        {
            // 53 significant bits, the most a double can represent exactly.
            return (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
        }

        /// <summary>In-place Fisher-Yates shuffle. The only shuffle used by the protocol.</summary>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null)
                return;

            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = NextInt(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>Picks one element uniformly. Returns default when the list is empty.</summary>
        public T Pick<T>(IReadOnlyList<T> list)
        {
            if (list == null || list.Count == 0)
                return default;

            return list[NextInt(list.Count)];
        }
    }
}
