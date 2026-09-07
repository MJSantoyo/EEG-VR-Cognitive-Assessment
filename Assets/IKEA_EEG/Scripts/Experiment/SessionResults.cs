using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using IkeaEeg.Core;
using IkeaEeg.Interaction;

namespace IkeaEeg.Experiment
{
    /// <summary>
    /// The behavioural record of ONE chair-selection trial.
    ///
    /// This is the per-trial "TrialResult" of the chair task: one instance per trial, held in
    /// order in <see cref="SessionResults"/>. The event CSV remains the authoritative raw
    /// record — this is the derived, analysis-shaped view of it, and the two are written from
    /// the same values at the same moment so they cannot disagree.
    /// </summary>
    [Serializable]
    public class ChairTrialResult
    {
        /// <summary>1-based index within the Area B block.</summary>
        public int trialIndex;

        /// <summary>How many chair trials the run contained.</summary>
        public int trialCount;

        public DifficultyLevel difficulty;

        // ---- What was asked for --------------------------------------------------------
        public ChairSpec target;
        public string targetChairId = string.Empty;
        public int targetSlotIndex;

        // ---- What the participant did ---------------------------------------------------
        public bool selectionMade;
        public string selectedChairId = string.Empty;
        public ChairSpec selected;

        /// <summary>How many of the three attributes the chosen chair shared with the target.</summary>
        public int attributeMatchCount;

        public bool correct;

        /// <summary>Response time in seconds, measured from CHAIR_SELECTION_TIMER_START.</summary>
        public double responseTimeSeconds;

        /// <summary>The same interval in milliseconds. Both are stored so neither analysis has to convert.</summary>
        public double responseTimeMs;

        /// <summary>
        /// False when the trial did not produce a usable response (aborted, restarted, ended
        /// mid-trial). Invalid trials are EXCLUDED from every session-level average.
        /// </summary>
        public bool valid;

        /// <summary>Why the trial is invalid, when it is.</summary>
        public string invalidReason = string.Empty;

        /// <summary>Seed of the per-trial random stream that produced this layout.</summary>
        public long trialSeed;

        public string DifficultyLabel() => difficulty.ToString().ToUpperInvariant();

