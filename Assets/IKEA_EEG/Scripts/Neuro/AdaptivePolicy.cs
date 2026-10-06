namespace IkeaEeg.Neuro
{
    /// <summary>
    /// What the policy would request for the NEXT chair trial. Immutable.
    ///
    /// <see cref="hasProposal"/> false means "no EEG-driven request": whatever difficulty the
    /// experiment's own schedule gives the next trial stands. That is the only value this build
    /// can produce.
    /// </summary>
    public readonly struct DifficultyProposal
    {
        public readonly bool hasProposal;
        public readonly NeuroDifficulty proposed;
        public readonly string reason;

        public DifficultyProposal(bool hasProposal, NeuroDifficulty proposed, string reason)
        {
            this.hasProposal = hasProposal;
            this.proposed = hasProposal ? proposed : NeuroDifficulty.Unknown;
            this.reason = reason ?? string.Empty;
        }

        public static DifficultyProposal None(string reason) =>
            new DifficultyProposal(false, NeuroDifficulty.Unknown, reason);
    }

    /// <summary>
    /// Workload state + current difficulty in, proposed next difficulty out.
    ///
    /// A proposal is only ever for trial N+1, computed after trial N has ended. Nothing in this
    /// layer can reach a trial in progress.
    /// </summary>
    public interface IAdaptivePolicy
    {
        DifficultyProposal Propose(WorkloadEstimate workload, NeuroDifficulty currentDifficulty);
    }

    /// <summary>
    /// NO STARTING DIFFICULTY IS DEFINED HERE. While adaptation is inactive every trial's
    /// difficulty — trial 1 included — is whatever the experiment's own frozen schedule runs, and
    /// this layer only records it. A starting difficulty for an adaptive design belongs here once
    /// such a design is approved; until then a second, parallel difficulty history would only
    /// make the record ambiguous.
    /// </summary>
    public static class AdaptivePolicy
    {
        /// <summary>
        /// The participant experience is never altered by this layer. A compile-time constant,
        /// like the scientific gates in ShadowModeController, so it cannot be switched on from
        /// the inspector. Turning it on would also need code that does not exist: nothing in the
        /// experiment reads a proposal.
        /// </summary>
        public const bool ActiveAdaptationEnabled = false;

        public const string ModeLabel = "SHADOW_ONLY";
    }

    /// <summary>
    /// THE ONLY POLICY, AND IT NEVER REQUESTS A CHANGE.
    ///
    /// While workload is INDETERMINATE there is nothing to act on. For a determinate level there
    /// is still no approved mapping from workload to difficulty — not even its direction — so it
    /// declines as well rather than inventing one.
    /// </summary>
    public sealed class NoEegChangePolicy : IAdaptivePolicy
    {
        public const string Version = "policy-no-eeg-change-1.0.0";

        public DifficultyProposal Propose(WorkloadEstimate workload,
            NeuroDifficulty currentDifficulty)
        {
            if (workload.level == WorkloadLevel.Indeterminate)
            {
                return DifficultyProposal.None(
                    "WORKLOAD_INDETERMINATE: no EEG-driven proposal; the configured schedule stands");
            }

            return DifficultyProposal.None(
                "NO_APPROVED_POLICY_RULE: no approved workload-to-difficulty mapping exists");
        }
    }
}
