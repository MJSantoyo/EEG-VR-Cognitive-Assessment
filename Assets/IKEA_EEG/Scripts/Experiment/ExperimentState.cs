namespace IkeaEeg.Experiment
{
    /// <summary>
    /// The explicit states of the trial.
    ///
    /// The ExperimentManager is the only thing allowed to change this value. Every other
    /// system is told what to do; nothing polls the state to decide its own behaviour and no
    /// subsystem schedules its own timers. That is what keeps the timing auditable.
    /// </summary>
    public enum ExperimentState
    {
        /// <summary>Before Start is pressed. Participant is at Spawn_A.</summary>
        Idle,

        /// <summary>Area A: task instructions are being read.</summary>
        AreaAInstructions,

        /// <summary>Area A: the five words are being presented one at a time.</summary>
        WordEncoding,

        /// <summary>Area A: beep has sounded, participant is repeating the words.</summary>
        ImmediateRecall,

        /// <summary>Area A: waiting for the participant to activate the control to enter Area B.</summary>
        ReadyForAreaB,

        /// <summary>
        /// Area B: the GENERAL executive-task instructions are on screen and the participant
        /// has not yet pressed READY. No trial exists, no target is shown and no response timer
        /// is running — time spent here is instruction-reading time, never response time.
        /// Entered once per Area B block.
        /// </summary>
        AreaBInstructions,

        /// <summary>
        /// Area B: a trial's chairs are arranged and the pre-target interval is elapsing. The
        /// target is NOT yet visible and selection is NOT open.
        /// </summary>
        ChairInstruction,

        /// <summary>Area B: selection is open and the response timer is running.</summary>
        ChairSelection,

        /// <summary>
        /// Area B: a chair was selected and the brief feedback is showing. The response timer
        /// is already stopped, so nothing measured is running during this state.
        /// </summary>
        ChairTrialFeedback,

        /// <summary>
        /// Area B: the blank interval between one chair trial and the next. The participant
        /// stays in Area B for the whole block; this is not a transition between areas.
        /// </summary>
        ChairInterTrialInterval,

        /// <summary>Area B: every chair trial is done; waiting for the participant to exit.</summary>
        ReadyForAreaC,

        /// <summary>Area C: beep has sounded, participant is recalling the words again.</summary>
        DelayedRecall,

        /// <summary>Area C: results shown, Restart / End available.</summary>
        Results,

        /// <summary>Session terminated by the participant/researcher.</summary>
        Ended,

        /// <summary>
        /// The language screen. The FIRST thing a participant sees — before Area 0 and before
        /// any cognitive content — because every instruction after it depends on the answer.
        /// Appended so the enum's existing numbering is untouched.
        /// </summary>
        LanguageSelection,

        /// <summary>
        /// Area 0 — VR familiarization. Comes BEFORE Idle/Area A in the flow, but is appended
        /// here so the enum's existing numbering is untouched.
        ///
        /// Nothing in this state is a cognitive trial: no word is presented, no chair exists,
        /// no response time is measured and no metric is produced. The participant practises
        /// pointing and selecting, and the researcher confirms the headset works.
        /// </summary>
        Familiarization,

        /// <summary>
        /// The run was ABORTED. A TERMINAL state: every participant-facing process has been
        /// stopped and none may resume from here. Distinct from Ended, which is a run that
        /// finished normally. Appended so the enum's existing numbering is untouched.
        ///
        /// Because every participant handler is gated on the state it belongs to, reaching this
        /// state is by itself enough to make all of them inert — there is no path back into the
        /// protocol that does not go through the developer menu.
        /// </summary>
        Aborted,

        /// <summary>
        /// Area A: recognition items are being presented one at a time, immediately after
        /// encoding. The participant answers SEEN BEFORE / NOT SEEN BEFORE.
        ///
        /// APPENDED so every existing state keeps its number — the enum is serialized in the
        /// scene and written to the CSV, so inserting this next to WordEncoding where it
        /// logically belongs would silently renumber ImmediateRecall and everything after it.
        /// Logical position and numeric position are different things here.
        ///
        /// Reached only when the protocol mode is Recognition; the FreeRecall path still goes
        /// WordEncoding -> ImmediateRecall and is untouched.
        /// </summary>
        ImmediateRecognition,

        /// <summary>
        /// Area C: the delayed recognition phase, after the intervening Area B activity.
        /// Same interaction and the same classification as ImmediateRecognition.
        ///
        /// Its item set is configured, never assumed: the delayed item-level structure is not
        /// yet established, so nothing here fixes a count.
        /// </summary>
        DelayedRecognition,
    }
}