        /// <summary>One line of the researcher summary.</summary>
        public string ToSummaryLine()
        {
            if (!selectionMade)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "  trial {0}/{1}  {2,-6}  NO RESPONSE  ({3})",
                    trialIndex, trialCount, DifficultyLabel(),
                    string.IsNullOrEmpty(invalidReason) ? "not completed" : invalidReason);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "  trial {0}/{1}  {2,-6}  {3,-9}  RT {4}  target={5} chose={6}{7}",
                trialIndex, trialCount, DifficultyLabel(),
                correct ? "CORRECT" : "INCORRECT",
                TimeFormat.FromMilliseconds(responseTimeMs),
                target, selected,
                valid ? string.Empty : $"  [EXCLUDED: {invalidReason}]");
        }
    }

    /// <summary>
    /// Session-level behavioural results.
    ///
    /// The averages here deliberately use ONLY trials marked valid. A trial that was aborted,
    /// restarted through, or ended mid-response has no meaningful response time, and letting
    /// one into a mean would quietly corrupt the number a researcher reads off the screen.
    /// </summary>
    public class SessionResults
    {
        public string sessionId = string.Empty;
        public long randomizationSeed;
        public string wordSetId = string.Empty;
        public string protocolDescription = string.Empty;

        /// <summary>
        /// Which verbal protocol produced this run, as a TYPED value.
        ///
        /// <see cref="protocolDescription"/> is prose for a human reader and is unchanged; this
        /// is what the results screen branches on. Written by
        /// <c>ExperimentManager.CaptureRecognitionResults</c> before the panel is built, for the
        /// same reason the run duration is captured there: a field the panel reads must already
        /// hold this run's value when the panel reads it.
        /// </summary>
        public VerbalProtocolMode protocolMode = VerbalProtocolMode.FreeRecall;

        /// <summary>
        /// Typed outcome tallies for the IMMEDIATE recognition phase.
        ///
        /// Empty (<c>hasData == false</c>) for a FreeRecall run and for any run that did not
        /// reach the phase. Populated from the manager's own item list — never parsed back out
        /// of the event notes.
        /// </summary>
        public readonly RecognitionPhaseResults immediateRecognition = new RecognitionPhaseResults();

        /// <summary>
        /// Typed outcome tallies for the DELAYED recognition phase.
        ///
        /// Kept as a SEPARATE object from the immediate phase and never merged with it: the two
        /// are distinct measurements over overlapping words, and one combined memory number
        /// would destroy the only comparison the protocol is built to support.
        /// </summary>
        public readonly RecognitionPhaseResults delayedRecognition = new RecognitionPhaseResults();

        /// <summary>True when this run has recognition data to report.</summary>
        public bool hasRecognitionResults =>
            immediateRecognition.hasData || delayedRecognition.hasData;

        /// <summary>True when this session replayed an earlier session's seed on purpose.</summary>
        public bool isSeedReplay;

        public readonly List<ChairTrialResult> chairTrials = new List<ChairTrialResult>();

        /// <summary>
        /// Length of THIS RUN's cognitive flow, in seconds: from TRIAL_START (START pressed in
        /// Area A) to the moment the Area C flow completes and the results are reached.
        ///
        /// It is a RUN value, not a sitting value — run 2 reports run 2's own length — and it
        /// deliberately excludes the language screen and Area 0 familiarization, whose length
        /// depends on how long the participant chose to practise rather than on the protocol.
        ///
        /// Written once by ExperimentManager.CaptureRunDuration from the EventLogger's run
        /// timer. Every consumer — the participant panel, the session summary, the researcher
        /// summary and the SESSION_SUMMARY event — reads this same field, so none of them can
        /// report a different number.
        /// </summary>
        public double totalExperimentDurationSeconds;

        public string csvPath = string.Empty;
        public string audioFolder = string.Empty;
        public string sessionDirectory = string.Empty;

        /// <summary>Status strings such as "recording saved" / "not scored".</summary>
        public string immediateRecallStatus = "not recorded";
        public string delayedRecallStatus = "not recorded";

        public void Reset()
        {
            sessionId = string.Empty;
            randomizationSeed = 0;
            wordSetId = string.Empty;
            protocolDescription = string.Empty;
            protocolMode = VerbalProtocolMode.FreeRecall;
            isSeedReplay = false;
            chairTrials.Clear();

            // Emptied with everything else, so one run's recognition tallies can never be shown
            // or written under the next run's identity.
            immediateRecognition.Reset();
            delayedRecognition.Reset();
            totalExperimentDurationSeconds = 0d;
            csvPath = string.Empty;
            audioFolder = string.Empty;
            sessionDirectory = string.Empty;
            immediateRecallStatus = "not recorded";
            delayedRecallStatus = "not recorded";
        }

        // ---------------------------------------------------------------------------------
        // Metrics
        // ---------------------------------------------------------------------------------

        /// <summary>Every chair trial that produced a usable response.</summary>
        public List<ChairTrialResult> ValidTrials()
        {
            var valid = new List<ChairTrialResult>(chairTrials.Count);
            foreach (var trial in chairTrials)
            {
                if (trial != null && trial.valid && trial.selectionMade)
                    valid.Add(trial);
            }

            return valid;
        }

        /// <summary>Number of chair trials attempted (valid and invalid).</summary>
        public int chairTrialsTotal => chairTrials.Count;

        /// <summary>Number of VALID chair trials — the denominator of the accuracy figure.</summary>
        public int chairTrialsScored => ValidTrials().Count;

        public int chairTrialsCorrect
        {
            get
            {
                var correct = 0;
                foreach (var trial in ValidTrials())
                {
                    if (trial.correct)
                        correct++;
                }

                return correct;
            }
        }

        /// <summary>Proportion correct over VALID trials, or NaN when there are none.</summary>
        public double chairAccuracy
        {
            get
            {
                var scored = chairTrialsScored;
                return scored == 0 ? double.NaN : (double)chairTrialsCorrect / scored;
            }
        }

        /// <summary>Mean response time over VALID trials, seconds. NaN when there are none.</summary>
        public double meanResponseTimeSeconds
        {
            get
            {
                var valid = ValidTrials();
                if (valid.Count == 0)
                    return double.NaN;

                var sum = 0d;
                foreach (var trial in valid)
                    sum += trial.responseTimeSeconds;

                return sum / valid.Count;
            }
        }

        /// <summary>
        /// Median response time over VALID trials, seconds. NaN when there are none.
        /// Reported alongside the mean because with three trials one slow response moves the
        /// mean a long way and the median says whether that happened.
        /// </summary>
        public double medianResponseTimeSeconds
        {
            get
            {
                var valid = ValidTrials();
                if (valid.Count == 0)
                    return double.NaN;

                var times = new List<double>(valid.Count);
                foreach (var trial in valid)
                    times.Add(trial.responseTimeSeconds);

                times.Sort();

                var middle = times.Count / 2;
                return times.Count % 2 == 1
                    ? times[middle]
                    : (times[middle - 1] + times[middle]) * 0.5d;
            }
        }

        public double meanResponseTimeMs => meanResponseTimeSeconds * 1000d;
        public double medianResponseTimeMs => medianResponseTimeSeconds * 1000d;

        // ---------------------------------------------------------------------------------
        // Reporting
        // ---------------------------------------------------------------------------------

        /// <summary>Compact form for the SESSION_SUMMARY event's notes column.</summary>
        public string ToEventNotes()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "chair_trials_total={0}; chair_trials_scored={1}; chair_trials_correct={2}; " +
                "chair_accuracy={3}; mean_rt_s={4}; median_rt_s={5}; total_duration_s={6:F3}; " +
                "immediate_recall={7}; delayed_recall={8}",
                chairTrialsTotal, chairTrialsScored, chairTrialsCorrect,
                FormatRatio(chairAccuracy), FormatSeconds(meanResponseTimeSeconds),
                FormatSeconds(medianResponseTimeSeconds), totalExperimentDurationSeconds,
                immediateRecallStatus, delayedRecallStatus);
        }

        /// <summary>
        /// The RESEARCHER SUMMARY. Deliberately plain text: it goes to the Unity Console and to
        /// a file next to the data, NOT onto the participant's panel in the headset.
        /// </summary>
        public string BuildResearcherSummary()
        {
            var sb = new StringBuilder();

            sb.AppendLine("==================== IKEA_EEG RESEARCHER SUMMARY ====================");
            sb.AppendLine($"Session ID:          {sessionId}");
            sb.AppendLine($"Randomization seed:  {randomizationSeed.ToString(CultureInfo.InvariantCulture)}" +
                          (isSeedReplay ? "   (REPLAY of a previous session's seed)" : string.Empty));
            sb.AppendLine($"Word set:            {wordSetId}");
            sb.AppendLine($"Protocol:            {protocolDescription}");
            sb.AppendLine();

            sb.AppendLine($"Chair accuracy:      {FormatAccuracy()}");
            sb.AppendLine($"Mean response time:  {FormatDuration(meanResponseTimeSeconds)}");
            sb.AppendLine($"Median response time:{FormatDuration(medianResponseTimeSeconds)}");
            sb.AppendLine();

            sb.AppendLine("Chair trials:");
            if (chairTrials.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var trial in chairTrials)
                    sb.AppendLine(trial.ToSummaryLine());
            }

            sb.AppendLine();
            sb.AppendLine($"Total duration:      {TimeFormat.FromSeconds(totalExperimentDurationSeconds)}");
            sb.AppendLine();

            // Recognition totals, when this run produced any. Raw counts, kept per phase, with
            // no accuracy figure and no composite — the researcher's own analysis decides what
            // to make of them, and the per-item rows in the event CSV remain authoritative.
            if (hasRecognitionResults)
            {
                sb.AppendLine("Recognition (raw counts, no derived score):");
                sb.AppendLine(DescribeRecognitionPhase("  immediate", immediateRecognition));
                sb.AppendLine(DescribeRecognitionPhase("  delayed  ", delayedRecognition));
                sb.AppendLine();
            }

            // Kept unconditionally: these describe the microphone recall recordings and are a
            // FreeRecall fact. In Recognition mode they correctly read "no recording", which is
            // useful to a researcher reading a file and is NOT shown to the participant.
            sb.AppendLine($"Immediate recall:    {immediateRecallStatus}");
            sb.AppendLine($"Delayed recall:      {delayedRecallStatus}");
            sb.AppendLine();
            sb.AppendLine($"CSV path:            {csvPath}");
            sb.AppendLine($"Audio folder:        {audioFolder}");
            sb.AppendLine($"Session folder:      {sessionDirectory}");
            sb.AppendLine("====================================================================");

            return sb.ToString();
        }

        /// <summary>
        /// The PARTICIPANT-facing end-of-block result: a heading and "N / M correct".
        ///
        /// Deliberately contains nothing else. No reaction times, no seed, no target details,
        /// no per-trial breakdown, no file paths — all of that is researcher information and
        /// lives in the researcher summary.
        ///
        /// M is the number of VALID SCORED trials, not the number attempted. A trial that was
        /// aborted or never answered is neither correct nor incorrect, so counting it in the
        /// denominator would show the participant a worse score than they earned. When trials
        /// were excluded, that is stated plainly rather than hidden.
        /// </summary>
        public string BuildParticipantBlockResult(string heading, string footer)
        {
            var sb = new StringBuilder();
            sb.Append("<b>").Append(string.IsNullOrWhiteSpace(heading)
                ? "Executive task complete"
                : heading).Append("</b>");

            var scored = chairTrialsScored;
            var attempted = chairTrialsTotal;

            if (scored == 0)
            {
                // No usable responses. Say so neutrally; never render this as "0 / 0 correct".
                sb.Append("\n\nNo responses were recorded.");
            }
            else
            {
                sb.Append("\n\n<b>").Append(chairTrialsCorrect).Append(" / ").Append(scored)
                  .Append(" correct</b>");

                if (attempted > scored)
                {
                    var excluded = attempted - scored;
                    sb.Append("\n\n(").Append(excluded)
                      .Append(excluded == 1 ? " task was not completed" : " tasks were not completed")
                      .Append(" and ").Append(excluded == 1 ? "is" : "are").Append(" not counted.)");
                }
            }

            if (!string.IsNullOrWhiteSpace(footer))
                sb.Append("\n\n").Append(footer);

            return sb.ToString();
        }

        /// <summary>
        /// The end-of-run summary shown on the Area C results panel.
        ///
        /// LEADS WITH THE RESULT THAT MEANS SOMETHING: how many chairs were selected
        /// correctly. It previously led with "TRIAL COMPLETE", a trial id and a completed-trial
        /// count, which told the reader that something finished but nothing about how it went —
        /// and showed "T001" on every run, so run 2 looked identical to run 1.
        ///
        /// Trial counts and ids are still recorded in full; they live in the researcher summary
        /// and the CSV, which is where that detail belongs.
        /// </summary>
        public string BuildParticipantRunSummary(string heading, int runIndex, int runsInSession)
        {
            var sb = new StringBuilder();

            var scored = chairTrialsScored;
            var isRecognition = protocolMode == VerbalProtocolMode.Recognition;

            // WHITESPACE IS A BUDGET IN RECOGNITION MODE, not a style choice. The panel carries
            // two memory sections that FreeRecall does not, and the results rect is a fixed
            // 980 px that the buttons below it depend on. The blank lines are what get spent
            // first — the TYPE IS NOT SHRUNK below what the rest of this screen already uses,
            // because this has to stay readable for an older participant in a headset.
            // The self test measures the rendered height in all three languages.
            var gap = isRecognition ? "\n" : "\n\n";

            sb.Append("<b>").Append(string.IsNullOrWhiteSpace(heading)
                ? Localization.ExperimentLocalization.Get(Localization.LocKeys.ExecutiveTaskComplete)
                : heading).Append("</b>").Append(gap);

            // THE LARGE CHAIR HEADLINE IS FREERECALL-ONLY.
            //
            // In FreeRecall the chair block is the only task that produces a number on this
            // screen, so leading with it is right and it stays exactly as it was. In Recognition
            // the screen carries two memory sections as well, and giving the executive task a
            // 140% headline above them would present the intervening activity as the run's
            // headline result — which is the opposite of what the protocol measures. The chair
            // result is not removed; it keeps its own labelled row below, with the same numbers.
            if (!isRecognition)
            {
                if (scored == 0)
                {
                    sb.Append(Localization.ExperimentLocalization.Get(
                        Localization.LocKeys.NoResponsesRecorded)).Append('\n');
                }
                else
                {
                    // The prominent number: correct chair selections out of the scored trials.
                    sb.Append("<size=140%><b>")
                      .Append(Localization.ExperimentLocalization.Format(
                          Localization.LocKeys.NCorrect,
                          "N", chairTrialsCorrect.ToString(CultureInfo.InvariantCulture),
                          "TOTAL", scored.ToString(CultureInfo.InvariantCulture)))
                      .Append("</b></size>\n");

                    var attempted = chairTrialsTotal;
                    if (attempted > scored)
                    {
                        sb.Append("\n<size=70%>")
                          .Append(Localization.ExperimentLocalization.Format(
                              Localization.LocKeys.TasksNotCompleted,
                              "N", (attempted - scored).ToString(CultureInfo.InvariantCulture)))
                          .Append("</size>\n");
                    }
                }
            }

            // Run identity, so a second run is visibly a second run.
            if (runIndex > 0)
            {
                sb.Append(isRecognition ? "<size=80%>" : "\n<size=80%>")
                  .Append(Localization.ExperimentLocalization.Format(
                      Localization.LocKeys.ResultsRun,
                      "N", runIndex.ToString(CultureInfo.InvariantCulture)))
                  .Append("</size>\n");
            }

            // ---- Session summary -------------------------------------------------------------
            // Restored below the headline result, in its own clearly separated block. Every line
            // is a LABEL and a VALUE, short enough to read at a glance, and nothing here is a
            // session id, a path, a seed or a trial id — that detail belongs to the researcher
            // summary and the CSV, not to the participant's screen.
            sb.Append(isRecognition ? "<size=70%>" : "\n<size=70%>")
              .Append("────────────────</size>\n");
            sb.Append("<size=85%><b>")
              .Append(Localization.ExperimentLocalization.Get(
                  Localization.LocKeys.SessionSummaryHeading))
              .Append("</b></size>").Append(gap);

            // THE MEMORY RESULT COMES FIRST IN RECOGNITION MODE, because it is the measurement
            // the run exists for; the chair block is the intervening activity between its two
            // halves. In FreeRecall the order is unchanged.
            if (isRecognition)
            {
                AppendRecognitionSections(sb);

                // The same half-height spacer, so the executive block is separated from the
                // memory blocks by the same amount they are separated from each other.
                sb.Append("<size=20%>\n</size>");
            }

            sb.Append("<size=80%>");

            AppendStat(sb, Localization.LocKeys.StatExecutiveTask,
                scored == 0
                    ? Localization.ExperimentLocalization.Get(Localization.LocKeys.NotAvailable)
                    : Localization.ExperimentLocalization.Format(Localization.LocKeys.NCorrect,
                        "N", chairTrialsCorrect.ToString(CultureInfo.InvariantCulture),
                        "TOTAL", scored.ToString(CultureInfo.InvariantCulture)));

            AppendStat(sb, Localization.LocKeys.StatMeanResponseTime,
                FormatParticipantDuration(meanResponseTimeSeconds));

            AppendStat(sb, Localization.LocKeys.StatMedianResponseTime,
                FormatParticipantDuration(medianResponseTimeSeconds));

            AppendStat(sb, Localization.LocKeys.StatTotalDuration,
                FormatParticipantDuration(totalExperimentDurationSeconds));

            // THE PROTOCOL BRANCH THIS PASS EXISTS FOR.
            //
            // These two rows describe a MICROPHONE RECALL RECORDING. In Recognition mode no
            // recall phase ever runs, so both were permanently "Immediate verbal recall: Not
            // available" — a FreeRecall field, under a FreeRecall label, reporting the absence
            // of something the participant was never asked to do, directly beneath the delayed
            // recognition task they had just finished. They are not deleted; they are routed to
            // the protocol they belong to.
            if (!isRecognition)
            {
                AppendStat(sb, Localization.LocKeys.StatImmediateRecall,
                    RecallStatusLabel(immediateRecallStatus));

                AppendStat(sb, Localization.LocKeys.StatDelayedRecall,
                    RecallStatusLabel(delayedRecallStatus));
            }

            sb.Append("</size>");

            return sb.ToString();
        }

        /// <summary>
        /// The two recognition blocks, immediate then delayed, as two SEPARATE sections.
        ///
        /// They are never summed into one memory figure. Immediate and delayed recognition are
        /// the two halves of the comparison the protocol is built around, and a combined number
        /// would make that comparison impossible to read off the screen.
        /// </summary>
        void AppendRecognitionSections(StringBuilder sb)
        {
            AppendRecognitionSection(sb, Localization.LocKeys.RecognitionResultsImmediate,
                immediateRecognition);

            AppendRecognitionSection(sb, Localization.LocKeys.RecognitionResultsDelayed,
                delayedRecognition);
        }

        /// <summary>
        /// One recognition phase, as plain labelled counts.
        ///
        /// PRESENTATION RULES ENFORCED HERE, not by convention elsewhere:
        ///
        ///   * "Correct responses" is HITS + CORRECT REJECTIONS over the ANSWERED items, and the
        ///     denominator says so in words. Unanswered items are excluded from it because the
        ///     item loop explicitly does not score them as errors — writing "8 / 12" when two
        ///     items were never answered would silently count "did not answer" as "answered
        ///     wrongly", which is a different claim about the participant.
        ///   * "No response" and "Total items" are shown alongside it, so the excluded items are
        ///     visible rather than quietly dropped and the three numbers reconcile on screen.
        ///   * Nothing is called a score, a memory score, a cognitive score or a level, and
        ///     nothing is judged as normal, impaired, good or bad. These are counts of what
        ///     happened.
        /// </summary>
        void AppendRecognitionSection(StringBuilder sb, string headingKey,
            RecognitionPhaseResults results)
        {
            // A HALF-HEIGHT SPACER, not a blank line. The three blocks need to read as three
            // blocks, and a full empty line at this point costs ~35 px per section — enough to
            // push the Japanese rendering, which sets taller line boxes than English, past the
            // 980 px the rect allows. This buys the separation for about half the height.
            sb.Append("<size=20%>\n</size>");

            sb.Append("<size=76%><b>")
              .Append(Localization.ExperimentLocalization.Get(headingKey))
              .Append("</b></size>\n");

            sb.Append("<size=68%>");

            if (results == null || !results.hasData)
            {
                // The phase did not run — a developer jump, or an aborted run. Say so plainly
                // rather than printing a row of zeros that would read as a perfect failure.
                sb.Append(Localization.ExperimentLocalization.Get(
                      Localization.LocKeys.NotAvailable))
                  .Append("\n</size>");
                return;
            }

            if (results.answeredCount > 0)
            {
                sb.Append(Localization.ExperimentLocalization.Get(
                      Localization.LocKeys.RecognitionCorrectResponses))
                  .Append(":  <b>")
                  .Append(Localization.ExperimentLocalization.Format(
                      Localization.LocKeys.RecognitionAnsweredOf,
                      "N", results.totalCorrect.ToString(CultureInfo.InvariantCulture),
                      "TOTAL", results.answeredCount.ToString(CultureInfo.InvariantCulture)))
                  .Append("</b>\n");
            }
            else
            {
                sb.Append(Localization.ExperimentLocalization.Get(
                      Localization.LocKeys.RecognitionNoAnswersRecorded))
                  .Append('\n');
            }

            // Paired two to a line: the four outcome classes fit the panel without shrinking the
            // type, and each pair reads together (the two correct kinds, then the two incorrect).
            AppendPair(sb,
                Localization.LocKeys.RecognitionHits, results.hits,
                Localization.LocKeys.RecognitionCorrectRejections, results.correctRejections);

            AppendPair(sb,
                Localization.LocKeys.RecognitionMisses, results.misses,
                Localization.LocKeys.RecognitionFalseAlarms, results.falseAlarms);

            AppendPair(sb,
                Localization.LocKeys.RecognitionNoResponse, results.noResponse,
                Localization.LocKeys.RecognitionTotalItems, results.itemCount);

            sb.Append("</size>");
        }

        /// <summary>Two "label value" pairs on one line, separated by fixed spacing.</summary>
        static void AppendPair(StringBuilder sb, string leftKey, int leftValue,
            string rightKey, int rightValue)
        {
            sb.Append(Localization.ExperimentLocalization.Get(leftKey))
              .Append("  <b>").Append(leftValue.ToString(CultureInfo.InvariantCulture))
              .Append("</b>      ")
              .Append(Localization.ExperimentLocalization.Get(rightKey))
              .Append("  <b>").Append(rightValue.ToString(CultureInfo.InvariantCulture))
              .Append("</b>\n");
        }

        static void AppendStat(StringBuilder sb, string labelKey, string value)
        {
            sb.Append(Localization.ExperimentLocalization.Get(labelKey))
              .Append(":  <b>").Append(value).Append("</b>\n");
        }

        /// <summary>"X.XXX s (XXXX ms)", or the localized "Not available".</summary>
        static string FormatParticipantDuration(double seconds)
        {
            return double.IsNaN(seconds)
                ? Localization.ExperimentLocalization.Get(Localization.LocKeys.NotAvailable)
                : TimeFormat.FromSeconds(seconds);
        }

        /// <summary>
        /// Reduces the detailed internal recall status to what a participant should see:
        /// whether a recording exists. Peak levels, device names, silence flags and file paths
        /// stay in the researcher summary.
        /// </summary>
        static string RecallStatusLabel(string internalStatus)
        {
            var saved = !string.IsNullOrEmpty(internalStatus) &&
                        internalStatus.StartsWith("recording saved",
                            StringComparison.OrdinalIgnoreCase);

            return Localization.ExperimentLocalization.Get(saved
                ? Localization.LocKeys.RecordingSaved
                : Localization.LocKeys.NotAvailable);
        }

        /// <summary>One researcher-summary line for a phase. Counts only, never a proportion.</summary>
        static string DescribeRecognitionPhase(string label, RecognitionPhaseResults phase)
        {
            if (phase == null || !phase.hasData)
                return $"{label}: not run";

            return string.Format(CultureInfo.InvariantCulture,
                "{0}: hits {1}, misses {2}, correct rejections {3}, false alarms {4}, " +
                "no response {5} / {6} items ({7} targets, {8} lures)",
                label, phase.hits, phase.misses, phase.correctRejections, phase.falseAlarms,
                phase.noResponse, phase.itemCount, phase.targetCount, phase.lureCount);
        }

        public string FormatAccuracy()
        {
            var scored = chairTrialsScored;
            if (scored == 0)
                return "NA (no valid chair trials)";

            return string.Format(CultureInfo.InvariantCulture,
                "{0}/{1} = {2:P1}", chairTrialsCorrect, scored, chairAccuracy);
        }

        /// <summary>"X.XXX s (XXXX ms)", or NA when the metric has no valid trials behind it.</summary>
        public static string FormatDuration(double seconds)
        {
            return double.IsNaN(seconds) ? "NA" : " " + TimeFormat.FromSeconds(seconds);
        }

        public static string FormatSeconds(double seconds)
        {
            return double.IsNaN(seconds)
                ? "NA"
                : seconds.ToString("F3", CultureInfo.InvariantCulture);
        }

        public static string FormatRatio(double ratio)
        {
            return double.IsNaN(ratio)
                ? "NA"
                : ratio.ToString("F4", CultureInfo.InvariantCulture);
        }
    }
}
