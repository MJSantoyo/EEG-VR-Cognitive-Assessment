namespace IkeaEeg.Neuro
{
    /// <summary>
    /// A PROVISIONAL RULE-BASED LABEL. Not a measurement of cognitive workload.
    ///
    /// These four values are the output of an arbitrary, versioned threshold rule applied to
    /// two band-power ratios. They are NOT validated indices of workload, attention, effort,
    /// memory load or any cognitive or clinical state, and nothing in this project establishes
    /// that they correspond to one. They exist so that a decision rule can be logged and
    /// audited while the experiment runs — in shadow, changing nothing.
    ///
    /// Any report, figure or claim derived from these labels must carry that qualification.
    /// </summary>
    public enum WorkloadLevel
    {
        /// <summary>
        /// NO DECISION WAS MADE. The default, and the only honest answer whenever the inputs
        /// are invalid, the baseline is insufficient, or no approved threshold rule exists.
        ///
        /// Deliberately the zero value: a default-constructed decision is undecided, never
        /// accidentally "Low".
        /// </summary>
        Indeterminate = 0,

        /// <summary>Provisional label. See the type summary.</summary>
        Low = 1,

        /// <summary>Provisional label. See the type summary.</summary>
        Moderate = 2,

        /// <summary>Provisional label. See the type summary.</summary>
        High = 3,
    }
}
