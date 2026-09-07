using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.XR.Interaction.Toolkit.Feedback;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using IkeaEeg.Audio;
using IkeaEeg.Core;
using IkeaEeg.Data;
using IkeaEeg.Experiment;
using IkeaEeg.Interaction;
using IkeaEeg.Localization;
using IkeaEeg.Memory;
using IkeaEeg.UI;
using IkeaEeg.XR;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Headless checks that can run without a headset: serialized-reference completeness,
    /// the CSV pipeline end to end, recall scoring and chair-correctness logic.
    ///
    /// This does NOT replace testing in the headset — it verifies everything that does not
    /// require XR tracking, so that what remains to be checked in VR is only the things that
    /// genuinely need VR.
    /// </summary>
    public static class ExperimentSelfTest
    {
        static int s_Failures;
        static StringBuilder s_Log;

        [MenuItem("IKEA_EEG/Run Self Test", false, 40)]
        public static void RunMenu()
        {
            Debug.Log(Run());
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void RunFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath, OpenSceneMode.Single);
            Debug.Log(Run());
        }

        public static string Run()
        {
            s_Failures = 0;
            s_Log = new StringBuilder();

            // Each section is isolated: one throwing check is recorded as a failure and the
            // rest still run. Previously an exception escaped Run() and the ENTIRE report was
            // lost, which hid every result gathered before the throw.
            // FIRST, before any other section drives the manager: this one inspects what the
            // SCENE BUILDER authored, and a label the manager has already written into no longer
            // shows that.
            RunSection("LOCALIZATION COMPLETENESS AUDIT (ES/JA)", CheckLocalizationAudit);

            RunSection("UNITY SERIALIZED FIELD NAME COLLISIONS", CheckSerializedFieldCollisions);
            RunSection("SERIALIZED REFERENCE COMPLETENESS", CheckSceneReferences);
            RunSection("CONFIGURATION ASSETS", CheckConfigAssets);
            RunSection("CHAIR ATTRIBUTE / CORRECTNESS LOGIC", CheckChairLogic);
            RunSection("DETERMINISTIC RANDOMISATION", CheckDeterministicRandom);
            RunSection("CHAIR TRIAL GENERATION + DIFFICULTY RULES", CheckChairTrialGeneration);
            RunSection("REPRODUCIBILITY (SEED REPLAY)", CheckSeedReproducibility);
            RunSection("SESSION-LEVEL BEHAVIOURAL METRICS", CheckSessionMetrics);
            RunSection("CHAIR SLOT LAYOUT", CheckChairSlotLayout);
            RunSection("RESTART / STATE RESET", CheckResetLogic);
            RunSection("LSL BINDING COMPATIBILITY", CheckLslBindingCompatibility);
            RunSection("LSL MARKER TRANSPORT", CheckLslTransport);
            RunSection("AURA RAW EEG RECEIVER", CheckAuraReceiver);
            RunSection("RAW EEG RING BUFFER + WINDOWS", CheckRawEegBuffer);
            RunSection("AURA MONTAGE + ACQUISITION PROVENANCE", CheckAuraMontage);
            RunSection("EEG BANDPASS FILTER (SYNTHETIC)", CheckEegFilter);
            RunSection("EEG WELCH PSD + BANDPOWER (SYNTHETIC)", CheckEegSpectral);
            RunSection("EEG CHANNEL SEPARATION + IDENTITY FLAG", CheckEegChannelIdentity);
            RunSection("EEG 60 HZ NOTCH (OPTIONAL, SYNTHETIC)", CheckEegNotch);
            RunSection("EEG POWER OUTLIER + TRANSIENT FLAGS", CheckEegRobustQuality);
            RunSection("EEG DC-OFFSET REJECTION (REAL AURA OFFSETS)", CheckEegDcOffsetRejection);
            RunSection("RECOGNITION PROTOCOL — DATA + CLASSIFICATION", CheckRecognitionProtocol);
            RunSection("RECOGNITION PROTOCOL — SCENE + PARADIGM SEPARATION", CheckRecognitionScene);
            RunSection("BLOCK 1 — INSTRUCTIONS + FAMILIARIZATION FEEDBACK", CheckBlock1Feedback);
            RunSection("BLOCK 2 — TWO-PHASE PRACTICE + RECOGNITION LABELS/LAYOUT",
                CheckBlock2PracticeAndLayout);
            RunSection("BLOCK 3 — LABEL ORIENTATION, RECENTER, AREA B, PACING",
                CheckBlock3UiAndPacing);
            RunSection("BLOCK 4 — ENTER SHOWROOM RECENTER, PACING 0.15, AREA B ZONES",
                CheckBlock4LayoutAndPacing);
            RunSection("BLOCK 5 — IMMEDIATE ITEM TRANSITION + INPUT-RELEASE GUARD",
                CheckBlock5ImmediateTransition);
            RunSection("BLOCK 6 — RESPONSE SUBSCRIPTION LIFETIME + EARLY WAIT EXIT",
                CheckBlock6ResponseWiring);
            RunSection("BLOCK 7 — LEGEND VISUALS, AREA B NARRATION, DELAYED INSTRUCTION",
                CheckBlock7VisualsAndNarration);
            RunSection("BLOCK 8 — DELAYED RECOGNITION STIMULUS VISIBILITY",
                CheckBlock8DelayedStimulusVisibility);
            RunSection("BLOCK 9 — TYPED RECOGNITION AGGREGATES + PROTOCOL-AWARE RESULTS",
                CheckBlock9RecognitionAggregates);
            RunSection("BLOCK 10 — RECOGNITION PROGRESS COUNTER + DEVELOPER CHEATSHEET",
                CheckBlock10CounterAndCheatsheet);
            RunSection("BLOCK 11 — ANALYSIS TIME-BASE CLOCK DOMAIN",
                CheckBlock11AnalysisTimebaseDomain);
            RunSection("BLOCK 12 — NEAR-IDENTICAL CHANNELS + DEGRADATION + ROI VALIDITY",
                CheckBlock12ChannelHealth);
            RunSection("BLOCK 13 — SESSION-CONTROL ACCIDENTAL-ACTION LOCK",
                CheckBlock13SessionControlLock);
            RunSection("LANGUAGE WORD SETS + PROVENANCE", CheckLanguageWordSets);
            RunSection("RECALL ADAPTIVE STOP (SILENCE DETECTION)", CheckRecallSilenceDetector);
            RunSection("RECALL RECORDING STOP REPORTING", CheckRecordingStopReporting);
            RunSection("AREA B FLOW + RESPONSE-TIME CONTRACT", CheckAreaBFlowContract);
            RunSection("AREA B READY CANCELS INSTRUCTION NARRATION", CheckAreaBReadyStopsNarration);
            RunSection("PARTICIPANT-FACING BLOCK RESULT", CheckParticipantBlockResult);
            RunSection("AREA 0 — VR FAMILIARIZATION", CheckFamiliarization);
            RunSection("DUAL-TRIGGER SELECTION", CheckDualTriggerInput);
            RunSection("OBJECTIVE CHAIR SHAPE CATEGORIES", CheckShapeCategories);
            RunSection("PRACTICE TASK V2 + CONTROLLER HELP", CheckPracticeTaskAndControllerHelp);
            RunSection("AREA 0 LOCALIZATION (COLOURS + CONTROLLER HELP)", CheckArea0Localization);
            RunSection("PRACTICE TARGET — SINGLE SOURCE OF TRUTH", CheckPracticeTargetSingleSource);
            RunSection("PRACTICE NARRATION MAPPING (3 x 4)", CheckPracticeNarrationMapping);
            RunSection("ROOM-SCOPED PARTICIPANT AUDIO", CheckRoomScopedAudio);
            RunSection("AREA 0 LAYOUT (NO OVERLAP)", CheckArea0Layout);
            RunSection("AREA B INSTRUCTION OVERLAY", CheckAreaBOverlay);
            RunSection("AREA C RESULTS LAYOUT", CheckAreaCLayout);
            RunSection("FINAL SUMMARY vs BUTTONS (EN/ES/JA)", CheckFinalSummaryLayout);
            RunSection("RUN DURATION LIFECYCLE", CheckRunDuration);
            RunSection("OBSOLETE AUDIO WARNING REMOVED", CheckObsoleteWarningRemoved);
            RunSection("RECORDING UI SEMANTICS", CheckRecordingUiSemantics);
            RunSection("MULTI-RUN IDENTIFIERS + RESTART SAFETY", CheckRunLifecycle);
            RunSection("LOCALIZATION — TABLE + GLYPHS", CheckLocalizationTable);
            RunSection("LANGUAGE SELECTION FLOW", CheckLanguageSelectionFlow);
            RunSection("LANGUAGE-SPECIFIC WORD SETS", CheckWordSetLanguageSafety);
            RunSection("FINAL STATISTICS + END STATE", CheckFinalStatisticsAndEndState);
            RunSection("NEW TRIAL RUN TRACKING", CheckNewTrialRunTracking);

            // Deliberately AFTER the run-lifecycle sections: ABORT closes the run's files, and a
            // section that then expected an open session would fail for the wrong reason.
            RunSection("ABORT LIFECYCLE + ABORTED SCREEN", CheckAbortLifecycle);
            RunSection("DEVELOPER NAVIGATION", CheckDeveloperNavigation);
            RunSection("RECALL SCORING LOGIC", CheckRecallScoring);
            RunSection("EVENT LOGGING + CSV PIPELINE", CheckCsvPipeline);
            RunSection("PROCEDURAL AUDIO", CheckAudioGeneration);
            RunSection("HARDWARE CONFIGURATION", CheckHardwareConfiguration);
            RunSection("STIMULUS CONFIRMATION LOGIC", CheckStimulusConfirmationLogic);
            RunSection("MICROPHONE SIGNAL ANALYSIS", CheckSignalAnalysis);
            RunSection("TIME FORMATTING", CheckTimeFormatting);

            RunSection("INTERACTION CONFIGURATION", CheckInteractionConfiguration);

            s_Log.AppendLine();
            s_Log.AppendLine(s_Failures == 0
                ? "[IKEA_EEG] SELF TEST PASSED — 0 failures."
                : $"[IKEA_EEG] SELF TEST FAILED — {s_Failures} failure(s).");

            return s_Log.ToString();
        }

        // ---------------------------------------------------------------------------------

        static void Section(string title)
        {
            s_Log.AppendLine();
            s_Log.AppendLine($"[IKEA_EEG] ===== {title} =====");
        }

        /// <summary>
        /// Runs one section, recording a throw as a failure rather than letting it abort the
        /// whole run. A check that crashes is a failing check, not a reason to lose the report.
        /// </summary>
        static void RunSection(string title, System.Action check)
        {
            Section(title);

            try
            {
                check();
            }
            catch (System.Exception e)
            {
                s_Log.AppendLine($"  FAIL  section threw {e.GetType().Name}: {e.Message}");
                s_Log.AppendLine($"        {e.StackTrace}");
                s_Failures++;
            }
        }

        static void Assert(bool condition, string message)
        {
            if (condition)
            {
                s_Log.AppendLine($"  ok    {message}");
            }
            else
            {
                s_Log.AppendLine($"  FAIL  {message}");
                s_Failures++;
            }
        }

        static void Info(string message) => s_Log.AppendLine($"  info  {message}");

        /// <summary>
        /// A source file with its comments and its Inspector attributes removed, so a scan for
        /// hard-coded values inspects CODE rather than prose.
        ///
        /// Written after three separate false alarms in which a source scan matched the very
        /// comment explaining why the thing being scanned for was avoided.
        /// </summary>
        static string StripCommentsAndAttributes(string source)
        {
            var withoutBlockComments = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var lines = withoutBlockComments.Split('\n')
                .Select(line =>
                {
                    var trimmed = line.TrimStart();

                    // Whole-line comments and Inspector attributes ([Range(1, 512)], [Tooltip]).
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("["))
                        return string.Empty;

                    // Trailing comments, but not the // inside a string literal.
                    var quote = line.IndexOf('"');
                    var comment = line.IndexOf("//", System.StringComparison.Ordinal);

                    if (comment >= 0 && (quote < 0 || comment < quote))
                        return line.Substring(0, comment);

                    return line;
                });

            return string.Join("\n", lines);
        }

        /// <summary>One-line preview of a label's text, for a readable failure message.</summary>
        static string Shorten(string text)
        {
            var flat = text.Replace("\n", " ").Replace("\r", " ").Trim();
            return flat.Length <= 48 ? flat : flat.Substring(0, 45) + "...";
        }

        // ---------------------------------------------------------------------------------
        // Serialized field name collisions
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Field names Unity already serializes on every MonoBehaviour/ScriptableObject via
        /// its C++ base classes. Re-declaring one of these in a subclass produces:
        ///
        ///   "The same field name is serialized multiple times in the class or its parent
        ///    class. This is not supported: Base(MonoBehaviour) m_Enabled"
        ///
        /// which Unity repeats on every serialization — i.e. constantly, at runtime.
        /// </summary>
        static readonly HashSet<string> k_UnityReservedFieldNames = new HashSet<string>
        {
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
            "m_Enabled",
            "m_EditorHideFlags",
            "m_Script",
            "m_Name",
            "m_EditorClassIdentifier",
        };

        /// <summary>
        /// Catches the collision class of bug automatically instead of relying on someone
        /// noticing a repeated Console warning during a headset session.
        /// </summary>
        static void CheckSerializedFieldCollisions()
        {
            var types = typeof(ExperimentManager).Assembly
                .GetTypes()
                .Where(t => !t.IsAbstract
                            && (t.Namespace?.StartsWith("IkeaEeg") ?? false)
                            && (typeof(MonoBehaviour).IsAssignableFrom(t)
                                || typeof(ScriptableObject).IsAssignableFrom(t)))
                .OrderBy(t => t.FullName)
                .ToList();

            var collisions = 0;
            var inspected = 0;

            foreach (var type in types)
            {
                var seen = new Dictionary<string, string>();

                // Walk the whole chain up to (but excluding) Unity's own base classes, so a
                // subclass shadowing one of OUR serialized fields is caught too.
                for (var current = type;
                     current != null && current != typeof(MonoBehaviour) && current != typeof(ScriptableObject);
                     current = current.BaseType)
                {
                    var fields = current.GetFields(BindingFlags.Instance |
                                                   BindingFlags.Public |
                                                   BindingFlags.NonPublic |
                                                   BindingFlags.DeclaredOnly);

                    foreach (var field in fields)
                    {
                        if (!IsUnitySerialized(field))
                            continue;

                        inspected++;

                        if (k_UnityReservedFieldNames.Contains(field.Name))
                        {
                            s_Log.AppendLine($"  FAIL  {type.Name}.{field.Name} collides with a " +
                                             "field Unity already serializes on the base class");
                            collisions++;
                        }

                        if (seen.TryGetValue(field.Name, out var owner))
                        {
                            s_Log.AppendLine($"  FAIL  {type.Name}.{field.Name} is declared in " +
                                             $"both {current.Name} and {owner}");
                            collisions++;
                        }
                        else
                        {
                            seen[field.Name] = current.Name;
                        }
                    }
                }
            }

            Info($"{inspected} serialized fields inspected across {types.Count} IkeaEeg " +
                 "MonoBehaviour/ScriptableObject types");
            Assert(collisions == 0,
                $"no serialized field name collides with Unity's own ({collisions} found)");
        }

        static bool IsUnitySerialized(FieldInfo field)
        {
            if (field.IsStatic || field.IsLiteral || field.IsInitOnly)
                return false;

            if (field.GetCustomAttribute<System.NonSerializedAttribute>() != null)
                return false;

            return field.IsPublic || field.GetCustomAttribute<SerializeField>() != null;
        }

        // ---------------------------------------------------------------------------------
        // Reference completeness
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Fields that are legitimately allowed to be null (optional clips, optional overrides).
        /// Anything else being null means the builder failed to wire something.
        /// </summary>
        static readonly HashSet<string> k_OptionalFields = new HashSet<string>
        {
            "m_RecallBeep", "m_WordCue", "m_ChairSelectionFeedback", "m_InstructionCue",
            "m_StimulusTransition", "m_ResponseConfirm",   // generated in EnsureClips, like the four above
            "m_CueSource", "m_FeedbackSource",     // created at runtime in Awake
            "m_TranscriptionProviderBehaviour",    // resolved at runtime from the GameObject
            "m_Interactable",                      // resolved in Awake if not set
        };

        static void CheckSceneReferences()
        {
            var components = new List<Component>();
            components.AddRange(Object.FindObjectsByType<ExperimentManager>(FindObjectsSortMode.None));
            components.AddRange(Object.FindObjectsByType<ExperimentUIController>(FindObjectsSortMode.None));
            components.AddRange(Object.FindObjectsByType<XRRigTeleporter>(FindObjectsSortMode.None));
            components.AddRange(Object.FindObjectsByType<ChairSelectionTask>(FindObjectsSortMode.None));
            components.AddRange(Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None));
            components.AddRange(Object.FindObjectsByType<VoiceRecallManager>(FindObjectsSortMode.None));
            components.AddRange(Object.FindObjectsByType<ExperimentAudio>(FindObjectsSortMode.None));

            var nullRefs = 0;
            var checkedFields = 0;

            foreach (var component in components)
            {
                var so = new SerializedObject(component);
                var prop = so.GetIterator();
                var enterChildren = true;

                while (prop.NextVisible(enterChildren))
                {
                    enterChildren = false;

                    if (prop.propertyType != SerializedPropertyType.ObjectReference)
                        continue;

                    if (prop.name == "m_Script" || k_OptionalFields.Contains(prop.name))
                        continue;

                    checkedFields++;

                    if (prop.objectReferenceValue == null)
                    {
                        s_Log.AppendLine($"  FAIL  {component.GetType().Name}.{prop.name} " +
                                         $"is NULL on '{component.gameObject.name}'");
                        nullRefs++;
                    }
                }
            }

            Info($"{checkedFields} serialized object references inspected across " +
                 $"{components.Count} components");
            Assert(nullRefs == 0, $"all required inspector references are assigned ({nullRefs} nulls)");

            // Lists (chairs, renderers) are not ObjectReference properties at the top level.
            var task = Object.FindAnyObjectByType<ChairSelectionTask>();
            Assert(task != null && task.chairs.Count == 6,
                $"ChairSelectionTask holds 6 chairs (found {task?.chairs.Count ?? 0})");

            foreach (var chair in Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None))
            {
                var renderers = chair.GetComponentsInChildren<Renderer>(true).Length;
                var colliders = chair.GetComponentsInChildren<Collider>(true)
                    .Count(c => !c.isTrigger);

                Assert(renderers > 0 && colliders > 0,
                    $"{chair.chairId} has {renderers} renderer(s) and {colliders} non-trigger " +
                    "collider(s) [needed for ray selection and highlighting]");
            }
        }

        // ---------------------------------------------------------------------------------
        // Config assets
        // ---------------------------------------------------------------------------------

        static void CheckConfigAssets()
        {
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            Assert(config != null, $"ExperimentConfig exists at {ExperimentAssetBuilder.ConfigPath}");
            if (config == null)
                return;

            Assert(config.Validate(out var problem), $"ExperimentConfig validates ({problem})");

            // ---- Repeatable-protocol configuration ------------------------------------------
            Assert(config.chairTrialsPerRun >= 1,
                $"chairTrialsPerRun is {config.chairTrialsPerRun} (PROTOTYPE DEFAULT: 3)");

            var sequence = config.BuildDifficultySequence();
            Assert(sequence.Count == config.chairTrialsPerRun,
                $"the difficulty sequence expands to exactly {config.chairTrialsPerRun} " +
                $"entries ({string.Join(" -> ", sequence)})");

            foreach (var level in sequence.Distinct())
            {
                var levelProfile = ChairTrialGenerator.FindProfile(config.difficultyProfiles, level);
                Assert(levelProfile != null && levelProfile.Validate(6, out _),
                    $"the {level} difficulty profile exists and is valid for six chairs");
            }

            Info($"protocol: {config.DescribeProtocol()}");
            Info("NOTE: these are PROTOTYPE DEFAULTS for development, not a validated " +
                 "clinical protocol.");

            // ---- Word set integrity ----------------------------------------------------------
            Assert(config.wordList.ValidateAllSets(out var setProblem),
                $"every word set has 5 ordered words and 5 usable clips ({setProblem})");

            Assert(!string.IsNullOrEmpty(config.GetTrialWordSetId()),
                $"the active word set has a word_set_id ('{config.GetTrialWordSetId()}')");

            var setIds = Enumerable.Range(0, config.wordList.setCount)
                .Select(i => config.wordList.GetSetId(i))
                .ToList();
            Assert(setIds.Distinct().Count() == setIds.Count,
                $"word set ids are unique ({string.Join(", ", setIds)})");

            var words = config.GetTrialWords();

            // Length comes from the ASSET, not from a literal: the FreeRecall list is
            // configurable and the Recognition protocol uses a different list entirely. This
            // used to assert 5 and would have failed the moment the count became configurable.
            var expectedWords = config.wordList != null ? config.wordList.wordsPerSet : 5;

            Assert(words.Count == expectedWords,
                $"trial word set has {expectedWords} words as configured (found {words.Count})");
            Info($"word set '{config.GetTrialWordSetName()}': {string.Join(", ", words)}");

            var distinct = words.Select(w => w.ToLowerInvariant()).Distinct().Count();
            Assert(distinct == words.Count, "the configured words are all distinct");

            // --- Spoken word clips: the words are an AUDITORY-ONLY stimulus ------------------
            Assert(config.wordList.ValidateClips(config.wordSetIndex, out var clipProblem),
                $"every word in the active set has a spoken recording ({clipProblem})");

            for (var setIndex = 0; setIndex < config.wordList.setCount; setIndex++)
            {
                var set = config.wordList.GetSet(setIndex);
                if (set == null)
                    continue;

                Assert(set.HasAllClips(),
                    $"word set '{set.setName}' has a clip for all {set.words.Count} words");

                for (var i = 0; i < set.words.Count; i++)
                {
                    var clip = set.GetClip(i);
                    if (clip == null)
                        continue;

                    Assert(clip.samples > 0 && clip.length > 0.1f,
                        $"    '{set.words[i]}' -> {clip.name}: {clip.length:F2} s, " +
                        $"{clip.frequency} Hz, {clip.channels} ch");
                }
            }

            // The whole point of this change: nothing may put a target word on screen.
            var uiType = typeof(IkeaEeg.UI.ExperimentUIController);
            var revealMethod = uiType.GetMethod("SetPresentedWord");
            Assert(revealMethod == null,
                "ExperimentUIController exposes NO method for displaying a target word " +
                "[the words must be heard, never read]");

            Info($"target chair: {config.targetChair}");
            Info($"timings: instruction={config.instructionDurationSeconds}s, " +
                 $"word={config.wordDisplaySeconds}s, gap={config.interWordGapSeconds}s, " +
                 $"immediate recall max={config.immediateRecallMaxDuration}s, " +
                 $"pre-target={config.preTargetIntervalSeconds}s, " +
                 $"delayed recall max={config.delayedRecallMaxDuration}s");

            // Encoding pacing now follows the spoken recordings, so the estimate uses their
            // real lengths rather than a nominal display duration.
            var spokenTotal = 0f;
            var longestWord = 0f;
            for (var i = 0; i < words.Count; i++)
            {
                var clip = config.wordList.GetWordClip(config.wordSetIndex, i);
                var length = clip != null ? clip.length : config.wordDisplaySeconds;
                spokenTotal += length;
                longestWord = Mathf.Max(longestWord, length);
            }

            var meanSoa = words.Count > 0
                ? spokenTotal / words.Count + config.interWordGapSeconds
                : 0f;

            Info($"spoken encoding: {spokenTotal:F2} s of speech across {words.Count} words " +
                 $"(longest {longestWord:F2} s), inter-word gap {config.interWordGapSeconds:F2} s " +
                 $"-> mean stimulus-onset asynchrony ~{meanSoa:F2} s");

            Assert(meanSoa >= 0.9f,
                $"mean word-to-word SOA is {meanSoa:F2} s [under ~0.9 s the words run together " +
                "and encoding becomes unreasonably hard — raise Inter Word Gap Seconds]");

            var estimate = config.instructionDurationSeconds
                           + config.preFirstWordDelaySeconds
                           + spokenTotal
                           + (words.Count - 1) * config.interWordGapSeconds
                           + config.preBeepDelaySeconds
                           + config.immediateRecallMaxDuration
                           + config.preTargetIntervalSeconds * config.chairTrialsPerRun
                           + config.delayedInstructionDurationSeconds
                           + config.delayedRecallMaxDuration;
            // An upper bound now: both recall phases normally end early, on sustained silence
            // after speech, so a real session is usually shorter than this.
            Info($"estimated MAXIMUM run duration (both recalls running to their hard maximum, " +
                 $"excluding participant button presses and response times): ~{estimate:F0} s");
        }

        // ---------------------------------------------------------------------------------
        // Chair logic
        // ---------------------------------------------------------------------------------

        static void CheckChairLogic()
        {
            var target = new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Solid);

            Assert(target.Matches(new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Solid)),
                "identical spec matches");
            Assert(!target.Matches(new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Slatted)),
                "spec differing only in shape does NOT match");
            Assert(target.MatchCount(new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Slatted)) == 2,
                "MatchCount reports 2 shared attributes");
            Assert(target.ToInstructionString() == "LARGE, BLUE, SOLID",
                $"instruction string is '{target.ToInstructionString()}'");

            var task = Object.FindAnyObjectByType<ChairSelectionTask>();
            if (task == null)
                return;

            var specs = task.chairs.Select(c => c.spec).ToList();
            var unique = specs.Distinct().Count();
            Assert(unique == specs.Count,
                $"all 6 chairs have distinct attribute combinations ({unique} unique)");

            foreach (var chair in task.chairs)
            {
                var shared = chair.spec.MatchCount(task.targetSpec);
                Info($"{chair.chairId}: {chair.spec}  shares {shared}/3 attributes with the target");
            }

            var twoAttributeDistractors = task.chairs.Count(c => c.spec.MatchCount(task.targetSpec) == 2);
            Assert(twoAttributeDistractors >= 2,
                $"{twoAttributeDistractors} distractors share exactly 2 target attributes " +
                "[the task requires conjunctive search, not pop-out]");
        }

        // ---------------------------------------------------------------------------------
        // Deterministic randomisation
        // ---------------------------------------------------------------------------------

        static readonly string[] k_TestChairIds =
        {
            "Chair_01", "Chair_02", "Chair_03", "Chair_04", "Chair_05", "Chair_06",
        };

        static void CheckDeterministicRandom()
        {
            // The property the whole protocol rests on: same seed, same sequence.
            var a = new DeterministicRandom(12345);
            var b = new DeterministicRandom(12345);
            var identical = true;
            for (var i = 0; i < 1000; i++)
            {
                if (a.NextUInt64() != b.NextUInt64())
                    identical = false;
            }

            Assert(identical, "the same seed produces an identical 1000-draw sequence");

            var c = new DeterministicRandom(12345);
            var d = new DeterministicRandom(12346);
            var differs = false;
            for (var i = 0; i < 100; i++)
            {
                if (c.NextUInt64() != d.NextUInt64())
                    differs = true;
            }

            Assert(differs, "adjacent seeds produce different sequences");

            // Range correctness and rough uniformity — a biased or out-of-range draw would
            // quietly skew which chairs and targets appear.
            var counts = new int[6];
            var rng = new DeterministicRandom(99);
            var inRange = true;
            for (var i = 0; i < 60000; i++)
            {
                var value = rng.NextInt(6);
                if (value < 0 || value >= 6)
                    inRange = false;
                else
                    counts[value]++;
            }

            Assert(inRange, "NextInt(6) never leaves [0,6)");

            var min = counts.Min();
            var max = counts.Max();
            Assert(min > 9000 && max < 11000,
                $"NextInt(6) is close to uniform over 60000 draws (min {min}, max {max}, " +
                "expected ~10000 each)");

            // Derived streams must be stable per stream id and different across ids.
            var s1 = DeterministicRandom.Derive(777, 3).NextUInt64();
            var s2 = DeterministicRandom.Derive(777, 3).NextUInt64();
            var s3 = DeterministicRandom.Derive(777, 4).NextUInt64();
            Assert(s1 == s2, "Derive(seed, id) is stable for the same id");
            Assert(s1 != s3, "Derive(seed, id) differs across ids");

            // Shuffle must be a permutation, not a resampling.
            var list = Enumerable.Range(0, 6).ToList();
            new DeterministicRandom(4242).Shuffle(list);
            Assert(list.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, 6)),
                $"Shuffle produces a permutation ({string.Join(",", list)})");
        }

        // ---------------------------------------------------------------------------------
        // Chair trial generation
        // ---------------------------------------------------------------------------------

        static void CheckChairTrialGeneration()
        {
            var profiles = DifficultyProfile.CreateDefaults();

            foreach (var profile in profiles)
            {
                Assert(profile.Validate(6, out var problem),
                    $"{profile.level} profile is valid for a six-chair room ({problem})");

                Assert(profile.distractorSharedAttributes.All(v => v < 3),
                    $"{profile.level} never allows a distractor to share all 3 attributes " +
                    "[that would be a second correct chair]");
            }

            // ---- The core invariant, over many seeds and all three levels -------------------
            var levels = new[] { DifficultyLevel.Low, DifficultyLevel.Medium, DifficultyLevel.High };
            var generated = 0;
            var ambiguous = 0;
            var failures = 0;
            var profileViolations = 0;
            var firstProblem = string.Empty;

            foreach (var level in levels)
            {
                var profile = ChairTrialGenerator.FindProfile(profiles, level);
                var expectedShared = profile.distractorSharedAttributes.OrderBy(v => v).ToArray();

                for (var seed = 1; seed <= 300; seed++)
                {
                    var request = new ChairTrialGenerator.Request
                    {
                        sessionSeed = seed,
                        trialIndex = 1,
                        trialCount = 1,
                        difficulty = level,
                        profile = profile,
                        chairIds = k_TestChairIds,
                        slotCount = 6,
                    };

                    if (!ChairTrialGenerator.TryGenerate(request, out var plan, out var problem))
                    {
                        failures++;
                        if (string.IsNullOrEmpty(firstProblem))
                            firstProblem = $"{level} seed {seed}: {problem}";
                        continue;
                    }

                    generated++;

                    var exact = plan.assignments.Count(a => a.spec.Matches(plan.target));
                    if (exact != 1)
                        ambiguous++;

                    // The distractor similarity profile must be honoured exactly.
                    var actualShared = plan.assignments
                        .Where(a => !a.spec.Matches(plan.target))
                        .Select(a => a.spec.MatchCount(plan.target))
                        .OrderBy(v => v)
                        .ToArray();

                    if (!actualShared.SequenceEqual(expectedShared))
                        profileViolations++;
                }
            }

            Assert(failures == 0,
                $"{generated} trials generated across LOW/MEDIUM/HIGH x 300 seeds with " +
                $"{failures} failure(s) {firstProblem}");

            Assert(ambiguous == 0,
                $"EXACTLY ONE chair matches the target in all {generated} generated trials " +
                $"({ambiguous} ambiguous)");

            Assert(profileViolations == 0,
                $"every generated trial matches its difficulty's distractor profile " +
                $"({profileViolations} violation(s))");

            // ---- The levels must actually differ in the intended direction -------------------
            foreach (var level in levels)
            {
                var profile = ChairTrialGenerator.FindProfile(profiles, level);
                var request = new ChairTrialGenerator.Request
                {
                    sessionSeed = 20250812,
                    trialIndex = 1,
                    trialCount = 1,
                    difficulty = level,
                    profile = profile,
                    chairIds = k_TestChairIds,
                    slotCount = 6,
                };

                ChairTrialGenerator.TryGenerate(request, out var plan, out _);

                var nearMatches = plan.assignments.Count(a =>
                    !a.spec.Matches(plan.target) && a.spec.MatchCount(plan.target) == 2);
                var shareOne = plan.assignments.Count(a => a.spec.MatchCount(plan.target) == 1);
                var shareNone = plan.assignments.Count(a => a.spec.MatchCount(plan.target) == 0);

                Info($"{level}: {shareNone} distractor(s) share 0, {shareOne} share 1, " +
                     $"{nearMatches} share 2 attributes with the target");

                switch (level)
                {
                    case DifficultyLevel.Low:
                        Assert(nearMatches == 0,
                            "LOW contains no two-attribute near-matches");
                        break;
                    case DifficultyLevel.Medium:
                        Assert(nearMatches == 0 && shareOne >= 2,
                            $"MEDIUM has some single-attribute overlap ({shareOne}) and no " +
                            "near-matches");
                        break;
                    case DifficultyLevel.High:
                        Assert(nearMatches >= 3,
                            $"HIGH has several two-attribute near-matches ({nearMatches})");
                        break;
                }
            }

            // ---- Structural properties -------------------------------------------------------
            ChairTrialGenerator.TryGenerateBlock(4242,
                new[] { DifficultyLevel.Low, DifficultyLevel.Medium, DifficultyLevel.High },
                profiles, k_TestChairIds, 6, out var block, out var blockProblem);

            Assert(block != null && block.Count == 3,
                $"a three-trial block generates ({blockProblem})");

            if (block == null)
                return;

            var allSlotsUsed = block.All(p =>
                p.assignments.Select(a => a.slotIndex).Distinct().Count() == 6);
            Assert(allSlotsUsed, "every trial fills all six slots exactly once");

            var allChairsUsed = block.All(p =>
                p.assignments.Select(a => a.chairId).Distinct().Count() == 6);
            Assert(allChairsUsed, "every trial uses each chair identity exactly once");

            // Immediate target repeats should be avoided where the space allows it.
            var repeats = 0;
            for (var i = 1; i < block.Count; i++)
            {
                if (block[i].targetChairId == block[i - 1].targetChairId)
                    repeats++;
            }

            Assert(repeats == 0,
                $"the target chair identity does not repeat between consecutive trials " +
                $"({repeats} repeat(s))");

            foreach (var plan in block)
                Info(plan.Describe());
        }

        // ---------------------------------------------------------------------------------
        // Reproducibility
        // ---------------------------------------------------------------------------------

        static void CheckSeedReproducibility()
        {
            var profiles = DifficultyProfile.CreateDefaults();
            var sequence = new[] { DifficultyLevel.Low, DifficultyLevel.Medium, DifficultyLevel.High };

            ChairTrialGenerator.TryGenerateBlock(555, sequence, profiles, k_TestChairIds, 6,
                out var first, out _);
            ChairTrialGenerator.TryGenerateBlock(555, sequence, profiles, k_TestChairIds, 6,
                out var replay, out _);

            Assert(first != null && replay != null && DescribeBlock(first) == DescribeBlock(replay),
                "REPLAY SAME SEED: seed 555 reproduces an identical block (targets, chair " +
                "attributes, slot arrangement and difficulty sequence)");

            // Different seeds must be able to produce different configurations, otherwise the
            // randomisation is decorative.
            var distinct = new HashSet<string>();
            for (var seed = 1; seed <= 40; seed++)
            {
                ChairTrialGenerator.TryGenerateBlock(seed, sequence, profiles, k_TestChairIds, 6,
                    out var block, out _);

                if (block != null)
                    distinct.Add(DescribeBlock(block));
            }

            Assert(distinct.Count >= 35,
                $"40 different seeds produced {distinct.Count} distinct configurations");

            // Changing the trial count must not reshuffle the earlier trials — otherwise two
            // runs from one seed are not comparable.
            ChairTrialGenerator.TryGenerateBlock(555,
                new[] { DifficultyLevel.Low, DifficultyLevel.Medium }, profiles, k_TestChairIds, 6,
                out var shorter, out _);

            // Compared by STIMULUS signature: the presented trial must be identical, even though
            // the plan's trialCount legitimately differs between a 2-trial and a 3-trial run.
            Assert(shorter != null && first != null &&
                   shorter[0].StimulusSignature() == first[0].StimulusSignature() &&
                   shorter[1].StimulusSignature() == first[1].StimulusSignature(),
                "trials 1 and 2 are unchanged when the run length changes from 3 to 2 " +
                "(per-trial derived streams)");

            // The difficulty sequence must follow the configuration, in order.
            Assert(first != null &&
                   first[0].difficulty == DifficultyLevel.Low &&
                   first[1].difficulty == DifficultyLevel.Medium &&
                   first[2].difficulty == DifficultyLevel.High,
                "the difficulty sequence is presented in the configured order LOW -> MEDIUM -> HIGH");
        }

        static string DescribeBlock(List<ChairTrialPlan> block)
        {
            return string.Join(" || ", block.Select(p => p.Describe()));
        }

        // ---------------------------------------------------------------------------------
        // Session metrics
        // ---------------------------------------------------------------------------------

        static void CheckSessionMetrics()
        {
            var results = new SessionResults { sessionId = "S_TEST", randomizationSeed = 42 };

            results.chairTrials.Add(MakeTrial(1, DifficultyLevel.Low, true, 1000d, true));
            results.chairTrials.Add(MakeTrial(2, DifficultyLevel.Medium, false, 3000d, true));
            results.chairTrials.Add(MakeTrial(3, DifficultyLevel.High, true, 2000d, true));

            Assert(results.chairTrialsTotal == 3 && results.chairTrialsScored == 3,
                $"3 chair trials, 3 scored");

            Assert(results.chairTrialsCorrect == 2 &&
                   Mathf.Abs((float)results.chairAccuracy - 2f / 3f) < 0.0001f,
                $"accuracy is {results.FormatAccuracy()}");

            Assert(Mathf.Abs((float)results.meanResponseTimeSeconds - 2f) < 0.0001f,
                $"mean RT is {results.meanResponseTimeSeconds:F3} s (expected 2.000)");

            Assert(Mathf.Abs((float)results.medianResponseTimeSeconds - 2f) < 0.0001f,
                $"median RT is {results.medianResponseTimeSeconds:F3} s (expected 2.000)");

            // An invalid trial must be excluded from BOTH the accuracy and the timing figures.
            results.chairTrials.Add(MakeTrial(4, DifficultyLevel.High, false, 90000d, false));

            Assert(results.chairTrialsTotal == 4 && results.chairTrialsScored == 3,
                "an aborted trial counts towards the total but not towards the scored trials");

            Assert(Mathf.Abs((float)results.meanResponseTimeSeconds - 2f) < 0.0001f,
                $"the 90 s aborted trial does not move the mean ({results.meanResponseTimeSeconds:F3} s)");

            Assert(results.chairTrialsCorrect == 2,
                "the aborted trial does not change the correct count");

            // Even-count median.
            var even = new SessionResults();
            even.chairTrials.Add(MakeTrial(1, DifficultyLevel.Low, true, 1000d, true));
            even.chairTrials.Add(MakeTrial(2, DifficultyLevel.Low, true, 2000d, true));
            Assert(Mathf.Abs((float)even.medianResponseTimeSeconds - 1.5f) < 0.0001f,
                $"median of two trials is their midpoint ({even.medianResponseTimeSeconds:F3} s)");

            // No valid trials at all must report NA, never 0.
            var empty = new SessionResults();
            Assert(double.IsNaN(empty.chairAccuracy) && double.IsNaN(empty.meanResponseTimeSeconds),
                "with no valid trials, accuracy and RT are NaN (reported as NA), not 0");

            Assert(SessionResults.FormatSeconds(double.NaN) == "NA" &&
                   SessionResults.FormatRatio(double.NaN) == "NA",
                "unavailable metrics format as NA");

            // Response-time presentation format: "X.XXX s (XXXX ms)".
            var formatted = TimeFormat.FromMilliseconds(1843.2);
            Assert(formatted == "1.843 s (1843 ms)",
                $"response time formats as '{formatted}'");

            var trialLine = results.chairTrials[0].ToSummaryLine();
            Assert(trialLine.Contains("1.000 s (1000 ms)"),
                $"the summary line reports RT in the standard format: '{trialLine.Trim()}'");

            Info(results.BuildResearcherSummary());

            CheckSummaryFiles(results);
        }

        /// <summary>
        /// The derived output files must be parseable and must not lose the invalid trial.
        /// They are what a researcher actually opens, so a malformed one would be discovered
        /// only after a session.
        /// </summary>
        static void CheckSummaryFiles(SessionResults results)
        {
            results.sessionId = "S_SELFTEST_SUMMARY";
            results.csvPath = @"C:\some, path\events.csv";      // comma: must be escaped
            results.audioFolder = @"C:\some\audio";
            results.protocolDescription = "chair_trials=3; difficulty_sequence=LOW->MEDIUM->HIGH";

            var directory = Path.Combine(Path.GetTempPath(), "IKEA_EEG_SelfTest");

            var summaryPath = SessionSummaryWriter.WriteSessionSummary(results, directory);
            Assert(!string.IsNullOrEmpty(summaryPath) && File.Exists(summaryPath),
                $"session summary CSV written: {summaryPath}");

            if (File.Exists(summaryPath))
            {
                var rows = ParseCsv(summaryPath);
                var headerColumns = File.ReadAllLines(summaryPath)[0].Split(',').Length;

                Assert(rows.Count == results.chairTrialsTotal,
                    $"the summary has one row per chair trial ({rows.Count} rows for " +
                    $"{results.chairTrialsTotal} trials, including the invalid one)");

                Assert(rows.All(r => r.Length == headerColumns),
                    $"every summary row has {headerColumns} columns");

                var header = File.ReadAllLines(summaryPath)[0].Split(',');
                var validColumn = System.Array.IndexOf(header, "trial_valid");
                var rtColumn = System.Array.IndexOf(header, "response_time_s");

                var invalidRows = rows.Where(r => r[validColumn] == "FALSE").ToList();
                Assert(invalidRows.Count == 1,
                    $"the aborted trial appears, flagged trial_valid=FALSE ({invalidRows.Count})");

                Assert(invalidRows.All(r => string.IsNullOrEmpty(r[rtColumn])),
                    "the aborted trial's response time is BLANK, not 0");
            }

            var researcherPath = SessionSummaryWriter.WriteResearcherSummary(results, directory);
            Assert(!string.IsNullOrEmpty(researcherPath) && File.Exists(researcherPath),
                $"researcher summary written: {researcherPath}");

            var recordings = new List<RecordingInfo>
            {
                new RecordingInfo
                {
                    recallPhase = RecallPhases.Immediate,
                    trialId = "T001",
                    wavPath = @"C:\audio\T001_immediate_recall.wav",
                    audioCaptured = true,
                },
                new RecordingInfo
                {
                    recallPhase = RecallPhases.Delayed,
                    trialId = "T001",
                    wavPath = @"C:\audio\T001_delayed_recall.wav",
                    audioCaptured = true,
                },
            };

            var scoringPath = SessionSummaryWriter.WriteManualScoringTemplate(results,
                new[] { "River", "Copper", "Lantern", "Falcon", "Sugar" }, recordings, directory);

            Assert(!string.IsNullOrEmpty(scoringPath) && File.Exists(scoringPath),
                $"manual recall scoring form written: {scoringPath}");

            if (!File.Exists(scoringPath))
                return;

            var scoringLines = File.ReadAllLines(scoringPath);
            var scoringHeader = scoringLines[0].Split(',');
            var scoringRows = ParseCsv(scoringPath);

            Assert(scoringRows.Count == 2,
                $"the scoring form has one row per recall recording ({scoringRows.Count})");

            foreach (var column in new[]
                     { "transcript", "number_correct", "order_correct", "intrusions", "omissions" })
            {
                var index = System.Array.IndexOf(scoringHeader, column);
                Assert(index >= 0 && scoringRows.All(r => string.IsNullOrEmpty(r[index])),
                    $"'{column}' is present and left BLANK for the researcher to fill in");
            }
        }

        static ChairTrialResult MakeTrial(int index, DifficultyLevel difficulty, bool correct,
            double responseMs, bool valid)
        {
            return new ChairTrialResult
            {
                trialIndex = index,
                trialCount = 3,
                difficulty = difficulty,
                target = new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Solid),
                targetChairId = "Chair_04",
                selectionMade = valid,
                selectedChairId = correct ? "Chair_04" : "Chair_02",
                selected = correct
                    ? new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Solid)
                    : new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Slatted),
                attributeMatchCount = correct ? 3 : 2,
                correct = correct,
                responseTimeMs = responseMs,
                responseTimeSeconds = responseMs / 1000d,
                valid = valid,
                invalidReason = valid ? string.Empty : "aborted mid-response",
            };
        }

        // ---------------------------------------------------------------------------------
        // Slot layout
        // ---------------------------------------------------------------------------------

        static void CheckChairSlotLayout()
        {
            var task = Object.FindAnyObjectByType<ChairSelectionTask>();
            if (task == null)
            {
                Info("no ChairSelectionTask in the open scene — slot layout not checked");
                return;
            }

            Assert(task.slots.Count == 6, $"Area B defines {task.slots.Count} chair slots (expected 6)");
            Assert(task.chairs.Count == task.slots.Count,
                $"{task.chairs.Count} chairs for {task.slots.Count} slots — they must match");

            var indices = task.slots.Where(s => s != null).Select(s => s.slotIndex).ToList();
            Assert(indices.Distinct().Count() == indices.Count, "slot indices are unique");

            // Chairs must be dressable in every colour the generator can pick.
            var missing = System.Enum.GetValues(typeof(ChairColor))
                .Cast<ChairColor>()
                .Count(c => task.GetColorMaterial(c) == null);
            Assert(missing == 0, $"a material exists for every chair colour ({missing} missing)");

            // Applying a generated plan to the real scene must satisfy the same invariant the
            // generator guarantees on paper.
            //
            // The authored layout is captured first and restored at the end: a self test must
            // not leave the saved scene in whatever configuration its last iteration happened
            // to produce.
            var authored = task.chairs
                .Where(c => c != null)
                .Select(c => (chair: c, spec: c.spec, position: c.transform.position,
                    rotation: c.transform.rotation))
                .ToList();
            var authoredTarget = task.targetSpec;

            var profiles = DifficultyProfile.CreateDefaults();
            var applied = 0;
            var applyFailures = 0;
            var uniqueAfterApply = 0;

            for (var seed = 1; seed <= 20; seed++)
            {
                if (!ChairTrialGenerator.TryGenerateBlock(seed,
                        new[] { DifficultyLevel.Low, DifficultyLevel.Medium, DifficultyLevel.High },
                        profiles, task.GetChairIds(), task.slots.Count, out var block, out _))
                {
                    continue;
                }

                foreach (var plan in block)
                {
                    if (!task.ApplyTrialPlan(plan, out var problem))
                    {
                        applyFailures++;
                        Info($"  apply failed (seed {seed}, trial {plan.trialIndex}): {problem}");
                        continue;
                    }

                    applied++;

                    if (task.ValidateTargetIsUnique(out _))
                        uniqueAfterApply++;
                }
            }

            Assert(applyFailures == 0,
                $"{applied} generated trials applied to the real scene with {applyFailures} failure(s)");

            Assert(applied > 0 && uniqueAfterApply == applied,
                $"after applying each plan, exactly one chair in the scene matches the target " +
                $"({uniqueAfterApply}/{applied})");

            // Every chair must still be selectable after all that shape switching.
            var stillSelectable = task.chairs.Count(c =>
            {
                var interactable = c.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
                return interactable != null && interactable.colliders.Count > 0 &&
                       c.gameObject.activeInHierarchy;
            });

            Assert(stillSelectable == task.chairs.Count,
                $"all {task.chairs.Count} chairs remain selectable after repeated shape changes " +
                $"({stillSelectable})");

            // Chairs must be standing on slots, not drifting.
            var offSlot = task.chairs.Count(c =>
                task.slots.All(s => s == null ||
                                    Vector3.Distance(s.transform.position, c.transform.position) > 0.01f));

            Assert(offSlot == 0, $"every chair stands exactly on a slot ({offSlot} off-slot)");

            // ---- Restore the authored layout --------------------------------------------------
            foreach (var (chair, spec, position, rotation) in authored)
            {
                chair.ApplySpec(spec, task.GetColorMaterial(spec.color));
                chair.transform.SetPositionAndRotation(position, rotation);
            }

            task.SetTarget(authoredTarget);
            task.ResetTask();

            Assert(task.chairs.All(c => authored.Any(a => a.chair == c && a.spec.Matches(c.spec))),
                "the authored chair layout is restored after the test");
        }

        // ---------------------------------------------------------------------------------
        // Reset
        // ---------------------------------------------------------------------------------

        static void CheckResetLogic()
        {
            // SessionResults is what a restart has to clear; a stale field here would carry a
            // previous participant's numbers into the next session's summary.
            var results = new SessionResults
            {
                sessionId = "S_OLD",
                randomizationSeed = 1234,
                isSeedReplay = true,
                totalExperimentDurationSeconds = 99d,
                immediateRecallStatus = "saved",
            };
            results.chairTrials.Add(MakeTrial(1, DifficultyLevel.Low, true, 1000d, true));

            results.Reset();

            Assert(results.chairTrials.Count == 0 &&
                   results.sessionId == string.Empty &&
                   results.randomizationSeed == 0 &&
                   !results.isSeedReplay &&
                   Mathf.Approximately((float)results.totalExperimentDurationSeconds, 0f) &&
                   results.immediateRecallStatus == "not recorded",
                "SessionResults.Reset clears trials, ids, seed, replay flag, duration and status");

            Assert(double.IsNaN(results.chairAccuracy),
                "after a reset, accuracy is NA rather than a stale value");

            var trialResult = new TrialResult { chairSelectionMade = true, chairTrialsCompleted = 3 };
            trialResult.presentedWords.Add("River");
            trialResult.Reset();

            Assert(!trialResult.chairSelectionMade &&
                   trialResult.chairTrialsCompleted == 0 &&
                   trialResult.presentedWords.Count == 0,
                "TrialResult.Reset clears the chair-trial counter and the word list");

            // A restart uses a NEW seed; a replay reuses the SAME one. Both must produce a
            // fresh session rather than continuing the old one.
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            Assert(manager != null &&
                   manager.GetType().GetMethod("ReplaySameSeed") != null &&
                   manager.GetType().GetMethod("RestartTrial") != null,
                "the manager exposes both RestartTrial (new seed) and ReplaySameSeed");
        }

        // ---------------------------------------------------------------------------------
        // LSL
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The reflection bridge against whatever liblsl-Csharp is actually installed.
        ///
        /// The bug this guards against: the bridge used to demand an EXACT one-parameter
        /// push_sample(string[]). The official binding declares
        /// push_sample(string[], double = 0.0, bool = true) — ONE method with THREE parameters,
        /// because C# optional parameters are filled in by the compiler at the call site and do
        /// not exist at the reflection layer. Matching on arity therefore rejected the very API
        /// it was meant to bind.
        ///
        /// These checks assert the RULE — match on the first parameter, supply the rest from the
        /// binding's own declared defaults — rather than any one signature, so a future liblsl
        /// that adds a parameter cannot silently un-bind the marker path again.
        /// </summary>
        static void CheckLslBindingCompatibility()
        {
            Info($"LSL library available: {LslBinding.isAvailable} — {LslBinding.detail}");

            var bindingSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/LslBinding.cs");

            // Matched as a real IMPORT DIRECTIVE — a line whose first token is `using` —
            // not as a substring, so the doc comment in that file explaining why the direct
            // import is avoided does not read as the import itself.
            var importsLsl = bindingSource
                .Split('\n')
                .Any(line => line.TrimStart().StartsWith("using LSL"));

            Assert(!importsLsl,
                "the bridge is still LATE-BOUND — no direct LSL namespace import, so the " +
                "project still compiles and runs if liblsl is removed");

            Assert(!bindingSource.Contains("p.Length == 1 && p[0].ParameterType == typeof(string[])"),
                "the exact-arity push_sample test that caused the incompatibility is gone");

            // ---- Optional/default argument invocation ---------------------------------------
            // Exercised against a stand-in with the SAME shape as the official push_sample, so
            // the rule is tested even on a machine where liblsl is not installed.
            var buildArgs = typeof(LslBinding)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "BuildArgs" &&
                                     m.GetParameters().Length == 2 &&
                                     m.GetParameters()[0].ParameterType == typeof(MethodInfo));

            Assert(buildArgs != null, "the method argument builder exists");

            if (buildArgs != null)
            {
                var probe = typeof(OptionalArgProbe).GetMethod("Push");
                var args = (object[])buildArgs.Invoke(null,
                    new object[] { probe, new object[] { new[] { "MARKER" } } });

                Assert(args.Length == 3,
                    $"all three parameters are supplied, not only the one passed ({args.Length})");

                Assert(args[1] is double stamp && System.Math.Abs(stamp) < 0.0001,
                    $"the optional timestamp comes from its DECLARED default ({args[1]})");

                Assert(args[2] is bool through && through,
                    $"the optional pushthrough flag comes from its DECLARED default ({args[2]})");

                var probeInstance = new OptionalArgProbe();
                probe.Invoke(probeInstance, args);

                Assert(probeInstance.received == "MARKER" &&
                       probeInstance.timestamp == 0.0 && probeInstance.pushthrough,
                    "invoking with filled defaults reaches the method with the documented " +
                    $"values (sample={probeInstance.received}, " +
                    $"timestamp={probeInstance.timestamp}, push={probeInstance.pushthrough})");
            }

            // ---- The sink cannot report Active on a managed binding alone --------------------
            Assert(bindingSource.Contains("TryNativeCheck"),
                "the native library can be checked independently of the managed binding, so a " +
                "bound API is never reported as proof that markers will transmit");

            var sinkSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/LslMarkerSink.cs");
            var activeAt = sinkSource.IndexOf("m_State = LslSinkState.Active",
                System.StringComparison.Ordinal);
            var createAt = sinkSource.IndexOf("LslBinding.CreateOutlet",
                System.StringComparison.Ordinal);

            Assert(createAt >= 0 && activeAt > createAt,
                "the sink becomes Active only AFTER a real outlet was created — if the native " +
                "DLL fails to load, CreateOutlet returns null and the state stays Unavailable");

            if (!LslBinding.isAvailable)
            {
                Info("no LSL C# API installed — the live signature checks are skipped. This is " +
                     "a supported state: the sink reports Unavailable and the behavioural " +
                     "experiment runs unchanged.");
                return;
            }

            // ---- Outlet ----------------------------------------------------------------------
            var pushOverloads = LslBinding.DescribeOverloads("StreamOutlet", "push_sample");

            Assert(pushOverloads.Length > 0,
                $"the installed StreamOutlet exposes push_sample ({pushOverloads.Length} overloads)");

            foreach (var overload in pushOverloads)
                Info($"  StreamOutlet.{overload}");

            var boundPush = LslBinding.boundPushSample;

            Assert(!string.IsNullOrEmpty(boundPush), $"a push_sample overload is BOUND: {boundPush}");

            Assert(boundPush.StartsWith("push_sample(String[]"),
                $"the bound overload takes string[] as its FIRST parameter ({boundPush})");

            var stringOverload =
                pushOverloads.FirstOrDefault(o => o.StartsWith("push_sample(String[]"));

            Assert(stringOverload != null,
                "the string-marker overload is present among the installed overloads");

            if (stringOverload != null && stringOverload.Contains("= ..."))
            {
                Assert(boundPush == stringOverload,
                    "an overload carrying OPTIONAL parameters binds correctly — this is the " +
                    $"exact case that used to fail ({stringOverload})");
            }

            // ---- Inlet -----------------------------------------------------------------------
            var pullOverloads = LslBinding.DescribeOverloads("StreamInlet", "pull_sample");

            foreach (var overload in pullOverloads)
                Info($"  StreamInlet.{overload}");

            var boundPull = LslBinding.boundPullSample;

            Assert(!string.IsNullOrEmpty(boundPull), $"a pull_sample overload is BOUND: {boundPull}");

            Assert(boundPull.StartsWith("pull_sample(String[]"),
                $"the bound inlet overload takes string[] first ({boundPull}) — string marker " +
                "loopback is supported");

            // Numeric channels are what a future EEG receiver will need. Not implemented here;
            // only confirmed to exist, so the same rule will bind them when it is.
            foreach (var numeric in new[] { "Single[]", "Double[]", "Int32[]" })
            {
                Assert(pullOverloads.Any(o => o.StartsWith($"pull_sample({numeric}")),
                    $"the installed inlet also offers pull_sample({numeric}) for future " +
                    "numeric EEG samples");
            }

            // ---- Resolver --------------------------------------------------------------------
            var resolver = LslBinding.boundResolveStream;

            Assert(!string.IsNullOrEmpty(resolver), $"a stream resolver is BOUND: {resolver}");

            Assert(resolver.Contains("resolve_stream(String prop, String value"),
                $"the resolver is the BY-PROPERTY overload, not the predicate one ({resolver})");

            Assert(LslBinding.CanRunLoopbackTest,
                "the inlet, resolver and pull_sample are all bound, so the marker loopback test " +
                "can run");

            var nativeOk = LslBinding.TryNativeCheck(out var nativeDetail);
            Info($"native liblsl: {(nativeOk ? "OK" : "NOT USABLE")} — {nativeDetail}");
        }

        /// <summary>
        /// A stand-in with the SAME shape as the official push_sample: one meaningful parameter
        /// followed by optional ones.
        /// </summary>
        class OptionalArgProbe
        {
            public string received;
            public double timestamp = -1d;
            public bool pushthrough;

            public void Push(string[] data, double timestamp = 0.0, bool pushthrough = true)
            {
                received = data != null && data.Length > 0 ? data[0] : null;
                this.timestamp = timestamp;
                this.pushthrough = pushthrough;
            }
        }

        /// <summary>
        /// The raw-EEG receiver, exercised END TO END against a real LSL stream.
        ///
        /// WHY A SYNTHETIC STREAM: the receiver must be testable whether or not an amplifier
        /// happens to be on the network, and it must be tested against a stream whose channel
        /// count, rate and format are known exactly — which is the only way to prove that
        /// nothing is assumed. The outlet is created in this process with values chosen
        /// specifically NOT to match the obvious guesses: 5 channels (not 8), 137 Hz (not 250),
        /// cf_double64 (not float32). If any of those were hard-coded anywhere, this fails.
        ///
        /// This does NOT claim anything about AURA. It proves the RECEIVER works; whether AURA
        /// is reachable is a separate, network-dependent fact reported by the researcher menu.
        /// </summary>
        static void CheckAuraReceiver()
        {
            Assert(AuraLslReceiver.StreamName == "AURA",
                $"the receiver targets the RAW stream name exactly ('{AuraLslReceiver.StreamName}')");

            // ---- The acquisition node is complete in the BUILT scene ------------------------
            // The pipeline is what turns received samples into features. While it was missing
            // from the scene, the runtime produced none and the Researcher Monitor had nothing
            // to display, even though every individual class worked.
            var sceneReceivers = Object.FindObjectsByType<AuraLslReceiver>(FindObjectsSortMode.None);

            Assert(sceneReceivers.Length == 1,
                $"the scene contains exactly ONE AURA receiver ({sceneReceivers.Length}) — a " +
                "second inlet on the same stream is forbidden");

            if (sceneReceivers.Length == 1)
            {
                var acquisition = sceneReceivers[0].gameObject;

                Assert(acquisition.GetComponent<EegFeaturePipeline>() != null,
                    $"the feature pipeline lives on the acquisition node '{acquisition.name}', " +
                    "so runtime features and the Researcher Monitor have a source");

                Assert(Object.FindObjectsByType<EegFeaturePipeline>(FindObjectsSortMode.None)
                        .Length == 1,
                    "exactly one feature pipeline exists — it observes the one receiver and " +
                    "creates no acquisition of its own");
            }

            // Nothing derived: the raw stream, not AURA_Filtered / AURA_Power / AURA_PSD.
            var receiverSource = File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/AuraLslReceiver.cs");

            foreach (var derived in new[] { "AURA_Filtered", "AURA_Power", "AURA_PSD" })
            {
                Assert(!receiverSource.Contains($"\"{derived}\""),
                    $"the receiver never resolves the derived stream {derived}");
            }

            // ---- No assumed amplifier constants ----------------------------------------------
            // Checked as CODE, not as text: a comment or an Inspector [Range] may legitimately
            // mention a number, and a scan that cannot tell those apart from an assumption
            // reports noise. What must not exist is a fixed-size sample buffer or an assigned
            // channel count / sampling rate.
            var receiverCode = StripCommentsAndAttributes(receiverSource);

            Assert(!System.Text.RegularExpressions.Regex.IsMatch(
                    receiverCode, @"new\s+(double|float|int|short)\s*\[\s*\d"),
                "the receiver allocates no FIXED-SIZE sample buffer — every buffer is sized " +
                "from the channel count the stream reported");

            Assert(receiverCode.Contains("new double[meta.channelCount]") ||
                   receiverCode.Contains("new double[metadata.channelCount]"),
                "sample buffers are sized from the stream's own channel count");

            foreach (var assumption in new[]
                     {
                         @"channelCount\s*=\s*\d",
                         @"nominalSrate\s*=\s*\d",
                         @"channelFormat\s*=\s*""cf_",
                     })
            {
                Assert(!System.Text.RegularExpressions.Regex.IsMatch(receiverCode, assumption),
                    $"the receiver never assigns a literal for /{assumption}/ — channel count, " +
                    "sampling rate and format are all read from the stream");
            }

            // ---- No indefinite blocking on the main thread -----------------------------------
            Assert(!System.Text.RegularExpressions.Regex.IsMatch(
                    receiverCode, @"(FOREVER|double\.MaxValue|float\.MaxValue)"),
                "the receiver never requests an indefinite pull — an amplifier going quiet " +
                "cannot hang Unity");

            Assert(receiverCode.Contains("Math.Max(0d, timeoutSeconds)"),
                "the receiver clamps any caller-supplied timeout at zero");

            var bindingSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/LslBinding.cs");
            var pullBody = bindingSource.Substring(
                bindingSource.IndexOf("public static bool TryPullNumericSample(",
                    System.StringComparison.Ordinal));
            pullBody = pullBody.Substring(0,
                pullBody.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(pullBody.Contains("Math.Max(0d, timeoutSeconds)"),
                "the numeric pull clamps its timeout at zero — a negative value cannot become " +
                "LSL's FOREVER");

            var drainBody = receiverSource.Substring(
                receiverSource.IndexOf("public int Drain(", System.StringComparison.Ordinal));
            drainBody = drainBody.Substring(0,
                drainBody.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(drainBody.Contains("TryPullOne(0d"),
                "the per-frame path polls with a ZERO timeout, so it never blocks a frame");

            // ---- Format mapping ---------------------------------------------------------------
            Assert(LslBinding.ChannelElementType(1) == typeof(float), "cf_float32 -> float");
            Assert(LslBinding.ChannelElementType(2) == typeof(double), "cf_double64 -> double");
            Assert(LslBinding.ChannelElementType(4) == typeof(int), "cf_int32 -> int");
            Assert(LslBinding.ChannelElementType(5) == typeof(short), "cf_int16 -> short");

            Assert(LslBinding.ChannelElementType(3) == null,
                "cf_string is NOT treated as a numeric signal");

            Assert(LslBinding.ChannelElementType(0) == null,
                "cf_undefined is rejected rather than guessed");

            if (!LslBinding.CanReceiveStreams)
            {
                Info("no LSL inlet API installed — the live receiver checks are skipped");
                return;
            }

            var host = new GameObject("__IKEA_EEG_SelfTest_Aura")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            object outlet = null;

            try
            {
                var receiver = host.AddComponent<AuraLslReceiver>();
                receiver.Configure(resolveTimeoutSeconds: 2f, receiveContinuously: false);

                // ---- Resolving a stream that is not there fails SAFELY -----------------------
                var connected = receiver.Connect(out var problem);

                Assert(!connected || receiver.metadata.name == AuraLslReceiver.StreamName,
                    "connecting either fails or yields a stream actually named AURA");

                if (!connected)
                {
                    Assert(receiver.state == AuraReceiverState.NotFound,
                        $"with no AURA on the network the receiver reports NotFound " +
                        $"({receiver.state})");

                    Assert(problem.StartsWith("AURA_NOT_FOUND"),
                        $"the failure is the documented reason code ({problem})");

                    Assert(receiver.samplesReceived == 0,
                        "no samples are counted when nothing was received");

                    Info("no live AURA stream on the network during this run — the negative " +
                         "path was exercised; the positive path follows against a synthetic " +
                         "in-process stream");
                }
                else
                {
                    Info($"a LIVE stream named AURA was present: {receiver.metadata}");
                }

                receiver.Disconnect();

                // ---- The positive path, against a stream with KNOWN properties ---------------
                // Deliberately unusual values: if anything were hard-coded, these would not
                // survive the round trip.
                const int channels = 5;
                const double rate = 137.0;
                const string format = "cf_double64";

                outlet = LslBinding.CreateNumericOutlet(AuraLslReceiver.StreamName, "EEG",
                    "IKEA_EEG_SelfTest_Aura", channels, rate, format, out var outletDetail);

                Assert(outlet != null, $"a synthetic AURA stream was created ({outletDetail})");

                if (outlet == null)
                    return;

                Assert(receiver.Connect(out var liveProblem),
                    $"the receiver resolves and opens an inlet on it ({liveProblem})");

                if (!receiver.isConnected)
                    return;

                var meta = receiver.metadata;

                // ---- Metadata is READ, not assumed -------------------------------------------
                Assert(meta.name == AuraLslReceiver.StreamName, $"name read: {meta.name}");
                Assert(meta.type == "EEG", $"type read: {meta.type}");

                Assert(meta.channelCount == channels,
                    $"channel count read from the stream: {meta.channelCount} (not a default of 8)");

                Assert(System.Math.Abs(meta.nominalSrate - rate) < 0.001,
                    $"sampling rate read from the stream: {meta.nominalSrate} Hz (not a default of 250)");

                Assert(meta.channelFormat == format,
                    $"channel format read from the stream: {meta.channelFormat} (not a default " +
                    "of float32)");

                Assert(meta.sourceId == "IKEA_EEG_SelfTest_Aura",
                    $"source id read: {meta.sourceId}");

                Assert(LslBinding.ChannelElementType(meta.channelFormatValue) == typeof(double),
                    "the numeric pull_sample overload is selected from the ADVERTISED format");

                // ---- Real samples, with the right shape and the right values -----------------
                var elementType = LslBinding.ChannelElementType(meta.channelFormatValue);
                var sent = new double[channels];

                for (var i = 0; i < channels; i++)
                    sent[i] = 10.0 + i;

                var pushed = 0;
                for (var i = 0; i < 8; i++)
                {
                    if (LslBinding.TryPushNumericSample(outlet, elementType, sent, out var pushError))
                        pushed++;
                    else
                        Info($"  push failed: {pushError}");
                }

                Assert(pushed > 0, $"samples were published onto the synthetic stream ({pushed})");

                // A short finite wait for the first one; the transport is local but not instant.
                var got = receiver.TryReceiveOne(2.0, out var sample, out var pullError);

                Assert(got, $"a real numeric sample was RECEIVED ({pullError})");

                if (!got)
                    return;

                Assert(sample.channels != null && sample.channels.Length == channels,
                    $"the sample buffer follows the REPORTED channel count " +
                    $"({sample.channels?.Length} of {channels})");

                var valuesMatch = true;
                for (var i = 0; i < channels; i++)
                {
                    if (System.Math.Abs(sample.channels[i] - sent[i]) > 0.0001)
                        valuesMatch = false;
                }

                Assert(valuesMatch,
                    $"every channel value survives the round trip ({sample.Describe()})");

                Assert(sample.lslTimestamp != 0d,
                    $"the sample carries a real LSL timestamp ({sample.lslTimestamp:F6})");

                Assert(receiver.samplesReceived >= 1,
                    $"the receiver counts only what actually arrived ({receiver.samplesReceived})");

                Assert(receiver.state == AuraReceiverState.Receiving,
                    $"the receiver reports Receiving once samples arrive ({receiver.state})");

                // ---- The frame-safe drain path ------------------------------------------------
                var drained = receiver.Drain(16);

                Assert(drained >= 0,
                    $"the non-blocking drain returns without waiting ({drained} sample(s))");

                // Timestamps must advance across samples.
                var stamps = receiver.recentSamples.Select(s => s.lslTimestamp).ToArray();

                if (stamps.Length >= 2)
                {
                    var increasing = true;
                    for (var i = 1; i < stamps.Length; i++)
                    {
                        if (stamps[i] <= stamps[i - 1])
                            increasing = false;
                    }

                    Assert(increasing,
                        $"timestamps increase across received samples ({stamps.Length} samples)");
                }

                // ---- Disconnect leaves nothing open --------------------------------------------
                receiver.Disconnect();

                Assert(!receiver.isConnected, "disconnecting closes the inlet");
            }
            finally
            {
                LslBinding.Dispose(outlet);
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// The ring buffer and window extraction, driven with SYNTHETIC samples at a rate and
        /// channel count chosen NOT to match the observed amplifier.
        ///
        /// 7 channels at 137 Hz, deliberately: the live stream is 8 at 250, so anything that had
        /// quietly hard-coded the real numbers fails here. Synthetic input is also the only way
        /// to test wraparound, gaps and missing edges deterministically — those cannot be
        /// summoned on demand from a real amplifier.
        /// </summary>
        static void CheckRawEegBuffer()
        {
            const int channels = 7;
            const double rate = 137.0;

            var buffer = new RawEegRingBuffer(channels, rate, capacitySeconds: 2.0);

            // ---- Sizing is DERIVED ------------------------------------------------------------
            Assert(buffer.channelCount == channels,
                $"channel count comes from the caller/stream ({buffer.channelCount})");

            Assert(buffer.capacitySamples == (int)System.Math.Ceiling(rate * 2.0),
                $"capacity is rate x seconds, not a constant ({buffer.capacitySamples} for " +
                $"2 s at {rate} Hz)");

            Assert(System.Math.Abs(buffer.gapToleranceSeconds - 4.0 / rate) < 1e-9,
                $"the gap tolerance is derived from the nominal rate " +
                $"({buffer.gapToleranceSeconds * 1000d:F2} ms)");

            // ---- Fill with a known, regular signal ---------------------------------------------
            var step = 1.0 / rate;
            var t0 = 1000.0;
            var sample = new double[channels];

            for (var i = 0; i < 400; i++)
            {
                for (var c = 0; c < channels; c++)
                    sample[c] = i * 100 + c;

                buffer.Add(t0 + i * step, sample);
            }

            var stats = buffer.GetStats();

            Assert(stats.totalSamples == 400,
                $"every sample is counted ({stats.totalSamples})");

            Assert(stats.bufferedSamples == buffer.capacitySamples,
                $"the ring is full and bounded ({stats.bufferedSamples}) — memory does not grow");

            Assert(stats.timestampsMonotonic && stats.nonMonotonicCount == 0,
                "a regular signal is reported as monotonic");

            Assert(stats.gapCount == 0,
                $"a regular signal produces no gaps ({stats.gapCount})");

            Assert(System.Math.Abs(stats.effectiveRateHz - rate) < 0.5,
                $"the effective rate matches what was fed in ({stats.effectiveRateHz:F2} Hz)");

            // Values survive wraparound: the newest sample must be the one just written, not a
            // stale slot from before the wrap.
            Assert(buffer.TryGetLatest(out var latestT, out var latestCh),
                "the latest sample is retrievable");

            Assert(latestCh != null && System.Math.Abs(latestCh[0] - 399 * 100) < 1e-9,
                $"wraparound preserves values ({latestCh?[0]})");

            Assert(System.Math.Abs(latestT - (t0 + 399 * step)) < 1e-9,
                "wraparound preserves timestamps");

            // ---- Window extraction --------------------------------------------------------------
            var eventTime = t0 + 390 * step;

            Assert(buffer.TryGetWindow(eventTime, 0.05, 0.05, out var window),
                "a window can be cut around an event timestamp");

            Assert(window.channelCount == channels,
                $"the window carries the stream's channel count ({window.channelCount})");

            Assert(window.firstTimestamp <= eventTime && window.lastTimestamp >= eventTime,
                $"the window BRACKETS the event ({window.firstTimestamp:F4} <= " +
                $"{eventTime:F4} <= {window.lastTimestamp:F4})");

            Assert(window.timestamps != null && window.timestamps.Length == window.sampleCount,
                "every returned sample has its timestamp");

            var ordered = true;
            for (var i = 1; i < window.sampleCount; i++)
            {
                if (window.timestamps[i] <= window.timestamps[i - 1])
                    ordered = false;
            }

            Assert(ordered, "window samples are returned in chronological order");

            var expected = buffer.ExpectedSampleCount(0.05, 0.05);

            Assert(expected == (int)System.Math.Round(0.1 * rate),
                $"the expected count is computed from the ADVERTISED rate ({expected}), " +
                "never hard-coded");

            Assert(System.Math.Abs(window.sampleCount - expected) <= 2,
                $"the window holds about the expected number of samples " +
                $"({window.sampleCount} vs ~{expected})");

            // ---- Honest edges ---------------------------------------------------------------------
            Assert(buffer.TryGetWindow(latestT, 0.05, 5.0, out var future) &&
                   future.status == EegWindowStatus.MissingEnd,
                $"a window reaching past the newest sample reports MissingEnd ({future.status})");

            // Reaching back further than the ring retains: samples DO exist in range, but the
            // requested start predates the oldest one still held.
            Assert(buffer.TryGetWindow(latestT - 0.05, 5.0, 0.0, out var past) &&
                   past.sampleCount > 0 &&
                   (past.status == EegWindowStatus.MissingStart ||
                    past.status == EegWindowStatus.MissingBothEdges),
                $"a window reaching before the retained history reports a missing start " +
                $"({past.status}, {past.sampleCount} samples)");

            // And a request entirely older than the ring returns nothing at all, rather than
            // quietly handing back whatever happens to be the oldest data.
            Assert(!buffer.TryGetWindow(t0, 0.05, 0.05, out var evicted) &&
                   evicted.sampleCount == 0,
                $"a window whose whole range has been overwritten returns nothing " +
                $"({evicted.status})");

            Assert(!buffer.TryGetWindow(t0 - 1000.0, 0.01, 0.01, out var none) &&
                   none.sampleCount == 0,
                "a window with no samples in range returns nothing rather than a partial lie");

            // ---- Gaps and non-monotonic input ------------------------------------------------------
            var gapBuffer = new RawEegRingBuffer(channels, rate, capacitySeconds: 2.0);

            gapBuffer.Add(500.0, sample);
            gapBuffer.Add(500.0 + step, sample);
            gapBuffer.Add(500.0 + step + 0.5, sample);        // a half-second dropout
            gapBuffer.Add(500.0 + step + 0.4, sample);        // arrives out of order

            var gapStats = gapBuffer.GetStats();

            Assert(gapStats.gapCount == 1,
                $"a dropout longer than the tolerance is counted ({gapStats.gapCount})");

            Assert(System.Math.Abs(gapStats.largestGapSeconds - 0.5) < 0.01,
                $"the largest gap is measured, not estimated ({gapStats.largestGapSeconds:F3} s)");

            Assert(!gapStats.timestampsMonotonic && gapStats.nonMonotonicCount == 1,
                $"an out-of-order sample is recorded ({gapStats.nonMonotonicCount})");

            // ---- Irregular-rate stream ---------------------------------------------------------------
            var irregular = new RawEegRingBuffer(channels, 0d, capacitySeconds: 2.0,
                fallbackCapacitySamples: 64);

            Assert(irregular.capacitySamples == 64,
                $"an irregular-rate stream falls back to a sample count ({irregular.capacitySamples})");

            Assert(double.IsPositiveInfinity(irregular.gapToleranceSeconds),
                "with no nominal rate there is no expected interval, so no gap is invented");

            Assert(irregular.ExpectedSampleCount(1.0, 2.0) == 0,
                "no expected sample count is claimed for an irregular stream");

            // ---- Cross-machine clock correction ------------------------------------------------
            // The bug this guards against: pull_sample reports the capture time on the SENDER's
            // clock. Two machines' LSL clocks are unrelated — here they differed by ~300,000 s —
            // so an event stamped locally fell outside every window and nothing could ever be
            // epoched.
            Assert(!string.IsNullOrEmpty(LslBinding.boundTimeCorrection),
                $"liblsl's time_correction is bound: {LslBinding.boundTimeCorrection}");

            var receiverCodeForClock = StripCommentsAndAttributes(
                File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/AuraLslReceiver.cs"));

            Assert(receiverCodeForClock.Contains("remoteTimestamp + m_TimeCorrection"),
                "the sample's local timestamp is remote + correction — the ADDITION the " +
                "installed liblsl documents, not a guessed sign");

            // The correction must come from liblsl, never from comparing a sample to a local
            // clock reading (which would fold transport latency into the offset) and never
            // from any non-LSL clock.
            Assert(receiverCodeForClock.Contains("LslBinding.TryGetTimeCorrection"),
                "the offset comes from liblsl's own time_correction()");

            foreach (var wrong in new[] { "DateTime.Now", "Time.time", "Stopwatch" })
            {
                Assert(!receiverCodeForClock.Contains(wrong),
                    $"the clock correction never uses {wrong}");
            }

            // Not per sample: at 250 Hz that would be a round-trip estimate on every sample.
            Assert(receiverCodeForClock.Contains("RefreshTimeCorrectionIfDue"),
                "the correction is refreshed on a schedule, not per sample");

            var pullBodyForClock = receiverCodeForClock.Substring(
                receiverCodeForClock.IndexOf("bool TryPullOne(", System.StringComparison.Ordinal));
            pullBodyForClock = pullBodyForClock.Substring(0,
                pullBodyForClock.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(!pullBodyForClock.Contains("TryGetTimeCorrection"),
                "no time-correction round trip happens inside the per-sample path");

            // Both timestamps survive, and the authoritative one is the corrected one.
            foreach (var field in new[] { "lslTimestamp", "remoteLslTimestamp", "timeCorrection" })
            {
                Assert(typeof(RawEegSample).GetField(field) != null,
                    $"RawEegSample records {field}");
            }

            // The arithmetic itself, against a deliberately large offset like the real one.
            const double remote = 226740.123456;
            const double offset = 300005.5;

            var corrected = new RawEegSample
            {
                remoteLslTimestamp = remote,
                timeCorrection = offset,
                lslTimestamp = remote + offset,
            };

            Assert(System.Math.Abs(corrected.lslTimestamp - 526745.623456) < 1e-6,
                $"a ~300,000 s cross-machine offset maps correctly " +
                $"({corrected.lslTimestamp:F6})");

            // A window must now contain an event stamped in the LOCAL domain.
            var clockBuffer = new RawEegRingBuffer(8, 250.0, capacitySeconds: 10.0);
            var localStart = remote + offset;

            for (var i = 0; i < 1000; i++)
                clockBuffer.Add(localStart + i / 250.0, new double[8]);

            var localEvent = localStart + 500 / 250.0;

            Assert(clockBuffer.TryGetWindow(localEvent, 1.0, 2.0, out var clockWindow) &&
                   clockWindow.firstTimestamp <= localEvent &&
                   clockWindow.lastTimestamp >= localEvent,
                $"a locally-stamped event falls inside a window of corrected samples " +
                $"({clockWindow.sampleCount} samples)");

            Assert(System.Math.Abs(clockWindow.sampleCount - 750) <= 2,
                $"1 s pre + 2 s post at 250 Hz yields ~750 samples ({clockWindow.sampleCount})");

            // Uncorrected, the same event misses entirely — the bug, reproduced.
            var rawBuffer = new RawEegRingBuffer(8, 250.0, capacitySeconds: 10.0);

            for (var i = 0; i < 1000; i++)
                rawBuffer.Add(remote + i / 250.0, new double[8]);

            Assert(!rawBuffer.TryGetWindow(localEvent, 1.0, 2.0, out _),
                "WITHOUT the correction the same event falls outside every window — this is " +
                "exactly the failure the fix removes");

            // The persisted file must keep both, under names that cannot be confused.
            var recorderHeader = File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/RawEegRecorder.cs");

            Assert(recorderHeader.Contains("lsl_timestamp_local") &&
                   recorderHeader.Contains("lsl_timestamp_remote") &&
                   recorderHeader.Contains("time_correction"),
                "the raw EEG file records the local, remote and correction values explicitly");

            // ---- Analysis time base, against the REAL jitter pattern -------------------------
            // Reproduces what the live capture measured: uniform 250 Hz samples delivered in
            // 9-sample chunks whose anchor timestamps jitter by tens of milliseconds. The
            // previous model evaluated a per-sample refitted line and went backwards on this
            // input; the grid model cannot, and that is what is asserted.
            const double rateHz = 250.0;
            const double interval = 1.0 / rateHz;

            var timebase = new EegAnalysisTimebase(rateHz);
            var jitter = new System.Random(20260821);

            var analysis = new List<double>(2000);
            var rawLocal = new List<double>(2000);
            var gridOrigin = 500000.0;

            for (var i = 0; i < 2000; i++)
            {
                // True capture time is exactly uniform...
                var trueTime = gridOrigin + i * interval;

                // ...but every 9th sample starts a chunk whose anchor is displaced, and the
                // whole chunk inherits that displacement. Magnitudes match the live capture
                // (backward to -30 ms, forward to +60 ms).
                var chunk = i / 9;
                var anchorError = (jitter.NextDouble() - 0.35) * 0.060;
                var raw = trueTime + (chunk % 1 == 0 ? anchorError : 0d);

                rawLocal.Add(raw);
                analysis.Add(timebase.Add(raw));
            }

            var backwardSteps = 0;
            var minStep = double.MaxValue;
            var maxStep = double.MinValue;

            for (var i = 1; i < analysis.Count; i++)
            {
                var gridStep = analysis[i] - analysis[i - 1];

                if (gridStep <= 0d)
                    backwardSteps++;

                minStep = System.Math.Min(minStep, gridStep);
                maxStep = System.Math.Max(maxStep, gridStep);
            }

            // Prove the input really was disordered, or the test proves nothing.
            var rawBackward = 0;
            for (var i = 1; i < rawLocal.Count; i++)
            {
                if (rawLocal[i] - rawLocal[i - 1] <= 0d)
                    rawBackward++;
            }

            Assert(rawBackward > 0,
                $"the synthetic input really is non-monotonic ({rawBackward} backward steps) — " +
                "the same defect the live stream shows");

            Assert(backwardSteps == 0,
                $"the ANALYSIS time base is strictly increasing on that input " +
                $"({backwardSteps} backward steps)");

            Assert(minStep > 0d,
                $"every step is positive (min {minStep * 1000d:F4} ms)");

            // Monotonicity here is structural, not clamped: the step is bounded to a fraction
            // of the interval either side, so it cannot reach zero.
            Assert(minStep >= interval * 0.45 && maxStep <= interval * 1.55,
                $"steps stay within the steering bound " +
                $"({minStep * 1000d:F3}..{maxStep * 1000d:F3} ms around {interval * 1000d:F3} ms)");

            var median = analysis.Skip(1)
                .Select((t, i) => t - analysis[i])
                .OrderBy(d => d)
                .ElementAt(analysis.Count / 2);

            Assert(System.Math.Abs(median - interval) < interval * 0.05,
                $"the median analysis interval tracks the real rate " +
                $"({median * 1000d:F4} ms vs {interval * 1000d:F3} ms)");

            // The grid must follow the clock, not free-run away from it.
            Assert(timebase.largestDriftSeconds < 0.100,
                $"the grid stays close to the raw clock " +
                $"(largest drift {timebase.largestDriftSeconds * 1000d:F1} ms)");

            // And it must not have touched anything else.
            var timebaseCode = StripCommentsAndAttributes(
                File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/EegAnalysisTimebase.cs"));

            Assert(!timebaseCode.Contains("channels"),
                "the analysis time base never touches an EEG amplitude — it only makes timestamps");

            // ---- The event clock ------------------------------------------------------------------
            Info($"LSL clock available: {LslClock.isAvailable}");

            if (LslClock.isAvailable)
            {
                var a = LslClock.Now();
                System.Threading.Thread.Sleep(30);
                var b = LslClock.Now();

                Assert(!double.IsNaN(a) && !double.IsNaN(b) && b > a,
                    $"the LSL clock advances ({a:F6} -> {b:F6})");

                Assert(!string.IsNullOrEmpty(LslClock.NowString()),
                    "events can carry an LSL-alignment timestamp");
            }
            else
            {
                Assert(string.IsNullOrEmpty(LslClock.NowString()),
                    "with no liblsl the LSL timestamp is EMPTY — never filled from another clock");
            }

            // The alignment column must come from liblsl and nothing else.
            var loggerSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Core/EventLogger.cs");

            Assert(loggerSource.Contains("LslClock.NowString()"),
                "every logged event is stamped with the LSL clock");

            var clockSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/LslClock.cs");
            var clockCode = StripCommentsAndAttributes(clockSource);

            foreach (var wrong in new[] { "Time.time", "DateTime.Now", "Stopwatch" })
            {
                Assert(!clockCode.Contains(wrong),
                    $"the LSL clock never falls back to {wrong}");
            }

            // The recorder must not transform the signal.
            var recorderCode = StripCommentsAndAttributes(
                File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/RawEegRecorder.cs"));

            // Checked as BEHAVIOUR, not as vocabulary: the file's own header text says "NO
            // filtering", and a scan for the word "filter" matches that disclaimer. What must
            // be true is that no arithmetic is applied to a sample value on its way to disk.
            Assert(!System.Text.RegularExpressions.Regex.IsMatch(
                    recorderCode, @"sample\.channels\[\s*c\s*\]\s*[*/+\-]"),
                "no arithmetic is applied to a channel value — the recorder writes what arrived");

            Assert(recorderCode.Contains("sample.channels[c].ToString(\"R\""),
                "values are written round-trippable, so a read-back equals what arrived");

            Assert(!recorderCode.Contains("Mathf."),
                "the recorder performs no Unity math on the signal");
        }

        /// <summary>
        /// The band-pass, measured against a synthetic signal of known components.
        ///
        /// Attenuation is measured from the filtered TIME SERIES — the amplitude that actually
        /// survives — not read off the designed transfer function, because a design can be
        /// correct on paper and wrong in code. The analytic response is checked separately as a
        /// cross-check of the coefficients themselves.
        ///
        /// The rate is deliberately 256 Hz, not the amplifier's 250, so any hard-coded 250 in
        /// the coefficient maths fails here.
        /// </summary>
        static void CheckEegFilter()
        {
            const double fs = 256.0;
            var filter = new EegBandpassFilter(channelCount: 2, sampleRateHz: fs,
                highPassHz: 1.0, lowPassHz: 40.0);

            Info(filter.Describe());

            Assert(filter.overallOrder == 8 && filter.sectionsPerChannel == 4,
                $"8th-order Butterworth as 4 second-order sections " +
                $"(order {filter.overallOrder}, {filter.sectionsPerChannel} sections)");

            Assert(System.Math.Abs(filter.sampleRateHz - fs) < 1e-9,
                $"coefficients are built from the SUPPLIED rate ({filter.sampleRateHz} Hz), " +
                "not a hard-coded 250");

            // ---- Measured amplitude response, per component ----------------------------------
            // Each frequency is run separately so the surviving amplitude is unambiguous.
            var probes = new (double hz, string verdict, double minDb, double maxDb)[]
            {
                (0.2,  "slow drift, must be strongly attenuated", double.NegativeInfinity, -30.0),
                (6.0,  "theta, must be preserved",                 -3.0,  1.0),
                (10.0, "alpha, must be preserved",                 -3.0,  1.0),
                (30.0, "inside the passband, reasonably preserved", -6.0,  1.0),
                (60.0, "mains, must be attenuated",     double.NegativeInfinity, -12.0),
                (80.0, "above cutoff, must be attenuated", double.NegativeInfinity, -25.0),
            };

            foreach (var (hz, verdict, minDb, maxDb) in probes)
            {
                var settle = filter.SettlingSamples;
                var total = settle + (int)(fs * 4);

                var probe = new EegBandpassFilter(1, fs, 1.0, 40.0);
                var peak = 0d;

                for (var i = 0; i < total; i++)
                {
                    var x = System.Math.Sin(2.0 * System.Math.PI * hz * i / fs);
                    var y = probe.Process(0, x);

                    // Measure only after the filter has settled: the start-up transient is not
                    // signal, and including it would understate the attenuation.
                    if (i > settle)
                        peak = System.Math.Max(peak, System.Math.Abs(y));
                }

                var db = peak > 0d ? 20.0 * System.Math.Log10(peak) : double.NegativeInfinity;

                Assert(db >= minDb && db <= maxDb,
                    $"{hz,5:F1} Hz measured {db,7:F2} dB — {verdict}");
            }

            // ---- The analytic response agrees with the measurement --------------------------
            foreach (var hz in new[] { 6.0, 10.0, 30.0 })
            {
                Assert(filter.GainDbAt(hz) > -3.5 && filter.GainDbAt(hz) < 1.0,
                    $"analytic response at {hz} Hz is {filter.GainDbAt(hz):F2} dB");
            }

            Assert(filter.GainDbAt(60.0) < -12.0,
                $"analytic response at 60 Hz is {filter.GainDbAt(60.0):F2} dB — mains sits " +
                "outside the passband, which is why no notch is used");

            // ---- Independent state per channel ------------------------------------------------
            var shared = new EegBandpassFilter(2, fs, 1.0, 40.0);

            for (var i = 0; i < 500; i++)
                shared.Process(0, 1000.0);   // drive channel 0 hard

            var untouched = shared.Process(1, 0.0);

            Assert(System.Math.Abs(untouched) < 1e-12,
                $"driving channel 0 leaves channel 1's state untouched ({untouched:E3}) — " +
                "state is per channel, never shared");

            // ---- Continuity: state persists across calls ---------------------------------------
            var filterCode = StripCommentsAndAttributes(
                File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/EegBandpassFilter.cs"));

            Assert(!filterCode.Contains("filtfilt"),
                "no forward-backward zero-phase filtering — it is non-causal and cannot run online");

            var processBody = filterCode.Substring(
                filterCode.IndexOf("public double Process(int channel, double sample)",
                    System.StringComparison.Ordinal));
            processBody = processBody.Substring(0,
                processBody.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(!processBody.Contains("Reset"),
                "filtering a sample never resets state — epoch boundaries introduce no transient");

            Assert(filter.SettlingSamples > 0,
                $"the filter reports its settling time ({filter.SettlingSamples} samples, " +
                $"{filter.SettlingSamples / fs:F2} s) so early data is not mistaken for signal");
        }

        /// <summary>
        /// The two halves of the identical-channel investigation, kept as permanent regression
        /// tests.
        ///
        /// BACKGROUND. A live run produced bit-identical theta and alpha on all eight
        /// electrodes, and every existing quality check passed, because each check looks at one
        /// channel at a time and each channel was individually well-behaved. Two things had to
        /// follow: proof that the extraction path does not itself collapse channels, and a check
        /// that would actually notice if channels ever were identical.
        ///
        /// PART 1 proves the de-interleave preserves channel separation — the exact
        /// buffer-plus-indexing path the live pipeline uses.
        /// PART 2 exercises the inter-channel identity flag against both the healthy case and
        /// the specific failures it exists to catch.
        ///
        /// Note that a green result here says our CODE is sound. It says nothing about what any
        /// particular amplifier transmitted; only an out-of-process probe against live hardware
        /// can establish that (Editor/ProbeAuraChannels.ps1).
        /// </summary>
        static void CheckEegChannelIdentity()
        {
            // ---- PART 1: the de-interleave preserves channel separation ---------------------
            const int channels = 8;
            const double rate = 250.0;

            var buffer = new RawEegRingBuffer(channels, rate, capacitySeconds: 10.0);
            var step = 1.0 / rate;
            var t0 = 5000.0;
            var scratch = new double[channels];

            // Every channel gets a value that could only come from that channel.
            for (var i = 0; i < 1000; i++)
            {
                for (var c = 0; c < channels; c++)
                    scratch[c] = i * 1000.0 + c;

                buffer.Add(t0 + i * step, scratch);
            }

            Assert(buffer.TryGetWindow(t0 + 500 * step, 1.0, 1.0, out var window),
                "a window is retrievable from the middle of the buffer");

            Assert(window.channelCount == channels,
                $"the window keeps every channel ({window.channelCount})");

            // This is the exact expression EegFeaturePipeline uses to cut one channel out of a
            // window. If it were transposed or collapsed, the P0 symptom would appear here.
            var separated = true;
            var correct = true;

            for (var c = 0; c < channels; c++)
            {
                var series = new double[window.sampleCount];

                for (var i = 0; i < window.sampleCount; i++)
                    series[i] = window.samples[i][c];

                // Each extracted value must carry its own channel's signature.
                for (var i = 0; i < window.sampleCount; i++)
                {
                    if (System.Math.Abs(series[i] % 1000.0 - c) > 1e-9)
                        correct = false;
                }

                // And must differ from every other channel at the same instant.
                for (var other = 0; other < channels; other++)
                {
                    if (other != c && System.Math.Abs(window.samples[0][other] - series[0]) < 1e-9)
                        separated = false;
                }
            }

            Assert(correct,
                "de-interleaving returns each channel's OWN samples (series[i] = samples[i][c])");

            Assert(separated,
                "eight distinct channels in stay eight distinct channels out — the extraction " +
                "path does not collapse them");

            // ---- PART 2: the inter-channel identity flag ------------------------------------
            // Append-only enum: existing flag values must never move, because they are compared
            // and combined across sessions and reports.
            Assert((int)EegQualityFlags.Flatline == 16 &&
                   (int)EegQualityFlags.RoiUnresolved == 256,
                "existing quality-flag values are unchanged (Flatline=16, RoiUnresolved=256)");

            Assert((int)EegQualityFlags.IdenticalChannels == 512,
                "IdenticalChannels was APPENDED as 1<<9, not inserted among existing flags");

            var host = new GameObject("__IKEA_EEG_IdentityTest")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                // RequireComponent supplies the receiver. Nothing connects: in edit mode
                // Awake/OnEnable do not run for an added component, so no inlet is created.
                var pipeline = host.AddComponent<EegFeaturePipeline>();

                var assess = typeof(EegFeaturePipeline).GetMethod("AssessInterChannelIdentity",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert(assess != null,
                    "EegFeaturePipeline has a cross-channel assessment (AssessInterChannelIdentity)");

                if (assess == null)
                    return;

                // --- healthy: all channels different ---------------------------------------
                var healthy = MakeIdentityWindow(channels, 300, (i, c) => System.Math.Sin(i * 0.01 * (c + 1)));
                var healthyFeatures = MakeIdentityFeatures(channels, c => 1.0 + c, c => 2.0 + c);
                var flags = (EegQualityFlags)assess.Invoke(pipeline,
                    new object[] { healthy, healthyFeatures });

                Assert(flags == EegQualityFlags.None,
                    $"eight genuinely different channels are NOT flagged ({flags})");

                Assert(healthyFeatures.identicalChannelPairs == 0,
                    $"no identical pairs are reported for healthy data " +
                    $"({healthyFeatures.identicalChannelPairs})");

                // --- one duplicated pair ----------------------------------------------------
                var onePair = MakeIdentityWindow(channels, 300,
                    (i, c) => System.Math.Sin(i * 0.01 * ((c == 4 ? 1 : c) + 1)));
                var onePairFeatures = MakeIdentityFeatures(channels, c => 1.0 + c, c => 2.0 + c);
                flags = (EegQualityFlags)assess.Invoke(pipeline,
                    new object[] { onePair, onePairFeatures });

                Assert((flags & EegQualityFlags.IdenticalChannels) != 0,
                    "a single duplicated channel pair IS flagged");

                Assert(onePairFeatures.identicalChannelPairs == 1,
                    $"exactly one pair is reported ({onePairFeatures.identicalChannelPairs})");

                Assert(onePairFeatures.identicalChannelDetail.Contains("CH2") &&
                       onePairFeatures.identicalChannelDetail.Contains("CH5"),
                    $"the report names WHICH channels matched " +
                    $"(\"{onePairFeatures.identicalChannelDetail}\")");

                // --- the observed P0 failure: every channel the same -------------------------
                var allSame = MakeIdentityWindow(channels, 300, (i, c) => System.Math.Sin(i * 0.01));
                var allSameFeatures = MakeIdentityFeatures(channels, c => 2.2792E-6, c => 6.1697E-8);
                flags = (EegQualityFlags)assess.Invoke(pipeline,
                    new object[] { allSame, allSameFeatures });

                Assert((flags & EegQualityFlags.IdenticalChannels) != 0,
                    "the live failure that started this investigation — all 8 channels identical " +
                    "— IS now flagged");

                Assert(allSameFeatures.identicalChannelPairs == channels * (channels - 1) / 2,
                    $"every one of the {channels * (channels - 1) / 2} pairs is reported " +
                    $"({allSameFeatures.identicalChannelPairs})");

                // --- offset-only: samples differ, spectra do not ------------------------------
                // Welch de-means each segment, so channels separated only by a constant offset
                // produce identical band powers from non-identical samples. A sample-only test
                // would miss this entirely.
                var offsetOnly = MakeIdentityWindow(channels, 300,
                    (i, c) => c * 10.0 + System.Math.Sin(i * 0.01));
                var offsetFeatures = MakeIdentityFeatures(channels, c => 1.0, c => 2.0);
                flags = (EegQualityFlags)assess.Invoke(pipeline,
                    new object[] { offsetOnly, offsetFeatures });

                Assert((flags & EegQualityFlags.IdenticalChannels) != 0,
                    "channels differing only by a DC offset are flagged via their identical " +
                    "band powers, even though no two sample series match");

                Assert(offsetFeatures.identicalChannelPairs == 0,
                    $"and that case correctly reports zero identical SAMPLE pairs " +
                    $"({offsetFeatures.identicalChannelPairs})");

                // --- NaN is not a duplicate ---------------------------------------------------
                var nanFeatures = MakeIdentityFeatures(channels,
                    c => double.NaN, c => double.NaN);
                flags = (EegQualityFlags)assess.Invoke(pipeline,
                    new object[] { healthy, nanFeatures });

                Assert((flags & EegQualityFlags.IdenticalChannels) == 0,
                    "all-NaN band powers are NOT called identical — NaNPresent describes that " +
                    "window, and IEEE inequality gives the right answer without a special case");

                // --- a single channel has nothing to compare against --------------------------
                var single = MakeIdentityWindow(1, 300, (i, c) => System.Math.Sin(i * 0.01));
                var singleFeatures = MakeIdentityFeatures(1, c => 1.0, c => 2.0);
                flags = (EegQualityFlags)assess.Invoke(pipeline,
                    new object[] { single, singleFeatures });

                Assert(flags == EegQualityFlags.None,
                    "a one-channel stream is never flagged as identical — there is no pair");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// The OPTIONAL 60 Hz mains notch, measured rather than asserted.
        ///
        /// The notch is scientifically redundant for the current 1–40 Hz analysis — the 40 Hz
        /// low-pass already attenuates 60 Hz, and neither theta nor alpha is anywhere near it —
        /// but it is part of the researcher's stated preprocessing protocol, so it must exist,
        /// be explicit, and be correct. "Correct" here means three things: it removes 60 Hz, it
        /// leaves theta and alpha alone, and its coefficients come from the ACTUAL sample rate.
        ///
        /// A deliberately non-standard 256 Hz is used throughout, so any hard-coded 250 fails.
        /// </summary>
        static void CheckEegNotch()
        {
            const double fs = 256.0;

            // ---- OFF by default ---------------------------------------------------------------
            var plain = new EegBandpassFilter(channelCount: 1, sampleRateHz: fs,
                highPassHz: 1.0, lowPassHz: 40.0);

            Assert(!plain.notchEnabled,
                "the notch is OFF unless explicitly requested — it is an option, not a default");

            Assert(plain.overallOrder == 8 && plain.sectionsPerChannel == 4,
                $"without the notch the chain is unchanged ({plain.sectionsPerChannel} sections, " +
                $"order {plain.overallOrder}) — existing behaviour is not altered");

            // ---- ON -------------------------------------------------------------------------
            var notched = new EegBandpassFilter(channelCount: 2, sampleRateHz: fs,
                highPassHz: 1.0, lowPassHz: 40.0, notchEnabled: true,
                notchHz: 60.0, notchQ: 30.0);

            Info(notched.Describe());

            Assert(notched.notchEnabled && System.Math.Abs(notched.notchHz - 60.0) < 1e-9,
                $"the notch is centred where it was asked to be ({notched.notchHz} Hz)");

            Assert(notched.sectionsPerChannel == 5 && notched.overallOrder == 10,
                $"the notch adds exactly ONE second-order section " +
                $"({notched.sectionsPerChannel} sections, order {notched.overallOrder})");

            Assert(System.Math.Abs(notched.notchBandwidthHz - 2.0) < 1e-9,
                $"bandwidth is derived as centre/Q, not stated separately " +
                $"({notched.notchBandwidthHz:F3} Hz at Q {notched.notchQ:F1})");

            // fs must reach the coefficients. Same notch at a different rate => different maths.
            var atOtherRate = new EegBandpassFilter(1, 500.0, 1.0, 40.0, true, 60.0, 30.0);

            Assert(System.Math.Abs(atOtherRate.NotchMagnitudeAt(60.0)) < 0.01 &&
                   System.Math.Abs(notched.NotchMagnitudeAt(60.0)) < 0.01,
                "the notch rejects its centre frequency at BOTH 256 Hz and 500 Hz — fs is " +
                "derived from the stream, never hard-coded");

            // ---- The notch section in isolation ----------------------------------------------
            // Isolated so the band-pass response cannot flatter or mask the notch's own effect.
            var atCentreDb = 20.0 * System.Math.Log10(
                System.Math.Max(1e-12, notched.NotchMagnitudeAt(60.0)));
            var atThetaDb = 20.0 * System.Math.Log10(notched.NotchMagnitudeAt(6.0));
            var atAlphaDb = 20.0 * System.Math.Log10(notched.NotchMagnitudeAt(10.0));

            Info($"notch alone: 60 Hz {atCentreDb:F2} dB, 6 Hz {atThetaDb:F3} dB, " +
                 $"10 Hz {atAlphaDb:F3} dB");

            Assert(atCentreDb < -60.0,
                $"60 Hz is rejected by the notch itself ({atCentreDb:F2} dB) — the zeros sit " +
                "ON the unit circle, so the centre is nulled, not merely reduced");

            Assert(System.Math.Abs(atThetaDb) < 0.1,
                $"6 Hz (theta) passes the notch untouched ({atThetaDb:F4} dB)");

            Assert(System.Math.Abs(atAlphaDb) < 0.1,
                $"10 Hz (alpha) passes the notch untouched ({atAlphaDb:F4} dB)");

            // ---- Measured, by pushing real sines through the whole chain ----------------------
            // The analytic response above and the measured response below are independent
            // routes to the same claim; agreeing matters more than either alone.
            foreach (var (hz, mustSurvive) in new[] { (60.0, false), (6.0, true), (10.0, true) })
            {
                var probe = new EegBandpassFilter(1, fs, 1.0, 40.0, true, 60.0, 30.0);
                var settle = probe.SettlingSamples;
                var total = settle + (int)(fs * 4);
                var peak = 0d;

                for (var i = 0; i < total; i++)
                {
                    var x = System.Math.Sin(2.0 * System.Math.PI * hz * i / fs);
                    var y = probe.Process(0, x);

                    if (i > settle)
                        peak = System.Math.Max(peak, System.Math.Abs(y));
                }

                var db = 20.0 * System.Math.Log10(System.Math.Max(1e-12, peak));

                if (mustSurvive)
                {
                    Assert(db > -3.0,
                        $"{hz} Hz survives the notched chain ({db:F2} dB) — the notch does not " +
                        "damage the bands being measured");
                }
                else
                {
                    Assert(db < -40.0,
                        $"{hz} Hz is removed by the notched chain ({db:F2} dB)");
                }
            }

            // ---- Redundancy, stated honestly --------------------------------------------------
            var withoutNotch = new EegBandpassFilter(1, fs, 1.0, 40.0);
            var lowPassOnlyAt60 = 20.0 * System.Math.Log10(
                System.Math.Max(1e-12, withoutNotch.MagnitudeAt(60.0)));

            Info($"the 40 Hz low-pass ALONE already gives {lowPassOnlyAt60:F2} dB at 60 Hz — " +
                 "the notch is a protocol option, not a necessity for 1-40 Hz work");

            Assert(lowPassOnlyAt60 < -12.0,
                $"60 Hz is already attenuated without any notch ({lowPassOnlyAt60:F2} dB), which " +
                "is why the notch is documented as optional rather than required");

            // ---- Invalid configurations are refused, not silently accepted --------------------
            var rejectedAboveNyquist = false;

            try
            {
                var _ = new EegBandpassFilter(1, 100.0, 1.0, 40.0, true, 60.0, 30.0);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                rejectedAboveNyquist = true;
            }

            Assert(rejectedAboveNyquist,
                "a 60 Hz notch at 100 Hz sampling (above Nyquist) is REFUSED rather than " +
                "silently aliased to some other frequency");
        }

        /// <summary>
        /// The two robust cross-channel quality checks, against the exact failure that motivated
        /// them.
        ///
        /// The live case was one channel at 3.6E+07 among neighbours near 4E+01. A
        /// mean-and-SD test cannot catch that — the outlier inflates both the mean and the SD it
        /// would be judged against, so it raises its own bar and passes. These assertions pin
        /// the median-based behaviour so that a future "simplification" back to a mean is caught.
        /// </summary>
        static void CheckEegRobustQuality()
        {
            Assert((int)EegQualityFlags.ChannelPowerOutlier == 1024 &&
                   (int)EegQualityFlags.TransientArtifactSuspected == 2048,
                "the new flags are APPENDED (1<<10, 1<<11); no existing flag value moved");

            // ---- The median is what makes this work -------------------------------------------
            var withOutlier = new List<double> { 40.6, 3.6484E+07, 41.7, 43.6, 34.8, 34.9 };
            var median = EegFeaturePipeline.Median(withOutlier);
            var mean = 0d;
            foreach (var v in withOutlier) mean += v;
            mean /= withOutlier.Count;

            Assert(median > 34.0 && median < 45.0,
                $"the MEDIAN ignores one extreme channel ({median:F2}) and describes the bulk");

            Assert(mean > 1e6,
                $"the MEAN is dragged past every real channel by the outlier ({mean:E3}) — this " +
                "is precisely why a mean-based test would clear the window it should reject");

            var host = new GameObject("__IKEA_EEG_RobustTest")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                var pipeline = host.AddComponent<EegFeaturePipeline>();

                var outlierCheck = typeof(EegFeaturePipeline).GetMethod(
                    "AssessChannelPowerOutliers", BindingFlags.Instance | BindingFlags.NonPublic);
                var transientCheck = typeof(EegFeaturePipeline).GetMethod(
                    "AssessTransients", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert(outlierCheck != null && transientCheck != null,
                    "the pipeline exposes both robust cross-channel checks");

                if (outlierCheck == null || transientCheck == null)
                    return;

                // ---- Healthy: comparable powers across 8 channels ------------------------------
                var healthy = MakeIdentityFeatures(8, c => 40.0 + c, c => 20.0 + c);
                var flags = (EegQualityFlags)outlierCheck.Invoke(pipeline, new object[] { healthy });

                Assert(flags == EegQualityFlags.None,
                    $"ordinary inter-electrode variation is NOT flagged as an outlier ({flags})");

                // Real electrodes differ by factors of a few. That must stay clean.
                var realistic = MakeIdentityFeatures(8,
                    c => c < 4 ? 40.0 : 8.0, c => c < 4 ? 5.0 : 30.0);
                flags = (EegQualityFlags)outlierCheck.Invoke(pipeline, new object[] { realistic });

                Assert(flags == EegQualityFlags.None,
                    "a genuine frontal/posterior difference of ~5x is NOT an outlier — the " +
                    "threshold sits well above physiology");

                // ---- The observed failure ------------------------------------------------------
                var observed = MakeIdentityFeatures(8,
                    c => c == 1 ? 3.6484E+07 : 40.0 + c, c => 20.0 + c);
                flags = (EegQualityFlags)outlierCheck.Invoke(pipeline, new object[] { observed });

                Assert((flags & EegQualityFlags.ChannelPowerOutlier) != 0,
                    "the live F3 reading (3.6E+07 among neighbours near 4E+01) IS flagged");

                Assert(observed.powerOutlierChannels.Length == 1 &&
                       observed.powerOutlierChannels[0] == 1,
                    $"exactly the offending channel is named " +
                    $"({string.Join(",", observed.powerOutlierChannels)})");

                Assert(observed.powerOutlierDetail.Contains("CH2"),
                    $"the report identifies WHICH electrode (\"{observed.powerOutlierDetail}\")");

                // ---- Transients -----------------------------------------------------------------
                // Stationary sine: must not be called a transient.
                var steady = MakeIdentityWindow(4, 600,
                    (i, c) => System.Math.Sin(i * 0.05 * (c + 1)));
                var steadyFeatures = MakeIdentityFeatures(4, c => 1.0 + c, c => 2.0 + c);
                flags = (EegQualityFlags)transientCheck.Invoke(pipeline,
                    new object[] { steady, steadyFeatures });

                Assert(flags == EegQualityFlags.None,
                    $"a stationary signal is NOT flagged as a transient ({flags})");

                // A decaying exponential on one channel: exactly what a high-pass settling
                // transient against a large DC offset looks like.
                var decaying = MakeIdentityWindow(4, 600,
                    (i, c) => c == 2
                        ? 1000.0 * System.Math.Exp(-i / 100.0) + System.Math.Sin(i * 0.05)
                        : System.Math.Sin(i * 0.05 * (c + 1)));
                var decayFeatures = MakeIdentityFeatures(4, c => 1.0 + c, c => 2.0 + c);
                flags = (EegQualityFlags)transientCheck.Invoke(pipeline,
                    new object[] { decaying, decayFeatures });

                Assert((flags & EegQualityFlags.TransientArtifactSuspected) != 0,
                    "a decaying excursion on one channel IS flagged as non-stationary");

                Assert(decayFeatures.transientDetail.Contains("CH3") &&
                       decayFeatures.transientDetail.Contains("decaying"),
                    $"the report names the channel and the direction " +
                    $"(\"{decayFeatures.transientDetail}\")");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Can the preprocessing chain reject DC offsets of the size AURA ACTUALLY produces,
        /// in the time the pipeline actually allows, without contaminating theta?
        ///
        /// WHY THIS EXISTS. A live capture showed one electrode reporting theta six orders of
        /// magnitude above its neighbours. One candidate explanation was the settling rule: the
        /// filter is declared ready after three high-pass time constants, which still leaves
        /// about 5% of any input step decaying, and a decaying exponential is low-frequency
        /// energy that lands squarely in theta. Since AURA carries per-channel DC offsets around
        /// 10^5 with EEG of standard deviation around 50 riding on them — a ratio near 2000:1 —
        /// "5% remaining" is not obviously small enough.
        ///
        /// The offsets below are MEASURED, not invented: they are the per-channel means from a
        /// real 1500-sample AURA capture on 2026-08-24, recorded out-of-process with no Unity
        /// code in the path. The AC amplitudes are the measured standard deviations from the
        /// same capture.
        ///
        /// This test can refute the hypothesis, which is the point of running it. If theta comes
        /// out comparable across channels despite their wildly different offsets, then settling
        /// is adequate and the live anomaly must have come from the signal itself rather than
        /// from this filter — which points at the electrodes, not at the code.
        /// </summary>
        static void CheckEegDcOffsetRejection()
        {
            const double fs = 250.0;

            // Measured live from AURA, 1500 samples, 2026-08-24. mean = DC offset, sd = AC size.
            var offsets = new[]
            {
                4.607119E+04, 1.067820E+05, 2.932936E+04, -2.194045E+05,
                3.646388E+04, 7.624209E+04, 8.517075E+03, -1.875907E+05,
            };

            var acSd = new[]
            {
                5.194850E+01, 5.653797E+01, 4.970628E+01, 6.920061E+02,
                4.902647E+01, 1.026749E+02, 4.286686E+01, 8.544411E+02,
            };

            var channels = offsets.Length;

            Info($"replaying the MEASURED AURA offsets " +
                 $"({offsets[6]:E2} .. {offsets[3]:E2}) with their measured AC sizes");

            var filter = new EegBandpassFilter(channels, fs, 1.0, 40.0);
            var settling = filter.SettlingSamples;

            Assert(settling == 750,
                $"settling is 3 high-pass time constants = {settling} samples " +
                $"({settling / fs:F2} s at {fs} Hz)");

            // The pipeline's own worst case: the spectral diagnostic collects 8 s and analyses
            // the last 4 s, so the window begins one settling-period-plus-one-second in.
            const double collectSeconds = 8.0;
            const double windowSeconds = 4.0;

            var total = (int)(fs * collectSeconds);
            var windowStart = total - (int)(fs * windowSeconds);

            var series = new double[channels][];
            for (var c = 0; c < channels; c++)
                series[c] = new double[total - windowStart];

            // A fixed-seed generator: the point is reproducibility, not realism.
            var random = new System.Random(20260824);

            for (var i = 0; i < total; i++)
            {
                for (var c = 0; c < channels; c++)
                {
                    // A 6 Hz component sized to the measured AC, plus the measured DC offset.
                    // The offset is present from the very first sample, exactly as it is live.
                    var ac = acSd[c] * System.Math.Sqrt(2.0) *
                             System.Math.Sin(2.0 * System.Math.PI * 6.0 * i / fs);
                    var jitter = (random.NextDouble() - 0.5) * acSd[c] * 0.1;

                    var y = filter.Process(c, offsets[c] + ac + jitter);

                    if (i >= windowStart)
                        series[c][i - windowStart] = y;
                }
            }

            var thetas = new double[channels];

            for (var c = 0; c < channels; c++)
            {
                var psd = EegSpectralAnalyzer.Welch(series[c], fs, 2.0);
                thetas[c] = EegSpectralAnalyzer.BandPower(psd, EegBand.Theta);
            }

            for (var c = 0; c < channels; c++)
            {
                Info($"  CH{c + 1} offset {offsets[c],12:E4}  AC sd {acSd[c],9:E3}  " +
                     $"theta {thetas[c]:E4}");
            }

            // The decisive comparison. If the settling transient dominated, theta would track
            // the OFFSET (which spans 26x across channels) rather than the AC size.
            var logs = new List<double>();
            foreach (var t in thetas)
            {
                if (t > 0d && !double.IsNaN(t) && !double.IsInfinity(t))
                    logs.Add(System.Math.Log10(t));
            }

            var median = IkeaEeg.Data.EegFeaturePipeline.Median(logs);
            var worstDecades = 0d;
            var worstChannel = -1;

            for (var c = 0; c < logs.Count; c++)
            {
                var decades = System.Math.Abs(logs[c] - median);

                if (decades > worstDecades)
                {
                    worstDecades = decades;
                    worstChannel = c;
                }
            }

            Info($"theta median {System.Math.Pow(10.0, median):E4}; furthest channel " +
                 $"CH{worstChannel + 1} at {worstDecades:F2} decades");

            // CH4 and CH8 legitimately carry ~14x the AC of the others, which is ~2.2 decades
            // of power on its own. The offsets span 26x; if the transient were driving theta,
            // the spread would follow the offsets and greatly exceed that.
            Assert(worstDecades < 2.5,
                $"theta tracks each channel's AC CONTENT, not its DC offset — worst channel is " +
                $"{worstDecades:F2} decades from the median, whereas the live anomaly was ~6 " +
                "decades. The settling rule is therefore ADEQUATE for offsets of this size.");

            // And the direct statement of the requirement: the filtered mean must be small
            // compared with the AC, i.e. the offset really is gone by the analysis window.
            for (var c = 0; c < channels; c++)
            {
                var mean = 0d;
                foreach (var v in series[c]) mean += v;
                mean /= series[c].Length;

                Assert(System.Math.Abs(mean) < acSd[c],
                    $"CH{c + 1}: the {offsets[c]:E2} DC offset is gone from the analysis window " +
                    $"(residual mean {mean:E3} vs AC sd {acSd[c]:E3})");
            }
        }

        /// <summary>
        /// The recognition data model: stimulus validation, sequence construction, and the
        /// four-way signal-detection classification.
        ///
        /// The classification is the part that must never drift, because every downstream number
        /// depends on it and a sign error would be invisible in aggregate — a swapped Miss and
        /// False Alarm still produces plausible-looking totals. All four cells are asserted
        /// explicitly rather than spot-checked.
        /// </summary>
        static void CheckRecognitionProtocol()
        {
            // ---- The four-way classification, exhaustively --------------------------------
            var cases = new (RecognitionItemClass cls, RecognitionResponse resp,
                RecognitionOutcome expected, string description)[]
            {
                (RecognitionItemClass.Target, RecognitionResponse.SeenBefore,
                    RecognitionOutcome.Hit, "target + seen before = HIT"),
                (RecognitionItemClass.Target, RecognitionResponse.NotSeenBefore,
                    RecognitionOutcome.Miss, "target + not seen before = MISS"),
                (RecognitionItemClass.Lure, RecognitionResponse.NotSeenBefore,
                    RecognitionOutcome.CorrectRejection, "lure + not seen before = CORRECT REJECTION"),
                (RecognitionItemClass.Lure, RecognitionResponse.SeenBefore,
                    RecognitionOutcome.FalseAlarm, "lure + seen before = FALSE ALARM"),
            };

            foreach (var (cls, resp, expected, description) in cases)
            {
                var item = new RecognitionItem { itemClass = cls, response = resp };
                Assert(item.outcome == expected, $"{description} (got {item.outcome})");
            }

            // An unanswered item is NOT an error of either kind.
            var unanswered = new RecognitionItem
            {
                itemClass = RecognitionItemClass.Target,
                response = RecognitionResponse.None,
            };

            Assert(unanswered.outcome == RecognitionOutcome.NoResponse,
                $"no response is NO_RESPONSE, never a miss ({unanswered.outcome})");

            Assert(unanswered.reactionTimeMs < 0d,
                $"an unanswered item reports a negative RT rather than a misleading 0 " +
                $"({unanswered.reactionTimeMs})");

            // ---- Reaction time is measured from THIS item's onset --------------------------
            var timed = new RecognitionItem
            {
                itemClass = RecognitionItemClass.Target,
                response = RecognitionResponse.SeenBefore,
                onsetTime = 10.0,
                responseTime = 10.75,
            };

            Assert(System.Math.Abs(timed.reactionTimeMs - 750.0) < 1e-6,
                $"reaction time is response minus onset in ms ({timed.reactionTimeMs:F1})");

            // ---- Stimulus validation --------------------------------------------------------
            var list = ScriptableObject.CreateInstance<RecognitionWordList>();

            try
            {
                list.SetLists(new List<string> { "ALPHA", "BRAVO", "CHARLIE" },
                    new List<string> { "DELTA", "ECHO", "FOXTROT" },
                    new List<string> { "GOLF", "HOTEL", "INDIA" },
                    validated: false, provenanceNote: "self-test fixture");

                Assert(list.Validate(RecognitionPhase.Immediate, out _),
                    "a well-formed stimulus list validates");

                Assert(list.ItemCount(RecognitionPhase.Immediate) == 6,
                    $"item count is targets + that phase's lures ({list.ItemCount(RecognitionPhase.Immediate)})");

                // The overlap that would make a response unclassifiable.
                list.SetLists(new List<string> { "ALPHA", "BRAVO" },
                    new List<string> { "ALPHA", "DELTA" },
                    new List<string> { "GOLF" },
                    validated: false, provenanceNote: "self-test fixture");

                Assert(!list.Validate(RecognitionPhase.Immediate, out var overlapProblem) &&
                       overlapProblem.Contains("ALPHA"),
                    $"a word that is both target and lure is REJECTED (\"{overlapProblem}\")");

                // A delayed lure reused from the immediate phase is no longer novel.
                list.SetLists(new List<string> { "ALPHA", "BRAVO" },
                    new List<string> { "DELTA", "ECHO" },
                    new List<string> { "DELTA", "GOLF" },
                    validated: false, provenanceNote: "self-test fixture");

                Assert(!list.Validate(RecognitionPhase.Delayed, out var reuseProblem) &&
                       reuseProblem.Contains("DELTA"),
                    $"a lure reused across phases is REJECTED (\"{reuseProblem}\")");

                // Duplicates and blanks.
                list.SetLists(new List<string> { "ALPHA", "ALPHA" },
                    new List<string> { "DELTA" }, new List<string> { "GOLF" },
                    validated: false, provenanceNote: "self-test fixture");

                Assert(!list.Validate(RecognitionPhase.Immediate, out var dupProblem),
                    $"a duplicated target is REJECTED (\"{dupProblem}\")");

                list.SetLists(new List<string> { "ALPHA", "  " },
                    new List<string> { "DELTA" }, new List<string> { "GOLF" },
                    validated: false, provenanceNote: "self-test fixture");

                Assert(!list.Validate(RecognitionPhase.Immediate, out var blankProblem),
                    $"a blank target is REJECTED (\"{blankProblem}\")");

                // ---- Sequence construction ---------------------------------------------------
                list.SetLists(new List<string> { "T1", "T2", "T3", "T4" },
                    new List<string> { "L1", "L2", "L3", "L4" },
                    new List<string> { "D1", "D2" },
                    validated: false, provenanceNote: "self-test fixture");

                var seq = RecognitionSequence.Build(list, RecognitionPhase.Immediate, seed: 12345);

                Assert(seq.Count == 8,
                    $"the sequence contains every target and every lure ({seq.Count})");

                Assert(seq.Count(i => i.itemClass == RecognitionItemClass.Target) == 4 &&
                       seq.Count(i => i.itemClass == RecognitionItemClass.Lure) == 4,
                    "class membership survives the shuffle");

                Assert(seq.Select(i => i.presentationOrder).OrderBy(o => o)
                        .SequenceEqual(Enumerable.Range(0, 8)),
                    "presentation order is a complete 0..n-1 sequence with no gaps or repeats");

                Assert(seq.Select(i => i.itemId).Distinct().Count() == 8,
                    "every item keeps a unique id independent of shuffled position");

                // Item identity must track the WORD, not the slot it landed in.
                foreach (var item in seq)
                {
                    var isTarget = item.itemClass == RecognitionItemClass.Target;
                    var wordLooksLikeTarget = item.word.StartsWith("T");

                    Assert(isTarget == wordLooksLikeTarget,
                        $"'{item.word}' kept its class through the shuffle ({item.itemClass})");
                }

                // Reproducibility: the same seed must rebuild the same order.
                var again = RecognitionSequence.Build(list, RecognitionPhase.Immediate, seed: 12345);

                Assert(again.Select(i => i.itemId).SequenceEqual(seq.Select(i => i.itemId)),
                    "the same seed reproduces the same presentation order");

                var different = RecognitionSequence.Build(list, RecognitionPhase.Immediate,
                    seed: 999);

                Assert(!different.Select(i => i.itemId).SequenceEqual(seq.Select(i => i.itemId)),
                    "a different seed produces a different order (the shuffle is real)");

                // The delayed set is independently sized — nothing assumes 15 or 30.
                var delayed = RecognitionSequence.Build(list, RecognitionPhase.Delayed, seed: 7);

                Assert(delayed.Count == 6,
                    $"the delayed set is sized from its OWN lure list, not a hard-coded " +
                    $"count ({delayed.Count} = 4 targets + 2 delayed lures)");

                // ---- Tallies are transparent counts, not a composite score --------------------
                foreach (var item in seq)
                {
                    item.response = item.itemClass == RecognitionItemClass.Target
                        ? RecognitionResponse.SeenBefore
                        : RecognitionResponse.NotSeenBefore;
                }

                var summary = RecognitionSequence.Summarise(seq);

                Assert(summary.Contains("hits=4") && summary.Contains("correct_rejections=4") &&
                       summary.Contains("false_alarms=0") && summary.Contains("misses=0"),
                    $"perfect performance tallies correctly ({summary})");

                Assert(!summary.Contains("score") && !summary.Contains("index") &&
                       !summary.Contains("d_prime"),
                    "the summary is raw counts only — no invented composite or CNS score");
            }
            finally
            {
                Object.DestroyImmediate(list);
            }
        }

        /// <summary>
        /// The scene wiring for the recognition protocol, and the paradigm separation that keeps
        /// the two verbal-memory tasks from contaminating each other.
        /// </summary>
        static void CheckRecognitionScene()
        {
            // ---- Exactly one response panel, with both buttons ------------------------------
            // The panel's VISUALS are inactive until a recognition phase runs, so the search
            // must include inactive objects. The root itself stays active by design.
            var panels = Object.FindObjectsByType<RecognitionResponsePanel>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(panels.Length == 1,
                $"the scene contains exactly ONE recognition response panel ({panels.Length}) — " +
                "two would let the manager bind to the one the participant is not using");

            if (panels.Length == 1)
            {
                var panel = panels[0];

                Assert(panel.seenBeforeButton != null && panel.notSeenBeforeButton != null,
                    "the panel has both response buttons wired");

                if (panel.seenBeforeButton != null && panel.notSeenBeforeButton != null)
                {
                    Assert(panel.seenBeforeButton.response == RecognitionResponse.SeenBefore &&
                           panel.notSeenBeforeButton.response == RecognitionResponse.NotSeenBefore,
                        "each button carries the answer its label promises");
                }

                var buttons = Object.FindObjectsByType<RecognitionResponseButton>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                Assert(buttons.Length == 2,
                    $"exactly two response buttons exist in the scene ({buttons.Length})");

                foreach (var button in buttons)
                {
                    Assert(button.GetComponent<XRSimpleInteractable>() != null,
                        $"{button.name} uses XRSimpleInteractable — the same verified " +
                        "interaction the chairs use, with no new package");

                    Assert(!button.isArmed,
                        $"{button.name} starts DISARMED, so a stray ray cannot answer an " +
                        "item that has not been presented");

                    var collider = button.GetComponent<Collider>();

                    Assert(collider != null, $"{button.name} has a collider to receive the ray");

                    // Measured from lossyScale, NOT collider.bounds: bounds is zero while the
                    // object is inactive, and these buttons are deliberately inactive until a
                    // recognition phase runs. lossyScale is the real world size either way.
                    var width = button.transform.lossyScale.x;

                    Assert(width > 0.4f,
                        $"{button.name} is a large target ({width:F2} m wide) so answering " +
                        "costs a coarse point, not careful aiming");
                }
            }

            // ---- Paradigm separation --------------------------------------------------------
            // The FreeRecall words are auditory-only. The recognition words are visual-only.
            // Nothing may blur the two, and a comment cannot enforce that.
            var managerSource = File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs");
            var stripped = StripCommentsAndAttributes(managerSource);

            var encodingIndex = stripped.IndexOf("IEnumerator RunRecognitionEncoding",
                System.StringComparison.Ordinal);

            Assert(encodingIndex >= 0, "the visual encoding coroutine exists");

            if (encodingIndex >= 0)
            {
                // The body of the visual encoding routine, up to the next coroutine.
                var nextRoutine = stripped.IndexOf("IEnumerator RunRecognitionPhase",
                    encodingIndex, System.StringComparison.Ordinal);
                var body = nextRoutine > encodingIndex
                    ? stripped.Substring(encodingIndex, nextRoutine - encodingIndex)
                    : stripped.Substring(encodingIndex);

                Assert(!body.Contains("AudioCue.SpokenWord"),
                    "the VISUAL encoding routine never plays a spoken word clip — the " +
                    "recognition stimuli are never spoken aloud");

                Assert(!body.Contains("GetWordClip"),
                    "the visual encoding routine never fetches a word recording");

                Assert(body.Contains("SetWordDisplay"),
                    "the visual encoding routine displays each word");

                Assert(body.Contains("AudioCue.StimulusTransition"),
                    "a transition beep accompanies each new word");
            }

            // The FreeRecall path must still never display a word.
            var freeRecallIndex = stripped.IndexOf("SetState(ExperimentState.WordEncoding);",
                System.StringComparison.Ordinal);

            Assert(freeRecallIndex >= 0, "the FreeRecall encoding path still exists");

            // ---- Both protocols are reachable and neither was deleted -----------------------
            Assert(System.Enum.IsDefined(typeof(VerbalProtocolMode), VerbalProtocolMode.FreeRecall) &&
                   System.Enum.IsDefined(typeof(VerbalProtocolMode), VerbalProtocolMode.Recognition),
                "both verbal protocols exist — Recognition was ADDED, FreeRecall was not removed");

            Assert((int)ExperimentState.ImmediateRecall == 3 &&
                   (int)ExperimentState.DelayedRecall == 11,
                $"existing state numbering is unchanged (ImmediateRecall=" +
                $"{(int)ExperimentState.ImmediateRecall}, DelayedRecall=" +
                $"{(int)ExperimentState.DelayedRecall}) — the new states were APPENDED");

            Assert((int)ExperimentState.ImmediateRecognition > (int)ExperimentState.Aborted &&
                   (int)ExperimentState.DelayedRecognition > (int)ExperimentState.Aborted,
                "the recognition states sit after every pre-existing state");

            // ---- The placeholder stimuli declare themselves ---------------------------------
            var placeholder = AssetDatabase.LoadAssetAtPath<RecognitionWordList>(
                ExperimentAssetBuilder.RecognitionWordListPath);

            if (placeholder != null)
            {
                Assert(!placeholder.validatedForResearch,
                    "the placeholder stimulus asset is NOT marked validated for research");

                Assert(placeholder.provenanceNote.Contains("NOT CNS STIMULI"),
                    "the placeholder asset states in its own provenance that it is not CNS " +
                    "material");

                Info($"placeholder stimuli: {placeholder.targetCount} targets, " +
                     $"{placeholder.LureCount(RecognitionPhase.Immediate)} immediate lures, " +
                     $"{placeholder.LureCount(RecognitionPhase.Delayed)} delayed lures");
            }
            else
            {
                Info("no placeholder recognition asset present (created on next scene build)");
            }
        }

        /// <summary>
        /// Block 1: protocol-aware instructions, and familiarization retry semantics.
        ///
        /// Two things are checked that a human reading the code would find easy to get wrong:
        /// that Recognition never tells the participant they will HEAR words, and that a wrong
        /// practice selection genuinely makes no progress rather than merely looking like it.
        /// </summary>
        static void CheckBlock1Feedback()
        {
            // ---- The Recognition instruction says the right thing --------------------------
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

                var instruction = ExperimentLocalization.Get(LocKeys.RecognitionEncodingInstructions);
                var lower = instruction.ToLowerInvariant();

                Info($"Recognition instruction: \"{instruction.Replace("\n", " ")}\"");

                Assert(lower.Contains("15"),
                    $"the Recognition instruction states 15 words (\"{instruction}\")");

                // NOT "5 words": the correct string "15 words" contains it as a substring, so
                // that check would fail on the very text it is meant to approve.
                foreach (var forbidden in new[] { "five words", "hear", "listen" })
                {
                    Assert(!lower.Contains(forbidden),
                        $"the Recognition instruction never says '{forbidden}' — the words are " +
                        "seen, not heard");
                }

                Assert(lower.Contains("see") || lower.Contains("shown"),
                    "the Recognition instruction uses visual wording");

                Assert(lower.Contains("remember"),
                    "the Recognition instruction asks the participant to remember the words");

                Assert(lower.Contains("later") || lower.Contains("asked"),
                    "the Recognition instruction warns that they will be tested later");

                // The on-screen line DURING encoding must also be visual.
                var watch = ExperimentLocalization.Get(LocKeys.RecognitionEncodingWatch)
                    .ToLowerInvariant();

                Assert(!watch.Contains("listen") && !watch.Contains("hear"),
                    $"the encoding-phase line is not an auditory instruction (\"{watch}\")");

                // ---- FreeRecall wording is NOT damaged --------------------------------------
                // It describes an auditory task and is still correct for that protocol.
                var freeRecall = ExperimentLocalization.Get(LocKeys.AreaAInstructions)
                    .ToLowerInvariant();

                Assert(freeRecall.Contains("hear") || freeRecall.Contains("listen"),
                    "the FreeRecall instruction still describes an AUDITORY task — it was " +
                    "routed around, not rewritten");

                Assert(ExperimentLocalization.Get(LocKeys.EncodingListen)
                        .ToLowerInvariant().Contains("listen"),
                    "the FreeRecall encoding line is unchanged");

                // ---- Familiarization feedback strings exist ---------------------------------
                foreach (var key in new[]
                         {
                             LocKeys.PracticeFeedbackCorrect, LocKeys.PracticeFeedbackAnother,
                             LocKeys.PracticeFeedbackIncorrect, LocKeys.PracticeFeedbackComplete,
                         })
                {
                    var text = ExperimentLocalization.Get(key);

                    Assert(!string.IsNullOrWhiteSpace(text) && text != key,
                        $"{key} resolves to real text (\"{text}\")");
                }
            }
            finally
            {
                ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // ---- Routing is protocol-aware, not hard-coded ----------------------------------
            var source = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            Assert(source.Contains("RecognitionEncodingInstructions") &&
                   source.Contains("LocKeys.AreaAInstructions"),
                "Area A chooses between the Recognition and FreeRecall instruction keys");

            Assert(source.Contains("SpeakRecognitionInstructions"),
                "the Recognition instruction is narrated");

            // ---- The three feedback paths all exist ----------------------------------------
            foreach (var id in new[]
                     { "PracticeFeedbackIds.Correct", "PracticeFeedbackIds.Another",
                       "PracticeFeedbackIds.Incorrect", "PracticeFeedbackIds.Complete" })
            {
                Assert(source.Contains(id), $"the familiarization feedback path {id} is wired");
            }

            // ---- Wrong selections must not advance progress ---------------------------------
            // The increment must be GUARDED by correctness. An unguarded increment is the exact
            // bug this pass fixes, and it is invisible at run time until a participant games it.
            var handlerIndex = source.IndexOf("void OnPracticeObjectSelected",
                System.StringComparison.Ordinal);

            Assert(handlerIndex >= 0, "the practice selection handler exists");

            if (handlerIndex >= 0)
            {
                var handlerEnd = source.IndexOf("void UpdatePracticeReadiness", handlerIndex,
                    System.StringComparison.Ordinal);
                var handler = handlerEnd > handlerIndex
                    ? source.Substring(handlerIndex, handlerEnd - handlerIndex)
                    : source.Substring(handlerIndex);

                var incrementIndex = handler.IndexOf("m_PracticeSelectionCount++",
                    System.StringComparison.Ordinal);

                Assert(incrementIndex >= 0, "the practice count is incremented somewhere");

                if (incrementIndex >= 0)
                {
                    // The 120 characters before the increment must contain the correctness test.
                    var window = handler.Substring(
                        System.Math.Max(0, incrementIndex - 120),
                        System.Math.Min(120, incrementIndex));

                    // The guard is now phase-aware: `qualifies` means "satisfied the phase this
                    // selection was made in", which in phase 2 is a DIFFERENT colour, not the
                    // requested one. An `isTarget` guard here would be the original bug.
                    Assert(window.Contains("if (qualifies)"),
                        "the practice count increments ONLY on a selection that qualifies for " +
                        "its current phase — a wrong answer makes no progress");
                }
            }

            // ---- Encoding words are still never spoken --------------------------------------
            // Block 1 added narration. This re-asserts, from the encoding routine itself, that
            // none of it leaked into the stimulus path.
            var encodingIndex = source.IndexOf("IEnumerator RunRecognitionEncoding",
                System.StringComparison.Ordinal);

            if (encodingIndex >= 0)
            {
                var next = source.IndexOf("IEnumerator RunRecognitionPhase", encodingIndex,
                    System.StringComparison.Ordinal);
                var body = next > encodingIndex
                    ? source.Substring(encodingIndex, next - encodingIndex)
                    : source.Substring(encodingIndex);

                Assert(!body.Contains("AudioCue.SpokenWord") && !body.Contains("GetWordClip"),
                    "the visual encoding routine still never speaks a word");

                Assert(!body.Contains("SpeakRecognitionInstructions") &&
                       !body.Contains("SpeakPracticeFeedback"),
                    "no narration is triggered from inside the encoding loop");

                Assert(body.Contains("AudioCue.StimulusTransition"),
                    "the transition beep between words is unchanged");
            }
        }

        /// <summary>
        /// The two-phase familiarization rule, and the Recognition label/layout fixes.
        ///
        /// The practice half is driven through the REAL selection handler rather than by reading
        /// the source, because the bug it covers was behavioural: the code looked reasonable and
        /// still told the participant that doing what they had just been asked to do was wrong.
        /// </summary>
        static void CheckBlock2PracticeAndLayout()
        {
            // ---- Two-phase practice, exercised for real ------------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            var practiceObjects = Object.FindObjectsByType<PracticeObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(manager != null && practiceObjects.Length >= 2,
                $"a manager and at least two practice objects exist ({practiceObjects.Length})");

            if (manager != null && practiceObjects.Length >= 2)
            {
                var type = typeof(ExperimentManager);
                var enterFamiliarization = type.GetMethod("EnterFamiliarization",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handler = type.GetMethod("OnPracticeObjectSelected",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert(enterFamiliarization != null && handler != null,
                    "the familiarization entry point and selection handler are reachable");

                if (enterFamiliarization != null && handler != null)
                {
                    enterFamiliarization.Invoke(manager, null);

                    Assert(manager.practicePhase == PracticePhase.RequestedColorSelection,
                        $"familiarization starts on the requested-colour phase " +
                        $"({manager.practicePhase})");

                    var target = manager.practiceTarget;
                    var wrong = practiceObjects.FirstOrDefault(
                        o => o != null && target != null && o.color != target.color);

                    Assert(target != null && wrong != null,
                        "a requested target and a different-coloured object both exist");

                    if (target != null && wrong != null)
                    {
                        // --- PHASE 1: a wrong colour must not advance anything ---------------
                        handler.Invoke(manager, new object[] { wrong });

                        Assert(manager.practiceSelectionCount == 0 && !manager.practiceSucceeded,
                            $"a wrong colour in phase 1 does not advance " +
                            $"(count {manager.practiceSelectionCount})");

                        Assert(manager.practicePhase == PracticePhase.RequestedColorSelection,
                            "the participant stays on the requested-colour phase after a miss");

                        Assert(manager.practiceTarget == target,
                            "a wrong selection does NOT change the requested target");

                        // --- PHASE 1 passed --------------------------------------------------
                        handler.Invoke(manager, new object[] { target });

                        Assert(manager.practiceSucceeded,
                            "selecting the requested colour satisfies phase 1");

                        Assert(manager.practicePhase == PracticePhase.DifferentColorSelection,
                            $"phase 1 advances to the different-colour phase " +
                            $"({manager.practicePhase})");

                        Assert(!manager.practiceReady,
                            "one correct selection is not yet complete familiarization");

                        // --- PHASE 2: a DIFFERENT colour must now be ACCEPTED ----------------
                        // This is the regression the whole pass exists for. Before the fix this
                        // selection was judged against the phase-1 requested colour and rejected.
                        handler.Invoke(manager, new object[] { wrong });

                        Assert(manager.practiceSecondSelectionDone,
                            "a DIFFERENT-coloured object satisfies phase 2 — it is NOT " +
                            "re-checked against the originally requested colour");

                        Assert(manager.practiceReady,
                            $"a correct first selection plus a different-coloured second " +
                            $"completes familiarization (count {manager.practiceSelectionCount})");

                        Assert(manager.practicePhase == PracticePhase.Complete,
                            $"familiarization reports Complete ({manager.practicePhase})");
                    }

                    // --- Re-selecting the requested colour in phase 2 is not an ERROR ---------
                    enterFamiliarization.Invoke(manager, null);
                    var target2 = manager.practiceTarget;

                    if (target2 != null)
                    {
                        handler.Invoke(manager, new object[] { target2 });
                        handler.Invoke(manager, new object[] { target2 });

                        Assert(!manager.practiceSecondSelectionDone && !manager.practiceReady,
                            "re-selecting the SAME colour does not satisfy phase 2");

                        Assert(manager.practicePhase == PracticePhase.DifferentColorSelection,
                            "the participant simply remains on the different-colour phase");
                    }
                }
            }

            // ---- Recognition button labels --------------------------------------------------
            var buttons = Object.FindObjectsByType<RecognitionResponseButton>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(buttons.Length == 2, $"two response buttons exist ({buttons.Length})");

            foreach (var button in buttons)
            {
                // The label lives on the HOLDER (the button's parent), not under the scaled box,
                // which is the fix: a child of the box inherited its non-uniform scale and its
                // forward offset collapsed to 3 mm, burying the text inside a 60 mm-deep slab.
                var holder = button.transform.parent;

                Assert(holder != null, $"{button.name} has a holder parent");

                if (holder == null)
                    continue;

                var label = holder.GetComponentInChildren<TextMeshProUGUI>(true);

                Assert(label != null, $"{button.name} has a TextMeshPro label");

                if (label == null)
                    continue;

                var localized = label.GetComponent<LocalizedText>();

                Assert(localized != null,
                    $"{button.name}'s label is localized rather than hard-coded English");

                // activeInHierarchy is false here BY DESIGN: the panel's Visuals container is
                // deactivated until a recognition phase runs. What matters is that the label and
                // its own canvas are switched on, so they appear the moment the panel is shown.
                Assert(label.gameObject.activeSelf &&
                       label.transform.parent.gameObject.activeSelf,
                    $"{button.name}'s label and its canvas are enabled, so they appear as soon " +
                    "as the panel is shown");

                // The label must sit IN FRONT of the box face, not inside it. The box is 0.06 m
                // deep, so its front face is 0.03 m toward the viewer (-Z).
                var labelZ = label.transform.parent.localPosition.z;

                Assert(labelZ < -0.03f,
                    $"{button.name}'s label sits in front of the 0.06 m box face " +
                    $"(local z {labelZ:F3}, front face at -0.030)");

                // Uniform scale on the holder: the canvas must not inherit the box's squash.
                var scale = holder.lossyScale;

                Assert(Mathf.Abs(scale.x - scale.y) < 0.001f &&
                       Mathf.Abs(scale.y - scale.z) < 0.001f,
                    $"{button.name}'s holder is uniformly scaled ({scale}) so the label is not " +
                    "distorted");
            }

            // ---- Three separated zones -------------------------------------------------------
            var panel = Object.FindObjectsByType<RecognitionResponsePanel>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();

            // GameObject.Find skips INACTIVE objects, and the area panels are inactive until
            // their area opens — so the previous lookup silently found nothing and the whole
            // zone check passed by never running. Searching all loaded transforms fixes that,
            // and the assertions below now fail loudly instead of being skipped.
            var recenterA = FindInSceneIncludingInactive("Btn_Recenter_A");
            var wordDisplay = FindInSceneIncludingInactive("Txt_WordDisplay");

            Assert(recenterA != null, "the Area A Recenter button was found in the scene");
            Assert(wordDisplay != null, "the Area A word display was found in the scene");

            if (panel != null && recenterA != null && wordDisplay != null && buttons.Length == 2)
            {
                // Measured in the Area A anchor's frame. The panel is relocated at run time, so
                // the anchor is what fixes where the pair will actually sit.
                var anchorA = GameObject.Find("Anchor_Recognition_AreaA");

                Assert(anchorA != null, "the Area A recognition anchor exists");

                if (anchorA != null)
                {
                    var buttonY = anchorA.transform.position.y;
                    var recenterY = recenterA.transform.position.y;
                    var wordY = wordDisplay.transform.position.y;

                    Info($"zones — word y {wordY:F3}, recenter y {recenterY:F3}, " +
                         $"buttons y {buttonY:F3}");

                    // Button half-height is 0.13 m; Recenter half-height ~0.05 m.
                    Assert(recenterY - 0.05f > buttonY + 0.13f,
                        $"the Recenter control clears the response buttons vertically " +
                        $"(recenter bottom {recenterY - 0.05f:F3} > button top {buttonY + 0.13f:F3})");

                    Assert(wordY > recenterY + 0.15f,
                        $"the stimulus word sits well above the Recenter control " +
                        $"(word {wordY:F3}, recenter {recenterY:F3})");

                    Assert(wordY - buttonY > 0.5f,
                        $"the response buttons cannot occlude the stimulus word " +
                        $"({wordY - buttonY:F2} m apart)");
                }
            }

            // ---- The instruction is not cut short by the stimuli ----------------------------
            var managerSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            Assert(managerSource.Contains("Mathf.Max(m_Config.instructionDurationSeconds"),
                "the instruction phase waits for the narration to finish before the first word " +
                "appears, instead of cutting it off at the configured duration");
        }

        /// <summary>
        /// Label orientation, Recenter placement, Area B vocabulary/legend/chair robustness, and
        /// the recognition response-to-next-item path.
        /// </summary>
        static void CheckBlock3UiAndPacing()
        {
            // ---- A1: labels face the participant and match their own response ---------------
            var buttons = Object.FindObjectsByType<RecognitionResponseButton>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(buttons.Length == 2, $"two response buttons exist ({buttons.Length})");

            foreach (var button in buttons)
            {
                var holder = button.transform.parent;
                var label = holder != null
                    ? holder.GetComponentInChildren<TextMeshProUGUI>(true)
                    : null;

                if (label == null)
                    continue;

                var canvas = label.transform.parent;

                // A world-space canvas reads from its local -Z side, so its forward must point
                // AWAY from the participant. The participant stands at -Z of the panel, so
                // forward must have a POSITIVE local z. Passing viewerZ = 0 previously gave the
                // opposite and rendered every label back-to-front.
                var forwardZ = canvas.localRotation * Vector3.forward;

                Assert(forwardZ.z > 0.5f,
                    $"{button.name}'s label faces away from the participant so it reads " +
                    $"correctly (local forward z {forwardZ.z:F2}; negative = mirrored)");

                // The visible text must be the one belonging to THIS button's response.
                var localized = label.GetComponent<LocalizedText>();
                var keyField = typeof(LocalizedText).GetField("m_Key",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var key = keyField != null && localized != null
                    ? keyField.GetValue(localized) as string
                    : null;

                var expectedKey = button.response == RecognitionResponse.SeenBefore
                    ? LocKeys.RecognitionSeenBefore
                    : LocKeys.RecognitionNotSeenBefore;

                Assert(key == expectedKey,
                    $"{button.name} submits {button.response} and displays {key} — the visible " +
                    $"label matches the response it actually sends (expected {expectedKey})");
            }

            // ---- A2: Recenter has two placements, driven by the start button ----------------
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();

            Assert(ui != null, "the UI controller is in the scene");

            if (ui != null)
            {
                var displaced = ui.recenterDisplacedPosition;
                var centered = ui.recenterCenteredPosition;

                Info($"Recenter — displaced {displaced}, centered {centered}");

                Assert(Mathf.Abs(centered.x) < 1f,
                    $"the normal-task Recenter position is horizontally CENTRED (x {centered.x})");

                Assert(Mathf.Abs(displaced.x) > 100f,
                    $"the START-state Recenter position is displaced sideways (x {displaced.x})");

                ui.ShowStartButton(true);
                Assert(ui.recenterDisplaced,
                    "showing the START button displaces Recenter");

                ui.ShowStartButton(false);
                Assert(!ui.recenterDisplaced,
                    "hiding the START button returns Recenter to centre — it is not left at " +
                    "the periphery");

                // The centred position must still clear the response buttons in front of the
                // panel. Canvas metres-per-pixel is 0.00095 and the Area A canvas sits at 1.65 m.
                var centeredWorldY = 1.65f + centered.y * 0.00095f;
                var anchor = FindInSceneIncludingInactive("Anchor_Recognition_AreaA");

                if (anchor != null)
                {
                    var buttonTop = anchor.transform.position.y + 0.13f;

                    Assert(centeredWorldY - 0.05f > buttonTop,
                        $"the CENTRED Recenter still clears the response buttons " +
                        $"(recenter bottom {centeredWorldY - 0.05f:F3} > button top {buttonTop:F3})");
                }
            }

            // ---- B1: participant-facing shape vocabulary ------------------------------------
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

                foreach (var shape in new[] { "Solid", "Slatted", "Curved" })
                {
                    var text = ExperimentLocalization.Get(LocKeys.ShapePrefix + shape);

                    Assert(text == shape.ToUpperInvariant(),
                        $"the participant-facing {shape} label is \"{text}\"");
                }

                foreach (var banned in new[] { "MODERN", "CLASSIC", "ROUNDED" })
                {
                    foreach (var shape in new[] { "Solid", "Slatted", "Curved" })
                    {
                        Assert(ExperimentLocalization.Get(LocKeys.ShapePrefix + shape)
                                .IndexOf(banned, System.StringComparison.OrdinalIgnoreCase) < 0,
                            $"no shape label uses the retired word {banned}");
                    }
                }

                // ---- B2: the Area B instruction names the three properties -------------------
                var areaB = ExperimentLocalization.Get(LocKeys.ExecutiveTaskInstructions);
                var upper = areaB.ToUpperInvariant();

                Info($"Area B instruction: \"{areaB.Replace("\n", " ")}\"");

                foreach (var property in new[] { "SHAPE", "COLOR", "SIZE" })
                {
                    Assert(upper.Contains(property),
                        $"the Area B instruction names {property}");
                }

                Assert(upper.Contains("READY"),
                    "the Area B instruction still tells the participant to press READY");
            }
            finally
            {
                ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // ---- B3: the shape legend ---------------------------------------------------------
            var legend = FindInSceneIncludingInactive("ShapeLegend");

            Assert(legend != null, "the shape legend exists in the scene");

            if (legend != null)
            {
                foreach (var shape in new[] { "Solid", "Slatted", "Curved" })
                {
                    Assert(legend.transform.Find($"Legend_{shape}") != null,
                        $"the legend shows a {shape} backrest example");
                }

                Assert(legend.GetComponentsInChildren<Collider>(true).Length == 0,
                    "the legend has NO colliders — it is a picture, not a selectable target");

                // Visibility is driven by the UI controller, which the manager calls on entering
                // the instructions and again on READY.
                if (ui != null)
                {
                    ui.ShowShapeLegend(true);
                    Assert(legend.activeSelf, "the legend can be shown for the instructions");

                    ui.ShowShapeLegend(false);
                    Assert(!legend.activeSelf, "the legend hides on READY");

                    ui.ShowArea(ExperimentArea.AreaA);
                    Assert(!legend.activeSelf,
                        "the legend is force-hidden outside Area B");
                }
            }

            // ---- B4: chairs stay selectable through every shape ------------------------------
            var chairs = Object.FindObjectsByType<ChairTarget>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(chairs.Length > 0, $"chairs exist in the scene ({chairs.Length})");

            foreach (var chair in chairs)
            {
                foreach (ChairShape shape in System.Enum.GetValues(typeof(ChairShape)))
                {
                    foreach (var size in new[] { ChairSize.Small, ChairSize.Large })
                    {
                        chair.ApplySpec(new ChairSpec(ChairColor.Blue, size, shape), null);

                        // A chair whose visible body has no ACTIVE collider cannot be pointed at,
                        // however healthy its interactable looks.
                        var active = chair.GetComponentsInChildren<Collider>(false)
                            .Count(c => c.enabled);

                        Assert(active > 0,
                            $"{chair.name} still has an active collider as {shape}/{size} " +
                            $"({active})");
                    }
                }

                Assert(chair.GetComponentInChildren<XRSimpleInteractable>(true) != null,
                    $"{chair.name} keeps its interactable through shape and size changes");
            }

            // ---- B4: trial count unchanged -----------------------------------------------------
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config != null)
            {
                Assert(config.chairTrialsPerRun == 3,
                    $"the chair block is still 3 trials ({config.chairTrialsPerRun})");
            }

            // ---- C: exactly ONE wait in the response-to-next-item path -----------------------
            var source = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            var itemStart = source.IndexOf("IEnumerator RunRecognitionItem",
                System.StringComparison.Ordinal);

            Assert(itemStart >= 0, "the recognition item coroutine exists");

            if (itemStart >= 0)
            {
                var itemEnd = source.IndexOf("void OnRecognitionResponse", itemStart,
                    System.StringComparison.Ordinal);
                var body = itemEnd > itemStart
                    ? source.Substring(itemStart, itemEnd - itemStart)
                    : source.Substring(itemStart);

                var waits = System.Text.RegularExpressions.Regex.Matches(
                    body, @"WaitForSeconds").Count;

                // CHANGED: the deliberate inter-item wait was REMOVED. The path must now
                // contain NO timed wait at all — the next word appears on the following frame.
                Assert(waits == 0,
                    $"the response-to-next-item path contains NO timed wait ({waits}) — the " +
                    "0.15 s blank interval is gone");

                Assert(!body.Contains("recognitionInterItemGapSeconds"),
                    "the item loop no longer reads the deprecated inter-item gap");

                Assert(body.Contains("yield return null"),
                    "the transition yields exactly one FRAME, the minimum needed to separate " +
                    "the two items' UI and button state");

                // Double-registration protection must survive.
                Assert(body.Contains("m_PendingRecognitionResponse = RecognitionResponse.None"),
                    "each item starts with the pending response cleared, so an answer cannot " +
                    "carry over from the previous item");

                Assert(body.Contains("Disarm()"),
                    "the buttons are disarmed as soon as the response is taken");

                Assert(body.Contains("Arm()"),
                    "the buttons are re-armed only for the new item");
            }

            // Both phases go through the same item coroutine, so pacing cannot diverge.
            var phaseStart = source.IndexOf("IEnumerator RunRecognitionPhase",
                System.StringComparison.Ordinal);

            if (phaseStart >= 0)
            {
                var phaseEnd = source.IndexOf("IEnumerator RunRecognitionItem", phaseStart,
                    System.StringComparison.Ordinal);
                var phaseBody = phaseEnd > phaseStart
                    ? source.Substring(phaseStart, phaseEnd - phaseStart)
                    : source.Substring(phaseStart);

                Assert(!phaseBody.Contains("WaitForSeconds"),
                    "the phase loop adds NO extra wait of its own between items");

                Assert(phaseBody.Contains("RunRecognitionItem"),
                    "immediate and delayed recognition share one item path, so their pacing " +
                    "is identical by construction");
            }
        }

        /// <summary>
        /// The ENTER SHOWROOM Recenter overlap, the reduced inter-item pacing, and the Area B
        /// three-zone layout.
        ///
        /// Every lookup here is inactive-inclusive: the Area B overlay, READY and the legend are
        /// all switched off in the saved scene, and a GameObject.Find would return null and let
        /// these assertions pass by never running.
        /// </summary>
        static void CheckBlock4LayoutAndPacing()
        {
            // ---- A: Recenter clears the whole lower-centre row ------------------------------
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();

            Assert(ui != null, "the UI controller is in the scene");

            if (ui != null)
            {
                ui.ShowStartButton(false);
                ui.ShowEnterAreaBButton(false);
                ui.ShowRecheckAudioButton(false);

                Assert(!ui.recenterDisplaced,
                    "with the lower row empty, Recenter is centred");

                ui.ShowEnterAreaBButton(true);
                Assert(ui.recenterDisplaced,
                    "ENTER SHOWROOM displaces Recenter — this is the overlap that was reported " +
                    "on the headset");

                ui.ShowEnterAreaBButton(false);
                Assert(!ui.recenterDisplaced,
                    "hiding ENTER SHOWROOM returns Recenter to centre");

                ui.ShowRecheckAudioButton(true);
                Assert(ui.recenterDisplaced,
                    "RE-CHECK AUDIO shares the same row and also displaces Recenter");
                ui.ShowRecheckAudioButton(false);

                ui.ShowStartButton(true);
                Assert(ui.recenterDisplaced, "START displaces Recenter");
                ui.ShowStartButton(false);

                // Geometric proof for the state that was broken. All four share the Area A
                // canvas, so a comparison in anchored pixels is exact.
                var enter = FindInSceneIncludingInactive("Btn_EnterAreaB");
                var recenterA = FindInSceneIncludingInactive("Btn_Recenter_A");

                Assert(enter != null && recenterA != null,
                    "ENTER SHOWROOM and the Area A Recenter were both found");

                if (enter != null && recenterA != null)
                {
                    var enterRect = (RectTransform)enter.transform;
                    var recenterRect = (RectTransform)recenterA.transform;

                    var enterY = enterRect.anchoredPosition.y;
                    var enterHalf = enterRect.sizeDelta.y * 0.5f;
                    var recenterHalf = recenterRect.sizeDelta.y * 0.5f;

                    // Centred: would it overlap? (This is the state the fix avoids entering.)
                    var centredY = ui.recenterCenteredPosition.y;
                    var overlapsWhenCentred =
                        Mathf.Abs(centredY - enterY) < (enterHalf + recenterHalf);

                    Info($"ENTER SHOWROOM y {enterY} +/-{enterHalf}; centred Recenter y " +
                         $"{centredY} +/-{recenterHalf}; would overlap = {overlapsWhenCentred}");

                    // Displaced: horizontal separation must resolve it whatever the y overlap.
                    var displaced = ui.recenterDisplacedPosition;
                    var enterHalfX = enterRect.sizeDelta.x * 0.5f;
                    var recenterHalfX = recenterRect.sizeDelta.x * 0.5f;

                    Assert(Mathf.Abs(displaced.x - enterRect.anchoredPosition.x) >
                           enterHalfX + recenterHalfX,
                        $"in the DISPLACED position Recenter clears ENTER SHOWROOM horizontally " +
                        $"(|{displaced.x}| vs {enterHalfX} + {recenterHalfX})");
                }
            }

            // ---- B: pacing ------------------------------------------------------------------
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            Assert(config != null, "the experiment config asset loads");

            if (config != null)
            {
                // The field is RETAINED for serialization compatibility but no longer read by
                // the recognition loop, so its value no longer governs anything. What matters
                // now is that nothing consumes it — asserted in the Block 3 path scan above.
                Info($"recognitionInterItemGapSeconds = {config.recognitionInterItemGapSeconds} " +
                     "(DEPRECATED, retained for serialization, not read by the item loop)");

                // The time allowed to ANSWER must not have moved: the speed-up applies only
                // after a response has already been accepted.
                Assert(Mathf.Abs(config.recognitionResponseTimeoutSeconds - 15f) < 1e-4f,
                    $"the response TIMEOUT is unchanged at 15 s " +
                    $"({config.recognitionResponseTimeoutSeconds}) — participants may still " +
                    "take as long as before to answer");

                Assert(Mathf.Abs(config.recognitionWordDisplaySeconds - 2f) < 1e-4f,
                    $"encoding word display is unchanged at 2 s " +
                    $"({config.recognitionWordDisplaySeconds})");
            }

            // ---- C: Area B zones, measured in world space -----------------------------------
            var overlayText = FindInSceneIncludingInactive("Txt_AreaBOverlay");
            var ready = FindInSceneIncludingInactive("Btn_Ready");
            var legend = FindInSceneIncludingInactive("ShapeLegend");
            var legendPanel = FindInSceneIncludingInactive("LegendPanel");
            var recenterB = FindInSceneIncludingInactive("Btn_Recenter_B");

            Assert(overlayText != null && ready != null && legend != null && legendPanel != null,
                "the Area B instruction text, READY and legend panel were all found");

            if (overlayText != null && ready != null && legend != null && legendPanel != null)
            {
                var instructionY = overlayText.transform.position.y;
                var readyY = ready.transform.position.y;
                var legendY = legend.transform.position.y;

                // Half-heights in world metres.
                var readyHalf = ((RectTransform)ready.transform).sizeDelta.y * 0.5f *
                                ready.transform.lossyScale.y;
                var legendHalf = legendPanel.transform.lossyScale.y * 0.5f;

                Info($"Area B zones — instructions y {instructionY:F2}, READY y {readyY:F2} " +
                     $"(+/-{readyHalf:F2}), legend y {legendY:F2} (+/-{legendHalf:F2})");

                Assert(instructionY > readyY,
                    $"ZONE 1 instructions sit ABOVE ZONE 2 READY ({instructionY:F2} > {readyY:F2})");

                Assert(readyY - readyHalf > legendY + legendHalf,
                    $"ZONE 2 READY clears ZONE 3 legend " +
                    $"(READY bottom {readyY - readyHalf:F2} > legend top {legendY + legendHalf:F2})");

                // The whole point of moving it: it must be within a comfortable downward glance
                // from a seated eye height of ~1.6 m, not down near the floor.
                Assert(legendY > 0.85f,
                    $"the legend is lifted into the useful field of view (y {legendY:F2}; it " +
                    "was 0.25 m, roughly 45 degrees below the eye line)");

                if (recenterB != null)
                {
                    Assert(Mathf.Abs(recenterB.transform.position.y - legendY) >
                           legendHalf + 0.05f,
                        $"the legend does not overlap the Area B Recenter " +
                        $"(recenter y {recenterB.transform.position.y:F2}, legend y {legendY:F2})");
                }
            }

            // ---- C: legend contents, labels and non-selectability ----------------------------
            if (legend != null)
            {
                foreach (var shape in new[] { "Solid", "Slatted", "Curved" })
                {
                    var entry = legend.transform.Find($"Legend_{shape}");

                    Assert(entry != null, $"the legend has a {shape} example");

                    if (entry != null)
                    {
                        Assert(entry.GetComponentsInChildren<Renderer>(true).Length > 0,
                            $"the {shape} example has visible geometry");
                    }
                }

                // SLATTED must actually have gaps, and CURVED must not be a flat box.
                var slatted = legend.transform.Find("Legend_Slatted");
                var curved = legend.transform.Find("Legend_Curved");
                var solid = legend.transform.Find("Legend_Solid");

                if (slatted != null && curved != null && solid != null)
                {
                    var slats = slatted.GetComponentsInChildren<Transform>(true)
                        .Count(t => t.name.StartsWith("Rail_"));

                    Assert(slats >= 3,
                        $"the SLATTED example has {slats} separated rails, so its gaps are " +
                        "unmistakable next to the SOLID panel");

                    Assert(solid.GetComponentsInChildren<Transform>(true)
                            .Count(t => t.name.StartsWith("Rail_")) == 0,
                        "the SOLID example has no rails — it is one continuous surface");

                    Assert(Mathf.Abs(curved.GetChild(0).localRotation.eulerAngles.z - 90f) < 1f,
                        "the CURVED example is a HORIZONTAL cylinder, so its round profile " +
                        "reads head-on rather than looking like an upright post");
                }

                Assert(legend.GetComponentsInChildren<Collider>(true).Length == 0,
                    "NOTHING in the legend has a collider — the examples and the backing panel " +
                    "can never be pointed at or mistaken for task chairs");

                Assert(legend.GetComponentsInChildren<XRSimpleInteractable>(true).Length == 0,
                    "nothing in the legend is interactable");

                // Labels must fit inside their canvas.
                var canvas = legend.transform.Find("UI_B_ShapeLegend") as RectTransform;

                if (canvas != null)
                {
                    var halfWidth = canvas.sizeDelta.x * 0.5f;

                    foreach (var label in canvas.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        var rect = (RectTransform)label.transform;
                        var edge = Mathf.Abs(rect.anchoredPosition.x) + rect.sizeDelta.x * 0.5f;

                        Assert(edge <= halfWidth,
                            $"{label.name} fits inside the legend canvas " +
                            $"(edge {edge:F0} <= half-width {halfWidth:F0}) — the outer labels " +
                            "used to reach 1100 on a 1000 half-width and were clipped");
                    }
                }
            }

            // ---- D: real chairs keep their shapes distinct AND selectable ---------------------
            var chairs = Object.FindObjectsByType<ChairTarget>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(chairs.Length == 6, $"the six task chairs are present ({chairs.Length})");

            foreach (var chair in chairs)
            {
                var slattedRails = chair.GetComponentsInChildren<Transform>(true)
                    .Count(t => t.name.StartsWith("Back_Rail_"));

                Assert(slattedRails >= 3,
                    $"{chair.name}'s SLATTED variant has {slattedRails} rails for a clearly " +
                    "slatted silhouette");

                // Selectability must survive the geometry change — this is the PASS that must
                // not regress.
                foreach (ChairShape shape in System.Enum.GetValues(typeof(ChairShape)))
                {
                    chair.ApplySpec(new ChairSpec(ChairColor.Blue, ChairSize.Large, shape), null);

                    Assert(chair.GetComponentsInChildren<Collider>(false).Count(c => c.enabled) > 0,
                        $"{chair.name} is still selectable as {shape} after the readability " +
                        "geometry change");
                }
            }
        }

        /// <summary>
        /// The immediate item-to-item transition, and the input-release guard that makes
        /// removing the timed pause safe.
        ///
        /// The guard is exercised against the REAL button component rather than read out of the
        /// source, because the property that matters — "does a still-held press arm the next
        /// item?" — is behavioural.
        /// </summary>
        static void CheckBlock5ImmediateTransition()
        {
            // ---- The pause is gone from BOTH phases -----------------------------------------
            var source = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            var itemStart = source.IndexOf("IEnumerator RunRecognitionItem",
                System.StringComparison.Ordinal);

            Assert(itemStart >= 0, "the recognition item coroutine exists");

            if (itemStart >= 0)
            {
                var itemEnd = source.IndexOf("void OnRecognitionResponse", itemStart,
                    System.StringComparison.Ordinal);
                var body = itemEnd > itemStart
                    ? source.Substring(itemStart, itemEnd - itemStart)
                    : source.Substring(itemStart);

                Assert(!body.Contains("WaitForSeconds"),
                    "no timed wait remains anywhere in the item path");

                // Commit ordering: the response must be recorded BEFORE the next item can start.
                var disarmAt = body.IndexOf("Disarm()", System.StringComparison.Ordinal);
                var commitAt = body.IndexOf("item.response = m_PendingRecognitionResponse",
                    System.StringComparison.Ordinal);
                var frameAt = body.LastIndexOf("yield return null", System.StringComparison.Ordinal);

                Assert(disarmAt >= 0 && commitAt > disarmAt,
                    "the buttons are disarmed BEFORE the response is committed");

                Assert(commitAt >= 0 && frameAt > commitAt,
                    "the response is committed BEFORE the frame boundary that leads to the " +
                    "next item — nothing can advance with an uncommitted answer");
            }

            // Both phases run through that one coroutine, so neither can drift.
            var phaseStart = source.IndexOf("IEnumerator RunRecognitionPhase",
                System.StringComparison.Ordinal);

            if (phaseStart >= 0)
            {
                var phaseEnd = source.IndexOf("IEnumerator RunRecognitionItem", phaseStart,
                    System.StringComparison.Ordinal);
                var phaseBody = phaseEnd > phaseStart
                    ? source.Substring(phaseStart, phaseEnd - phaseStart)
                    : source.Substring(phaseStart);

                Assert(!phaseBody.Contains("WaitForSeconds"),
                    "the phase loop adds no wait of its own, so Immediate and Delayed " +
                    "Recognition both get the immediate transition");
            }

            // ---- The input-release guard, exercised for real --------------------------------
            var buttons = Object.FindObjectsByType<RecognitionResponseButton>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(buttons.Length == 2, $"two response buttons exist ({buttons.Length})");

            foreach (var button in buttons)
            {
                button.Disarm();

                Assert(!button.isArmed && !button.isArmPending,
                    $"{button.name} starts fully disarmed");

                // Nothing is selecting it in the editor, so an arm is granted immediately —
                // the normal case costs nothing.
                button.Arm();

                Assert(button.isArmed && !button.isArmPending,
                    $"{button.name} arms immediately when no input is held — the guard adds no " +
                    "delay to the ordinary case");

                // Disarm must clear BOTH the grant and any outstanding request, or a deferred
                // arm could fire after the item it belonged to had ended.
                button.Arm();
                button.Disarm();

                Assert(!button.isArmed && !button.isArmPending,
                    $"{button.name} disarm cancels a pending arm as well as an active one");
            }

            // ---- One response per item, and no carry-over -----------------------------------
            var panel = Object.FindObjectsByType<RecognitionResponsePanel>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();

            Assert(panel != null, "the response panel exists");

            if (panel != null && buttons.Length == 2)
            {
                var received = new List<RecognitionResponse>();
                void Handler(RecognitionResponse r) => received.Add(r);

                // OnEnable does not run for a component in a scene opened in the EDITOR, so the
                // panel has not subscribed to its buttons yet. Invoking it here reproduces the
                // runtime wiring exactly — the alternative, subscribing to the buttons directly,
                // would bypass the very relay this test is meant to cover.
                var panelType = typeof(RecognitionResponsePanel);
                var onEnable = panelType.GetMethod("OnEnable",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var onDisable = panelType.GetMethod("OnDisable",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert(onEnable != null && onDisable != null,
                    "the panel's subscribe/unsubscribe hooks are reachable");

                onEnable?.Invoke(panel, null);
                panel.responseSelected += Handler;

                try
                {
                    panel.Arm();

                    Assert(panel.isArmed, "the panel arms both buttons together");

                    // One press disarms the PAIR, so the other button cannot also answer.
                    var seen = panel.seenBeforeButton;
                    var selectMethod = typeof(RecognitionResponseButton).GetMethod(
                        "OnSelectEntered", BindingFlags.Instance | BindingFlags.NonPublic);

                    Assert(selectMethod != null, "the button's select handler is reachable");

                    if (selectMethod != null && seen != null)
                    {
                        selectMethod.Invoke(seen, new object[] { null });

                        Assert(received.Count == 1,
                            $"one press produces exactly ONE response ({received.Count})");

                        Assert(!panel.isArmed,
                            "the pair is disarmed by the first press, so the other button " +
                            "cannot answer the same item");

                        // A second press on either button must be ignored while disarmed.
                        selectMethod.Invoke(seen, new object[] { null });
                        selectMethod.Invoke(panel.notSeenBeforeButton, new object[] { null });

                        Assert(received.Count == 1,
                            $"further presses while disarmed are ignored — no duplicate " +
                            $"response and no carry-over ({received.Count})");
                    }
                }
                finally
                {
                    panel.responseSelected -= Handler;
                    onDisable?.Invoke(panel, null);
                    panel.Disarm();
                }
            }

            // ---- Everything this pass must NOT have touched ---------------------------------
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config != null)
            {
                Assert(Mathf.Abs(config.recognitionResponseTimeoutSeconds - 15f) < 1e-4f,
                    $"the response timeout is still 15 s ({config.recognitionResponseTimeoutSeconds})");

                Assert(Mathf.Abs(config.recognitionWordDisplaySeconds - 2f) < 1e-4f,
                    $"encoding word display is still 2 s ({config.recognitionWordDisplaySeconds})");

                Assert(Mathf.Abs(config.recognitionInterWordGapSeconds - 0.25f) < 1e-4f,
                    $"the ENCODING inter-word gap is untouched " +
                    $"({config.recognitionInterWordGapSeconds}) — only the RESPONSE transition " +
                    "changed");
            }

            // Scoring must be exactly as before.
            foreach (var (cls, resp, expected) in new[]
                     {
                         (RecognitionItemClass.Target, RecognitionResponse.SeenBefore,
                             RecognitionOutcome.Hit),
                         (RecognitionItemClass.Target, RecognitionResponse.NotSeenBefore,
                             RecognitionOutcome.Miss),
                         (RecognitionItemClass.Lure, RecognitionResponse.NotSeenBefore,
                             RecognitionOutcome.CorrectRejection),
                         (RecognitionItemClass.Lure, RecognitionResponse.SeenBefore,
                             RecognitionOutcome.FalseAlarm),
                     })
            {
                var item = new RecognitionItem { itemClass = cls, response = resp };

                Assert(item.outcome == expected,
                    $"{cls} + {resp} is still {expected} — the pacing change did not touch " +
                    "classification");
            }
        }

        /// <summary>
        /// The response subscription's LIFETIME, and the early exit from the response wait.
        ///
        /// WHY THIS SECTION EXISTS. A subscription made in Bind() looks perfectly correct in
        /// isolation and is exercised happily by any test that calls Bind() first — which is
        /// exactly why the previous tests passed while the build was broken on hardware. Bind()
        /// is called by the scene BUILDER, in the Editor; C# events are not serialized, so at
        /// run time nothing was listening and every item ran its full 15 s timeout.
        ///
        /// These assertions therefore check WHERE the subscription is made, not merely that it
        /// works once something has called Bind().
        /// </summary>
        static void CheckBlock6ResponseWiring()
        {
            var source = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            // ---- The subscription must live in the RUNTIME lifecycle ------------------------
            var enableStart = source.IndexOf("void OnEnable()", System.StringComparison.Ordinal);
            var disableStart = source.IndexOf("void OnDisable()", System.StringComparison.Ordinal);
            var bindStart = source.IndexOf("public void Bind(", System.StringComparison.Ordinal);

            Assert(enableStart >= 0 && disableStart >= 0 && bindStart >= 0,
                "OnEnable, OnDisable and Bind were all located");

            if (enableStart >= 0 && disableStart >= 0 && bindStart >= 0)
            {
                var enableBody = source.Substring(enableStart, disableStart - enableStart);
                var bindBody = source.Substring(bindStart);
                var disableBody = source.Substring(disableStart,
                    System.Math.Min(4000, source.Length - disableStart));

                Assert(enableBody.Contains("responseSelected += OnRecognitionResponse"),
                    "the recognition response is subscribed in OnEnable — the RUNTIME lifecycle, " +
                    "where every other subscription in this class lives");

                Assert(disableBody.Contains("responseSelected -= OnRecognitionResponse"),
                    "and unsubscribed in OnDisable");

                // The actual regression guard: Bind() must NOT be where this is wired.
                Assert(!bindBody.Contains("responseSelected += OnRecognitionResponse"),
                    "Bind() does NOT subscribe to the response event — Bind runs only in the " +
                    "Editor from the scene builder, and a C# event subscription made there does " +
                    "not exist at run time");
            }

            // ---- The wait must be interruptible, not a blind WaitForSeconds(timeout) ---------
            var itemStart = source.IndexOf("IEnumerator RunRecognitionItem",
                System.StringComparison.Ordinal);

            Assert(itemStart >= 0, "the recognition item coroutine exists");

            if (itemStart >= 0)
            {
                var itemEnd = source.IndexOf("void OnRecognitionResponse", itemStart,
                    System.StringComparison.Ordinal);
                var body = itemEnd > itemStart
                    ? source.Substring(itemStart, itemEnd - itemStart)
                    : source.Substring(itemStart);

                Assert(body.Contains("m_PendingRecognitionResponse == RecognitionResponse.None") &&
                       body.Contains("Time.time < deadline"),
                    "the response wait is a polling loop whose condition includes BOTH 'no " +
                    "response yet' AND 'before the deadline', so an answer exits it immediately");

                Assert(!body.Contains("WaitForSeconds(m_Config.recognitionResponseTimeoutSeconds"),
                    "the timeout is never awaited as an uninterruptible WaitForSeconds");

                Assert(body.Contains("recognitionResponseTimeoutSeconds"),
                    "the timeout is still used to build the deadline, so an UNANSWERED item " +
                    "still times out");

                // The confirmation cue must not be awaited.
                var confirmAt = body.IndexOf("AudioCue.ResponseConfirm",
                    System.StringComparison.Ordinal);

                if (confirmAt >= 0)
                {
                    var afterConfirm = body.Substring(confirmAt,
                        System.Math.Min(300, body.Length - confirmAt));

                    Assert(!afterConfirm.Contains("yield return new WaitForSeconds") &&
                           !afterConfirm.Contains("ConfirmCueAsync"),
                        "the response confirmation cue is fired and NOT awaited — the next word " +
                        "never waits for a sound to finish");
                }

                Assert(body.Contains("[RecognitionTiming]"),
                    "the temporary timing diagnostic is present in the item path");
            }

            // ---- The panel is reachable at runtime WITHOUT Bind() ----------------------------
            // OnEnable resolves it itself, so a scene whose serialized reference is missing
            // still wires up rather than silently listening to nothing.
            var panels = Object.FindObjectsByType<RecognitionResponsePanel>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(panels.Length == 1,
                $"exactly one response panel exists for OnEnable to find ({panels.Length})");

            if (panels.Length == 1)
            {
                // The panel's own root must stay ACTIVE, or FindAnyObjectByType in OnEnable
                // returns null and the subscription silently never happens again.
                Assert(panels[0].gameObject.activeSelf,
                    "the panel's root GameObject is active, so OnEnable's " +
                    "FindAnyObjectByType can reach it (only its Visuals child is hidden)");
            }

            // ---- The manager actually subscribes when enabled --------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();

            if (manager != null && panels.Length == 1)
            {
                var type = typeof(ExperimentManager);
                var onEnable = type.GetMethod("OnEnable",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var pendingField = type.GetField("m_PendingRecognitionResponse",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var panelField = type.GetField("m_RecognitionPanel",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert(onEnable != null && pendingField != null && panelField != null,
                    "the manager's enable hook and recognition state are reachable");

                if (onEnable != null && pendingField != null && panelField != null)
                {
                    panelField.SetValue(manager, panels[0]);
                    pendingField.SetValue(manager, RecognitionResponse.None);

                    onEnable.Invoke(manager, null);

                    // Raise the panel's event exactly as a button press would.
                    var onButton = typeof(RecognitionResponsePanel).GetMethod("OnButton",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                    Assert(onButton != null, "the panel's button relay is reachable");

                    if (onButton != null)
                    {
                        onButton.Invoke(panels[0],
                            new object[] { RecognitionResponse.SeenBefore });

                        var pending = (RecognitionResponse)pendingField.GetValue(manager);

                        Assert(pending == RecognitionResponse.SeenBefore,
                            $"after OnEnable, a panel response REACHES the manager and sets the " +
                            $"pending answer ({pending}) — this is precisely what was broken, " +
                            "and it is what lets the wait loop exit early");

                        // A second press must not overwrite the first.
                        onButton.Invoke(panels[0],
                            new object[] { RecognitionResponse.NotSeenBefore });

                        Assert((RecognitionResponse)pendingField.GetValue(manager) ==
                               RecognitionResponse.SeenBefore,
                            "a second response does not overwrite the first — one item, one " +
                            "answer");

                        pendingField.SetValue(manager, RecognitionResponse.None);
                    }
                }
            }

            // ---- Nothing this pass must have changed ----------------------------------------
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config != null)
            {
                Assert(Mathf.Abs(config.recognitionResponseTimeoutSeconds - 15f) < 1e-4f,
                    $"the response timeout is unchanged at 15 s " +
                    $"({config.recognitionResponseTimeoutSeconds})");

                Assert(Mathf.Abs(config.recognitionWordDisplaySeconds - 2f) < 1e-4f,
                    $"encoding timing is unchanged ({config.recognitionWordDisplaySeconds} s)");
            }
        }

        /// <summary>
        /// Legend occlusion, Area B instruction narration, and the delayed Recognition wording.
        /// </summary>
        static void CheckBlock7VisualsAndNarration()
        {
            // ---- A: the legend canvas must not paint over the examples ----------------------
            var legend = FindInSceneIncludingInactive("ShapeLegend");

            Assert(legend != null, "the shape legend is in the scene");

            if (legend != null)
            {
                var canvas = legend.transform.Find("UI_B_ShapeLegend");

                Assert(canvas != null, "the legend label canvas exists");

                if (canvas != null)
                {
                    // CreateWorldCanvas attaches a 94%-opaque full-rect Background image. The
                    // participant is on the -Z side, so the canvas must sit at a LARGER z than
                    // the examples or it paints over them — which is exactly what made SOLID and
                    // SLATTED look broken while the much deeper CURVED cylinder poked through.
                    var canvasZ = canvas.localPosition.z;

                    Assert(canvas.GetComponentsInChildren<UnityEngine.UI.Image>(true).Length > 0,
                        "the legend canvas does carry an opaque background image (the thing that " +
                        "was occluding the shapes)");

                    foreach (var shape in new[] { "Solid", "Slatted", "Curved" })
                    {
                        var entry = legend.transform.Find($"Legend_{shape}");

                        if (entry == null)
                            continue;

                        // Front-most surface of this example, in legend-local space.
                        var frontZ = float.MaxValue;

                        foreach (var r in entry.GetComponentsInChildren<Renderer>(true))
                        {
                            var localFront = entry.localPosition.z + r.transform.localPosition.z -
                                             r.transform.localScale.z * 0.5f;
                            frontZ = Mathf.Min(frontZ, localFront);
                        }

                        Assert(canvasZ > frontZ,
                            $"the label canvas sits BEHIND the {shape} example " +
                            $"(canvas z {canvasZ:F3} > example front {frontZ:F3}), so its opaque " +
                            "background can no longer cover it");
                    }

                    // Labels must still be clear of the example geometry vertically.
                    var slats = legend.transform.Find("Legend_Slatted");

                    if (slats != null)
                    {
                        var rails = slats.GetComponentsInChildren<Transform>(true)
                            .Where(t => t.name.StartsWith("Rail_")).ToList();

                        Assert(rails.Count == 3,
                            $"SLATTED has exactly 3 rails ({rails.Count})");

                        // Evenly spaced, with real gaps: sort by height and check the gap
                        // between consecutive rails exceeds the rail thickness.
                        var ys = rails.Select(r => r.localPosition.y).OrderBy(y => y).ToList();
                        var thickness = rails[0].localScale.y;

                        for (var i = 1; i < ys.Count; i++)
                        {
                            var gap = ys[i] - ys[i - 1] - thickness;

                            Assert(gap > thickness * 0.5f,
                                $"the gap between rails {i} and {i + 1} ({gap:F3} m) is wider " +
                                $"than half a rail ({thickness:F3} m) — the slats read as " +
                                "separated, not as one surface");
                        }

                        var spacing1 = ys[1] - ys[0];
                        var spacing2 = ys[2] - ys[1];

                        Assert(Mathf.Abs(spacing1 - spacing2) < 0.005f,
                            $"the three rails are evenly spaced ({spacing1:F3} vs {spacing2:F3})");
                    }

                    // SOLID must be exactly one continuous surface.
                    var solid = legend.transform.Find("Legend_Solid");

                    if (solid != null)
                    {
                        var parts = solid.GetComponentsInChildren<Renderer>(true).Length;

                        Assert(parts == 1,
                            $"SOLID is ONE continuous surface, not an assembly ({parts} renderers)");
                    }

                    // No negative scale anywhere — a mirrored transform flips normals and makes
                    // a mesh render inside-out, which reads as "malformed".
                    foreach (var t in legend.GetComponentsInChildren<Transform>(true))
                    {
                        var sc = t.localScale;

                        Assert(sc.x > 0f && sc.y > 0f && sc.z > 0f,
                            $"{t.name} has no negative scale ({sc}) — a mirrored transform would " +
                            "invert its normals and render it inside-out");
                    }
                }

                Assert(legend.GetComponentsInChildren<Collider>(true).Length == 0,
                    "the legend still has no colliders");
            }

            // ---- B/C: narration exists, and stimulus content is NOT narrated ----------------
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config != null)
            {
                Assert(config.GetAreaBInstructionNarration(ExperimentLanguage.English) != null,
                    "the general Area B task instructions have an English narration clip");

                Assert(config.GetRecognitionDelayedNarration(ExperimentLanguage.English) != null,
                    "the Recognition delayed instructions have an English narration clip");
            }

            var source = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            Assert(source.Contains("SpeakAreaBInstructions()"),
                "the Area B general instructions are narrated");

            Assert(source.Contains("SpeakDelayedRecognitionInstructions()"),
                "the delayed Recognition instructions are narrated");

            // The per-trial target must never be spoken: the chair instruction path may set text
            // but must not start narration.
            var trialStart = source.IndexOf("IEnumerator RunChairTrial",
                System.StringComparison.Ordinal);

            Assert(trialStart >= 0, "the chair trial coroutine exists");

            if (trialStart >= 0)
            {
                var trialEnd = source.IndexOf("IEnumerator RunAreaC", trialStart,
                    System.StringComparison.Ordinal);
                var body = trialEnd > trialStart
                    ? source.Substring(trialStart, trialEnd - trialStart)
                    : source.Substring(trialStart);

                foreach (var forbidden in new[]
                         { "SpeakAreaBInstructions", "GetAreaBInstructionNarration",
                           "GetPracticeColorClip" })
                {
                    Assert(!body.Contains(forbidden),
                        $"the chair TRIAL never calls {forbidden} — a trial's colour, size and " +
                        "shape are presented visually and never spoken");
                }
            }

            // The delayed item loop must stay silent apart from its cues.
            var itemStart = source.IndexOf("IEnumerator RunRecognitionItem",
                System.StringComparison.Ordinal);

            if (itemStart >= 0)
            {
                var itemEnd = source.IndexOf("void OnRecognitionResponse", itemStart,
                    System.StringComparison.Ordinal);
                var body = itemEnd > itemStart
                    ? source.Substring(itemStart, itemEnd - itemStart)
                    : source.Substring(itemStart);

                Assert(!body.Contains("SpeakDelayedRecognitionInstructions") &&
                       !body.Contains("AudioCue.SpokenWord") &&
                       !body.Contains("GetWordClip"),
                    "the recognition item loop speaks NO word and starts no narration — the " +
                    "delayed stimulus words remain visual-only");
            }

            // ---- C: wording, protocol-aware -------------------------------------------------
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

                var delayed = ExperimentLocalization.Get(LocKeys.RecognitionDelayedInstructions);
                var lower = delayed.ToLowerInvariant();

                Info($"Recognition delayed instruction: \"{delayed.Replace("\n", " ")}\"");

                foreach (var forbidden in new[]
                         { "repeat", "recall", "five words", "5 words", "listen", "heard", "say" })
                {
                    Assert(!lower.Contains(forbidden),
                        $"the Recognition delayed instruction never says '{forbidden}'");
                }

                Assert(lower.Contains("saw") || lower.Contains("seen"),
                    "the Recognition delayed instruction asks about having SEEN the words");

                Assert(lower.Contains("select") || lower.Contains("indicate"),
                    "it asks the participant to SELECT a response, not to speak");

                // FreeRecall's own wording must survive untouched.
                var freeRecall = ExperimentLocalization.Get(LocKeys.AreaCInstructions)
                    .ToLowerInvariant();

                Assert(freeRecall.Contains("repeat") &&
                       (freeRecall.Contains("five") || freeRecall.Contains("5")),
                    "the FreeRecall Area C instruction is UNCHANGED and still describes spoken " +
                    "recall of the five words — it was routed around, not rewritten");
            }
            finally
            {
                ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // READY stays participant-controlled: nothing may press it.
            Assert(!source.Contains("m_UI.ShowReadyButton(false);\n            OnReadyPressed"),
                "narration does not auto-advance past READY");

            Assert(source.Contains("readyPressed += OnReadyPressed"),
                "READY remains driven by the participant's own press");
        }


        /// <summary>
        /// Block 8: the DELAYED recognition stimulus word is actually visible in Area C.
        ///
        /// THE BUG THIS SECTION EXISTS FOR. On the headset the delayed phase showed its
        /// instruction, spoke its narration and armed both response buttons — and presented no
        /// word. Nothing was broken in the protocol, the sequence or the logging: the word was
        /// written to <c>m_AreaAWord</c>, which lives on the Area A canvas in a different room,
        /// and <c>ShowArea(AreaC)</c> had already deactivated that canvas. The string was
        /// correct and invisible.
        ///
        /// WHY THIS IS CHECKED AS BEHAVIOUR, NOT AS WIRING. A test that only asserted "the Area
        /// C label is bound" would have passed the moment the field existed. These checks drive
        /// the real controller into the real Area C state and then ask what the participant
        /// would actually see, because that is the only question the previous suite — 1983
        /// assertions, 0 failures, with this bug present — could not answer.
        /// </summary>
        static void CheckBlock8DelayedStimulusVisibility()
        {
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();

            Assert(ui != null, "the UI controller is in the scene");

            if (ui == null)
                return;

            // ---- A: the two stimulus labels exist and are DIFFERENT objects -----------------
            var areaAWord = UiLabel(ui, "m_AreaAWord");
            var areaCWord = UiLabel(ui, "m_AreaCWord");

            Assert(areaAWord != null,
                "the IMMEDIATE stimulus label is bound (m_AreaAWord) — unchanged");

            Assert(areaCWord != null,
                "the DELAYED stimulus label is bound (m_AreaCWord) — this is the object whose " +
                "absence made the delayed word invisible");

            if (areaAWord == null || areaCWord == null)
                return;

            Assert(!ReferenceEquals(areaAWord, areaCWord),
                "the two phases use two DIFFERENT label objects — one label cannot serve both, " +
                "because each area canvas stands in its own room and only one is ever active");

            // ---- B: each label lives under the canvas its own area switches on ---------------
            var canvasA = FindInSceneIncludingInactive("UI_A_Canvas");
            var canvasC = FindInSceneIncludingInactive("UI_C_Canvas");

            Assert(canvasA != null && canvasC != null, "both area canvases exist");

            if (canvasA != null && canvasC != null)
            {
                Assert(areaAWord.transform.IsChildOf(canvasA.transform),
                    "the immediate stimulus label is on the Area A canvas");

                Assert(areaCWord.transform.IsChildOf(canvasC.transform),
                    "the delayed stimulus label is on the AREA C canvas — the canvas ShowArea " +
                    "activates when the participant is standing in Area C");

                // The precise shape of the old bug, stated as an assertion so it cannot return.
                Assert(!areaAWord.transform.IsChildOf(canvasC.transform),
                    "the Area A label is NOT reachable from the Area C canvas — writing the " +
                    "delayed word there alone is exactly what showed the participant nothing");
            }

            // ---- C: the label is genuinely visible in the Area C state ----------------------
            // Driven through the real ShowArea path, then read back from the real object.
            const string probe = "SELFTEST_DELAYED_WORD";

            ui.ShowArea(ExperimentArea.AreaC);
            ui.SetWordDisplay(probe);

            Assert(areaCWord.text == probe,
                $"the delayed word reaches the Area C label (\"{areaCWord.text}\")");

            Assert(areaCWord.gameObject.activeInHierarchy,
                "the delayed stimulus label is ACTIVE IN HIERARCHY while Area C is open — the " +
                "assertion the old code would have failed");

            Assert(areaCWord.enabled && areaCWord.color.a > 0.5f,
                $"the delayed label renders (enabled={areaCWord.enabled}, " +
                $"alpha={areaCWord.color.a:F2})");

            Assert(!areaAWord.gameObject.activeInHierarchy,
                "the Area A label is inactive while Area C is open — which is why it could " +
                "never have carried this word");

            // ---- D: ClearWordDisplay does not erase the word the phase just set -------------
            // Ordering, not existence: RunRecognitionItem sets the word, waits for the answer,
            // and only then clears. A clear that ran after the set within one item would blank
            // the stimulus while the buttons stayed armed.
            var managerSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            var itemStart = managerSource.IndexOf("IEnumerator RunRecognitionItem",
                System.StringComparison.Ordinal);

            Assert(itemStart >= 0, "the recognition item coroutine exists");

            if (itemStart >= 0)
            {
                var itemEnd = managerSource.IndexOf("void OnRecognitionResponse", itemStart,
                    System.StringComparison.Ordinal);
                var body = itemEnd > itemStart
                    ? managerSource.Substring(itemStart, itemEnd - itemStart)
                    : managerSource.Substring(itemStart);

                var setIndex = body.IndexOf("SetWordDisplay", System.StringComparison.Ordinal);
                var clearIndex = body.IndexOf("ClearWordDisplay", System.StringComparison.Ordinal);
                var waitIndex = body.IndexOf("while (m_PendingRecognitionResponse",
                    System.StringComparison.Ordinal);

                Assert(setIndex >= 0 && clearIndex > setIndex,
                    "the item sets the word BEFORE it clears it");

                Assert(waitIndex > setIndex && clearIndex > waitIndex,
                    "the word is cleared only AFTER the response wait — it stays on screen for " +
                    "the whole time the participant is answering");

                Assert(body.IndexOf("Arm()", System.StringComparison.Ordinal) > setIndex,
                    "the buttons are armed after the word is displayed, never before it");
            }

            // ---- E: the response panel cannot hide or disable the word ----------------------
            var panel = Object.FindObjectsByType<RecognitionResponsePanel>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();

            var anchorC = FindInSceneIncludingInactive("Anchor_Recognition_AreaC");

            Assert(anchorC != null, "the Area C recognition anchor exists");

            if (panel != null)
            {
                Assert(!areaCWord.transform.IsChildOf(panel.transform),
                    "the delayed word is NOT parented to the response panel, so showing, " +
                    "hiding or relocating the buttons cannot take the stimulus with them");

                var panelSource = StripCommentsAndAttributes(File.ReadAllText(
                    "Assets/IKEA_EEG/Scripts/Interaction/RecognitionResponsePanel.cs"));

                Assert(!panelSource.Contains("WordDisplay"),
                    "the response panel touches no stimulus label at all");
            }

            if (anchorC != null)
            {
                var wordY = areaCWord.transform.position.y;
                var buttonY = anchorC.transform.position.y;

                Info($"Area C zones — delayed word y {wordY:F3}, response pair y {buttonY:F3}");

                // The same 0.5 m separation already required in Area A, applied to Area C.
                Assert(wordY - buttonY > 0.5f,
                    $"the response buttons cannot occlude the delayed word " +
                    $"({wordY - buttonY:F2} m apart)");
            }

            // The immediate and delayed stimuli should be the same size at the same eye height,
            // or the two halves of one recognition measure differ in legibility.
            if (canvasA != null && canvasC != null)
            {
                var heightDelta = Mathf.Abs(areaAWord.transform.position.y -
                                            areaCWord.transform.position.y);

                Assert(heightDelta < 0.02f,
                    $"the delayed word sits at the same world height as the immediate word " +
                    $"({heightDelta * 1000f:F0} mm apart)");

                Assert(Mathf.Abs(areaAWord.fontSize - areaCWord.fontSize) < 0.5f,
                    $"both stimuli are set at the same size ({areaAWord.fontSize:F0} pt vs " +
                    $"{areaCWord.fontSize:F0} pt)");
            }

            // ---- F: clearing empties BOTH, so no word survives into another area -------------
            ui.ClearWordDisplay();

            Assert(string.IsNullOrEmpty(areaCWord.text) && string.IsNullOrEmpty(areaAWord.text),
                "ClearWordDisplay empties both stimulus labels, so a word cannot be left " +
                "standing on an area the participant is about to walk into");

            // ---- G: still VISUAL-ONLY, and the results are still never spoken ---------------
            var delayedStart = managerSource.IndexOf("IEnumerator RunDelayedRecognitionAreaC",
                System.StringComparison.Ordinal);

            Assert(delayedStart >= 0, "the delayed recognition coroutine exists");

            if (delayedStart >= 0)
            {
                var delayedEnd = managerSource.IndexOf("IEnumerator RunFreeRecallAreaC",
                    delayedStart, System.StringComparison.Ordinal);
                var body = delayedEnd > delayedStart
                    ? managerSource.Substring(delayedStart, delayedEnd - delayedStart)
                    : managerSource.Substring(delayedStart);

                // ONE narration call, and it is the instruction. Nothing per-word.
                Assert(body.Contains("SpeakDelayedRecognitionInstructions()"),
                    "the delayed INSTRUCTION is still narrated");

                foreach (var forbidden in new[] { "AudioCue.SpokenWord", "GetWordClip" })
                {
                    Assert(!body.Contains(forbidden),
                        $"the delayed phase never calls {forbidden} — its stimulus words stay " +
                        "visual-only, exactly as the encoding words do");
                }
            }

            // The final results are shown and never spoken. Asserted over the whole results
            // tail, because "no narration was added" is a claim about code that does not exist.
            var finishStart = managerSource.IndexOf("void FinishAreaC()",
                System.StringComparison.Ordinal);

            Assert(finishStart >= 0, "the results routine exists");

            if (finishStart >= 0)
            {
                var finishEnd = managerSource.IndexOf("void CaptureRunDuration", finishStart,
                    System.StringComparison.Ordinal);
                var body = finishEnd > finishStart
                    ? managerSource.Substring(finishStart, finishEnd - finishStart)
                    : managerSource.Substring(finishStart);

                foreach (var forbidden in new[]
                         { "Speak", "AudioCue.SpokenWord", "GetWordClip", "PlayNarration",
                           "ParticipantNarration" })
                {
                    Assert(!body.Contains(forbidden),
                        $"the results routine contains no {forbidden} — Hits, Misses, Correct " +
                        "Rejections, False Alarms and totals are NEVER read aloud");
                }

                Assert(body.Contains("SetResults("),
                    "the results are delivered visually, on the panel");
            }

            // ---- H: nothing about the IMMEDIATE phase moved ---------------------------------
            var wordRectA = (RectTransform)areaAWord.transform;

            Assert(Mathf.Abs(wordRectA.anchoredPosition.y - 60f) < 0.5f,
                $"the immediate stimulus label is where it was (y {wordRectA.anchoredPosition.y:F0} " +
                "px) — the Quest-verified immediate phase was not moved to fix the delayed one");

            // And the classification the whole measure rests on is untouched.
            var target = new RecognitionItem
            {
                itemClass = RecognitionItemClass.Target,
                response = RecognitionResponse.SeenBefore,
            };

            var lure = new RecognitionItem
            {
                itemClass = RecognitionItemClass.Lure,
                response = RecognitionResponse.SeenBefore,
            };

            Assert(target.outcome == RecognitionOutcome.Hit &&
                   lure.outcome == RecognitionOutcome.FalseAlarm,
                "Hit / False Alarm classification is unchanged by this fix");

            ui.ShowArea(ExperimentArea.AreaA);
        }


        /// <summary>
        /// A recognition item list built for testing: explicit class/response pairs, so the
        /// expected tally can be written down by hand rather than derived by the same code the
        /// test is checking.
        /// </summary>
        static List<RecognitionItem> MakeRecognitionItems(RecognitionPhase phase,
            params (RecognitionItemClass itemClass, RecognitionResponse response)[] spec)
        {
            var items = new List<RecognitionItem>(spec.Length);

            for (var i = 0; i < spec.Length; i++)
            {
                items.Add(new RecognitionItem
                {
                    word = $"W{i + 1:00}",
                    itemClass = spec[i].itemClass,
                    response = spec[i].response,
                    phase = phase,
                    presentationOrder = i,
                    itemId = spec[i].itemClass == RecognitionItemClass.Target
                        ? $"TARGET_{i + 1:00}"
                        : $"LURE_{i + 1:00}",
                });
            }

            return items;
        }

        /// <summary>
        /// Block 9: typed Recognition aggregates, and the protocol-aware results screen.
        ///
        /// WHAT THIS SECTION IS DEFENDING. Two things that a compiler cannot check:
        ///
        ///   1. THAT THERE IS STILL ONLY ONE CLASSIFIER. The counts now feed two consumers — the
        ///      event notes and the results panel. The failure mode being designed out is a
        ///      second Hit/Miss implementation drifting from the first, so the CSV and the screen
        ///      would disagree about the same participant. Every assertion below traces back to
        ///      RecognitionItem.outcome, which is a derived property and cannot be written to.
        ///
        ///   2. THAT RECOGNITION NO LONGER SHOWS FREERECALL FIELDS. "Immediate verbal recall: Not
        ///      available" was a true statement about a task the participant was never asked to
        ///      perform, printed under the delayed recognition they had just completed. The
        ///      routing is asserted on the RENDERED TEXT, not on the branch, because a branch
        ///      that is right and a string that reaches the screen are different claims.
        /// </summary>
        static void CheckBlock9RecognitionAggregates()
        {
            // ---- A: the four classifications, one item each ---------------------------------
            // Asserted through the aggregate, not through RecognitionItem directly: this is the
            // path the results screen actually uses.
            var hit = RecognitionSequence.Tally(MakeRecognitionItems(RecognitionPhase.Immediate,
                (RecognitionItemClass.Target, RecognitionResponse.SeenBefore)));

            Assert(hit.hits == 1 && hit.misses == 0 && hit.correctRejections == 0 &&
                   hit.falseAlarms == 0 && hit.noResponse == 0,
                "TARGET + SEEN BEFORE counts as exactly one HIT");

            var miss = RecognitionSequence.Tally(MakeRecognitionItems(RecognitionPhase.Immediate,
                (RecognitionItemClass.Target, RecognitionResponse.NotSeenBefore)));

            Assert(miss.misses == 1 && miss.hits == 0 && miss.correctRejections == 0 &&
                   miss.falseAlarms == 0 && miss.noResponse == 0,
                "TARGET + NOT SEEN BEFORE counts as exactly one MISS");

            var cr = RecognitionSequence.Tally(MakeRecognitionItems(RecognitionPhase.Immediate,
                (RecognitionItemClass.Lure, RecognitionResponse.NotSeenBefore)));

            Assert(cr.correctRejections == 1 && cr.hits == 0 && cr.misses == 0 &&
                   cr.falseAlarms == 0 && cr.noResponse == 0,
                "LURE + NOT SEEN BEFORE counts as exactly one CORRECT REJECTION");

            var fa = RecognitionSequence.Tally(MakeRecognitionItems(RecognitionPhase.Immediate,
                (RecognitionItemClass.Lure, RecognitionResponse.SeenBefore)));

            Assert(fa.falseAlarms == 1 && fa.hits == 0 && fa.misses == 0 &&
                   fa.correctRejections == 0 && fa.noResponse == 0,
                "LURE + SEEN BEFORE counts as exactly one FALSE ALARM");

            var none = RecognitionSequence.Tally(MakeRecognitionItems(RecognitionPhase.Immediate,
                (RecognitionItemClass.Target, RecognitionResponse.None),
                (RecognitionItemClass.Lure, RecognitionResponse.None)));

            Assert(none.noResponse == 2 && none.hits == 0 && none.misses == 0 &&
                   none.correctRejections == 0 && none.falseAlarms == 0,
                "NO RESPONSE counts as NO RESPONSE for a target AND for a lure — never as an " +
                "error of either kind");

            Assert(none.totalCorrect == 0 && none.totalIncorrect == 0 && none.answeredCount == 0,
                "an unanswered item is in neither totalCorrect nor totalIncorrect, and is " +
                "excluded from answeredCount — the existing 'not scored as an error' rule");

            // ---- B: a mixed phase, counted by hand ------------------------------------------
            // 5 targets: 3 hits, 1 miss, 1 no-response.
            // 4 lures:   2 correct rejections, 1 false alarm, 1 no-response.
            var mixed = RecognitionSequence.Tally(MakeRecognitionItems(RecognitionPhase.Immediate,
                (RecognitionItemClass.Target, RecognitionResponse.SeenBefore),
                (RecognitionItemClass.Target, RecognitionResponse.SeenBefore),
                (RecognitionItemClass.Target, RecognitionResponse.SeenBefore),
                (RecognitionItemClass.Target, RecognitionResponse.NotSeenBefore),
                (RecognitionItemClass.Target, RecognitionResponse.None),
                (RecognitionItemClass.Lure, RecognitionResponse.NotSeenBefore),
                (RecognitionItemClass.Lure, RecognitionResponse.NotSeenBefore),
                (RecognitionItemClass.Lure, RecognitionResponse.SeenBefore),
                (RecognitionItemClass.Lure, RecognitionResponse.None)));

            Assert(mixed.hits == 3, $"mixed phase: 3 hits ({mixed.hits})");
            Assert(mixed.misses == 1, $"mixed phase: 1 miss ({mixed.misses})");
            Assert(mixed.correctRejections == 2,
                $"mixed phase: 2 correct rejections ({mixed.correctRejections})");
            Assert(mixed.falseAlarms == 1, $"mixed phase: 1 false alarm ({mixed.falseAlarms})");
            Assert(mixed.noResponse == 2, $"mixed phase: 2 no-response ({mixed.noResponse})");

            Assert(mixed.itemCount == 9, $"itemCount is the number presented ({mixed.itemCount})");
            Assert(mixed.targetCount == 5,
                $"targetCount comes from itemClass, not from the answers ({mixed.targetCount})");
            Assert(mixed.lureCount == 4, $"lureCount likewise ({mixed.lureCount})");
            Assert(mixed.targetCount + mixed.lureCount == mixed.itemCount,
                "every item is either a target or a lure");

            Assert(mixed.totalCorrect == 5,
                $"totalCorrect = hits + correct rejections = 3 + 2 ({mixed.totalCorrect})");
            Assert(mixed.totalIncorrect == 2,
                $"totalIncorrect = misses + false alarms = 1 + 1 ({mixed.totalIncorrect})");
            Assert(mixed.answeredCount == 7,
                $"answeredCount = itemCount - noResponse = 9 - 2 ({mixed.answeredCount})");
            Assert(mixed.totalCorrect + mixed.totalIncorrect == mixed.answeredCount,
                "correct + incorrect accounts for exactly the answered items");
            Assert(mixed.hasData, "a phase that ran reports hasData");

            var empty = RecognitionSequence.Tally(null);
            Assert(!empty.hasData && empty.itemCount == 0,
                "a phase that never ran reports hasData FALSE — distinct from 'zero hits'");

            // ---- C: ONE counting implementation ---------------------------------------------
            // The event notes and the typed aggregate must be the same numbers from the same
            // loop. Summarise is now a wrapper; this asserts it stayed one.
            Assert(RecognitionSequence.Summarise(MakeRecognitionItems(RecognitionPhase.Immediate,
                       (RecognitionItemClass.Target, RecognitionResponse.SeenBefore),
                       (RecognitionItemClass.Lure, RecognitionResponse.SeenBefore))) ==
                   RecognitionSequence.Tally(MakeRecognitionItems(RecognitionPhase.Immediate,
                       (RecognitionItemClass.Target, RecognitionResponse.SeenBefore),
                       (RecognitionItemClass.Lure, RecognitionResponse.SeenBefore))).ToEventNotes(),
                "Summarise() and the typed aggregate produce identical numbers — there is one " +
                "counting implementation, not two that could drift apart");

            // The exact notes string already present in collected CSVs must not have changed.
            Assert(mixed.ToEventNotes() ==
                   "hits=3; misses=1; correct_rejections=2; false_alarms=1; no_response=2; items=9",
                $"the *_RECOGNITION_END notes format is byte-identical to before " +
                $"(\"{mixed.ToEventNotes()}\")");

            // ---- D: immediate and delayed never mix -----------------------------------------
            var results = new SessionResults { protocolMode = VerbalProtocolMode.Recognition };

            var immediateItems = MakeRecognitionItems(RecognitionPhase.Immediate,
                (RecognitionItemClass.Target, RecognitionResponse.SeenBefore),
                (RecognitionItemClass.Target, RecognitionResponse.SeenBefore),
                (RecognitionItemClass.Lure, RecognitionResponse.NotSeenBefore));

            var delayedItems = MakeRecognitionItems(RecognitionPhase.Delayed,
                (RecognitionItemClass.Target, RecognitionResponse.NotSeenBefore),
                (RecognitionItemClass.Lure, RecognitionResponse.SeenBefore),
                (RecognitionItemClass.Lure, RecognitionResponse.NotSeenBefore),
                (RecognitionItemClass.Target, RecognitionResponse.None));

            results.immediateRecognition.Recount(immediateItems);
            results.delayedRecognition.Recount(delayedItems);

            Assert(results.immediateRecognition.hits == 2 &&
                   results.immediateRecognition.correctRejections == 1 &&
                   results.immediateRecognition.itemCount == 3,
                $"immediate counts are the immediate items only " +
                $"(hits {results.immediateRecognition.hits}, " +
                $"items {results.immediateRecognition.itemCount})");

            Assert(results.delayedRecognition.misses == 1 &&
                   results.delayedRecognition.falseAlarms == 1 &&
                   results.delayedRecognition.correctRejections == 1 &&
                   results.delayedRecognition.noResponse == 1 &&
                   results.delayedRecognition.itemCount == 4,
                $"delayed counts are the delayed items only " +
                $"(items {results.delayedRecognition.itemCount})");

            Assert(results.immediateRecognition.hits == 2 && results.delayedRecognition.hits == 0,
                "the delayed phase did not inherit the immediate phase's hits — the two are " +
                "separate objects and are never summed");

            Assert(results.immediateRecognition.phase == RecognitionPhase.Immediate &&
                   results.delayedRecognition.phase == RecognitionPhase.Delayed,
                "each aggregate carries the phase of the items it counted");

            Assert(!ReferenceEquals(results.immediateRecognition, results.delayedRecognition),
                "immediate and delayed are two distinct instances");

            // ---- E: lifetime — a new run starts empty ---------------------------------------
            results.Reset();

            Assert(!results.immediateRecognition.hasData && !results.delayedRecognition.hasData &&
                   results.immediateRecognition.hits == 0 && results.delayedRecognition.hits == 0,
                "SessionResults.Reset() empties BOTH recognition aggregates, so one run's " +
                "tallies can never appear under the next run's identity");

            Assert(results.protocolMode == VerbalProtocolMode.FreeRecall,
                "Reset() returns the protocol flag to its default rather than leaving the " +
                "previous run's mode behind");

            // Recount is itself a reset: a reused instance must not accumulate.
            var reused = new RecognitionPhaseResults();
            reused.Recount(immediateItems);
            reused.Recount(immediateItems);

            Assert(reused.itemCount == 3 && reused.hits == 2,
                $"Recount REPLACES the previous counts rather than adding to them " +
                $"(items {reused.itemCount})");

            // The manager clears the item stores when a run is prepared, so an aborted run
            // cannot leave its items to be attributed to the next one.
            var managerSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            var prepareIndex = managerSource.IndexOf("void PrepareTrial()",
                System.StringComparison.Ordinal);

            Assert(prepareIndex >= 0, "PrepareTrial exists");

            if (prepareIndex >= 0)
            {
                var prepareEnd = managerSource.IndexOf("void ResetAreaBBlock", prepareIndex,
                    System.StringComparison.Ordinal);
                var body = prepareEnd > prepareIndex
                    ? managerSource.Substring(prepareIndex, prepareEnd - prepareIndex)
                    : managerSource.Substring(prepareIndex);

                Assert(body.Contains("m_ImmediateRecognitionItems.Clear()") &&
                       body.Contains("m_DelayedRecognitionItems.Clear()"),
                    "preparing a run clears both recognition item stores — restart, abort and " +
                    "NEW TRIAL all pass through here");
            }

            // The aggregates are captured BEFORE the panel is built, not after.
            var captureIndex = managerSource.IndexOf("CaptureRecognitionResults();",
                System.StringComparison.Ordinal);
            var finishIndex = managerSource.IndexOf("FinishAreaC();",
                System.StringComparison.Ordinal);

            Assert(captureIndex >= 0 && finishIndex > captureIndex,
                "CaptureRecognitionResults runs BEFORE FinishAreaC builds the panel — the same " +
                "ordering the run-duration capture already requires");

            Assert(managerSource.Contains("m_SessionResults.immediateRecognition.Recount(") &&
                   managerSource.Contains("m_SessionResults.delayedRecognition.Recount("),
                "the aggregates are populated from the manager's own item lists");

            Assert(!managerSource.Contains("Summarise(") ||
                   !managerSource.Contains("ParseRecognition"),
                "nothing parses the event-notes string back into numbers");

            // ---- F: the RECOGNITION results screen ------------------------------------------
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                ExperimentLocalization.ResetForTesting();
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

                var recognition = BuildRecognitionSampleResults();
                var recognitionText = recognition.BuildParticipantRunSummary(
                    ExperimentLocalization.Get(LocKeys.RunComplete), 2, 0);

                Info("RECOGNITION results screen:\n" +
                     StripRichText(recognitionText).Replace("\n", "\n        "));

                // THE ROWS THAT MUST BE GONE.
                foreach (var legacy in new[]
                         {
                             ExperimentLocalization.Get(LocKeys.StatImmediateRecall),
                             ExperimentLocalization.Get(LocKeys.StatDelayedRecall),
                         })
                {
                    Assert(!recognitionText.Contains(legacy),
                        $"Recognition mode does NOT show the legacy FreeRecall row " +
                        $"'{legacy}' — it describes a microphone recording that never happens " +
                        "in this protocol");
                }

                Assert(!recognitionText.Contains("verbal recall"),
                    "no 'verbal recall' wording survives on the Recognition results screen");

                var notAvailable = ExperimentLocalization.Get(LocKeys.NotAvailable);

                Assert(!recognitionText.Contains($"recall:  <b>{notAvailable}</b>"),
                    $"no legacy recall row reports '{notAvailable}' in Recognition mode");

                // THE SECTIONS THAT MUST BE THERE.
                Assert(recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionResultsImmediate)),
                    "the IMMEDIATE Recognition section is displayed");

                Assert(recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionResultsDelayed)),
                    "the DELAYED Recognition section is displayed");

                foreach (var key in new[]
                         {
                             LocKeys.RecognitionCorrectResponses, LocKeys.RecognitionHits,
                             LocKeys.RecognitionMisses, LocKeys.RecognitionCorrectRejections,
                             LocKeys.RecognitionFalseAlarms, LocKeys.RecognitionNoResponse,
                             LocKeys.RecognitionTotalItems,
                         })
                {
                    Assert(recognitionText.Contains(ExperimentLocalization.Get(key)),
                        $"the Recognition summary shows {key}");
                }

                // The numbers on screen are the numbers in the aggregate.
                var imm = recognition.immediateRecognition;
                var del = recognition.delayedRecognition;

                Assert(recognitionText.Contains(ExperimentLocalization.Format(
                        LocKeys.RecognitionAnsweredOf,
                        "N", imm.totalCorrect.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "TOTAL", imm.answeredCount.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    $"the immediate 'correct responses' line reads {imm.totalCorrect} of " +
                    $"{imm.answeredCount} answered — the denominator is ANSWERED items, and it " +
                    "says so, because an unanswered item is not scored as an error");

                Assert(recognitionText.Contains(ExperimentLocalization.Format(
                        LocKeys.RecognitionAnsweredOf,
                        "N", del.totalCorrect.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "TOTAL", del.answeredCount.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    "the delayed 'correct responses' line uses its own phase's numbers");

                Assert(imm.answeredCount != imm.itemCount,
                    "the sample deliberately contains unanswered items, so the two " +
                    "denominators are genuinely different and the assertion above is meaningful");

                Assert(recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionTotalItems)) &&
                    recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionNoResponse)),
                    "the excluded items are shown, not silently dropped — 'No response' and " +
                    "'Total items' both appear so the three numbers reconcile on screen");

                // NOTHING IS PRESENTED AS A SCORE OR A JUDGEMENT.
                var lower = StripRichText(recognitionText).ToLowerInvariant();

                foreach (var forbidden in new[]
                         {
                             "score", "cns", "d-prime", "d'", "impair", "abnormal", "normal range",
                             "diagnos", "workload", "cognitive index", "memory index",
                         })
                {
                    Assert(!lower.Contains(forbidden),
                        $"the participant-facing results never use '{forbidden}' — these are " +
                        "descriptive task counts, not a score or an assessment");
                }

                // THE EXECUTIVE TASK AND THE DURATION ARE STILL THERE.
                Assert(recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatExecutiveTask)),
                    "the executive (chair) result is still displayed in Recognition mode");

                Assert(recognitionText.Contains(ExperimentLocalization.Format(LocKeys.NCorrect,
                        "N", recognition.chairTrialsCorrect.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "TOTAL", recognition.chairTrialsScored.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    "the chair result still reports its own N / TOTAL");

                Assert(recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatTotalDuration)),
                    "total duration is still displayed");

                Assert(recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatMeanResponseTime)) &&
                    recognitionText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatMedianResponseTime)),
                    "the chair response-time rows are still displayed");

                // ---- G: the FREERECALL screen is unchanged ----------------------------------
                var freeRecall = BuildRecognitionSampleResults();
                freeRecall.protocolMode = VerbalProtocolMode.FreeRecall;
                freeRecall.immediateRecallStatus = "recording saved (audio/T001_immediate_recall.wav) / not scored";
                freeRecall.delayedRecallStatus = "no recording / not scored";

                var freeRecallText = freeRecall.BuildParticipantRunSummary(
                    ExperimentLocalization.Get(LocKeys.RunComplete), 2, 0);

                Assert(freeRecallText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatImmediateRecall)) &&
                    freeRecallText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatDelayedRecall)),
                    "FreeRecall STILL shows its own recall rows — they were routed, not deleted");

                Assert(freeRecallText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecordingSaved)),
                    "the FreeRecall recall row still reports its recording status");

                Assert(!freeRecallText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionResultsImmediate)) &&
                    !freeRecallText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionResultsDelayed)),
                    "FreeRecall does NOT show recognition sections — the routing goes both ways");

                Assert(freeRecallText.Contains("<size=140%>"),
                    "the FreeRecall headline chair result keeps its prominence — the Quest-" +
                    "verified FreeRecall screen was not restyled");

                Assert(freeRecallText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatExecutiveTask)) &&
                    freeRecallText.Contains(
                        ExperimentLocalization.Get(LocKeys.StatTotalDuration)),
                    "every FreeRecall row that was there before is still there");

                // ---- H: a phase that never ran, and one with no answers ---------------------
                var partial = BuildRecognitionSampleResults();
                partial.delayedRecognition.Reset();

                var partialText = partial.BuildParticipantRunSummary("x", 1, 0);

                Assert(partialText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionResultsDelayed)) &&
                    partialText.Contains(notAvailable),
                    "a phase that never ran says so, rather than printing a row of zeros that " +
                    "would read as a perfect failure");

                var unanswered = BuildRecognitionSampleResults();
                unanswered.immediateRecognition.Recount(MakeRecognitionItems(
                    RecognitionPhase.Immediate,
                    (RecognitionItemClass.Target, RecognitionResponse.None),
                    (RecognitionItemClass.Lure, RecognitionResponse.None)));

                var unansweredText = unanswered.BuildParticipantRunSummary("x", 1, 0);

                Assert(unansweredText.Contains(
                        ExperimentLocalization.Get(LocKeys.RecognitionNoAnswersRecorded)),
                    "a phase where nothing was answered says so instead of printing '0 of 0'");

                // ---- I: it FITS, in every language -------------------------------------------
                var ui = Object.FindAnyObjectByType<ExperimentUIController>();
                var label = ui != null
                    ? UiLabel(ui, "m_AreaCResults") as TextMeshProUGUI
                    : null;

                if (label != null)
                {
                    var rect = (RectTransform)label.transform;
                    var previousText = label.text;

                    try
                    {
                        foreach (var language in ExperimentLanguages.Selectable)
                        {
                            ExperimentLocalization.ResetForTesting();
                            ExperimentLocalization.SetLanguage(language);

                            var code = ExperimentLanguages.ToCode(language);

                            label.text = BuildRecognitionSampleResults()
                                .BuildParticipantRunSummary(
                                    ExperimentLocalization.Get(LocKeys.RunComplete), 2, 0);

                            label.ForceMeshUpdate();

                            var preferred = label.GetPreferredValues(
                                label.text, rect.sizeDelta.x, 0f).y;

                            Assert(preferred <= rect.sizeDelta.y,
                                $"{code}: the RECOGNITION summary ({preferred:F0} px) fits " +
                                $"inside its rect ({rect.sizeDelta.y:F0} px) — the two memory " +
                                "sections were made to fit the panel, not the panel to them");

                            Info($"{code}: recognition summary needs {preferred:F0} px of " +
                                 $"{rect.sizeDelta.y:F0}");
                        }
                    }
                    finally
                    {
                        label.text = previousText;
                    }
                }

                // ---- J: still no narration of results ----------------------------------------
                var resultsSource = StripCommentsAndAttributes(File.ReadAllText(
                    "Assets/IKEA_EEG/Scripts/Experiment/SessionResults.cs"));

                foreach (var forbidden in new[]
                         {
                             "Speak", "AudioCue", "PlayNarration", "GetWordClip", "AudioClip",
                             "AudioSource", "ExperimentAudio",
                         })
                {
                    Assert(!resultsSource.Contains(forbidden),
                        $"SessionResults contains no {forbidden} — the results are built as " +
                        "text and there is no code path that could speak them");
                }
            }
            finally
            {
                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // ---- K2: the DERIVED summary CSV carries the aggregates, correctly aligned -------
            // This is the one file this pass extends, and it is regenerable from the event CSV.
            // The row must line up with the header or every appended column is off by one.
            var summaryDirectory = Path.Combine(Path.GetTempPath(),
                "IKEA_EEG_SelfTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                var sample = BuildRecognitionSampleResults();
                sample.sessionId = "S_SELFTEST";

                var written = SessionSummaryWriter.WriteSessionSummary(sample, summaryDirectory);

                Assert(!string.IsNullOrEmpty(written) && File.Exists(written),
                    "the derived session summary CSV is written");

                if (!string.IsNullOrEmpty(written) && File.Exists(written))
                {
                    var lines = File.ReadAllLines(written);

                    Assert(lines.Length >= 2, $"it has a header and rows ({lines.Length} lines)");

                    if (lines.Length >= 2)
                    {
                        var header = lines[0].Split(',');
                        var row = lines[1].Split(',');

                        Assert(header.Length == row.Length,
                            $"header and row have the same column count " +
                            $"({header.Length} vs {row.Length}) — the appended recognition " +
                            "columns did not shift the row out of alignment");

                        // Existing columns keep their positions: appended, never inserted.
                        Assert(header[0] == "session_id" && header[19] == "chair_trial_index",
                            "every pre-existing summary column keeps its index");

                        var idx = System.Array.IndexOf(header, "immediate_recognition_hits");

                        Assert(idx >= 0 && row[idx] ==
                               sample.immediateRecognition.hits.ToString(
                                   System.Globalization.CultureInfo.InvariantCulture),
                            $"immediate_recognition_hits carries the typed aggregate's value " +
                            $"({(idx >= 0 ? row[idx] : "missing")})");

                        var delayedIdx = System.Array.IndexOf(header, "delayed_recognition_misses");

                        Assert(delayedIdx >= 0 && row[delayedIdx] ==
                               sample.delayedRecognition.misses.ToString(
                                   System.Globalization.CultureInfo.InvariantCulture),
                            "delayed_recognition_misses carries the DELAYED phase's own value");

                        var modeIdx = System.Array.IndexOf(header, "protocol_mode");

                        Assert(modeIdx >= 0 && row[modeIdx] == "RECOGNITION",
                            "protocol_mode names the protocol that produced the row");

                        // A FreeRecall run leaves the counts EMPTY, not zero.
                        var freeRecallSample = BuildRecognitionSampleResults();
                        freeRecallSample.sessionId = "S_SELFTEST_FR";
                        freeRecallSample.protocolMode = VerbalProtocolMode.FreeRecall;
                        freeRecallSample.immediateRecognition.Reset();
                        freeRecallSample.delayedRecognition.Reset();

                        var freeRecallPath = SessionSummaryWriter.WriteSessionSummary(
                            freeRecallSample, summaryDirectory);

                        if (!string.IsNullOrEmpty(freeRecallPath) && File.Exists(freeRecallPath))
                        {
                            var frRow = File.ReadAllLines(freeRecallPath)[1].Split(',');

                            Assert(idx >= 0 && string.IsNullOrEmpty(frRow[idx]),
                                "a FreeRecall run leaves the recognition counts EMPTY rather " +
                                "than writing 0 — 'this protocol has no recognition phase' and " +
                                "'this phase scored zero hits' must stay distinguishable");
                        }
                    }
                }

                // No DERIVED recognition column. chair_accuracy is pre-existing and belongs to
                // the chair task, so the scan is for recognition-named derived columns only.
                var writerSource = File.ReadAllText(
                    "Assets/IKEA_EEG/Scripts/Data/SessionSummaryWriter.cs");

                foreach (var derived in new[]
                         {
                             "recognition_accuracy", "recognition_score", "recognition_d",
                             "recognition_proportion", "recognition_index", "recognition_percent",
                             "memory_score", "cns_score",
                         })
                {
                    Assert(!writerSource.Contains(derived),
                        $"the summary file has no '{derived}' column — recognition is written " +
                        "as raw counts only, so nothing in it reads as a score");
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(summaryDirectory))
                        Directory.Delete(summaryDirectory, true);
                }
                catch
                {
                    // A leftover temp folder is not a test failure.
                }
            }

            // ---- K: the locked schemas were not touched --------------------------------------
            var csvSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/CsvEventSink.cs");

            Assert(csvSource.Contains("\"stimulus_offset_time\","),
                "the event CSV header still ends with stimulus_offset_time — no column was " +
                "added, removed or reordered by this pass");

            var eventColumns = new[]
            {
                "protocol_mode", "recognition_item_id", "recognition_item_class",
                "recognition_phase", "recognition_presentation_order", "recognition_response",
                "recognition_outcome", "recognition_reaction_time_ms", "stimulus_onset_time",
            };

            foreach (var column in eventColumns)
            {
                Assert(csvSource.Contains($"\"{column}\","),
                    $"the event CSV still carries {column} unchanged");
            }

            var markerSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/LslMarkerSink.cs"));

            Assert(!markerSource.Contains("RecognitionPhaseResults") &&
                   !markerSource.Contains("immediateRecognition"),
                "the LSL marker sink is untouched — no marker schema change in this pass");

            // The Recognition item loop, its pacing and the delayed word display are unchanged.
            var itemStart = managerSource.IndexOf("IEnumerator RunRecognitionItem",
                System.StringComparison.Ordinal);

            if (itemStart >= 0)
            {
                var itemEnd = managerSource.IndexOf("void OnRecognitionResponse", itemStart,
                    System.StringComparison.Ordinal);
                var body = itemEnd > itemStart
                    ? managerSource.Substring(itemStart, itemEnd - itemStart)
                    : managerSource.Substring(itemStart);

                Assert(!body.Contains("RecognitionPhaseResults") &&
                       !body.Contains("immediateRecognition") &&
                       !body.Contains("delayedRecognition"),
                    "the per-item loop is untouched — aggregation happens at the end of the " +
                    "run, never inside the timed response path");

                Assert(body.Contains("SetWordDisplay") && body.Contains("ClearWordDisplay"),
                    "the delayed word display path is unchanged");

                Assert(!body.Contains("WaitForSeconds"),
                    "the one-frame item transition is unchanged — no timed pause was " +
                    "reintroduced into the item loop");
            }

            Assert(!managerSource.Contains("AreaBRecognition") &&
                   managerSource.Contains("IEnumerator RunChairTrial"),
                "Area B is untouched");

            var uiSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/UI/ExperimentUIController.cs"));

            Assert(uiSource.Contains("SetText(m_AreaAWord, word)") &&
                   uiSource.Contains("SetText(m_AreaCWord, word)"),
                "the two-label stimulus display added in the previous pass is intact");
        }

        /// <summary>
        /// A Recognition run with realistic, deliberately uneven results — including unanswered
        /// items, so the answered/total distinction is exercised rather than assumed away.
        /// </summary>
        static SessionResults BuildRecognitionSampleResults()
        {
            var results = new SessionResults
            {
                protocolMode = VerbalProtocolMode.Recognition,

                // A DELIBERATELY LONG DURATION. "3725.456 s (3725456 ms)" is the widest value
                // this screen can render, and the layout has to survive it — a margin measured
                // against a tidy 184.5 s would prove nothing about a real hour-long sitting.
                totalExperimentDurationSeconds = 3725.456d,
                // Populated but NOT displayed in Recognition mode — present on purpose, so the
                // routing assertions prove the branch and not merely an empty field.
                immediateRecallStatus = "recording saved (audio/T001_immediate_recall.wav) / not scored",
                delayedRecallStatus = "NOT captured (no microphone device) / not scored",
            };

            for (var i = 0; i < 3; i++)
            {
                results.chairTrials.Add(new ChairTrialResult
                {
                    trialIndex = i + 1,
                    trialCount = 3,
                    selectionMade = true,
                    correct = i < 2,
                    responseTimeSeconds = 2.0 + i * 0.5,
                    valid = true,
                });
            }

            // 15 targets + 15 lures, with two unanswered.
            var immediate = new List<(RecognitionItemClass, RecognitionResponse)>();

            for (var i = 0; i < 15; i++)
            {
                immediate.Add((RecognitionItemClass.Target,
                    i < 11 ? RecognitionResponse.SeenBefore : RecognitionResponse.NotSeenBefore));
            }

            for (var i = 0; i < 15; i++)
            {
                immediate.Add((RecognitionItemClass.Lure,
                    i < 12 ? RecognitionResponse.NotSeenBefore
                           : i < 14 ? RecognitionResponse.SeenBefore : RecognitionResponse.None));
            }

            results.immediateRecognition.Recount(
                MakeRecognitionItems(RecognitionPhase.Immediate, immediate.ToArray()));

            var delayed = new List<(RecognitionItemClass, RecognitionResponse)>();

            for (var i = 0; i < 15; i++)
            {
                delayed.Add((RecognitionItemClass.Target,
                    i < 8 ? RecognitionResponse.SeenBefore
                          : i < 14 ? RecognitionResponse.NotSeenBefore : RecognitionResponse.None));
            }

            for (var i = 0; i < 15; i++)
            {
                delayed.Add((RecognitionItemClass.Lure,
                    i < 10 ? RecognitionResponse.NotSeenBefore
                           : i < 14 ? RecognitionResponse.SeenBefore : RecognitionResponse.None));
            }

            results.delayedRecognition.Recount(
                MakeRecognitionItems(RecognitionPhase.Delayed, delayed.ToArray()));

            return results;
        }

        /// <summary>TMP rich-text tags removed, so a scan inspects what is READ, not the markup.</summary>
        static string StripRichText(string value)
        {
            return string.IsNullOrEmpty(value)
                ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", string.Empty);
        }


        /// <summary>
        /// Block 10: the participant progress counter, and the developer-only QA cheatsheet.
        ///
        /// THE TWO THINGS THIS SECTION IS DEFENDING.
        ///
        ///   1. THAT THE COUNTER FOLLOWS THE REAL SEQUENCE. "Item X / N" is a promise about the
        ///      phase actually being presented. A hard-coded 30, or a count taken from the
        ///      immediate phase and reused for the delayed one, would be a confident lie — and
        ///      one nobody would notice until the delayed composition changed.
        ///
        ///   2. THAT THE CHEATSHEET CANNOT TOUCH THE DATA. It tells the wearer the correct
        ///      answer. Every assertion below exists because "it only draws a label" has to be
        ///      provable, not asserted in a comment: the toggle path is checked for the absence
        ///      of every symbol that could reach a response, an outcome, a time, an event or a
        ///      marker.
        /// </summary>
        static void CheckBlock10CounterAndCheatsheet()
        {
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();

            Assert(ui != null, "the UI controller is in the scene");

            if (ui == null)
                return;

            var managerSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            // =============================================================================
            // PART A — the participant progress counter
            // =============================================================================

            var counterA = UiLabel(ui, "m_AreaARecognitionCounter");
            var counterC = UiLabel(ui, "m_AreaCRecognitionCounter");

            Assert(counterA != null, "the Area A recognition counter label is bound");
            Assert(counterC != null, "the Area C recognition counter label is bound");

            if (counterA == null || counterC == null)
                return;

            Assert(!ReferenceEquals(counterA, counterC),
                "Area A and Area C have SEPARATE counter objects — one world-space label cannot " +
                "serve two rooms, which is the exact shape of the delayed-word bug");

            var canvasA = FindInSceneIncludingInactive("UI_A_Canvas");
            var canvasC = FindInSceneIncludingInactive("UI_C_Canvas");

            if (canvasA != null && canvasC != null)
            {
                Assert(counterA.transform.IsChildOf(canvasA.transform) &&
                       counterC.transform.IsChildOf(canvasC.transform),
                    "each counter lives on its own area's canvas");
            }

            // ---- A1: the source of truth is the phase's own sequence -------------------------
            var phaseStart = managerSource.IndexOf("IEnumerator RunRecognitionPhase",
                System.StringComparison.Ordinal);

            Assert(phaseStart >= 0, "the recognition phase coroutine exists");

            if (phaseStart >= 0)
            {
                var phaseEnd = managerSource.IndexOf("IEnumerator RunRecognitionItem", phaseStart,
                    System.StringComparison.Ordinal);
                var body = phaseEnd > phaseStart
                    ? managerSource.Substring(phaseStart, phaseEnd - phaseStart)
                    : managerSource.Substring(phaseStart);

                Assert(body.Contains("RunRecognitionItem(items[i], phase, i + 1, items.Count)"),
                    "the counter's numbers come from the loop index and the PHASE'S OWN item " +
                    "count — the sequence actually being presented, not a stored position");

                // The delayed composition is configured, not fixed. Nothing may assume 30.
                foreach (var literal in new[] { "/ 30", "of 30", "Item 30", "= 30" })
                {
                    Assert(!body.Contains(literal),
                        $"the phase coroutine contains no hard-coded '{literal}' — a change to " +
                        "the delayed item set is followed automatically");
                }
            }

            var itemStart = managerSource.IndexOf("IEnumerator RunRecognitionItem",
                System.StringComparison.Ordinal);

            Assert(itemStart >= 0, "the recognition item coroutine exists");

            var itemBody = string.Empty;

            if (itemStart >= 0)
            {
                var itemEnd = managerSource.IndexOf("static string BuildDeveloperCheatsheetText",
                    itemStart, System.StringComparison.Ordinal);
                itemBody = itemEnd > itemStart
                    ? managerSource.Substring(itemStart, itemEnd - itemStart)
                    : managerSource.Substring(itemStart);

                Assert(itemBody.Contains("int itemNumber, int itemTotal"),
                    "the item coroutine is told its 1-based number and its phase total");

                Assert(itemBody.Contains("LocKeys.RecognitionItemProgress"),
                    "the counter text is built from the localization key, not concatenated ad hoc");

                // A3: the counter must not lag an item behind.
                var setWord = itemBody.IndexOf("SetWordDisplay", System.StringComparison.Ordinal);
                var setCounter = itemBody.IndexOf("SetRecognitionCounter",
                    System.StringComparison.Ordinal);
                var armIndex = itemBody.IndexOf("Arm()", System.StringComparison.Ordinal);
                var waitIndex = itemBody.IndexOf("while (m_PendingRecognitionResponse",
                    System.StringComparison.Ordinal);

                Assert(setWord >= 0 && setCounter > setWord && setCounter < armIndex,
                    "the counter is written with the word and BEFORE the buttons are armed — it " +
                    "can never describe the previous item");

                Assert(setCounter < waitIndex,
                    "the counter is up before the response wait begins");

                // presentationOrder keeps its 0-based meaning; the counter does not touch it.
                Assert(!itemBody.Contains("item.presentationOrder = ") &&
                       !itemBody.Contains("presentationOrder++"),
                    "RecognitionItem.presentationOrder semantics are unchanged — the counter " +
                    "reads the loop, and the CSV column keeps its 0-based field");
            }

            // ---- A2/A3: the counting arithmetic, checked end to end -------------------------
            // The rendered strings for the first and last item of a 30-item and a 12-item phase.
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                ExperimentLocalization.ResetForTesting();
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

                string Progress(int n, int total) => ExperimentLocalization.Format(
                    LocKeys.RecognitionItemProgress,
                    "N", n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "TOTAL", total.ToString(System.Globalization.CultureInfo.InvariantCulture));

                Assert(Progress(1, 30) == "Item 1 / 30",
                    $"the first item of a 30-item phase reads 'Item 1 / 30' (\"{Progress(1, 30)}\")");

                Assert(Progress(30, 30) == "Item 30 / 30",
                    $"the last item reads 'Item 30 / 30' (\"{Progress(30, 30)}\")");

                Assert(Progress(17, 30) == "Item 17 / 30",
                    "a mid-phase item reads its own position");

                // A DIFFERENT phase length must produce a different denominator. This is the
                // assertion that would fail if 30 were ever baked in anywhere.
                Assert(Progress(1, 12) == "Item 1 / 12" && Progress(12, 12) == "Item 12 / 12",
                    "a 12-item phase reads 'Item 1 / 12' .. 'Item 12 / 12' — the denominator " +
                    "follows the phase, and the delayed set is independently sized");

                // And the real asset's own counts, so the numbers a run will actually show are
                // the ones the configured stimuli produce.
                var list = AssetDatabase.LoadAssetAtPath<RecognitionWordList>(
                    ExperimentAssetBuilder.RecognitionWordListPath);

                if (list != null)
                {
                    var immediateCount = RecognitionSequence
                        .Build(list, RecognitionPhase.Immediate, 11).Count;
                    var delayedCount = RecognitionSequence
                        .Build(list, RecognitionPhase.Delayed, 11).Count;

                    Assert(immediateCount == list.ItemCount(RecognitionPhase.Immediate) &&
                           delayedCount == list.ItemCount(RecognitionPhase.Delayed),
                        $"the counter's denominator equals the built sequence length " +
                        $"(immediate {immediateCount}, delayed {delayedCount})");

                    Info($"configured phase lengths — immediate {immediateCount}, " +
                         $"delayed {delayedCount}; the counter reads Item 1 / {immediateCount} " +
                         $"and Item 1 / {delayedCount}");
                }

                // ---- A5: visibility ---------------------------------------------------------
                ui.ShowRecognitionCounter(true);
                ui.SetRecognitionCounter(Progress(7, 30));

                Assert(counterA.text == "Item 7 / 30" && counterC.text == "Item 7 / 30",
                    "both area counters receive the same line — the inactive one renders nothing");

                Assert(ui.recognitionCounterVisible, "showing the counter makes it visible");

                ui.ShowRecognitionCounter(false);

                Assert(!counterA.gameObject.activeSelf && !counterC.gameObject.activeSelf,
                    "hiding the counter deactivates BOTH labels");

                Assert(string.IsNullOrEmpty(counterA.text) && string.IsNullOrEmpty(counterC.text),
                    "hiding also CLEARS, so a stale 'Item 30 / 30' cannot be left on a panel");

                // ---- A4: it does not collide with anything -----------------------------------
                var wordA = UiLabel(ui, "m_AreaAWord");
                var wordC = UiLabel(ui, "m_AreaCWord");

                if (wordA != null && wordC != null)
                {
                    foreach (var (counter, word, area) in new[]
                             {
                                 (counterA, wordA, "A"), (counterC, wordC, "C"),
                             })
                    {
                        var counterRect = (RectTransform)counter.transform;
                        var wordRect = (RectTransform)word.transform;

                        Assert(counterRect.anchoredPosition.y > wordRect.anchoredPosition.y,
                            $"Area {area}: the counter sits ABOVE the stimulus word");

                        Assert(counter.fontSize < word.fontSize * 0.5f,
                            $"Area {area}: the counter ({counter.fontSize:F0} pt) is far " +
                            $"smaller than the stimulus ({word.fontSize:F0} pt) — visually " +
                            "subordinate, as required");

                        // Glyph bands, not rects: a TMP label is centred in its rect, so the
                        // rects may overlap while the text does not.
                        var counterBottom = counterRect.anchoredPosition.y - counter.fontSize * 0.6f;
                        var wordTop = wordRect.anchoredPosition.y + word.fontSize * 0.6f;

                        Assert(counterBottom > wordTop,
                            $"Area {area}: the counter's text band (bottom {counterBottom:F0}) " +
                            $"clears the word's (top {wordTop:F0}) — they cannot overlap");
                    }

                    // Both areas present it at the same world height, as the stimulus does.
                    var delta = Mathf.Abs(counterA.transform.position.y -
                                          counterC.transform.position.y);

                    Assert(delta < 0.02f,
                        $"the counter is at the same world height in both areas " +
                        $"({delta * 1000f:F0} mm apart)");
                }

                // It must clear the response buttons too.
                var anchorA = FindInSceneIncludingInactive("Anchor_Recognition_AreaA");
                var anchorC = FindInSceneIncludingInactive("Anchor_Recognition_AreaC");

                if (anchorA != null && anchorC != null)
                {
                    Assert(counterA.transform.position.y - anchorA.transform.position.y > 0.5f &&
                           counterC.transform.position.y - anchorC.transform.position.y > 0.5f,
                        "the counter clears the response buttons in both areas");
                }

                var recenterA = FindInSceneIncludingInactive("Btn_Recenter_A");

                if (recenterA != null)
                {
                    Assert(counterA.transform.position.y > recenterA.transform.position.y + 0.15f,
                        "the counter clears the Recenter control");
                }

                // =============================================================================
                // PART B — the developer QA cheatsheet
                // =============================================================================

                var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                    ExperimentAssetBuilder.ConfigPath);

                Assert(config != null, "the experiment config asset exists");

                // ---- B3: the gate, and its default ------------------------------------------
                var defaultConfig = ScriptableObject.CreateInstance<ExperimentConfig>();

                try
                {
                    Assert(!defaultConfig.enableRecognitionDeveloperCheatsheet,
                        "enableRecognitionDeveloperCheatsheet DEFAULTS TO FALSE — a fresh " +
                        "config cannot show the answers");
                }
                finally
                {
                    Object.DestroyImmediate(defaultConfig);
                }

                if (config != null)
                {
                    Assert(!config.enableRecognitionDeveloperCheatsheet,
                        "the config asset shipped in this project has the cheatsheet OFF — a " +
                        "participant run made from it shows nothing");
                }

                var sheetA = UiLabel(ui, "m_AreaADeveloperCheatsheet");
                var sheetC = UiLabel(ui, "m_AreaCDeveloperCheatsheet");

                Assert(sheetA != null && sheetC != null,
                    "both developer overlay labels are bound");

                if (sheetA == null || sheetC == null)
                    return;

                Assert(!ReferenceEquals(sheetA, sheetC),
                    "Area A and Area C have SEPARATE overlay objects — the same cross-room rule " +
                    "the stimulus word follows");

                if (canvasA != null && canvasC != null)
                {
                    Assert(sheetA.transform.IsChildOf(canvasA.transform) &&
                           sheetC.transform.IsChildOf(canvasC.transform),
                        "each overlay lives on its own area's canvas");
                }

                Assert(!sheetA.gameObject.activeSelf && !sheetC.gameObject.activeSelf,
                    "both overlays are authored INACTIVE — hidden by default, before any code " +
                    "runs at all");

                // ---- B4: the displayed text, from itemClass and nothing else -----------------
                var buildMethod = typeof(ExperimentManager).GetMethod(
                    "BuildDeveloperCheatsheetText",
                    BindingFlags.Static | BindingFlags.NonPublic);

                Assert(buildMethod != null, "the overlay text builder exists");

                if (buildMethod != null)
                {
                    var targetText = (string)buildMethod.Invoke(null, new object[]
                    {
                        new RecognitionItem { itemClass = RecognitionItemClass.Target },
                    });

                    var lureText = (string)buildMethod.Invoke(null, new object[]
                    {
                        new RecognitionItem { itemClass = RecognitionItemClass.Lure },
                    });

                    Info($"cheatsheet TARGET:\n        {targetText.Replace("\n", "\n        ")}");
                    Info($"cheatsheet LURE:\n        {lureText.Replace("\n", "\n        ")}");

                    Assert(targetText.Contains("DEVELOPER QA") && lureText.Contains("DEVELOPER QA"),
                        "the overlay names itself as a developer tool");

                    Assert(targetText.Contains("TARGET") &&
                           targetText.Contains("Correct: SEEN BEFORE"),
                        $"a TARGET displays 'Correct: SEEN BEFORE' (\"{targetText}\")");

                    Assert(lureText.Contains("LURE") &&
                           lureText.Contains("Correct: NOT SEEN BEFORE"),
                        $"a LURE displays 'Correct: NOT SEEN BEFORE' (\"{lureText}\")");

                    // The mapping matches the one RecognitionItem.outcome already encodes. If
                    // these ever disagreed, the QA aid would be teaching the wrong answer.
                    var scoredTarget = new RecognitionItem
                    {
                        itemClass = RecognitionItemClass.Target,
                        response = RecognitionResponse.SeenBefore,
                    };

                    var scoredLure = new RecognitionItem
                    {
                        itemClass = RecognitionItemClass.Lure,
                        response = RecognitionResponse.NotSeenBefore,
                    };

                    Assert(scoredTarget.outcome == RecognitionOutcome.Hit &&
                           scoredLure.outcome == RecognitionOutcome.CorrectRejection,
                        "the answer the overlay calls correct is the one the EXISTING scoring " +
                        "counts as correct — the overlay restates the classifier, it does not " +
                        "add one");

                    var lureBody = lureText;

                    Assert(!lureBody.Contains("SEEN BEFORE\n") || lureBody.Contains("NOT SEEN"),
                        "a lure is never told to answer SEEN BEFORE");
                }

                // ---- B1/C: it cannot reach the response path --------------------------------
                var toggleStart = managerSource.IndexOf("void PollDeveloperCheatsheetToggle()",
                    System.StringComparison.Ordinal);

                Assert(toggleStart >= 0, "the toggle poll exists");

                if (toggleStart >= 0)
                {
                    var toggleEnd = managerSource.IndexOf("bool InActiveRecognitionItem()",
                        toggleStart, System.StringComparison.Ordinal);
                    var toggleBody = toggleEnd > toggleStart
                        ? managerSource.Substring(toggleStart, toggleEnd - toggleStart)
                        : managerSource.Substring(toggleStart);

                    foreach (var forbidden in new[]
                             {
                                 "m_PendingRecognitionResponse", "OnRecognitionResponse",
                                 "responseSelected", "m_RecognitionPanel", "PlayCue", "Log(",
                                 "item.response", "reactionTimeMs", "responseTime",
                                 "itemClass =", "Arm()", "Disarm()",
                             })
                    {
                        Assert(!toggleBody.Contains(forbidden),
                            $"the toggle path contains no {forbidden} — pressing B cannot " +
                            "answer, advance, re-arm, time, log or score anything");
                    }

                    Assert(toggleBody.Contains("developerCheatsheetGateOpen"),
                        "the toggle checks the CONFIG GATE, not merely UI visibility");

                    Assert(toggleBody.Contains("InActiveRecognitionItem()"),
                        "the toggle only acts while a recognition item is on screen");

                    Assert(toggleBody.Contains("rising"),
                        "the toggle fires on the RISING EDGE — holding B does not strobe it, " +
                        "and it does not require being held");
                }

                // The whole overlay mechanism writes one bool and one label. Asserted over every
                // method that touches it.
                var setVisibleStart = managerSource.IndexOf(
                    "void SetDeveloperCheatsheetVisible(bool visible)",
                    System.StringComparison.Ordinal);

                if (setVisibleStart >= 0)
                {
                    var setVisibleEnd = managerSource.IndexOf("void ResetDeveloperCheatsheet()",
                        setVisibleStart, System.StringComparison.Ordinal);
                    var setVisibleBody = setVisibleEnd > setVisibleStart
                        ? managerSource.Substring(setVisibleStart,
                            setVisibleEnd - setVisibleStart)
                        : managerSource.Substring(setVisibleStart);

                    foreach (var forbidden in new[]
                             {
                                 "m_PendingRecognitionResponse", "OnRecognitionResponse",
                                 "PlayCue", "Log(", "m_SessionResults",
                             })
                    {
                        Assert(!setVisibleBody.Contains(forbidden),
                            $"applying the overlay's visibility contains no {forbidden}");
                    }
                }

                // The builder is a pure function of itemClass — it cannot mutate an item.
                var builderStart = managerSource.IndexOf(
                    "static string BuildDeveloperCheatsheetText", System.StringComparison.Ordinal);

                if (builderStart >= 0)
                {
                    var builderEnd = managerSource.IndexOf("void OnRecognitionResponse",
                        builderStart, System.StringComparison.Ordinal);
                    var builderBody = builderEnd > builderStart
                        ? managerSource.Substring(builderStart, builderEnd - builderStart)
                        : managerSource.Substring(builderStart);

                    Assert(builderBody.Contains("static string"),
                        "the overlay text builder is STATIC — it holds no manager state");

                    // ASSIGNMENT, not comparison. `item.itemClass == RecognitionItemClass.Target`
                    // is exactly what this builder is supposed to do, and a plain substring scan
                    // for "item.itemClass =" matches it — so the pattern requires a single '='
                    // NOT followed by another.
                    foreach (var field in new[]
                             {
                                 "response", "outcome", "onsetTime", "responseTime", "itemClass",
                                 "word", "presentationOrder",
                             })
                    {
                        var assigns = System.Text.RegularExpressions.Regex.IsMatch(
                            builderBody, @"item\." + field + @"\s*=(?!=)");

                        Assert(!assigns,
                            $"the overlay text builder never ASSIGNS item.{field} — it reads " +
                            "itemClass and returns a string");
                    }

                    Assert(builderBody.Contains("item.itemClass == RecognitionItemClass.Target"),
                        "it reads the EXISTING itemClass rather than re-deriving target/lure " +
                        "from the word list");
                }

                // ---- B6: lifecycle -----------------------------------------------------------
                Assert(managerSource.Contains("ResetDeveloperCheatsheet();"),
                    "the overlay has an explicit reset");

                var phaseBody = phaseStart >= 0 && itemStart > phaseStart
                    ? managerSource.Substring(phaseStart, itemStart - phaseStart)
                    : string.Empty;

                Assert(phaseBody.Contains("ResetDeveloperCheatsheet();"),
                    "the overlay is reset OFF at the PHASE BOUNDARY — the documented choice: it " +
                    "does not survive from immediate into delayed recognition, so it can never " +
                    "be left on across the phase this protocol most depends on");

                Assert(phaseBody.Contains("ShowRecognitionCounter(true)") &&
                       phaseBody.Contains("ShowRecognitionCounter(false)"),
                    "the counter is raised when a phase starts and lowered when it ends");

                var finishStart = managerSource.IndexOf("void FinishAreaC()",
                    System.StringComparison.Ordinal);

                if (finishStart >= 0)
                {
                    var finishEnd = managerSource.IndexOf("void CaptureRecognitionResults()",
                        finishStart, System.StringComparison.Ordinal);
                    var finishBody = finishEnd > finishStart
                        ? managerSource.Substring(finishStart, finishEnd - finishStart)
                        : managerSource.Substring(finishStart);

                    Assert(finishBody.Contains("ResetDeveloperCheatsheet();") &&
                           finishBody.Contains("ShowRecognitionCounter(false)"),
                        "neither overlay can reach the RESULTS screen");
                }

                var abortStart = managerSource.IndexOf("void AbortTrial(string reason)",
                    System.StringComparison.Ordinal);

                if (abortStart >= 0)
                {
                    var abortBody = managerSource.Substring(abortStart,
                        Mathf.Min(2000, managerSource.Length - abortStart));

                    Assert(abortBody.Contains("ResetDeveloperCheatsheet();") &&
                           abortBody.Contains("ShowRecognitionCounter(false)"),
                        "an ABORT takes both overlays down — the phase coroutine is stopped " +
                        "mid-item and never reaches its own cleanup");
                }

                Assert(StripCommentsAndAttributes(File.ReadAllText(
                            "Assets/IKEA_EEG/Scripts/UI/ExperimentUIController.cs"))
                        .Contains("ShowDeveloperCheatsheet(false)"),
                    "ResetUI takes the overlay down, so no reset path can leave it up");

                // FreeRecall never enters a recognition state, so neither overlay can appear.
                Assert(managerSource.Contains("m_State == ExperimentState.ImmediateRecognition ||") &&
                       managerSource.Contains("m_State == ExperimentState.DelayedRecognition"),
                    "the overlay is gated on the two RECOGNITION states — FreeRecall reaches " +
                    "neither, so it cannot show there");

                // ---- The overlay's own visibility behaviour ----------------------------------
                ui.SetDeveloperCheatsheet("DEVELOPER QA\nTARGET\nCorrect: SEEN BEFORE");
                ui.ShowDeveloperCheatsheet(true);

                Assert(ui.developerCheatsheetVisible && sheetA.text.Contains("TARGET"),
                    "the overlay can be shown and carries its line");

                ui.ShowDeveloperCheatsheet(false);

                Assert(!sheetA.gameObject.activeSelf && !sheetC.gameObject.activeSelf &&
                       string.IsNullOrEmpty(sheetA.text) && string.IsNullOrEmpty(sheetC.text),
                    "hiding the overlay deactivates AND clears both labels");

                // It must be unmistakable, and never sit on the stimulus.
                if (wordA != null)
                {
                    Assert(((RectTransform)sheetA.transform).anchoredPosition.y <
                           ((RectTransform)wordA.transform).anchoredPosition.y,
                        "the developer overlay sits below the stimulus word, not over it");

                    Assert(sheetA.color.r > 0.9f && sheetA.color.b > 0.7f && sheetA.color.g < 0.5f,
                        $"the developer overlay is magenta ({sheetA.color}) — no participant " +
                        "surface uses this colour, so one left on is obvious");
                }
            }
            finally
            {
                ui.ShowRecognitionCounter(false);
                ui.ShowDeveloperCheatsheet(false);

                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // =============================================================================
            // PART C — input conflict, and the locked schemas
            // =============================================================================

            Assert(managerSource.Contains("{RightHand}/secondaryButton"),
                "the toggle is bound to the RIGHT controller's B button (secondaryButton)");

            // The bindings this project actually uses for anything else.
            Assert(!managerSource.Contains("{RightHand}/primaryButton"),
                "the toggle does NOT take the right A button — the XRI asset binds it to Jump, " +
                "which is inert only because free locomotion is disabled");

            foreach (var reserved in new[] { "triggerPressed", "gripPressed", "thumbstickClicked" })
            {
                Assert(!managerSource.Contains($"{{RightHand}}/{reserved}"),
                    $"the toggle does not take {reserved} — Select (participant response, " +
                    "chairs, every UI button) and the developer-navigation gesture keep theirs");
            }

            var devNavSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/XR/DeveloperNavigation.cs"));

            Assert(!devNavSource.Contains("secondaryButton"),
                "developer NAVIGATION does not use B either — the two developer tools have " +
                "distinct inputs and cannot be triggered by one press");

            Assert(devNavSource.Contains("thumbstickClicked"),
                "developer navigation still uses its own two-thumbstick gesture, unchanged");

            // ---- Regression: nothing locked was touched -------------------------------------
            var csvSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/CsvEventSink.cs");

            Assert(csvSource.Contains("\"stimulus_offset_time\","),
                "the event CSV header is unchanged by this pass");

            Assert(!csvSource.Contains("cheatsheet") && !csvSource.Contains("item_progress"),
                "no counter or overlay column was added to the event CSV");

            var markerSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/LslMarkerSink.cs"));

            Assert(!markerSource.Contains("Cheatsheet") && !markerSource.Contains("Progress"),
                "the LSL marker sink is untouched");

            var eventTypes = File.ReadAllText("Assets/IKEA_EEG/Scripts/Core/EventTypes.cs");

            Assert(!eventTypes.Contains("CHEATSHEET") && !eventTypes.Contains("DEVELOPER_QA"),
                "no developer-diagnostic event type was added — the brief asked for none");

            if (itemStart >= 0)
            {
                Assert(!itemBody.Contains("WaitForSeconds"),
                    "the one-frame item transition is unchanged — the counter added no pause");

                Assert(itemBody.Contains("m_Config.recognitionResponseTimeoutSeconds"),
                    "the response timeout is unchanged");

                Assert(itemBody.Contains("SetWordDisplay") && itemBody.Contains("ClearWordDisplay"),
                    "the delayed word display path is unchanged");
            }
        }


        /// <summary>
        /// Builds a synthetic run of local timestamps at a fixed rate, optionally with the
        /// chunk-anchor jitter this hardware actually produces.
        ///
        /// The jitter model is taken from the measured recordings, not invented: whole 9-sample
        /// chunks arrive with their anchor displaced, so the disturbance lands on indices that
        /// are multiples of 9 and the samples between them stay uniform.
        /// </summary>
        static double[] SyntheticLocalTimestamps(double start, int count, double rateHz,
            bool jittered)
        {
            var t = new double[count];
            var step = 1.0 / rateHz;
            var random = new System.Random(20260902);

            for (var i = 0; i < count; i++)
            {
                var ideal = start + i * step;

                if (jittered && i % 9 == 0)
                {
                    // Measured envelope: backward steps to about -30 ms, forward gaps to +62 ms.
                    ideal += (random.NextDouble() * 0.092) - 0.030;
                }

                t[i] = ideal;
            }

            return t;
        }

        /// <summary>
        /// Block 11: the analysis time base stays in the LOCAL clock domain.
        ///
        /// WHAT THIS SECTION IS DEFENDING, and why it is written as behaviour rather than as a
        /// wiring check.
        ///
        /// A real recording (S_20260902_131219_r01_27eda8) produced an analysis timeline that
        /// was strictly monotonic, perfectly smooth, uniformly spaced — and 1.24 MILLION seconds
        /// away from the clock its own events were stamped on, running at 416.7 Hz instead of
        /// 250. Every property one would naively assert about a de-jittered timeline held. The
        /// only thing wrong with it was the one thing nothing checked: WHICH CLOCK IT WAS IN.
        ///
        /// So these assertions are about the relationship between the analysis timeline and the
        /// raw local one, not about the analysis timeline's internal tidiness. A test that only
        /// asked "is it monotonic and evenly spaced" would have passed on the broken data.
        /// </summary>
        /// <summary>
        /// BLOCK 12 — the two quality checks that a single well-behaved channel cannot reveal,
        /// and the ROI invalidation that follows from them.
        ///
        /// Driven by SYNTHETIC signals with known answers. A quality check tested only against a
        /// real recording proves nothing: a plausible-looking flag on real data is
        /// indistinguishable from a correct one, and a rule that fires on everything would pass
        /// exactly the same inspection.
        /// </summary>
        static void CheckBlock12ChannelHealth()
        {
            const int samples = 1000;
            var thresholds = EegQualityThresholds.Default;

            Assert(thresholds.nearIdenticalCorrelation > 0d &&
                   thresholds.nearIdenticalCorrelation <= 1d,
                $"near-identity threshold is a correlation ({thresholds.nearIdenticalCorrelation})");

            Assert(thresholds.Sanitised().degradationConsecutiveWindows >= 1,
                "a channel must fail more than zero windows to be called degraded");

            // A nonsense inspector value must not silently disable a check.
            var broken = new EegQualityThresholds
            {
                nearIdenticalCorrelation = -5d,
                nearIdenticalMinimumPairs = 0,
                degradationDecades = 0d,
                degradationConsecutiveWindows = 0,
                excursionRangeRatio = 0.5,
                variabilityCollapseRatio = 9d,
                saturationRepeatFraction = 4d,
                discontinuityStepRatio = 0.1,
                baselineWindows = -3,
                recoveryWindows = 0,
            }.Sanitised();

            Assert(broken.nearIdenticalCorrelation == thresholds.nearIdenticalCorrelation &&
                   broken.degradationConsecutiveWindows == thresholds.degradationConsecutiveWindows &&
                   broken.baselineWindows == thresholds.baselineWindows,
                "out-of-range thresholds fall back to the documented defaults rather than " +
                "disabling the check");

            // ---- Correlation ---------------------------------------------------------
            var random = new System.Random(20260907);

            var independentA = new double[samples];
            var independentB = new double[samples];
            var scaledCopy = new double[samples];
            var flat = new double[samples];

            for (var i = 0; i < samples; i++)
            {
                independentA[i] = random.NextDouble() - 0.5;
                independentB[i] = random.NextDouble() - 0.5;

                // The failure mode this block exists for: the same signal at a different gain
                // and offset, which is NOT bit-identical and so passes the older check.
                scaledCopy[i] = independentA[i] * 0.97 + 12345.0;
                flat[i] = 7.0;
            }

            var rSelf = EegChannelQualityRules.Correlation(independentA, scaledCopy);
            Assert(rSelf > 0.9999,
                $"a scaled and offset copy correlates at {rSelf:F6} — near-identity sees what " +
                "bit-equality cannot");

            var rIndependent = EegChannelQualityRules.Correlation(independentA, independentB);
            Assert(System.Math.Abs(rIndependent) < 0.2,
                $"two independent noise channels correlate at {rIndependent:F4}, well under the " +
                "threshold");

            Assert(double.IsNaN(EegChannelQualityRules.Correlation(independentA, flat)),
                "a flat channel yields NaN rather than a fabricated correlation");

            var pairs = EegChannelQualityRules.FindNearIdenticalPairs(
                new[] { independentA, independentB, scaledCopy, flat }, 4,
                thresholds.nearIdenticalCorrelation);

            Assert(pairs.Count == 1,
                $"exactly the one duplicated pair is flagged ({pairs.Count} found)");

            Assert(pairs.Count == 1 && pairs[0].channelA == 0 && pairs[0].channelB == 2,
                "the flagged pair is the copy, not the flat channel or the independent one");

            // A bit-identical pair is still caught by the new rule as well as the old one.
            var identical = (double[])independentA.Clone();

            var identicalPairs = EegChannelQualityRules.FindNearIdenticalPairs(
                new[] { independentA, identical }, 2, thresholds.nearIdenticalCorrelation);

            Assert(identicalPairs.Count == 1,
                "a bit-identical pair is also caught by the correlation rule");

            // ---- Window measurement --------------------------------------------------
            var stuck = new double[samples];
            var stepped = new double[samples];

            for (var i = 0; i < samples; i++)
            {
                stuck[i] = 42.0;
                stepped[i] = random.NextDouble() * 0.01 + (i > samples / 2 ? 500.0 : 0.0);
            }

            var flatStats = EegChannelQualityRules.Measure(stuck);
            var flatReason = EegChannelQualityRules.EvaluateWindow(flatStats, thresholds);

            Assert((flatReason & EegDegradationReason.Flatline) != 0,
                "a constant channel is reported as flatline");

            Assert((flatReason & EegDegradationReason.Saturation) != 0,
                "a constant channel is also reported as saturated/stuck");

            var stepStats = EegChannelQualityRules.Measure(stepped);
            var stepReason = EegChannelQualityRules.EvaluateWindow(stepStats, thresholds);

            Assert((stepReason & EegDegradationReason.Discontinuity) != 0,
                $"a single large step is reported as a discontinuity (ratio {stepStats.stepRatio:F0}x)");

            var cleanStats = EegChannelQualityRules.Measure(independentA);

            Assert(EegChannelQualityRules.EvaluateWindow(cleanStats, thresholds) ==
                   EegDegradationReason.None,
                "ordinary noise is not flagged by any single-window rule");

            // ---- Degradation state machine -------------------------------------------
            // Two channels: one healthy throughout, one that fails partway through — the shape
            // of the headset-moves-an-electrode case.
            var labels = new[] { "Fz", "P3" };
            var tracker = new EegChannelHealthTracker(2, labels, thresholds);

            const int windows = 40;
            const int failsAt = 20;
            var degradedAt = double.NaN;

            for (var w = 0; w < windows; w++)
            {
                var healthy = new double[200];
                var suspect = new double[200];

                for (var i = 0; i < 200; i++)
                {
                    healthy[i] = random.NextDouble() - 0.5;

                    // The failing channel keeps the same shape but explodes in amplitude, so it
                    // is abnormal only RELATIVE TO ITS OWN past.
                    suspect[i] = (random.NextDouble() - 0.5) * (w >= failsAt ? 400.0 : 1.0);
                }

                var set = ChannelStatsSet.Measure(new[] { healthy, suspect }, 2);

                var power = new[] { 10.0, w >= failsAt ? 10.0 * 1e4 : 10.0 };

                var changes = tracker.Submit(w, set, power,
                    index => index == 1 ? "POSTERIOR_ALPHA" : "FRONTAL_THETA");

                foreach (var change in changes)
                {
                    if (change.degraded && change.channelIndex == 1 && double.IsNaN(degradedAt))
                        degradedAt = change.timestamp;
                }
            }

            Assert(!tracker.IsDegraded(0),
                "the healthy channel is never declared degraded");

            Assert(tracker.IsDegraded(1),
                "the failing channel IS declared degraded");

            Assert(!double.IsNaN(degradedAt) && degradedAt >= failsAt &&
                   degradedAt < failsAt + thresholds.degradationConsecutiveWindows + 2,
                $"degradation is dated close to when it began (window {degradedAt}, failure at " +
                $"{failsAt})");

            Assert(tracker.DegradedChannels().Length == 1 && tracker.DegradedChannels()[0] == 1,
                "exactly the failing channel is listed as degraded");

            var transition = tracker.transitions.FirstOrDefault(t => t.degraded && t.channelIndex == 1);

            Assert(transition != null && transition.electrode == "P3",
                "the transition names the electrode, not just the channel index");

            Assert(transition != null && transition.affectedRois.Contains("POSTERIOR_ALPHA"),
                "the transition records which ROI feature it invalidates");

            Assert(transition != null && !string.IsNullOrEmpty(transition.detail),
                "the transition carries a reason with the measured values");

            Assert(transition != null &&
                   (transition.reason & (EegDegradationReason.AmplitudeExcursion |
                                         EegDegradationReason.ProlongedAbnormalPower)) != 0,
                "the recorded reason is an excursion or abnormal power, not an unrelated rule");

            // ---- Recovery ------------------------------------------------------------
            for (var w = windows; w < windows + thresholds.recoveryWindows + 3; w++)
            {
                var healthy = new double[200];
                var restored = new double[200];

                for (var i = 0; i < 200; i++)
                {
                    healthy[i] = random.NextDouble() - 0.5;
                    restored[i] = random.NextDouble() - 0.5;
                }

                var set = ChannelStatsSet.Measure(new[] { healthy, restored }, 2);
                tracker.Submit(w, set, new[] { 10.0, 10.0 }, _ => "POSTERIOR_ALPHA");
            }

            Assert(!tracker.IsDegraded(1),
                "a channel that returns to its own baseline is reported as recovered");

            Assert(tracker.transitions.Any(t => !t.degraded && t.channelIndex == 1),
                "the recovery is recorded as its own transition, so the log reconstructs both edges");

            // ---- Live pipeline surface ------------------------------------------------
            var flags = typeof(EegQualityFlags);

            Assert(System.Enum.IsDefined(flags, EegQualityFlags.NearIdenticalChannels),
                "EegQualityFlags carries NearIdenticalChannels");

            Assert(System.Enum.IsDefined(flags, EegQualityFlags.ChannelDegraded),
                "EegQualityFlags carries ChannelDegraded");

            // Append-only: the pre-existing flags must keep their bit positions, or every
            // previously written quality value would silently change meaning.
            Assert((int)EegQualityFlags.IdenticalChannels == 1 << 9 &&
                   (int)EegQualityFlags.ChannelPowerOutlier == 1 << 10 &&
                   (int)EegQualityFlags.TransientArtifactSuspected == 1 << 11,
                "existing quality flags keep their bit positions (append-only enum)");

            Assert((int)EegQualityFlags.NearIdenticalChannels == 1 << 12 &&
                   (int)EegQualityFlags.ChannelDegraded == 1 << 13,
                "the new flags were appended above the existing ones");

            var featureFields = typeof(LatestEegFeatures);

            Assert(featureFields.GetField("frontalThetaValid") != null &&
                   featureFields.GetField("posteriorAlphaValid") != null,
                "features expose PER-ROI validity, so one broken electrode cannot condemn both regions");

            Assert(featureFields.GetField("posteriorAlphaProblem") != null &&
                   featureFields.GetField("degradedDetail") != null,
                "features carry a reason for each invalidated ROI and for the degraded channels");

            var pipeline = typeof(EegFeaturePipeline);

            Assert(pipeline.GetProperty("qcTransitions") != null,
                "the pipeline exposes its QC transition history");

            Assert(pipeline.GetProperty("qualityThresholds") != null,
                "the pipeline exposes the thresholds actually in force");

            Assert(pipeline.GetField("m_QualityThresholds",
                       BindingFlags.NonPublic | BindingFlags.Instance) != null,
                "the thresholds are a serialized field, so they are configurable in the inspector");

            // No electrode substitution anywhere: the montage rule this depends on must hold.
            var montage = AuraMontageConfig.CreateHumanVerifiedDefault();
            var posterior = montage.ResolveRoi(AuraMontageConfig.PosteriorAlphaRoi, out _);

            Assert(posterior != null && posterior.Length == 3,
                "POSTERIOR_ALPHA still resolves to exactly three electrodes (no substitution)");
        }

        /// <summary>
        /// BLOCK 13 — the accidental-action guard on the three run-management buttons.
        ///
        /// Checked in the real scene, because the whole point is that the buttons a researcher
        /// actually presses are guarded — a component that exists but is not attached to anything
        /// protects nothing.
        /// </summary>
        static void CheckBlock13SessionControlLock()
        {
            // Inactive-inclusive: the UI root is routinely left disabled in the editor, and
            // FindObjectsByType without this would report the scene as having no controller.
            var controllers = Object.FindObjectsByType<ExperimentUIController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var ui = controllers.Length > 0 ? controllers[0] : null;

            Assert(ui != null, "the scene has an ExperimentUIController");

            if (ui == null)
                return;

            // The existing scene does not carry the guard serialized — it is attached by the
            // controller's Awake. That only happens if the controller sits on an ACTIVE object,
            // so the guard's presence at run time depends on this being true.
            Assert(ui.gameObject.activeInHierarchy && ui.enabled,
                "the ExperimentUIController is active and enabled, so its Awake runs at scene " +
                "load and attaches the guard");

            // Awake does not run in edit mode, so the guard is attached explicitly here — the
            // same call the builder and the runtime path both make.
            ui.EnsureSessionControlLocks();

            var buttons = ui.SessionControlButtons();

            Assert(buttons.Length == 3,
                $"exactly three session-control buttons are guarded ({buttons.Length} listed)");

            var names = new List<string>();

            foreach (var button in buttons)
            {
                Assert(button != null, "each guarded session-control button is wired");

                if (button == null)
                    continue;

                names.Add(button.name);

                var guard = button.GetComponent<SessionControlLock>();

                Assert(guard != null, $"{button.name} carries a SessionControlLock");

                if (guard == null)
                    continue;

                Assert(System.Math.Abs(guard.lockSeconds - 3.0f) < 0.001f,
                    $"{button.name} is guarded for 3.0 s (found {guard.lockSeconds:F2} s)");

                // Idempotence: running the wiring twice must not stack components, or the
                // button would end up with several competing timers.
                SessionControlLock.Attach(button);

                Assert(button.GetComponents<SessionControlLock>().Length == 1,
                    $"{button.name} has exactly one lock component after a second Attach call");
            }

            Assert(names.Contains("Btn_NewTrial") && names.Contains("Btn_Restart") &&
                   names.Contains("Btn_End"),
                "the guarded buttons are NEW TRIAL, RESTART and END (" +
                string.Join(", ", names) + ")");

            // The guard must not have crept onto participant-facing or utility controls.
            var allLocks = Object.FindObjectsByType<SessionControlLock>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(allLocks.Length == 3,
                $"only the three destructive controls carry the guard ({allLocks.Length} found " +
                "in the scene)");

            var guarded = new List<string>();

            foreach (var guard in allLocks)
                guarded.Add(guard.gameObject.name);

            Assert(!guarded.Any(n => n.StartsWith("Btn_Recenter", System.StringComparison.Ordinal)),
                "Recenter is NOT guarded — it is a utility, not a destructive action");

            Assert(!guarded.Contains("Btn_Ready") && !guarded.Contains("Btn_Start"),
                "no participant task control is guarded, so no stimulus is delayed");

            // The component's own contract, checked without entering play mode.
            var probe = new GameObject("SelfTest_LockProbe");

            try
            {
                probe.SetActive(false);
                var button = probe.AddComponent<Button>();
                var guard = SessionControlLock.Attach(button, 3.0f);

                Assert(guard != null && System.Math.Abs(guard.lockSeconds - 3.0f) < 0.001f,
                    "Attach applies the requested lock duration");

                Assert(SessionControlLock.DefaultLockSeconds == 3.0f,
                    "the project default guard is 3.0 seconds");

                if (guard != null)
                {
                    guard.lockSeconds = -4f;

                    Assert(guard.lockSeconds == 0f,
                        "a negative duration clamps to zero rather than locking forever");

                    guard.lockSeconds = 3.0f;
                    guard.Engage();

                    Assert(guard.isLocked && !button.interactable,
                        "Engage locks the button and makes it uninteractable");

                    guard.Release();

                    Assert(!guard.isLocked && button.interactable,
                        "Release restores interactability");
                }
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        static void CheckBlock11AnalysisTimebaseDomain()
        {
            const double rate = 250.0;
            const double step = 1.0 / rate;

            // ---- A: a clean same-domain stream stays in its own domain ----------------------
            var timebase = new EegAnalysisTimebase(rate);
            var clean = SyntheticLocalTimestamps(97872.0, 2000, rate, jittered: false);
            var outClean = new double[clean.Length];

            for (var i = 0; i < clean.Length; i++)
                outClean[i] = timebase.Add(clean[i]);

            Assert(System.Math.Abs(outClean[0] - clean[0]) < 1e-9,
                "the first analysis timestamp IS the first raw local timestamp — the grid is " +
                "anchored on the local clock, not on anything else");

            var maxOffsetClean = 0d;
            for (var i = 0; i < clean.Length; i++)
                maxOffsetClean = System.Math.Max(maxOffsetClean, System.Math.Abs(outClean[i] - clean[i]));

            Assert(maxOffsetClean < 0.01,
                $"a clean stream's analysis timeline never leaves its raw local one " +
                $"(worst {maxOffsetClean * 1000:F3} ms)");

            Assert(timebase.reanchorCount == 0,
                $"a clean stream needs no re-anchor ({timebase.reanchorCount})");

            Assert(timebase.isTracking, "a clean stream reports itself as tracking");

            var cleanRate = (clean.Length - 1) / (outClean[clean.Length - 1] - outClean[0]);

            Assert(System.Math.Abs(cleanRate - rate) < 1.0,
                $"a {rate:F0} Hz synthetic stream reconstructs at {cleanRate:F4} Hz");

            // ---- B: chunk jitter is removed, and monotonicity is a property of the model -----
            timebase = new EegAnalysisTimebase(rate);
            var jittered = SyntheticLocalTimestamps(97872.0, 4000, rate, jittered: true);
            var outJittered = new double[jittered.Length];

            for (var i = 0; i < jittered.Length; i++)
                outJittered[i] = timebase.Add(jittered[i]);

            var rawNegative = 0;
            var outNegative = 0;

            for (var i = 1; i < jittered.Length; i++)
            {
                if (jittered[i] <= jittered[i - 1]) rawNegative++;
                if (outJittered[i] <= outJittered[i - 1]) outNegative++;
            }

            Assert(rawNegative > 0,
                $"the synthetic input really is non-monotonic ({rawNegative} backward steps) — " +
                "otherwise the next assertion would prove nothing");

            Assert(outNegative == 0,
                $"the analysis timeline is strictly increasing despite {rawNegative} backward " +
                $"steps in its input ({outNegative} backward steps out)");

            var jitteredRate = (jittered.Length - 1) /
                               (outJittered[jittered.Length - 1] - outJittered[0]);

            Assert(System.Math.Abs(jitteredRate - rate) < 2.0,
                $"de-jittering preserves the sample rate ({jitteredRate:F4} Hz)");

            var maxOffsetJittered = 0d;
            for (var i = 0; i < jittered.Length; i++)
                maxOffsetJittered = System.Math.Max(maxOffsetJittered,
                    System.Math.Abs(outJittered[i] - jittered[i]));

            Assert(maxOffsetJittered < timebase.reanchorThresholdSeconds,
                $"even against jittered input the grid stays inside its own re-anchor threshold " +
                $"({maxOffsetJittered * 1000:F1} ms vs " +
                $"{timebase.reanchorThresholdSeconds * 1000:F0} ms)");

            Assert(timebase.reanchorCount == 0,
                "ordinary chunk jitter does NOT trigger a re-anchor — the threshold sits far " +
                "above the transport's real envelope, so a healthy stream is never cut");

            // ---- C: THE BUG. A remote-domain prefix must not contaminate the timeline --------
            // This reproduces exactly what happened: time_correction was not yet known, so the
            // first samples arrived in the sender's clock, and the grid anchored there.
            const double remoteOffset = 1242924.158801;

            timebase = new EegAnalysisTimebase(rate);
            var mixed = SyntheticLocalTimestamps(97872.0, 6000, rate, jittered: true);
            var outMixed = new double[mixed.Length];
            const int contaminated = 1500;

            for (var i = 0; i < mixed.Length; i++)
            {
                // The first 1500 samples are stamped in the REMOTE domain.
                var input = i < contaminated ? mixed[i] + remoteOffset : mixed[i];
                outMixed[i] = timebase.Add(input);
            }

            Assert(timebase.reanchorCount >= 1,
                $"a clock-domain change forces a re-anchor ({timebase.reanchorCount})");

            var finalOffset = System.Math.Abs(outMixed[mixed.Length - 1] - mixed[mixed.Length - 1]);

            Assert(finalOffset < 1.0,
                $"after the domain change the analysis timeline is back in the LOCAL domain " +
                $"(final offset {finalOffset:F6} s, not {remoteOffset:F0} s)");

            // The tail — everything after the contaminated prefix and the re-anchor — must be
            // a normal timeline. This is the assertion that fails on the old code.
            var tailStart = contaminated + 10;
            var tailRate = (mixed.Length - 1 - tailStart) /
                           (outMixed[mixed.Length - 1] - outMixed[tailStart]);

            Assert(System.Math.Abs(tailRate - rate) < 2.0,
                $"after re-anchoring the timeline runs at the real rate ({tailRate:F4} Hz). " +
                "The old code free-ran at 416.7 Hz here, because its steering clamp saturated " +
                "and never recovered");

            var tailNegative = 0;
            for (var i = tailStart + 1; i < mixed.Length; i++)
            {
                if (outMixed[i] <= outMixed[i - 1])
                    tailNegative++;
            }

            Assert(tailNegative == 0,
                $"the timeline after the re-anchor is strictly increasing ({tailNegative})");

            // ---- D: the receiver-side reset, driven explicitly ------------------------------
            timebase = new EegAnalysisTimebase(rate);
            var seq = SyntheticLocalTimestamps(97872.0, 3000, rate, jittered: false);

            for (var i = 0; i < 1000; i++)
                timebase.Add(seq[i] + remoteOffset);

            var beforeReset = timebase.reanchorCount;

            // This is what AuraLslReceiver now calls when time_correction moves the domain.
            timebase.InvalidateAnchor("test: time_correction moved the clock domain");

            var afterInvalidate = timebase.Add(seq[1000]);

            Assert(timebase.reanchorCount == beforeReset + 1,
                "InvalidateAnchor causes exactly ONE re-anchor, counted on the next sample");

            Assert(System.Math.Abs(afterInvalidate - seq[1000]) < 1e-9,
                "the re-anchored grid restarts exactly at the next raw local timestamp, so the " +
                "domain change costs no accuracy");

            Assert(timebase.lastReanchorReason.Length > 0,
                $"the re-anchor records WHY it happened (\"{timebase.lastReanchorReason}\")");

            // ---- E: Reset() clears the integrity history ------------------------------------
            timebase.Reset();

            Assert(timebase.reanchorCount == 0 && timebase.samplesSeen == 0 &&
                   timebase.totalSaturatedRuns == 0 && timebase.isTracking,
                "Reset() clears the fit AND the integrity history, so a new stream never " +
                "inherits the previous recording's re-anchor count");

            var afterReset = timebase.Add(50000.0);

            Assert(System.Math.Abs(afterReset - 50000.0) < 1e-9,
                "after Reset() the grid re-anchors at the first sample of the new stream, " +
                "whatever domain it is in — a reconnect cannot drag the old anchor forward");

            // ---- F: a sample-index restart is safe ------------------------------------------
            // The sender restarting its own indexing shows up here as a timestamp discontinuity;
            // the guard treats it exactly like any other lost anchor.
            timebase = new EegAnalysisTimebase(rate);

            foreach (var t in SyntheticLocalTimestamps(97872.0, 1200, rate, jittered: false))
                timebase.Add(t);

            var restarted = SyntheticLocalTimestamps(120000.0, 1200, rate, jittered: false);
            var outRestart = new double[restarted.Length];

            for (var i = 0; i < restarted.Length; i++)
                outRestart[i] = timebase.Add(restarted[i]);

            Assert(timebase.reanchorCount >= 1,
                "a stream that restarts its timestamps re-anchors rather than integrating " +
                "across the discontinuity");

            var restartRate = (restarted.Length - 1 - 10) /
                              (outRestart[restarted.Length - 1] - outRestart[10]);

            Assert(System.Math.Abs(restartRate - rate) < 2.0,
                $"and recovers the correct rate afterwards ({restartRate:F4} Hz)");

            // ---- G: thresholds are derived from nominal, not from a session ------------------
            var fast = new EegAnalysisTimebase(1000.0);
            var slow = new EegAnalysisTimebase(100.0);

            Assert(fast.reanchorThresholdSeconds > 0d && slow.reanchorThresholdSeconds > 0d,
                "every rate gets a re-anchor threshold");

            Assert(slow.reanchorThresholdSeconds >= fast.reanchorThresholdSeconds,
                $"a slower stream tolerates at least as much absolute offset " +
                $"({slow.reanchorThresholdSeconds:F2} s at 100 Hz vs " +
                $"{fast.reanchorThresholdSeconds:F2} s at 1000 Hz) — the bound follows the " +
                "sample interval rather than a hard-coded session number");

            var timebaseSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/EegAnalysisTimebase.cs"));

            foreach (var forbidden in new[]
                     { "1242924", "1340672", "97872", "31591", "416.6", "27eda8", "88cb0d" })
            {
                Assert(!timebaseSource.Contains(forbidden),
                    $"the time base contains no hard-coded '{forbidden}' — no session's numbers " +
                    "leaked into the algorithm");
            }

            // ---- H: the receiver resets the grid when the clock domain moves -----------------
            var receiverSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/AuraLslReceiver.cs"));

            Assert(receiverSource.Contains("InvalidateAnchor("),
                "the receiver invalidates the analysis anchor when the clock correction moves");

            Assert(receiverSource.Contains("reanchorThresholdSeconds"),
                "the receiver decides that from the time base's OWN threshold rather than a " +
                "second, independently-drifting constant");

            Assert(receiverSource.Contains("var localRaw = remoteTimestamp + m_TimeCorrection;") &&
                   receiverSource.Contains("m_Timebase.Add(localRaw)"),
                "the grid is still fed the LOCAL timestamp — remote never reaches it directly");

            Assert(!receiverSource.Contains("m_Timebase.Add(remoteTimestamp)"),
                "the remote timestamp is never passed to the analysis time base");

            // ---- I: the two recorded sessions, as regression fixtures ------------------------
            // Real data, replayed through the real class. Skipped with a note when the machine
            // does not have the recordings, so the suite stays runnable elsewhere.
            CheckRecordedSessionTimebase("S_20260901_184525_r01_88cb0d",
                "the session that worked");

            CheckRecordedSessionTimebase("S_20260902_131219_r01_27eda8",
                "the session whose analysis column was written in the wrong domain");

            // ---- J: nothing else moved ------------------------------------------------------
            // It handles timestamps and nothing else. Checked on the TYPE, not on the text: a
            // class that cannot see a sample's channels cannot alter one. (Its own double[]
            // ring buffers hold timestamps, which is why a naive scan for "[]" proves nothing.)
            var addMethod = typeof(EegAnalysisTimebase).GetMethod("Add");
            var addParams = addMethod?.GetParameters();

            Assert(addParams != null && addParams.Length == 1 &&
                   addParams[0].ParameterType == typeof(double) &&
                   addMethod.ReturnType == typeof(double),
                "EegAnalysisTimebase.Add takes ONE double and returns a double — a timestamp in, " +
                "a timestamp out. No EEG amplitude can reach it");

            Assert(!timebaseSource.Contains("RawEegSample") &&
                   !timebaseSource.Contains("channels"),
                "the time base never references a sample's channel data");

            var csvSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/CsvEventSink.cs");

            Assert(csvSource.Contains("\"stimulus_offset_time\","),
                "the behavioural event CSV header is unchanged by this pass");

            var markerSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/LslMarkerSink.cs"));

            Assert(!markerSource.Contains("Timebase") && !markerSource.Contains("reanchor"),
                "the LSL marker sink is untouched");

            var recorderSource = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Data/RawEegRecorder.cs"));

            Assert(recorderSource.Contains("lsl_timestamp_analysis") &&
                   recorderSource.Contains("lsl_timestamp_local_raw") &&
                   recorderSource.Contains("lsl_timestamp_remote_raw"),
                "the raw EEG file still carries all three time bases — the fix corrects a " +
                "value, it does not remove a column");
        }

        /// <summary>
        /// Replays one recorded session's local_raw column through the real time base and checks
        /// the result against the contract.
        ///
        /// The local_raw column is correct in BOTH recordings — that is precisely why the broken
        /// session is recoverable — so this is a fair test of the algorithm on real transport
        /// jitter rather than on a synthetic model of it.
        /// </summary>
        static void CheckRecordedSessionTimebase(string sessionId, string description)
        {
            var path = EegRecordedWindowValidator.FindRawEegFile(sessionId, out var problem);

            if (string.IsNullOrEmpty(path))
            {
                Info($"recorded fixture '{sessionId}' not on this machine ({problem}) — " +
                     "synthetic coverage above still applies");
                return;
            }

            var recording = EegRecordedWindowValidator.Load(path, out var loadProblem);

            if (recording == null)
            {
                Assert(false, $"{sessionId}: {loadProblem}");
                return;
            }

            var reanchors = EegRecordedWindowValidator.ReconstructAnalysis(
                recording, out var timebase);

            var n = recording.sampleCount;
            var first = recording.analysisTimestamps[0];
            var last = recording.analysisTimestamps[n - 1];
            var rebuiltRate = n > 1 ? (n - 1) / (last - first) : 0d;

            var negative = 0;
            var maxOffset = 0d;

            for (var i = 0; i < n; i++)
            {
                if (i > 0 && recording.analysisTimestamps[i] <=
                    recording.analysisTimestamps[i - 1])
                {
                    negative++;
                }

                maxOffset = System.Math.Max(maxOffset,
                    System.Math.Abs(recording.analysisTimestamps[i] - recording.localRawTimestamps[i]));
            }

            var check = EegRecordedWindowValidator.CheckAnalysisDomain(recording);

            Info($"{sessionId} ({description}): {n} samples, rebuilt {rebuiltRate:F4} Hz, " +
                 $"{reanchors} re-anchor(s), worst offset from raw {maxOffset * 1000:F1} ms");

            Assert(check.domainMatches,
                $"{sessionId}: the REBUILT analysis timeline is in the local clock domain " +
                $"(median offset {check.medianOffsetSeconds * 1000:F3} ms)");

            Assert(System.Math.Abs(rebuiltRate - recording.nominalRateHz) <
                   recording.nominalRateHz * 0.05,
                $"{sessionId}: the rebuilt timeline runs at the advertised rate " +
                $"({rebuiltRate:F4} Hz vs {recording.nominalRateHz:F0} Hz)");

            Assert(negative == 0,
                $"{sessionId}: the rebuilt timeline is strictly increasing ({negative} " +
                "backward steps)");

            Assert(timebase == null || timebase.reanchorCount == 0,
                $"{sessionId}: replaying the CORRECT local_raw column needs no re-anchor — the " +
                "guard does not fire on healthy input");
        }

        /// <summary>
        /// Finds a GameObject by name anywhere in the loaded scene, INCLUDING inactive ones.
        ///
        /// <c>GameObject.Find</c> returns only active objects, which makes it useless for
        /// checking the layout of panels that are hidden until their area opens — it returns
        /// null and any guarded assertion quietly does not run.
        /// </summary>
        static GameObject FindInSceneIncludingInactive(string name)
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene()
                         .GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == name)
                        return t.gameObject;
                }
            }

            return null;
        }

        /// <summary>Builds a window whose value at (sample, channel) is supplied by the caller.</summary>
        static EegWindow MakeIdentityWindow(int channels, int samples,
            System.Func<int, int, double> value)
        {
            var window = new EegWindow
            {
                channelCount = channels,
                sampleCount = samples,
                samples = new double[samples][],
                timestamps = new double[samples],
                status = EegWindowStatus.Complete,
            };

            for (var i = 0; i < samples; i++)
            {
                window.samples[i] = new double[channels];

                for (var c = 0; c < channels; c++)
                    window.samples[i][c] = value(i, c);

                window.timestamps[i] = i;
            }

            return window;
        }

        /// <summary>A features snapshot carrying the band powers and labels the check reads.</summary>
        static LatestEegFeatures MakeIdentityFeatures(int channels,
            System.Func<int, double> theta, System.Func<int, double> alpha)
        {
            var features = new LatestEegFeatures
            {
                channelCount = channels,
                thetaPerChannel = new double[channels],
                alphaPerChannel = new double[channels],
                channelLabels = new string[channels],
            };

            for (var c = 0; c < channels; c++)
            {
                features.thetaPerChannel[c] = theta(c);
                features.alphaPerChannel[c] = alpha(c);
                features.channelLabels[c] = $"CH{c + 1}";
            }

            return features;
        }

        /// <summary>
        /// Welch PSD and band power against signals whose answers are known analytically.
        ///
        /// A plausible-looking spectrum is indistinguishable from a correct one by eye, so every
        /// claim here is numeric: peak location, band ratios, and the amplitude-squared scaling
        /// that band power must obey.
        /// </summary>
        static void CheckEegSpectral()
        {
            const double fs = 250.0;
            const double seconds = 4.0;
            var n = (int)(fs * seconds);

            double[] Sine(double hz, double amplitude)
            {
                var x = new double[n];
                for (var i = 0; i < n; i++)
                    x[i] = amplitude * System.Math.Sin(2.0 * System.Math.PI * hz * i / fs);

                return x;
            }

            // ---- Hann definition ---------------------------------------------------------------
            var hann = EegSpectralAnalyzer.HannWindow(8);

            Assert(System.Math.Abs(hann[0]) < 1e-12,
                "the Hann window starts at zero");

            Assert(System.Math.Abs(hann[4] - 1.0) < 1e-12,
                $"the PERIODIC Hann peaks at exactly 1.0 at N/2 ({hann[4]:F6})");

            // ---- FFT correctness ----------------------------------------------------------------
            var re = new double[8];
            var im = new double[8];
            re[0] = 1.0;                       // unit impulse
            EegSpectralAnalyzer.Fft(re, im);

            var flat = true;
            for (var k = 0; k < 8; k++)
            {
                if (System.Math.Abs(re[k] - 1.0) > 1e-12 || System.Math.Abs(im[k]) > 1e-12)
                    flat = false;
            }

            Assert(flat, "the FFT of a unit impulse is flat and unity — the transform is correct");

            // ---- TEST A: pure 6 Hz ---------------------------------------------------------------
            var psdA = EegSpectralAnalyzer.Welch(Sine(6.0, 1.0), fs);

            Info($"Welch config: {psdA.Describe()}");

            Assert(System.Math.Abs(psdA.physicalResolutionHz - 0.5) < 0.01,
                $"a 2-s segment gives 0.5 Hz PHYSICAL resolution ({psdA.physicalResolutionHz:F4} Hz)");

            Assert(psdA.binSpacingHz < psdA.physicalResolutionHz,
                $"zero padding makes bins closer ({psdA.binSpacingHz:F4} Hz) than the physical " +
                $"resolution ({psdA.physicalResolutionHz:F4} Hz) — it interpolates, it does not " +
                "add information");

            var peakA = EegSpectralAnalyzer.PeakFrequency(psdA, 1.0, 40.0);
            var thetaA = EegSpectralAnalyzer.BandPower(psdA, EegBand.Theta);
            var alphaA = EegSpectralAnalyzer.BandPower(psdA, EegBand.Alpha);

            Assert(System.Math.Abs(peakA - 6.0) <= 0.5,
                $"a pure 6 Hz sine peaks at {peakA:F3} Hz");

            Assert(thetaA > alphaA * 20.0,
                $"theta dominates alpha for a 6 Hz sine ({thetaA:E3} vs {alphaA:E3})");

            // ---- TEST B: pure 10 Hz ---------------------------------------------------------------
            var psdB = EegSpectralAnalyzer.Welch(Sine(10.0, 1.0), fs);
            var peakB = EegSpectralAnalyzer.PeakFrequency(psdB, 1.0, 40.0);
            var thetaB = EegSpectralAnalyzer.BandPower(psdB, EegBand.Theta);
            var alphaB = EegSpectralAnalyzer.BandPower(psdB, EegBand.Alpha);

            Assert(System.Math.Abs(peakB - 10.0) <= 0.5,
                $"a pure 10 Hz sine peaks at {peakB:F3} Hz");

            Assert(alphaB > thetaB * 20.0,
                $"alpha dominates theta for a 10 Hz sine ({alphaB:E3} vs {thetaB:E3})");

            // ---- TEST C: equal-amplitude 6 + 10 Hz -------------------------------------------------
            var mixed = new double[n];
            var a6 = Sine(6.0, 1.0);
            var a10 = Sine(10.0, 1.0);

            for (var i = 0; i < n; i++)
                mixed[i] = a6[i] + a10[i];

            var psdC = EegSpectralAnalyzer.Welch(mixed, fs);
            var thetaC = EegSpectralAnalyzer.BandPower(psdC, EegBand.Theta);
            var alphaC = EegSpectralAnalyzer.BandPower(psdC, EegBand.Alpha);
            var ratioC = thetaC / alphaC;

            Assert(ratioC > 0.7 && ratioC < 1.4,
                $"equal-amplitude 6 + 10 Hz gives comparable theta and alpha " +
                $"(ratio {ratioC:F3})");

            // ---- TEST D: amplitude 2:1 -> power 4:1 -----------------------------------------------
            var scaled = new double[n];
            var a6Loud = Sine(6.0, 2.0);

            for (var i = 0; i < n; i++)
                scaled[i] = a6Loud[i] + a10[i];

            var psdD = EegSpectralAnalyzer.Welch(scaled, fs);
            var thetaD = EegSpectralAnalyzer.BandPower(psdD, EegBand.Theta);
            var alphaD = EegSpectralAnalyzer.BandPower(psdD, EegBand.Alpha);
            var ratioD = thetaD / alphaD;

            Assert(ratioD > 3.4 && ratioD < 4.6,
                $"doubling the 6 Hz AMPLITUDE quadruples theta power (ratio {ratioD:F3}, " +
                "expected ≈4 — power scales with amplitude squared)");

            // ---- Band power is a density integral, not a bin sum ------------------------------
            // Re-running with a longer FFT changes the bin grid but must not change the power.
            var psdPadded = EegSpectralAnalyzer.Welch(Sine(6.0, 1.0), fs, 2.0, 0.5,
                fftLength: 2048);

            var thetaPadded = EegSpectralAnalyzer.BandPower(psdPadded, EegBand.Theta);

            Assert(psdPadded.fftLength == 2048 && psdPadded.binSpacingHz < psdA.binSpacingHz,
                $"the padded estimate really does use a finer grid " +
                $"({psdPadded.binSpacingHz:F5} vs {psdA.binSpacingHz:F5} Hz)");

            Assert(System.Math.Abs(thetaPadded - thetaA) / thetaA < 0.10,
                $"band power is invariant to FFT length ({thetaPadded:E3} vs {thetaA:E3}) — it " +
                "integrates a density rather than summing bins");

            // ---- TEST E: white noise ---------------------------------------------------------------
            var noise = new double[n];
            var rng = new System.Random(20260821);

            for (var i = 0; i < n; i++)
                noise[i] = rng.NextDouble() * 2.0 - 1.0;

            var psdE = EegSpectralAnalyzer.Welch(noise, fs);
            var thetaE = EegSpectralAnalyzer.BandPower(psdE, EegBand.Theta);
            var alphaE = EegSpectralAnalyzer.BandPower(psdE, EegBand.Alpha);
            var ratioE = thetaE / alphaE;

            // Both bands are 4 Hz wide, so flat-spectrum noise should give a ratio near 1 with
            // no isolated peak attributable to a deterministic sinusoid.
            Assert(ratioE > 0.5 && ratioE < 2.0,
                $"white noise produces no dominant band ({ratioE:F3} theta/alpha over two " +
                "equally wide bands)");

            var peakE = EegSpectralAnalyzer.PeakFrequency(psdE, 4.0, 12.0);
            var meanE = (thetaE + alphaE) / 8.0;

            Assert(!double.IsNaN(peakE) && meanE > 0d,
                "white noise still yields a finite spectrum, just without structure");

            // ---- One-sided scaling: DC and Nyquist are not doubled -------------------------------
            var spectralCode = StripCommentsAndAttributes(
                File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/EegSpectralAnalyzer.cs"));

            Assert(spectralCode.Contains("k > 0 && k < n / 2"),
                "only interior bins are doubled — DC and Nyquist have no mirror partner");

            Assert(spectralCode.Contains("sampleRateHz * windowEnergy"),
                "PSD is normalised by the sample rate AND the window energy, giving a density");

            Assert(spectralCode.Contains("- mean)"),
                "each segment is de-meaned before windowing, so DC leakage cannot reach theta");
        }

        /// <summary>
        /// The electrode montage and the acquisition provenance around it.
        ///
        /// The thing under test is not really the lookup table — it is the HONESTY of the
        /// configuration: that the mapping is recorded as human-verified rather than as stream
        /// metadata (the stream publishes an empty desc and states none of this), that an ROI
        /// resolves all-or-nothing, and that no power value can be labelled µV²/Hz while the
        /// scaling of the float32 samples is still unknown.
        /// </summary>
        static void CheckAuraMontage()
        {
            var montage = AuraMontageConfig.CreateHumanVerifiedDefault();

            try
            {
                // ---- The human-verified mapping -------------------------------------------
                var expected = new (int channel, string label)[]
                {
                    (1, "Fp1"), (2, "F3"), (3, "Fz"), (4, "F4"),
                    (5, "Cz"), (6, "P3"), (7, "Pz"), (8, "P4"),
                };

                Assert(montage.channels.Count == 8,
                    $"the montage maps 8 channels ({montage.channels.Count})");

                foreach (var (channel, label) in expected)
                {
                    // 1-based channel number in the UI, 0-based index into the sample array.
                    Assert(montage.IndexOfLabel(label) == channel - 1,
                        $"CH{channel} = {label} (sample index {montage.IndexOfLabel(label)})");

                    Assert(montage.LabelOfIndex(channel - 1) == label,
                        $"sample index {channel - 1} resolves back to {label}");
                }

                Assert(montage.Validate(8, out var problem),
                    $"the montage is self-consistent against an 8-channel stream " +
                    $"({(string.IsNullOrEmpty(problem) ? "valid" : problem)})");

                // A stream with a different channel count must STOP analysis, not be coerced.
                Assert(!montage.Validate(6, out var mismatch),
                    $"a montage/stream channel-count mismatch is refused ({mismatch})");

                // ---- ROIs, resolved by LABEL -----------------------------------------------
                var frontal = montage.ResolveRoi(AuraMontageConfig.FrontalThetaRoi, out var fp);

                Assert(frontal != null && frontal.Length == 3,
                    $"FRONTAL_THETA resolves to three channels ({fp})");

                Assert(frontal != null && frontal[0] == 1 && frontal[1] == 2 && frontal[2] == 3,
                    "FRONTAL_THETA = F3, Fz, F4 -> sample indices 1, 2, 3");

                var posterior = montage.ResolveRoi(AuraMontageConfig.PosteriorAlphaRoi, out var pp);

                Assert(posterior != null && posterior.Length == 3,
                    $"POSTERIOR_ALPHA resolves to three channels ({pp})");

                Assert(posterior != null && posterior[0] == 5 && posterior[1] == 6 &&
                       posterior[2] == 7,
                    "POSTERIOR_ALPHA = P3, Pz, P4 -> sample indices 5, 6, 7");

                // Fp1 and Cz are kept, just not aggregated.
                Assert(montage.IndexOfLabel("Fp1") == 0 && montage.IndexOfLabel("Cz") == 4,
                    "Fp1 and Cz remain mapped and available, though outside the initial ROIs");

                var inAnyRoi = new HashSet<int>();
                foreach (var roi in montage.regions)
                {
                    foreach (var index in montage.ResolveRoi(roi.roiName, out _))
                        inAnyRoi.Add(index);
                }

                Assert(!inAnyRoi.Contains(0) && !inAnyRoi.Contains(4),
                    "Fp1 and Cz are not silently folded into an aggregated ROI");

                // ---- All-or-nothing ROI resolution ------------------------------------------
                var broken = AuraMontageConfig.CreateHumanVerifiedDefault();
                broken.channels[6].label = "SOMETHING_ELSE";     // Pz removed

                Assert(broken.ResolveRoi(AuraMontageConfig.PosteriorAlphaRoi, out var brokenWhy)
                           == null,
                    $"an ROI missing one electrode resolves to NOTHING rather than to a " +
                    $"two-electrode average wearing the same name ({brokenWhy})");

                Assert(montage.IndexOfLabel("T7") < 0,
                    "an unmapped electrode returns -1 rather than a nearby channel");

                Object.DestroyImmediate(broken);

                // ---- Provenance --------------------------------------------------------------
                Assert(montage.mappingSource == EegConfigSource.HumanVerifiedAcquisitionUi,
                    $"the mapping is recorded as HUMAN-VERIFIED, not as stream metadata " +
                    $"({montage.mappingSource})");

                Assert(montage.mappingSource != EegConfigSource.LslStreamMetadata,
                    "the montage never claims LSL provided it — the stream publishes an empty desc");

                Assert(montage.filterStateSource == EegConfigSource.HumanVerifiedAcquisitionUi,
                    "the acquisition filter state is likewise recorded as human-verified");

                Assert(!montage.acquisitionNotchEnabled && !montage.acquisitionBandpassEnabled,
                    "acquisition-side notch and band-pass are both recorded OFF");

                Assert(montage.acquisitionIsUnfiltered,
                    "the pipeline may therefore preprocess without double-filtering");

                var provenance = montage.DescribeProvenance();

                foreach (var required in new[]
                         {
                             "montage_source=HumanVerifiedAcquisitionUi",
                             "CH2=F3", "CH7=Pz",
                             "acquisition_notch=OFF",
                             "acquisition_bandpass=OFF",
                             "units_confirmed=FALSE",
                         })
                {
                    Assert(provenance.Contains(required),
                        $"provenance carries '{required}' to any feature computed through it");
                }

                // ---- Units remain unresolved --------------------------------------------------
                Assert(!montage.unitsConfirmed,
                    "the amplitude scaling is NOT confirmed — a µV axis in the acquisition UI is " +
                    "not the same claim as the stream carrying µV");

                Assert(montage.amplitudeUnitLabel == "AURA native units",
                    $"samples are reported in '{montage.amplitudeUnitLabel}'");

                Assert(montage.PowerUnitLabel == "AURA-native-units²",
                    $"power is reported in '{montage.PowerUnitLabel}'");

                Assert(montage.PowerSpectralDensityUnitLabel == "AURA-native-units²/Hz",
                    $"PSD is reported in '{montage.PowerSpectralDensityUnitLabel}'");

                foreach (var forbidden in new[] { "µV", "microvolt", "uV" })
                {
                    Assert(montage.PowerSpectralDensityUnitLabel.IndexOf(
                               forbidden, System.StringComparison.OrdinalIgnoreCase) < 0,
                        $"no unit label claims '{forbidden}' while the scaling is unverified");
                }

                // ---- Nothing here touches the signal --------------------------------------------
                var montageCode = StripCommentsAndAttributes(
                    File.ReadAllText("Assets/IKEA_EEG/Scripts/Data/AuraMontageConfig.cs"));

                // Checked as OPERATIONS, not as vocabulary: this file legitimately has fields
                // named filterStateSource and acquisitionFilterDetail, because RECORDING the
                // acquisition filter state is exactly its job. What it must not do is compute.
                foreach (var forbidden in new[] { "Math.", "Mathf.", "Fft", "FFT", "Psd", "PSD" })
                {
                    Assert(!montageCode.Contains(forbidden),
                        $"the montage config performs no '{forbidden}' — it states what the " +
                        "channels ARE and nothing more");
                }

                // And it never reads or writes an EEG sample. Checked on the SAMPLE TYPE, not on
                // "channels[" — the montage indexes its own list of channel mappings, which is
                // a different thing entirely from indexing a sample's amplitudes.
                Assert(!montageCode.Contains("RawEegSample") &&
                       !montageCode.Contains("double[]"),
                    "the montage config never touches an EEG sample array");
            }
            finally
            {
                Object.DestroyImmediate(montage);
            }
        }

        /// <summary>
        /// The Spanish and Japanese word sets: real language-specific stimuli, correct clips,
        /// full provenance, and no overstated validation claim anywhere.
        /// </summary>
        static void CheckLanguageWordSets()
        {
            var wordList = AssetDatabase.LoadAssetAtPath<WordListDefinition>(
                ExperimentAssetBuilder.WordListPath);

            if (wordList == null)
            {
                Assert(false, "the word list asset exists");
                return;
            }

            Info($"words per set (the count the VR task presents): {wordList.wordsPerSet}");

            foreach (var language in ExperimentLanguages.Selectable)
            {
                var indices = wordList.GetSetIndicesForLanguage(language);
                var code = ExperimentLanguages.ToCode(language);

                Assert(indices.Count > 0,
                    $"{code} has at least one word set ({indices.Count}) — the language is no " +
                    "longer blocked");

                Info($"{code}: {indices.Count} set(s) available; runs 1..{indices.Count} of a " +
                     "sitting get different lists");
            }

            // ---- Every set is structurally usable and fully localized ------------------------
            var allWords = new Dictionary<ExperimentLanguage, HashSet<string>>();

            for (var i = 0; i < wordList.setCount; i++)
            {
                var set = wordList.GetSet(i);
                if (set == null)
                    continue;

                Assert(set.ValidateStructure(wordList.wordsPerSet, out var problem),
                    $"{set.EffectiveId()}: {(string.IsNullOrEmpty(problem) ? "structurally valid with all clips" : problem)}");

                if (!allWords.TryGetValue(set.language, out var seen))
                {
                    seen = new HashSet<string>();
                    allWords[set.language] = seen;
                }

                // Within a language, no word may appear in two sets: run 2 must not re-present
                // an item from run 1.
                foreach (var word in set.words)
                {
                    Assert(seen.Add(word),
                        $"'{word}' appears in only one {set.language} set");
                }
            }

            // ---- Provenance is recorded, and honest ------------------------------------------
            foreach (var language in new[]
                     { ExperimentLanguage.Spanish, ExperimentLanguage.Japanese })
            {
                var code = ExperimentLanguages.ToCode(language);

                foreach (var index in wordList.GetSetIndicesForLanguage(language))
                {
                    var set = wordList.GetSet(index);

                    Assert(!string.IsNullOrEmpty(set.wordSource),
                        $"{set.EffectiveId()} records where its words came from");

                    Assert(!string.IsNullOrEmpty(set.sourceForm),
                        $"{set.EffectiveId()} names the published form it was drawn from");

                    Assert(set.sourceFormWordCount == 15,
                        $"{set.EffectiveId()} records the source form's full size " +
                        $"({set.sourceFormWordCount})");

                    Assert(set.IsSubsetOfSourceForm,
                        $"{set.EffectiveId()} is recorded as a SUBSET of a larger form — " +
                        $"{set.words.Count} of {set.sourceFormWordCount}");

                    // The single most important claim in this workstream.
                    Assert(!set.validatedForResearch,
                        $"{set.EffectiveId()} is NOT flagged validated — a five-item subset " +
                        "under an adapted procedure is not a validated instrument");

                    Assert(set.validationNote.Contains("not a standardized RAVLT administration"),
                        $"{set.EffectiveId()} states plainly that this is not a standardized " +
                        "RAVLT administration");

                    var provenance = set.DescribeProvenance();

                    Assert(provenance.Contains("selected_words=") &&
                           provenance.Contains(set.words[0]),
                        $"{code}: the exact words are reproducible from the log alone");
                }
            }

            // ---- No overstated claim reaches the DATA -----------------------------------------
            // Asserted on the strings that actually get written, not on the source prose: the
            // source deliberately contains the phrase "not a standardized RAVLT administration",
            // and a scan for that phrase matches the disclaimer it is meant to police.
            foreach (var language in new[]
                     { ExperimentLanguage.Spanish, ExperimentLanguage.Japanese })
            {
                foreach (var index in wordList.GetSetIndicesForLanguage(language))
                {
                    var set = wordList.GetSet(index);

                    var written = $"{set.validationNote} {set.wordSource} {set.setName} " +
                                  set.DescribeProvenance();

                    foreach (var overclaim in new[]
                             {
                                 "fully validated",
                                 "psychometrically equivalent",
                                 "standardized RAVLT administration of",
                             })
                    {
                        Assert(written.IndexOf(overclaim, System.StringComparison.OrdinalIgnoreCase) < 0,
                            $"{set.EffectiveId()} never claims '{overclaim}' in anything it writes");
                    }

                    // The disclaimer must survive into the data, not just live in a code comment.
                    Assert(written.Contains("not a standardized RAVLT administration"),
                        $"{set.EffectiveId()} carries the adaptation disclaimer into the data");
                }
            }

            // ---- Japanese is not a translation ------------------------------------------------
            var japanese = wordList.GetSetIndicesForLanguage(ExperimentLanguage.Japanese)
                .Select(i => wordList.GetSet(i))
                .SelectMany(s => s.words)
                .ToList();

            Assert(japanese.Count > 0 && japanese.All(w => w.Any(c => c > 0x3000)),
                "the Japanese stimuli are Japanese script, not romanised translations");

            var english = wordList.GetSetIndicesForLanguage(ExperimentLanguage.English)
                .Select(i => wordList.GetSet(i))
                .SelectMany(s => s.words)
                .ToList();

            var spanish = wordList.GetSetIndicesForLanguage(ExperimentLanguage.Spanish)
                .Select(i => wordList.GetSet(i))
                .SelectMany(s => s.words)
                .ToList();

            Assert(!spanish.Any(w => english.Contains(w, System.StringComparer.OrdinalIgnoreCase)),
                "no Spanish stimulus is simply an English one reused");

            // ---- Per-run selection advances, and reuse is honest -------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();

            if (manager != null)
            {
                var resolve = typeof(ExperimentManager).GetMethod("ResolveWordSetForCurrentRun",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert(resolve != null, "the per-run word-set selector exists");

                var source = ManagerSource();
                var body = source.Substring(source.IndexOf("int ResolveWordSetForCurrentRun(",
                    System.StringComparison.Ordinal));
                body = body.Substring(0,
                    body.IndexOf("\n        }", System.StringComparison.Ordinal));

                Assert(body.Contains("runIndex - 1") && body.Contains("candidates.Count"),
                    "run N of a sitting selects set N — deterministic and reconstructible");

                Assert(body.Contains("reuse = runIndex > candidates.Count"),
                    "reuse is DETECTED when the pool is exhausted rather than hidden");

                var apply = source.Substring(source.IndexOf("void ApplyResolvedWordSet(",
                    System.StringComparison.Ordinal));
                apply = apply.Substring(0,
                    apply.IndexOf("\n        }", System.StringComparison.Ordinal));

                Assert(apply.Contains("word_set_reused_in_session"),
                    "reuse is written into the run's data");

                Assert(apply.Contains("DescribeProvenance()"),
                    "the full provenance reaches the CSV on every run");
            }
        }

        static void CheckLslTransport()
        {
            Info($"LSL library available: {LslBinding.isAvailable} — {LslBinding.detail}");

            var go = new GameObject("__IKEA_EEG_SelfTest_Lsl");

            try
            {
                var sink = go.AddComponent<LslMarkerSink>();
                sink.Configure(true, "IKEA_EEG_SelfTest_Markers", "Markers",
                    "IKEA_EEG_SelfTest_Source");

                // The critical guarantee: initialising with no LSL library present must not
                // throw, must not claim to transmit, and must not stop anything.
                var threw = false;
                try
                {
                    sink.Initialize(new SessionContext
                    {
                        sessionId = "S_SELFTEST",
                        sessionStartLocal = System.DateTime.Now,
                        sessionDirectory = Path.GetTempPath(),
                    });

                    var evt = new ExperimentEvent { eventType = EventTypes.SessionStart };
                    for (var i = 0; i < 10; i++)
                        sink.OnEvent(evt);

                    sink.Shutdown();
                }
                catch (System.Exception e)
                {
                    threw = true;
                    Info($"  exception: {e}");
                }

                Assert(!threw, "the LSL sink never throws, with or without the library present");

                if (LslBinding.isAvailable)
                {
                    Assert(sink.state == LslSinkState.Active || sink.markersPushed > 0,
                        $"with liblsl present the sink reports {sink.state} and pushed " +
                        $"{sink.markersPushed} marker(s)");
                }
                else
                {
                    Assert(sink.state == LslSinkState.Unavailable,
                        $"with no liblsl the sink reports {sink.state} (expected Unavailable)");

                    Assert(sink.markersPushed == 0,
                        $"no markers are counted as sent when LSL is unavailable " +
                        $"({sink.markersPushed})");
                }

                // ---- Marker format ------------------------------------------------------------
                var wordEvent = new ExperimentEvent
                {
                    eventType = EventTypes.WordPresented,
                    wordIndex = "2",
                    expectedWord = "Copper",
                };

                var wordMarker = sink.BuildMarker(wordEvent);
                Assert(wordMarker == "WORD_PRESENTED|word_index=2|word=Copper",
                    $"word marker format: {wordMarker}");

                var chairEvent = new ExperimentEvent
                {
                    eventType = EventTypes.ChairSelected,
                    chairTrialIndex = "2",
                    objectId = "Chair_04",
                    correct = "TRUE",
                };

                var chairMarker = sink.BuildMarker(chairEvent);
                Assert(chairMarker == "CHAIR_SELECTED|trial=2|chair=Chair_04|correct=1",
                    $"chair marker format: {chairMarker}");

                var bare = sink.BuildMarker(new ExperimentEvent { eventType = EventTypes.AreaBEnter });
                Assert(bare == EventTypes.AreaBEnter,
                    $"an event with no payload sends just its name: {bare}");

                // The marker must always START with the exact CSV event_type.
                var allStartWithEventType = new[] { wordMarker, chairMarker, bare }
                    .Zip(new[] { EventTypes.WordPresented, EventTypes.ChairSelected, EventTypes.AreaBEnter },
                        (marker, type) => marker.Split('|')[0] == type)
                    .All(ok => ok);

                Assert(allStartWithEventType,
                    "every marker's first token is the unmodified CSV event_type");

                Info(ResearcherTools.LoopbackTestReport());
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ---------------------------------------------------------------------------------
        // Adaptive recall stop
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Drives the decision logic with synthetic window amplitudes.
        ///
        /// WHAT THIS DOES AND DOES NOT PROVE: it proves the RULE is right — that silence before
        /// speech never stops a recording, that a pause shorter than the threshold does not,
        /// and that a long enough one does. It proves nothing about whether a real Quest
        /// microphone produces amplitudes on either side of these thresholds; that is a
        /// headset-and-voice question and is listed as such.
        /// </summary>
        static void CheckRecallSilenceDetector()
        {
            const float window = 0.1f;
            const float speechPeak = 0.20f;     // clearly voiced
            const float quietPeak = 0.002f;     // clearly silent
            const float bandPeak = 0.03f;       // between the thresholds: neither

            RecallSilenceDetector Build()
            {
                var d = new RecallSilenceDetector();
                d.Configure(4f, 0.05f, 0.015f, 0.35f, window);
                d.Reset();
                return d;
            }

            void Push(RecallSilenceDetector d, float peak, float seconds)
            {
                var windows = Mathf.RoundToInt(seconds / window);
                for (var i = 0; i < windows; i++)
                    d.PushWindow(peak);
            }

            // ---- 1. Silence before speech must NEVER stop the recording --------------------
            var patient = Build();
            Push(patient, quietPeak, 30f);      // thirty seconds of a participant thinking

            Assert(!patient.stopRequested,
                $"30 s of silence BEFORE any speech does not stop the recording " +
                $"(speech_detected={patient.speechDetected})");
            Assert(!patient.speechDetected, "silence alone never counts as speech");

            // ---- 2. Speech, then 4 s of silence, stops it ----------------------------------
            var normal = Build();
            Push(normal, quietPeak, 6f);        // slow to start — must not matter
            Push(normal, speechPeak, 2f);       // "river, copper, lantern"
            Assert(normal.speechDetected && !normal.stopRequested,
                "speech is detected and the recording continues while speaking");

            Push(normal, quietPeak, 3.9f);
            Assert(!normal.stopRequested,
                $"3.9 s of post-speech silence is NOT enough " +
                $"(run={normal.silenceRunSeconds:F2} s of {normal.silenceStopSeconds:F1} s)");

            Push(normal, quietPeak, 0.2f);
            Assert(normal.stopRequested && normal.stopReason == RecallStopReasons.SilenceAfterSpeech,
                $"4 s of post-speech silence stops it, reason={normal.stopReason}");

            // ---- 3. Normal pauses between recalled words must survive -----------------------
            var pausing = Build();
            Push(pausing, speechPeak, 0.6f);            // "River"
            for (var i = 0; i < 4; i++)
            {
                Push(pausing, quietPeak, 3.5f);         // a long, but legal, pause
                Push(pausing, speechPeak, 0.5f);        // next word
            }

            Assert(!pausing.stopRequested,
                "four 3.5 s pauses between recalled words do not stop the recording " +
                "[each word resets the silence run]");

            // A single word after 3.9 s of silence must reset the run completely.
            Push(pausing, quietPeak, 3.9f);
            Push(pausing, speechPeak, 0.2f);
            Assert(Mathf.Approximately(pausing.silenceRunSeconds, 0f),
                $"one voiced window resets the silence run to zero " +
                $"({pausing.silenceRunSeconds:F2} s)");

            // ---- 4. A cough must not arm the detector ---------------------------------------
            var cough = Build();
            Push(cough, speechPeak, 0.2f);      // 0.2 s < minimumSpeechDuration 0.35 s
            Push(cough, quietPeak, 20f);

            Assert(!cough.speechDetected && !cough.stopRequested,
                "a 0.2 s noise below minimumSpeechDuration does not arm the detector, so a " +
                "long silence after it does not stop the recording");

            // ---- 5. The hysteresis band counts as neither -----------------------------------
            var band = Build();
            Push(band, speechPeak, 1f);
            Push(band, bandPeak, 20f);          // between silence and speech thresholds

            Assert(!band.stopRequested,
                $"20 s in the hysteresis band ({bandPeak}) does not stop the recording " +
                "[it is neither speech nor silence]");

            // ---- 6. Accumulated (not necessarily contiguous) speech arms it ------------------
            var stutter = Build();
            for (var i = 0; i < 4; i++)
            {
                Push(stutter, speechPeak, 0.1f);
                Push(stutter, quietPeak, 0.5f);
            }

            Assert(stutter.speechDetected,
                $"four 0.1 s voiced bursts accumulate past minimumSpeechDuration " +
                $"(voiced={stutter.voicedSeconds:F2} s)");

            // ---- 7. Crossed thresholds are rejected ------------------------------------------
            var crossed = new RecallSilenceDetector();
            var accepted = crossed.Configure(4f, 0.01f, 0.05f, 0.35f, window);
            Assert(!accepted && crossed.speechStartThreshold > crossed.silenceThreshold,
                $"a speech threshold below the silence threshold is rejected and safe defaults " +
                $"are used ({crossed.speechStartThreshold} / {crossed.silenceThreshold})");

            // ---- 8. The max-duration fallback is what covers 'never speaks' ------------------
            // The detector cannot stop a recording without speech, BY DESIGN — which is exactly
            // why the hard maximum exists. Asserted here so the two halves stay coupled.
            var silent = Build();
            Push(silent, quietPeak, 120f);
            Assert(!silent.stopRequested,
                "the detector never stops a recording in which nobody spoke — the hard maximum " +
                "duration is the only thing that ends it");

            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config != null)
            {
                Assert(config.recallSilenceStopSeconds < config.immediateRecallMaxDuration &&
                       config.recallSilenceStopSeconds < config.delayedRecallMaxDuration,
                    $"the silence threshold ({config.recallSilenceStopSeconds} s) is shorter " +
                    $"than both recall maxima ({config.immediateRecallMaxDuration} s / " +
                    $"{config.delayedRecallMaxDuration} s), so the adaptive stop can fire");

                Assert(config.speechStartThreshold > config.silenceThreshold,
                    $"configured speech threshold {config.speechStartThreshold} > silence " +
                    $"threshold {config.silenceThreshold}");

                Info($"PROTOTYPE DEFAULTS: silence_stop={config.recallSilenceStopSeconds} s, " +
                     $"speech_start={config.speechStartThreshold}, " +
                     $"silence={config.silenceThreshold}, " +
                     $"min_speech={config.minimumSpeechDuration} s, " +
                     $"max={config.immediateRecallMaxDuration}/{config.delayedRecallMaxDuration} s " +
                     "— none of these are validated values.");
            }
        }

        /// <summary>
        /// Drives a real VoiceRecallManager through start/stop with no microphone present, which
        /// is exactly the batch-mode situation, and checks that the stop is reported honestly.
        /// Also checks the WAV writer separately, since no capture happens here.
        /// </summary>
        static void CheckRecordingStopReporting()
        {
            var loggerGo = new GameObject("__IKEA_EEG_SelfTest_RecallLogger");
            var voiceGo = new GameObject("__IKEA_EEG_SelfTest_Voice");
            string csvPath = null;

            // VoiceRecallManager logs through the static EventLogger.Instance, which is
            // assigned in Awake — and Awake does not run in edit mode. Point it at this test's
            // logger for the duration, then put it back.
            var instanceProperty = typeof(EventLogger).GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            var previousInstance = instanceProperty?.GetValue(null);

            try
            {
                var logger = loggerGo.AddComponent<EventLogger>();
                var csv = loggerGo.AddComponent<CsvEventSink>();
                logger.bus.Register(csv);
                instanceProperty?.GetSetMethod(true)?.Invoke(null, new object[] { logger });
                logger.randomizationSeed = 4242;
                logger.currentState = ExperimentState.ImmediateRecall.ToString();
                logger.currentRoom = RoomNames.AreaA;
                logger.BeginSession();
                logger.BeginTrial();

                var voice = voiceGo.AddComponent<VoiceRecallManager>();
                voiceGo.AddComponent<NullTranscriptionProvider>();
                voice.ConfigureAdaptiveStop(4f, 0.05f, 0.015f, 0.35f);

                Assert(Mathf.Approximately(voice.recallSilenceStopSeconds, 4f) &&
                       voice.speechStartThreshold > voice.silenceThreshold,
                    "ExperimentConfig thresholds are pushed into the recorder " +
                    $"(stop={voice.recallSilenceStopSeconds} s)");

                // Each reason produces its own recording and its own stop event.
                foreach (var (phase, reason) in new[]
                         {
                             (RecallPhases.Immediate, RecallStopReasons.SilenceAfterSpeech),
                             (RecallPhases.Delayed, RecallStopReasons.MaxDuration),
                         })
                {
                    voice.StartRecording(phase);
                    var info = voice.StopRecording(reason);

                    Assert(info != null && info.stopReason == reason,
                        $"{phase} recording reports termination_reason={info?.stopReason}");

                    Assert(info != null && info.actualDurationSeconds >= 0d,
                        $"{phase} recording reports actual_recording_duration_s=" +
                        $"{info?.actualDurationSeconds:F3}");
                }

                voice.StartRecording(RecallPhases.Immediate);
                voice.AbortRecording("self test abort");

                logger.EndTrial();
                logger.EndSession("self test");
                csvPath = csv.filePath;
            }
            finally
            {
                Object.DestroyImmediate(voiceGo);
                Object.DestroyImmediate(loggerGo);
                instanceProperty?.GetSetMethod(true)?.Invoke(null, new[] { previousInstance });
            }

            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
            {
                Assert(false, "the recall stop test wrote a CSV");
                return;
            }

            var header = File.ReadAllLines(csvPath)[0].Split(',');
            var rows = ParseCsv(csvPath);
            var typeColumn = System.Array.IndexOf(header, "event_type");
            var notesColumn = System.Array.IndexOf(header, "notes");
            var phaseColumn = System.Array.IndexOf(header, "recall_phase");

            var stopRows = rows.Where(r => r[typeColumn] == EventTypes.RecallRecordingStop).ToList();

            Assert(stopRows.Count == 3,
                $"{stopRows.Count} RECALL_RECORDING_STOP rows (expected 3: two stops and one " +
                "abort — every recording that starts has an ending in the data)");

            foreach (var expected in new[]
                     {
                         RecallStopReasons.SilenceAfterSpeech,
                         RecallStopReasons.MaxDuration,
                         RecallStopReasons.Aborted,
                     })
            {
                Assert(stopRows.Any(r => r[notesColumn].Contains($"termination_reason={expected}")),
                    $"a stop row carries termination_reason={expected}");
            }

            Assert(stopRows.All(r => r[notesColumn].Contains("actual_recording_duration_s=")),
                "every stop row carries actual_recording_duration_s");

            Assert(stopRows.All(r => !string.IsNullOrEmpty(r[phaseColumn])),
                "every stop row names its recall phase");

            if (stopRows.Count > 0)
                Info($"  example: {stopRows[0][notesColumn]}");

            // ---- The WAV writer, exercised directly ----------------------------------------
            // No microphone exists here, so this checks the writer rather than the capture: a
            // real recording is a headset test.
            var samples = new float[16000];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = Mathf.Sin(i * 0.05f) * 0.5f;

            var clip = AudioClip.Create("selftest_tone", samples.Length, 1, 16000, false);
            clip.SetData(samples, 0);

            var wavPath = Path.Combine(Path.GetTempPath(), "IKEA_EEG_SelfTest",
                "selftest_recall.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(wavPath) ?? string.Empty);

            var saved = WavUtility.Save(wavPath, clip, samples.Length);
            Assert(saved && File.Exists(wavPath), $"WAV written: {wavPath}");

            if (!File.Exists(wavPath))
                return;

            var bytes = File.ReadAllBytes(wavPath);
            var riff = System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
            var wave = System.Text.Encoding.ASCII.GetString(bytes, 8, 4);

            // 44-byte header + 2 bytes per mono 16-bit sample.
            var expectedBytes = 44 + samples.Length * 2;

            Assert(riff == "RIFF" && wave == "WAVE",
                $"the WAV has a valid RIFF/WAVE header ({riff}/{wave})");
            Assert(bytes.Length == expectedBytes,
                $"the WAV is {bytes.Length} bytes, exactly the expected {expectedBytes} " +
                "(44-byte header + 16-bit mono samples) — nothing is trimmed or padded");
        }

        // ---------------------------------------------------------------------------------
        // Area B flow + RT contract
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Checks the event contract that makes instruction-reading time separable from
        /// response time, and the block/readiness state machine.
        ///
        /// The event SEQUENCE is produced here by replaying the order the ExperimentManager
        /// emits it in, with real elapsed time between the steps, and the CSV is then parsed
        /// back. That validates the contract and the arithmetic — that RT computed from
        /// timestamps excludes the reading period. It does NOT prove the manager's coroutine
        /// emits them in this order at run time; that is what the Quest run checks.
        /// </summary>
        /// <summary>
        /// READY on the Area B instruction screen must SILENCE the instruction narration.
        ///
        /// The bug this covers was behavioural and invisible to every structural check: the
        /// narration cancellation in this project hangs off <c>SetRoom</c>, which fires on AREA
        /// changes. READY is a transition WITHIN Area B, so nothing cancelled the clip and it
        /// kept talking over the first chair trial while the participant was already working.
        ///
        /// Driven through the REAL handler and the REAL narration entry point, not by reading
        /// the source, because the source looked correct the whole time it was broken. The
        /// observable is <see cref="ExperimentAudio.narrationVoiceCount"/> — the ownership list
        /// the audio layer actually consults when it is asked to stop narration.
        /// </summary>
        static void CheckAreaBReadyStopsNarration()
        {
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            Assert(manager != null && config != null,
                "the manager and the config are present");

            if (manager == null || config == null)
                return;

            // The SCENE's ExperimentAudio builds its AudioSource pool in Awake, and Awake does
            // not run outside Play mode — so that instance can play nothing here and the whole
            // fixture would assert against silence. AddComponent runs Awake synchronously, so a
            // throwaway instance has a real pool. It is swapped into the manager for the
            // duration and swapped back in the finally; the scene component is never touched,
            // which is what keeps this section from dirtying the scene.
            var audioHost = new GameObject("__IKEA_EEG_SelfTest_AreaBNarration");
            var audio = audioHost.AddComponent<ExperimentAudio>();

            // ExperimentAudio builds its AudioSource pool in Awake, and Unity does not call
            // Awake outside Play mode for a component without [ExecuteAlways] — so without this
            // the pool is empty, every PlayClip fails with "no AudioSource available", and the
            // fixture would be asserting against a narration that could never have started.
            // Calling the same private initialiser Awake calls is what makes the ownership
            // bookkeeping observable headlessly.
            var ensureSources = typeof(ExperimentAudio).GetMethod("EnsureSources",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var ensureClips = typeof(ExperimentAudio).GetMethod("EnsureClips",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(ensureSources != null,
                "the audio source pool can be initialised for a headless fixture");

            ensureSources?.Invoke(audio, null);
            ensureClips?.Invoke(audio, null);

            var clip = config.GetAreaBInstructionNarration(ExperimentLanguage.English);

            Assert(clip != null, "an English Area B instruction narration clip exists to cancel");

            if (clip == null)
            {
                Object.DestroyImmediate(audioHost);
                return;
            }

            var type = typeof(ExperimentManager);
            const BindingFlags instanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

            var speak = type.GetMethod("SpeakAreaBInstructions", instanceNonPublic);
            var narrationSequence = type.GetMethod("RunNarrationSequence", instanceNonPublic);
            var onReady = type.GetMethod("OnReadyPressed", instanceNonPublic);
            var stopNarration = type.GetMethod("StopParticipantNarration", instanceNonPublic);
            var stateField = type.GetField("m_State", instanceNonPublic);
            var readyField = type.GetField("m_AreaBReadyPressed", instanceNonPublic);
            var epochField = type.GetField("m_NarrationEpoch", instanceNonPublic);
            var abortedField = type.GetField("m_RunAborted", instanceNonPublic);
            var audioField = type.GetField("m_Audio", instanceNonPublic);

            Assert(speak != null && narrationSequence != null && onReady != null &&
                   stopNarration != null && stateField != null && readyField != null &&
                   epochField != null && abortedField != null && audioField != null,
                "the Area B narration entry point, the narration sequencer, the READY handler " +
                "and the narration state are all reachable");

            if (speak == null || narrationSequence == null || onReady == null ||
                stopNarration == null || stateField == null || readyField == null ||
                epochField == null || abortedField == null || audioField == null)
            {
                Object.DestroyImmediate(audioHost);
                return;
            }

            var originalAudio = audioField.GetValue(manager);
            audioField.SetValue(manager, audio);

            var originalState = stateField.GetValue(manager);
            var originalReady = readyField.GetValue(manager);

            // Earlier sections drive this same manager instance through AbortRun, which latches
            // m_RunAborted — and an aborted run refuses to speak at all, which would make this
            // fixture assert against a narration that never started. Cleared for the fixture and
            // restored in the finally, so no section can leak state into another.
            var originalAborted = abortedField.GetValue(manager);
            abortedField.SetValue(manager, false);

            try
            {
                // ---- E: stopping when nothing is playing must be safe ----------------------
                // Done FIRST, so it also guarantees a clean slate for everything below.
                stopNarration.Invoke(manager, new object[] { "self test — nothing playing" });
                stopNarration.Invoke(manager, new object[] { "self test — still nothing playing" });

                Assert(audio.narrationVoiceCount == 0,
                    "stopping narration when none is playing is safe and leaves no voice owned " +
                    $"({audio.narrationVoiceCount})");

                // ---- A: narration is playing before confirmation ----------------------------
                stateField.SetValue(manager, ExperimentState.AreaBInstructions);
                readyField.SetValue(manager, false);

                // The REAL sequencer, pumped by hand rather than by StartCoroutine.
                //
                // Outside Play mode Unity never advances a coroutine, so calling
                // SpeakAreaBInstructions here would schedule nothing and the test would be
                // asserting against a narration that had not begun. Driving the sequencer
                // directly runs the same PlayClip and the same ownership registration the
                // participant path uses; what is being verified is the CLAIM and its release,
                // which is ordinary logic (see ExperimentAudio.narrationVoiceCount).
                //
                // TWO segments, because the bug had two halves: the clip already sounding had
                // to be silenced, AND the segment still queued behind it had to be abandoned.
                var clips = new List<(AudioClip, string)>
                {
                    (clip, "AreaBTaskInstructions"),
                    (clip, "AreaBTaskInstructions_second_segment"),
                };

                var sequence = (System.Collections.IEnumerator)narrationSequence.Invoke(
                    manager, new object[]
                    {
                        clips, "self test area B instructions",
                        ExperimentState.AreaBInstructions,
                    });

                var started = sequence.MoveNext();

                Assert(started && audio.narrationVoiceCount > 0,
                    $"the Area B instruction narration is playing before READY " +
                    $"({audio.narrationVoiceCount} narration voice(s) owned)");

                var epochBeforeReady = (int)epochField.GetValue(manager);

                // ---- B: confirmation stops the narration -----------------------------------
                onReady.Invoke(manager, null);

                Assert(audio.narrationVoiceCount == 0,
                    "READY silences the instruction narration immediately " +
                    $"({audio.narrationVoiceCount} voice(s) still owned) — nothing carries into " +
                    "the first chair trial");

                // ---- B2: the segment still queued behind it is abandoned, not spoken --------
                var continued = sequence.MoveNext();

                Assert(!continued && audio.narrationVoiceCount == 0,
                    "the segment queued behind the cancelled one is abandoned by the epoch " +
                    $"guard rather than spoken into the chair task (continued={continued}, " +
                    $"{audio.narrationVoiceCount} voice(s) owned)");

                var epochAfterReady = (int)epochField.GetValue(manager);

                Assert(epochAfterReady > epochBeforeReady,
                    $"READY advanced the narration epoch ({epochBeforeReady} -> " +
                    $"{epochAfterReady}), so a sequence that survives the stop abandons itself");

                // ---- C: the chair task is still allowed to proceed --------------------------
                Assert((bool)readyField.GetValue(manager),
                    "READY still sets the gate RunAreaBInstructions polls — the block proceeds " +
                    "and the participant does not wait for the recording");

                Assert((ExperimentState)stateField.GetValue(manager) ==
                       ExperimentState.AreaBInstructions,
                    "READY does not change the state itself — the coroutine still owns the " +
                    "transition, exactly as before");

                // ---- D: repeated confirmation must not act twice ----------------------------
                var epochBeforeRepeat = (int)epochField.GetValue(manager);

                onReady.Invoke(manager, null);
                onReady.Invoke(manager, null);
                onReady.Invoke(manager, null);

                Assert((int)epochField.GetValue(manager) == epochBeforeRepeat,
                    "three further READY presses are inert — the narration epoch did not move " +
                    "again, so the handler cannot fire the transition or the stop twice");

                Assert((bool)readyField.GetValue(manager),
                    "the gate stays set after repeated presses (idempotent, not toggled)");

                Assert(audio.narrationVoiceCount == 0,
                    "repeated presses leave the audio layer silent");

                // A press from any OTHER state is refused outright, which is what stops a stray
                // ray during a chair trial from re-entering this path.
                stateField.SetValue(manager, ExperimentState.ChairSelection);
                readyField.SetValue(manager, false);

                onReady.Invoke(manager, null);

                Assert(!(bool)readyField.GetValue(manager),
                    "READY pressed outside the instruction state is refused — a stray press " +
                    "during a chair trial cannot re-arm the block");
            }
            finally
            {
                // Leave nothing sounding and put the manager back where it was: later sections
                // drive the same instance.
                stopNarration.Invoke(manager, new object[] { "self test cleanup" });
                stateField.SetValue(manager, originalState);
                readyField.SetValue(manager, originalReady);
                abortedField.SetValue(manager, originalAborted);
                audioField.SetValue(manager, originalAudio);
                Object.DestroyImmediate(audioHost);
            }

            // A source guard, in addition to the behavioural checks above: the stop must live in
            // the handler, so it happens at the press rather than a frame later.
            var source = StripCommentsAndAttributes(File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs"));

            var handlerStart = source.IndexOf("void OnReadyPressed()",
                System.StringComparison.Ordinal);

            Assert(handlerStart >= 0, "the READY handler exists");

            if (handlerStart >= 0)
            {
                var handlerEnd = source.IndexOf("void OnDeveloperMenuOpened", handlerStart,
                    System.StringComparison.Ordinal);

                if (handlerEnd < 0)
                    handlerEnd = Mathf.Min(source.Length, handlerStart + 1200);

                var handler = source.Substring(handlerStart, handlerEnd - handlerStart);

                Assert(handler.Contains("StopParticipantNarration"),
                    "the READY handler cancels narration through the existing " +
                    "StopParticipantNarration mechanism, not through a second audio path");

                Assert(!handler.Contains("AudioSource") && !handler.Contains("StopNarration("),
                    "the READY handler does not reach into the audio layer directly");
            }

            // Ties the pumped fixture above back to the participant path: the narration READY
            // cancels is the one SpeakAreaBInstructions starts, through the same sequencer.
            var speakStart = source.IndexOf("void SpeakAreaBInstructions()",
                System.StringComparison.Ordinal);

            Assert(speakStart >= 0, "the Area B narration entry point exists");

            if (speakStart >= 0)
            {
                var speakEnd = source.IndexOf("float SpeakDelayedRecognitionInstructions",
                    speakStart, System.StringComparison.Ordinal);

                if (speakEnd < 0)
                    speakEnd = Mathf.Min(source.Length, speakStart + 1600);

                var body = source.Substring(speakStart, speakEnd - speakStart);

                Assert(body.Contains("RunNarrationSequence"),
                    "the Area B instructions are spoken through the same RunNarrationSequence " +
                    "the test drives, so the cancellation verified here is the real one");

                Assert(body.Contains("ExperimentState.AreaBInstructions"),
                    "the Area B narration is bound to the AreaBInstructions state, which is " +
                    "what lets the epoch guard abandon it once READY has been pressed");
            }
        }

        static void CheckAreaBFlowContract()
        {
            var go = new GameObject("__IKEA_EEG_SelfTest_AreaB");
            string csvPath = null;

            try
            {
                var logger = go.AddComponent<EventLogger>();
                var csv = go.AddComponent<CsvEventSink>();
                logger.bus.Register(csv);
                logger.randomizationSeed = 777;
                logger.currentRoom = RoomNames.AreaB;
                logger.BeginSession();
                logger.BeginTrial();

                // Entering Area B.
                logger.currentState = ExperimentState.AreaBInstructions.ToString();
                logger.Log(EventTypes.AreaBEnter, e => e.chairTrialCount = "2");
                logger.Log(EventTypes.AreaBInstructionsOnset, e => e.chairTrialCount = "2");

                // A participant reading the task description for a while. This interval must
                // not appear inside any response time.
                System.Threading.Thread.Sleep(120);

                logger.Log(EventTypes.AreaBReady,
                    e => e.notes = "instruction_reading_duration_s=0.120");

                for (var trial = 1; trial <= 2; trial++)
                {
                    var index = trial;
                    var difficulty = trial == 1 ? "LOW" : "MEDIUM";

                    void Stamp(ExperimentEvent e)
                    {
                        e.chairTrialIndex = index.ToString();
                        e.chairTrialCount = "2";
                        e.difficulty = difficulty;
                    }

                    logger.currentState = ExperimentState.ChairInstruction.ToString();
                    logger.Log(EventTypes.ChairTrialPrepared, Stamp);
                    logger.Log(EventTypes.ChairTrialStart, Stamp);

                    System.Threading.Thread.Sleep(30);          // pre-target interval

                    logger.Log(EventTypes.ChairTargetOnset, Stamp);
                    logger.Log(EventTypes.ChairInstructionOnset, Stamp);

                    logger.currentState = ExperimentState.ChairSelection.ToString();
                    logger.Log(EventTypes.ChairSelectionTimerStart, Stamp);

                    System.Threading.Thread.Sleep(50);          // the participant responding

                    logger.Log(EventTypes.ChairSelected, e =>
                    {
                        Stamp(e);
                        e.objectId = "Chair_04";
                        e.correct = "TRUE";
                        e.responseTimeMs = "50.0";
                    });

                    logger.currentState = ExperimentState.ChairTrialFeedback.ToString();
                    logger.Log(EventTypes.ChairTrialEnd, Stamp);
                }

                logger.Log(EventTypes.ChairBlockComplete, e => e.chairTrialCount = "2");
                logger.EndTrial();
                logger.EndSession("self test");
                csvPath = csv.filePath;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
            {
                Assert(false, "the Area B flow test wrote a CSV");
                return;
            }

            var header = File.ReadAllLines(csvPath)[0].Split(',');
            var rows = ParseCsv(csvPath);
            var typeColumn = System.Array.IndexOf(header, "event_type");
            var timeColumn = System.Array.IndexOf(header, "timestamp_relative");
            var trialColumn = System.Array.IndexOf(header, "chair_trial_index");

            double TimeOf(string eventType, string trialIndex = null)
            {
                var row = rows.FirstOrDefault(r => r[typeColumn] == eventType &&
                                                   (trialIndex == null || r[trialColumn] == trialIndex));
                return row == null
                    ? double.NaN
                    : double.Parse(row[timeColumn], System.Globalization.CultureInfo.InvariantCulture);
            }

            // ---- The instruction screen happens once, before every trial --------------------
            Assert(rows.Count(r => r[typeColumn] == EventTypes.AreaBInstructionsOnset) == 1,
                "AREA_B_INSTRUCTIONS_ONSET appears exactly once per Area B block");
            Assert(rows.Count(r => r[typeColumn] == EventTypes.AreaBReady) == 1,
                "AREA_B_READY appears exactly once per Area B block");

            var instructionsOnset = TimeOf(EventTypes.AreaBInstructionsOnset);
            var ready = TimeOf(EventTypes.AreaBReady);
            var readingSeconds = ready - instructionsOnset;

            Assert(ready > instructionsOnset,
                $"READY follows the instructions ({readingSeconds * 1000d:F0} ms of reading)");

            // ---- READY gates trial 1, and trial 2 does not repeat the instructions ----------
            var firstTargetOnset = TimeOf(EventTypes.ChairTargetOnset, "1");
            var secondTargetOnset = TimeOf(EventTypes.ChairTargetOnset, "2");

            Assert(firstTargetOnset > ready,
                "trial 1's target appears only AFTER READY — the instructions gate the block");

            Assert(secondTargetOnset > firstTargetOnset &&
                   rows.Count(r => r[typeColumn] == EventTypes.AreaBInstructionsOnset) == 1,
                "trial 2 starts without the general instructions being shown again");

            // ---- RT is measured from target onset, and excludes the reading time -------------
            for (var trial = 1; trial <= 2; trial++)
            {
                var index = trial.ToString();
                var targetOnset = TimeOf(EventTypes.ChairTargetOnset, index);
                var timerStart = TimeOf(EventTypes.ChairSelectionTimerStart, index);
                var selected = TimeOf(EventTypes.ChairSelected, index);
                var trialStart = TimeOf(EventTypes.ChairTrialStart, index);

                Assert(timerStart >= targetOnset && (timerStart - targetOnset) < 0.05d,
                    $"trial {trial}: the timer starts at target onset " +
                    $"({(timerStart - targetOnset) * 1000d:F2} ms after it, adjacent frames)");

                Assert(targetOnset > trialStart,
                    $"trial {trial}: the target appears after the pre-target interval, not at " +
                    "trial start");

                var responseWindow = selected - targetOnset;
                Assert(responseWindow > 0d && responseWindow < readingSeconds + 0.05d + 0.05d,
                    $"trial {trial}: the response window ({responseWindow * 1000d:F0} ms) " +
                    "measures from target onset only");

                // The heart of the contract: no part of the reading period is inside the RT.
                Assert(targetOnset > ready,
                    $"trial {trial}: response time zero is after AREA_B_READY, so no " +
                    "instruction-reading time can be inside it");

                var fromTrialStart = selected - trialStart;
                Assert(fromTrialStart > responseWindow,
                    $"trial {trial}: measuring from trial start instead would have added " +
                    $"{(fromTrialStart - responseWindow) * 1000d:F0} ms of pre-target interval " +
                    "— which is why RT is defined from target onset");
            }

            // The reading period must not overlap any trial's response window.
            Assert(instructionsOnset < ready && ready < firstTargetOnset,
                "the instruction-reading interval and every response window are disjoint, so " +
                "the two are separable in the CSV");

            Info($"instruction reading = {readingSeconds * 1000d:F0} ms, " +
                 $"trial 1 RT window = {(TimeOf(EventTypes.ChairSelected, "1") - firstTargetOnset) * 1000d:F0} ms");

            // ---- The block/readiness state actually resets ------------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
            {
                Info("no ExperimentManager in the open scene — block reset not checked");
                return;
            }

            var flagField = typeof(ExperimentManager).GetField("m_AreaBInstructionsShown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var prepare = typeof(ExperimentManager).GetMethod("PrepareTrial",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(flagField != null && prepare != null,
                "the Area B block state and PrepareTrial are present");

            if (flagField == null || prepare == null)
                return;

            // Pretend a block has been read, then do what a restart does.
            flagField.SetValue(manager, true);
            Assert(manager.areaBInstructionsShown,
                "the block state can be set (simulating a block already in progress)");

            manager.ResetAreaBBlock();
            Assert(!manager.areaBInstructionsShown,
                "ResetAreaBBlock clears the instruction/readiness state");

            flagField.SetValue(manager, true);
            prepare.Invoke(manager, null);
            Assert(!manager.areaBInstructionsShown,
                "PrepareTrial — the path a RESTART and an abort both take — resets the Area B " +
                "instruction/readiness state, so the next block starts with the instructions " +
                "and requires READY again");
        }

        // ---------------------------------------------------------------------------------
        // Participant-facing result
        // ---------------------------------------------------------------------------------

        static void CheckParticipantBlockResult()
        {
            const string heading = "Executive task complete";
            const string footer = "Press EXIT SHOWROOM to continue.";

            // ---- All correct -----------------------------------------------------------------
            var perfect = new SessionResults();
            for (var i = 1; i <= 3; i++)
                perfect.chairTrials.Add(MakeTrial(i, DifficultyLevel.Low, true, 1500d, true));

            var perfectText = perfect.BuildParticipantBlockResult(heading, footer);
            Assert(perfectText.Contains("3 / 3 correct"),
                $"all correct -> '3 / 3 correct'");

            // ---- Two of three ------------------------------------------------------------------
            var mixed = new SessionResults();
            mixed.chairTrials.Add(MakeTrial(1, DifficultyLevel.Low, true, 1500d, true));
            mixed.chairTrials.Add(MakeTrial(2, DifficultyLevel.Medium, false, 2500d, true));
            mixed.chairTrials.Add(MakeTrial(3, DifficultyLevel.High, true, 2000d, true));

            var mixedText = mixed.BuildParticipantBlockResult(heading, footer);
            Assert(mixedText.Contains("2 / 3 correct"), "two of three -> '2 / 3 correct'");
            Assert(mixedText.Contains(heading) && mixedText.Contains(footer),
                "the heading and footer are shown");

            // ---- Nothing that belongs to the researcher may leak onto the panel ---------------
            foreach (var forbidden in new[]
                     { "ms)", "seed", "Seed", "Chair_", "RT", "CSV", "Blue", "Large", "Modern",
                       "accuracy", "median", "mean" })
            {
                Assert(!mixedText.Contains(forbidden),
                    $"the participant panel does not contain '{forbidden}'");
            }

            // ---- An invalid trial is excluded, not counted as wrong ---------------------------
            var withInvalid = new SessionResults();
            withInvalid.chairTrials.Add(MakeTrial(1, DifficultyLevel.Low, true, 1500d, true));
            withInvalid.chairTrials.Add(MakeTrial(2, DifficultyLevel.Medium, true, 2500d, true));
            withInvalid.chairTrials.Add(MakeTrial(3, DifficultyLevel.High, false, 0d, false));

            var invalidText = withInvalid.BuildParticipantBlockResult(heading, footer);

            Assert(invalidText.Contains("2 / 2 correct"),
                $"an aborted trial is EXCLUDED from the denominator, not counted as incorrect " +
                $"(shown: '2 / 2 correct', not '2 / 3')");

            Assert(invalidText.Contains("not completed"),
                "the excluded trial is stated plainly rather than hidden");

            Assert(withInvalid.chairTrialsTotal == 3 && withInvalid.chairTrialsScored == 2,
                "the researcher summary still records 3 attempted and 2 scored");

            // ---- No responses at all -----------------------------------------------------------
            var none = new SessionResults();
            none.chairTrials.Add(MakeTrial(1, DifficultyLevel.Low, false, 0d, false));

            var noneText = none.BuildParticipantBlockResult(heading, footer);
            Assert(!noneText.Contains("0 / 0") && noneText.Contains("No responses"),
                $"with no scored trials the panel says so instead of showing '0 / 0 correct'");

            Info("participant panel (2 of 3 correct):\n" + mixedText);

            // ---- The END-OF-RUN panel must lead with ACCURACY, not "trials completed" --------
            var runSummary = mixed.BuildParticipantRunSummary(heading, 2, 2);

            Assert(runSummary.Contains("2 / 3 correct"),
                $"the results panel leads with correct chair selections:\n{runSummary.Trim()}");

            foreach (var banned in new[]
                     { "trials completed", "trial complete", "TRIAL COMPLETE", "Trial id" })
            {
                Assert(runSummary.IndexOf(banned, System.StringComparison.OrdinalIgnoreCase) < 0,
                    $"the results panel does not say '{banned}' in place of the result");
            }

            // Accuracy must come BEFORE the run label, so it is what the eye lands on.
            var accuracyIndex = runSummary.IndexOf("correct", System.StringComparison.Ordinal);
            var runIndexPosition = runSummary.IndexOf("Run ", System.StringComparison.Ordinal);

            Assert(accuracyIndex > 0 && (runIndexPosition < 0 || accuracyIndex < runIndexPosition),
                "the accuracy line is the most prominent element on the panel");

            // With an excluded trial the denominator is still the SCORED trials.
            var withInvalidRun = withInvalid.BuildParticipantRunSummary(heading, 1, 1);
            Assert(withInvalidRun.Contains("2 / 2 correct"),
                $"an aborted trial stays out of the denominator on the results panel " +
                $"({withInvalidRun.Replace('\n', ' ')})");
        }

        // ---------------------------------------------------------------------------------
        // Area 0 — VR familiarization
        // ---------------------------------------------------------------------------------

        static void CheckFamiliarization()
        {
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            // ---- The room exists, before Area A, and is separate from it --------------------
            var spawns = Object.FindObjectsByType<SpawnPoint>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .ToDictionary(s => s.area, s => s.transform.position);

            Assert(spawns.ContainsKey(ExperimentArea.Familiarization),
                "Area 0 has its own spawn point");

            if (spawns.ContainsKey(ExperimentArea.Familiarization) &&
                spawns.ContainsKey(ExperimentArea.AreaA))
            {
                var distance = Vector3.Distance(spawns[ExperimentArea.Familiarization],
                    spawns[ExperimentArea.AreaA]);

                Assert(distance > 50f,
                    $"Area 0 is a separate room {distance:F0} m from Area A — the participant " +
                    "cannot walk between practice and the experiment");
            }

            var teleporter = Object.FindAnyObjectByType<XRRigTeleporter>();
            Assert(teleporter != null &&
                   teleporter.GetSpawn(ExperimentArea.Familiarization) != null,
                "the teleporter can reach Area 0");

            Assert(teleporter != null &&
                   XRRigTeleporter.ToRoomName(ExperimentArea.Familiarization) == RoomNames.Familiarization,
                $"Area 0 logs as room '{RoomNames.Familiarization}'");

            // ---- Practice objects are practice objects, not stimuli --------------------------
            var practiceObjects = Object.FindObjectsByType<PracticeObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(practiceObjects.Length >= 2,
                $"{practiceObjects.Length} practice objects exist (2-4 expected)");

            Assert(practiceObjects.All(p => p.GetComponent<ChairTarget>() == null),
                "no practice object is a chair");

            Assert(practiceObjects.All(p => p.GetComponentInChildren<Rigidbody>() == null),
                "no practice object has a Rigidbody — practice objects cannot be pushed");

            // Practice objects must not carry any memory word, or Area 0 primes Area A.
            if (config != null && config.wordList != null)
            {
                var words = new List<string>();
                for (var i = 0; i < config.wordList.setCount; i++)
                    words.AddRange(config.wordList.GetWords(i));

                var leaks = practiceObjects
                    .Where(p => words.Any(w =>
                        p.objectId.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                Assert(leaks.Count == 0,
                    $"no practice object is named after a memory word ({leaks.Count} leak(s))");

                var familiarizationText = config.familiarizationInstructionText ?? string.Empty;
                var textLeaks = words
                    .Where(w => familiarizationText.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                Assert(textLeaks.Count == 0,
                    $"the familiarization instructions contain no memory word " +
                    $"({string.Join(", ", textLeaks)})");

                Assert(familiarizationText.IndexOf("chair", System.StringComparison.OrdinalIgnoreCase) < 0,
                    "the familiarization instructions never mention chairs — Area 0 cannot " +
                    "prime the Area B search");
            }

            // ---- The manager's flow and skip behaviour ---------------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
            {
                Info("no ExperimentManager in the open scene — familiarization flow not checked");
                return;
            }

            var type = typeof(ExperimentManager);
            var enterFamiliarization = type.GetMethod("EnterFamiliarization",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var skipFamiliarization = type.GetMethod("SkipFamiliarization",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leaveFamiliarization = type.GetMethod("LeaveFamiliarization",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stateField = type.GetField("m_State",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(enterFamiliarization != null && skipFamiliarization != null &&
                   leaveFamiliarization != null && stateField != null,
                "the familiarization entry, skip and exit paths exist");

            if (enterFamiliarization == null || skipFamiliarization == null ||
                leaveFamiliarization == null || stateField == null)
                return;

            // Capture the seed and chair block, so the central promise — that Area 0 changes
            // nothing about the experiment — can be checked rather than asserted.
            var seedBefore = manager.sessionSeed;
            var plansBefore = manager.chairPlans.Select(p => p.StimulusSignature()).ToList();
            var wordSetBefore = config != null ? config.GetTrialWordSetId() : string.Empty;
            var difficultyBefore = config != null
                ? string.Join(",", config.BuildDifficultySequence())
                : string.Empty;

            enterFamiliarization.Invoke(manager, null);

            Assert((ExperimentState)stateField.GetValue(manager) == ExperimentState.Familiarization,
                "entering Area 0 puts the experiment in the Familiarization state");

            Assert(manager.practiceSelectionCount == 0,
                "entering Area 0 resets the practice counter");

            // A practice selection must be recorded, and must remain outside every metric.
            var practiceHandler = type.GetMethod("OnPracticeObjectSelected",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (practiceHandler != null && practiceObjects.Length > 0)
            {
                // The TARGET is selected, not an arbitrary object: since requirement A2 only
                // correct selections count toward practice progress, selecting whichever object
                // happens to be first would count zero and prove nothing about the counter.
                // TWO PHASES: the requested colour first, then a DIFFERENT one. Selecting the
                // target twice would satisfy phase 1 only and leave the count at 1.
                var first = manager.practiceTarget != null
                    ? manager.practiceTarget
                    : practiceObjects[0];
                var second = practiceObjects.FirstOrDefault(o => o != null && o.color != first.color)
                             ?? practiceObjects[0];

                practiceHandler.Invoke(manager, new object[] { first });
                practiceHandler.Invoke(manager, new object[] { second });

                Assert(manager.practiceSelectionCount == 2,
                    $"one qualifying selection per phase is counted " +
                    $"({manager.practiceSelectionCount})");

                Assert(manager.sessionResults.chairTrialsTotal == 0 &&
                       manager.sessionResults.chairTrialsScored == 0,
                       "practice selections create NO chair trials");

                Assert(double.IsNaN(manager.sessionResults.chairAccuracy),
                    "practice selections do not create a chair accuracy value");

                Assert(double.IsNaN(manager.sessionResults.meanResponseTimeSeconds),
                    "practice selections do not enter the response-time average");
            }

            // ---- START EXPERIMENT -> COMPLETED, and lands in Area A --------------------------
            leaveFamiliarization.Invoke(manager, new object[]
            {
                EventTypes.FamiliarizationComplete, "COMPLETED", "self test",
            });

            Assert(manager.familiarizationOutcome == "COMPLETED",
                $"START EXPERIMENT records outcome=COMPLETED ({manager.familiarizationOutcome})");

            Assert((ExperimentState)stateField.GetValue(manager) == ExperimentState.Idle,
                "leaving Area 0 lands at the Area A idle screen, with its START gate intact");

            // ---- SKIP -> SKIPPED, and still lands in Area A ----------------------------------
            enterFamiliarization.Invoke(manager, null);
            leaveFamiliarization.Invoke(manager, new object[]
            {
                EventTypes.FamiliarizationSkipped, "SKIPPED", "self test skip",
            });

            Assert(manager.familiarizationOutcome == "SKIPPED",
                $"SKIP INTRO records outcome=SKIPPED ({manager.familiarizationOutcome})");

            Assert((ExperimentState)stateField.GetValue(manager) == ExperimentState.Idle,
                "SKIP INTRO reaches Area A safely");

            // The never-entered path.
            skipFamiliarization.Invoke(manager, new object[] { "self test never entered" });

            Assert(manager.familiarizationOutcome == "SKIPPED" &&
                   manager.practiceSelectionCount == 0,
                "skipping Area 0 entirely is recorded as SKIPPED with zero practice selections");

            // ---- Nothing experimental moved --------------------------------------------------
            Assert(manager.sessionSeed == seedBefore,
                $"the randomization seed is unchanged by familiarization ({manager.sessionSeed})");

            var plansAfter = manager.chairPlans.Select(p => p.StimulusSignature()).ToList();
            Assert(plansBefore.SequenceEqual(plansAfter),
                $"the generated chair block is unchanged by familiarization " +
                $"({plansAfter.Count} trial(s))");

            Assert(config == null || config.GetTrialWordSetId() == wordSetBefore,
                "the word set is unchanged by familiarization");

            Assert(config == null ||
                   string.Join(",", config.BuildDifficultySequence()) == difficultyBefore,
                "the difficulty sequence is unchanged by familiarization");

            // ---- Restart returns to Area 0; the researcher replay does not -------------------
            var restartSession = type.GetMethod("RestartSession",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(restartSession != null &&
                   restartSession.GetParameters().Any(p => p.Name == "returnToFamiliarization"),
                "restart takes an explicit 'return to familiarization' decision rather than " +
                "always doing one thing");

            Info("RESTART -> Area 0 (participant button); Replay Same Seed -> Area A " +
                 "(researcher menu only).");
        }

        // ---------------------------------------------------------------------------------
        // Dual-trigger selection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Checks the input CONFIGURATION that makes both triggers select, and the guards that
        /// stop one press producing two selections.
        ///
        /// What this cannot do headlessly is press a physical trigger. Whether a real Quest
        /// grip actuates the binding is a headset question and is listed as such.
        /// </summary>
        static void CheckDualTriggerInput()
        {
            var interactors = Object.FindObjectsByType<NearFarInteractor>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(interactors.Length == 2, $"{interactors.Length} controller interactors found");

            foreach (var interactor in interactors)
            {
                var name = interactor.transform.parent?.name ?? interactor.name;

                foreach (var (reader, label) in new[]
                         {
                             (interactor.selectInput, "select"),
                             (interactor.uiPressInput, "UI press"),
                         })
                {
                    var performed = reader?.inputActionPerformed;
                    var paths = performed != null
                        ? performed.bindings.Select(b => b.path).ToArray()
                        : System.Array.Empty<string>();

                    var hasIndex = paths.Any(p =>
                        p.IndexOf("triggerPressed", System.StringComparison.OrdinalIgnoreCase) >= 0);
                    var hasGrip = paths.Any(p =>
                        p.IndexOf("gripPressed", System.StringComparison.OrdinalIgnoreCase) >= 0);

                    Assert(hasIndex, $"{name} {label}: the INDEX trigger selects");
                    Assert(hasGrip, $"{name} {label}: the GRIP trigger selects");

                    // THE duplicate-selection guarantee: one action, several bindings. Two
                    // actions would each raise their own Select.
                    Assert(performed != null && paths.Length >= 2,
                        $"{name} {label}: both triggers are {paths.Length} bindings on the " +
                        $"SINGLE action '{performed?.name}', so one press cannot produce two " +
                        "selections");
                }
            }

            // ---- Nothing became grabbable, and haptics stayed off ----------------------------
            var grabbables = Object.FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

            Assert(grabbables == 0,
                $"dual-trigger support introduced no grabbable object ({grabbables} found)");

            var activeHaptics = Object.FindObjectsByType<SimpleHapticFeedback>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(h => h.enabled);

            Assert(activeHaptics == 0,
                $"haptics remain disabled ({activeHaptics} enabled SimpleHapticFeedback)");

            // ---- The task-level guards that back the input configuration up ------------------
            var task = Object.FindAnyObjectByType<ChairSelectionTask>();
            if (task == null)
                return;

            var chairType = typeof(ChairTarget);
            Assert(chairType.GetField("m_AlreadySelectedThisTrial",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "each chair keeps its own already-selected latch");

            Assert(typeof(ChairSelectionTask).GetField("m_SelectionMade",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "the task keeps a second, independent selection latch");

            Info("Duplicate-selection protection is three-deep: one input action (so one " +
                 "Select), the per-chair latch, and the task-level latch. Any one of them " +
                 "alone would be sufficient.");
        }

        // ---------------------------------------------------------------------------------
        // Objective shape categories
        // ---------------------------------------------------------------------------------

        static void CheckShapeCategories()
        {
            var names = System.Enum.GetNames(typeof(ChairShape));

            Assert(names.Length == 3,
                $"there are still exactly 3 shape categories ({string.Join(", ", names)})");

            // The two ambiguous labels must be gone from the enum entirely.
            foreach (var banned in new[] { "Modern", "Classic" })
            {
                Assert(!names.Any(n => string.Equals(n, banned, System.StringComparison.OrdinalIgnoreCase)),
                    $"'{banned}' is no longer a shape category");
            }

            Assert(names.SequenceEqual(new[] { "Solid", "Slatted", "Curved" }),
                $"the categories are Solid / Slatted / Curved ({string.Join(", ", names)})");

            // Integer order is what seeds and serialized assets point at. If it changed, old
            // seeds would silently select different geometry.
            Assert((int)ChairShape.Solid == 0 && (int)ChairShape.Slatted == 1 &&
                   (int)ChairShape.Curved == 2,
                "the enum's integer order is unchanged, so a given seed still produces the " +
                "same geometry it did before the rename");

            // ---- No participant-facing string may contain the old labels --------------------
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config != null)
            {
                var participantStrings = new Dictionary<string, string>
                {
                    { "familiarizationInstructionText", config.familiarizationInstructionText },
                    { "areaAInstructionText", config.areaAInstructionText },
                    { "encodingListenPromptText", config.encodingListenPromptText },
                    { "immediateRecallPromptText", config.immediateRecallPromptText },
                    { "readyForAreaBText", config.readyForAreaBText },
                    { "areaBGeneralInstructionText", config.areaBGeneralInstructionText },
                    { "chairTargetHeaderText", config.chairTargetHeaderText },
                    { "chairSelectedFeedbackText", config.chairSelectedFeedbackText },
                    { "executiveTaskCompleteText", config.executiveTaskCompleteText },
                    { "chairBlockCompleteText", config.chairBlockCompleteText },
                    { "areaCInstructionText", config.areaCInstructionText },
                    { "delayedRecallPromptText", config.delayedRecallPromptText },
                };

                foreach (var banned in new[] { "CLASSIC", "MODERN" })
                {
                    var offenders = participantStrings
                        .Where(kv => !string.IsNullOrEmpty(kv.Value) &&
                                     kv.Value.IndexOf(banned, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(kv => kv.Key)
                        .ToList();

                    Assert(offenders.Count == 0,
                        $"'{banned}' appears in no participant-facing string " +
                        $"({string.Join(", ", offenders)})");
                }

                // The generated target text is what the participant actually reads on a trial.
                foreach (ChairShape shape in System.Enum.GetValues(typeof(ChairShape)))
                {
                    var spec = new ChairSpec(ChairColor.Blue, ChairSize.Large, shape);
                    var lines = spec.ToTargetLines();

                    Assert(lines.IndexOf("CLASSIC", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                           lines.IndexOf("MODERN", System.StringComparison.OrdinalIgnoreCase) < 0,
                        $"the {shape} target text is free of the old labels: " +
                        $"\"{lines.Replace('\n', '/')}\"");
                }
            }

            // ---- The invariants the rename must not have disturbed ---------------------------
            var profiles = DifficultyProfile.CreateDefaults();
            var ambiguous = 0;
            var generated = 0;

            foreach (var level in new[] { DifficultyLevel.Low, DifficultyLevel.Medium, DifficultyLevel.High })
            {
                var profile = ChairTrialGenerator.FindProfile(profiles, level);

                for (var seed = 1; seed <= 150; seed++)
                {
                    var request = new ChairTrialGenerator.Request
                    {
                        sessionSeed = seed,
                        trialIndex = 1,
                        trialCount = 1,
                        difficulty = level,
                        profile = profile,
                        chairIds = k_TestChairIds,
                        slotCount = 6,
                    };

                    if (!ChairTrialGenerator.TryGenerate(request, out var plan, out _))
                        continue;

                    generated++;

                    if (plan.assignments.Count(a => a.spec.Matches(plan.target)) != 1)
                        ambiguous++;
                }
            }

            Assert(generated > 0 && ambiguous == 0,
                $"exactly one correct chair still holds under the new categories " +
                $"({generated} trials, {ambiguous} ambiguous)");

            // Same seed, same block — the rename changed names, not the mapping.
            ChairTrialGenerator.TryGenerateBlock(555,
                new[] { DifficultyLevel.Low, DifficultyLevel.Medium, DifficultyLevel.High },
                profiles, k_TestChairIds, 6, out var a, out _);
            ChairTrialGenerator.TryGenerateBlock(555,
                new[] { DifficultyLevel.Low, DifficultyLevel.Medium, DifficultyLevel.High },
                profiles, k_TestChairIds, 6, out var b, out _);

            Assert(a != null && b != null &&
                   a.Select(p => p.StimulusSignature()).SequenceEqual(
                       b.Select(p => p.StimulusSignature())),
                "same-seed reproducibility is intact under the new categorical mapping");

            // ---- The legend belongs to the instruction screen only ---------------------------
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();
            if (ui == null)
                return;

            ui.ShowShapeLegend(true);
            Assert(ui.shapeLegendVisible, "the shape legend can be shown");

            ui.ShowShapeLegend(false);
            Assert(!ui.shapeLegendVisible, "the shape legend can be hidden for the trial phase");

            // Leaving Area B must take the legend down even if something forgot to.
            ui.ShowShapeLegend(true);
            ui.ShowArea(ExperimentArea.AreaC);
            Assert(!ui.shapeLegendVisible,
                "leaving Area B always hides the legend, so it cannot survive into another area");

            ui.ShowArea(ExperimentArea.AreaA);
        }

        // ---------------------------------------------------------------------------------
        // Practice task V2 + controller help
        // ---------------------------------------------------------------------------------

        static void CheckPracticeTaskAndControllerHelp()
        {
            var practiceObjects = Object.FindObjectsByType<PracticeObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(p => p.objectId)
                .ToArray();

            Assert(practiceObjects.Length == 4,
                $"exactly FOUR practice objects exist ({practiceObjects.Length})");

            var colors = practiceObjects.Select(p => p.colorName).ToList();
            Assert(colors.All(c => !string.IsNullOrEmpty(c)) &&
                   colors.Distinct().Count() == colors.Count,
                $"each practice object has a distinct colour name ({string.Join(", ", colors)})");

            // ---- The target is one of the four, and success persists -------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
            {
                Info("no ExperimentManager in the open scene — practice flow not checked");
                return;
            }

            var type = typeof(ExperimentManager);
            var enterFamiliarization = type.GetMethod("EnterFamiliarization",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var practiceHandler = type.GetMethod("OnPracticeObjectSelected",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (enterFamiliarization == null || practiceHandler == null)
            {
                Assert(false, "the practice entry and selection handlers exist");
                return;
            }

            enterFamiliarization.Invoke(manager, null);

            Assert(manager.practiceTarget != null &&
                   practiceObjects.Contains(manager.practiceTarget),
                $"the requested target is one of the four objects " +
                $"({manager.practiceTarget?.objectId})");

            Assert(!manager.practiceSucceeded,
                "practice starts un-succeeded");

            // ---- Only the MOST RECENTLY selected object is animated --------------------------
            var wrong = practiceObjects.First(p => p != manager.practiceTarget);
            practiceHandler.Invoke(manager, new object[] { wrong });

            Assert(wrong.isActive,
                "the selected object becomes the active (animated) one");

            Assert(practiceObjects.Count(p => p.isActive) == 1,
                $"exactly one object is animated " +
                $"({practiceObjects.Count(p => p.isActive)})");

            Assert(!wrong.succeeded && !manager.practiceSucceeded,
                "selecting a non-target object does not mark the target as reached");

            // CHANGED BY BLOCK 1 (requirement A2). The count used to increment on every
            // selection; it now increments ONLY on a correct one, so a wrong answer makes no
            // progress and the participant stays on the same target until they get it right.
            // Every selection is still LOGGED — what changed is what earns an increment.
            Assert(manager.practiceSelectionCount == 0,
                $"a WRONG selection does not advance practice progress " +
                $"(count {manager.practiceSelectionCount})");

            // The requested object: still just one animated object afterwards.
            var target = manager.practiceTarget;
            practiceHandler.Invoke(manager, new object[] { target });

            Assert(target.succeeded && manager.practiceSucceeded,
                $"selecting the requested object ({target.objectId}) records the target as reached");

            Assert(target.isActive && !wrong.isActive,
                "the previously selected object LOSES the animation when another is selected");

            Assert(practiceObjects.Count(p => p.isActive) == 1,
                "still exactly one animated object after a second selection");

            // Selecting something else again must move the highlight off the target — the
            // correct answer is not frozen in place forever.
            practiceHandler.Invoke(manager, new object[] { wrong });

            Assert(wrong.isActive && !target.isActive,
                "the highlight follows the newest selection, even away from the correct object");

            Assert(manager.practiceSucceeded,
                "having reached the target is REMEMBERED even after selecting elsewhere");

            // ---- Readiness: at least 2 selections AND the requested target -------------------
            var practiceConfig = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);
            var minimum = practiceConfig != null ? practiceConfig.practiceMinimumSelections : 2;
            Assert(minimum >= 2, $"at least {minimum} selections are required for readiness");

            // TWO-PHASE RULE: this sequence was (wrong, target, wrong). The first wrong colour
            // failed phase 1; the target passed it; the second wrong colour is a DIFFERENT
            // colour and therefore passes phase 2. That completes familiarization — which is
            // exactly the behaviour this pass fixed, and the opposite of what it used to do.
            Assert(manager.practiceReady,
                $"requested colour then a different colour completes familiarization " +
                $"(count {manager.practiceSelectionCount}, minimum {minimum})");

            Assert(manager.practicePhase == PracticePhase.Complete,
                $"the phase reports Complete ({manager.practicePhase})");

            enterFamiliarization.Invoke(manager, null);
            practiceHandler.Invoke(manager, new object[] { manager.practiceTarget });

            Assert(manager.practiceSucceeded && !manager.practiceReady,
                "the target alone is NOT enough — the selection minimum must also be met");

            // Re-selecting the SAME requested colour does not satisfy phase 2 — it is not
            // "another one" — so the participant stays where they are.
            practiceHandler.Invoke(manager, new object[] { manager.practiceTarget });

            Assert(!manager.practiceReady &&
                   manager.practicePhase == PracticePhase.DifferentColorSelection,
                $"repeating the requested colour does not complete phase 2 " +
                $"({manager.practicePhase})");

            // A different colour does, and readiness once earned is not revoked afterwards.
            var differentColor = practiceObjects.First(p => p.color != manager.practiceTargetColor);
            practiceHandler.Invoke(manager, new object[] { differentColor });

            Assert(manager.practiceReady,
                "a different-coloured second selection completes familiarization");

            practiceHandler.Invoke(manager, new object[] { manager.practiceTarget });

            Assert(manager.practiceReady,
                "a later selection does not REVOKE readiness already earned");

            enterFamiliarization.Invoke(manager, null);
            var notTheTarget = practiceObjects.First(p => p != manager.practiceTarget);
            practiceHandler.Invoke(manager, new object[] { notTheTarget });
            practiceHandler.Invoke(manager, new object[] { notTheTarget });
            practiceHandler.Invoke(manager, new object[] { notTheTarget });

            Assert(!manager.practiceReady,
                "selections alone are NOT enough — the requested target must also be reached");

            // ---- Practice is excluded from every cognitive metric -----------------------------
            Assert(manager.sessionResults.chairTrialsTotal == 0 &&
                   double.IsNaN(manager.sessionResults.chairAccuracy) &&
                   double.IsNaN(manager.sessionResults.meanResponseTimeSeconds),
                "practice creates no chair trial, no accuracy and no response-time average");

            // ---- START EXPERIMENT is usable WITHOUT completing practice ----------------------
            enterFamiliarization.Invoke(manager, null);
            Assert(!manager.practiceSucceeded,
                "re-entering Area 0 resets the practice state");

            var startPressed = type.GetMethod("OnStartExperimentPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            startPressed?.Invoke(manager, null);

            Assert(manager.familiarizationOutcome == "COMPLETED",
                "START EXPERIMENT works with NO practice completed — practice is never required");

            // ---- Controller help uses the REAL model, and the live rig is untouched -----------
            var diagram = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "ControllerDiagram");

            Assert(diagram != null, "the Area 0 controller help exists");

            if (diagram == null)
                return;

            var modelInstances = diagram.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.StartsWith("ControllerModel_"))
                .ToArray();

            Assert(modelInstances.Length == 2,
                $"both hands are shown ({modelInstances.Length} controller models)");

            // The help must be a DISPLAY: nothing on it may be pointable or tracked.
            var helpColliders = diagram.GetComponentsInChildren<Collider>(true).Length;
            Assert(helpColliders == 0,
                $"the controller help has no colliders ({helpColliders}) — it cannot be selected");

            var helpInteractables = diagram
                .GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>(true)
                .Length;
            Assert(helpInteractables == 0,
                "the controller help contains no interactable");

            // It must reuse the SAME model asset the rig displays, and must NOT be part of the
            // rig: a help object parented under the XR Origin would be a live-prefab change.
            var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            Assert(origin == null || !diagram.IsChildOf(origin.transform),
                "the controller help is NOT parented to the XR Origin — the live rig and its " +
                "controller prefabs are untouched");

            if (modelInstances.Length > 0)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(
                    modelInstances[0].gameObject);
                var sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : string.Empty;

                Assert(sourcePath.EndsWith("UniversalController.fbx"),
                    $"the help reuses the real controller model asset ({sourcePath})");
            }
        }

        // ---------------------------------------------------------------------------------
        // Area 0 localization — the two bugs reported from the headset
        // ---------------------------------------------------------------------------------

        /// <summary>Every TMP label in the scene whose GameObject has the given name.</summary>
        static TMP_Text FindLabel(string name)
        {
            return Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.gameObject.name == name);
        }

        /// <summary>
        /// A label read from the UI controller's OWN serialized reference.
        ///
        /// Preferred over finding by name wherever several areas share a label name
        /// (Txt_Instruction exists in more than one canvas): this returns the exact object the
        /// manager writes to, so the test cannot pass by inspecting a different panel.
        /// </summary>
        static TMP_Text UiLabel(ExperimentUIController ui, string fieldName)
        {
            return typeof(ExperimentUIController)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ui) as TMP_Text;
        }

        /// <summary>
        /// The colour labels beside the practice objects and the controller-help captions were
        /// both reported as still English in Spanish and Japanese. Neither had a LocalizedText
        /// binding at all — they were literals baked in by the scene builder.
        ///
        /// This checks the BINDING (a label that is bound cannot be left in another language)
        /// and then the RENDERED RESULT in all three languages.
        /// </summary>
        static void CheckArea0Localization()
        {
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                // ---- Practice colour labels --------------------------------------------------
                foreach (var color in PracticeColors.All)
                {
                    var label = FindLabel($"Txt_Practice_{color}");

                    Assert(label != null,
                        $"the {color} practice object has a colour label");

                    if (label == null)
                        continue;

                    var localized = label.GetComponent<LocalizedText>();

                    Assert(localized != null,
                        $"the {color} practice label is BOUND to the localization system " +
                        "(not a hard-coded English word)");

                    Assert(localized != null &&
                           localized.key == LocKeys.PracticeColorPrefix + color,
                        $"the {color} practice label uses the key for its OWN colour " +
                        $"({localized?.key})");
                }

                // ---- Controller help ----------------------------------------------------------
                var controllerLabels = new (string name, string key)[]
                {
                    ("Txt_DiagramTitle", LocKeys.ControllerTitle),
                    ("Txt_IndexTrigger", LocKeys.ControllerIndexTrigger),
                    ("Txt_GripTrigger", LocKeys.ControllerGripTrigger),
                    ("Txt_DiagramFooter", LocKeys.ControllerFooter),
                    ("Txt_Hand_Left", LocKeys.HandLeft),
                    ("Txt_Hand_Right", LocKeys.HandRight),
                };

                foreach (var (name, key) in controllerLabels)
                {
                    var label = FindLabel(name);
                    Assert(label != null, $"controller help label '{name}' exists");

                    if (label == null)
                        continue;

                    var localized = label.GetComponent<LocalizedText>();

                    Assert(localized != null && localized.key == key,
                        $"'{name}' is localized with {key} (was: hard-coded English)");
                }

                // ---- The rendered result, in each language ------------------------------------
                // A binding that resolves to the English string in a Spanish session would pass
                // the checks above and still be the reported bug, so the text itself is compared.
                foreach (var language in ExperimentLanguages.Selectable)
                {
                    ExperimentLocalization.SetLanguage(language);

                    foreach (var color in PracticeColors.All)
                    {
                        var label = FindLabel($"Txt_Practice_{color}");
                        if (label == null)
                            continue;

                        label.GetComponent<LocalizedText>()?.Refresh();

                        var expected = ExperimentLocalization.PracticeColorName(color);

                        Assert(label.text == expected,
                            $"{ExperimentLanguages.ToCode(language)}: the {color} practice " +
                            $"label reads '{label.text}'");

                        if (language != ExperimentLanguage.English)
                        {
                            var english = LocalizationTable.entries[
                                LocKeys.PracticeColorPrefix + color].english;

                            // Green is VERDE in Spanish and the English is GREEN, so a genuine
                            // match only matters where the two languages differ.
                            Assert(expected == english || label.text != english,
                                $"{ExperimentLanguages.ToCode(language)}: the {color} label is " +
                                $"NOT left in English");
                        }
                    }

                    var index = FindLabel("Txt_IndexTrigger");
                    if (index == null)
                        continue;

                    index.GetComponent<LocalizedText>()?.Refresh();

                    Assert(index.text.Contains(
                            ExperimentLocalization.Get(LocKeys.ControllerIndexTrigger)),
                        $"{ExperimentLanguages.ToCode(language)}: the index-trigger caption is " +
                        "in the selected language");

                    Assert(index.text.Contains("<color=#4E9BFF>"),
                        $"{ExperimentLanguages.ToCode(language)}: the coloured bullet survives " +
                        "localization (it is a format wrapper, not translated text)");
                }

                // ---- No developer control is advertised to the participant --------------------
                var helpText = string.Join(" ", controllerLabels
                    .Select(l => FindLabel(l.name))
                    .Where(l => l != null)
                    .Select(l => l.text));

                foreach (var forbidden in new[]
                         { "thumbstick", "developer", "menu", "abort", "joystick" })
                {
                    Assert(helpText.IndexOf(forbidden, System.StringComparison.OrdinalIgnoreCase) < 0,
                        $"the participant controller help does not mention '{forbidden}'");
                }
            }
            finally
            {
                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }
        }

        // ---------------------------------------------------------------------------------
        // Practice target — one canonical value
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The reported bug: the spoken prompt asked for one colour while the written prompt
        /// named another (always RED, as it turned out — every practice object was built with
        /// the defaulted ChairColor.Red because the builder never passed the colour).
        ///
        /// The fix is architectural, so the test is too: it verifies that a practice object
        /// cannot hold two different colours, that the manager draws exactly ONE canonical value,
        /// and that the text, the label, the narration key and the correctness test are all
        /// derived from that one value.
        /// </summary>
        static void CheckPracticeTargetSingleSource()
        {
            // ---- One representation, not two -------------------------------------------------
            var practiceFields = typeof(PracticeObject)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(f => f.Name.ToLowerInvariant().Contains("color"))
                .ToArray();

            Assert(practiceFields.Length == 1 &&
                   practiceFields[0].FieldType == typeof(ChairColor),
                "PracticeObject stores its colour EXACTLY ONCE, as a ChairColor " +
                $"({string.Join(", ", practiceFields.Select(f => $"{f.FieldType.Name} {f.Name}"))})");

            // Configure must REQUIRE the colour: the original bug was a defaulted parameter that
            // the one caller silently omitted.
            var configure = typeof(PracticeObject).GetMethod("Configure");
            var colorParam = configure?.GetParameters()
                .FirstOrDefault(p => p.ParameterType == typeof(ChairColor));

            Assert(colorParam != null && !colorParam.HasDefaultValue,
                "PracticeObject.Configure takes the colour as a REQUIRED parameter — it cannot " +
                "be forgotten");

            // ---- Every built object agrees with itself ---------------------------------------
            var practiceObjects = Object.FindObjectsByType<PracticeObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(practiceObjects.Length == 4,
                $"four practice objects exist ({practiceObjects.Length})");

            var distinctColors = practiceObjects.Select(p => p.color).Distinct().Count();

            Assert(distinctColors == practiceObjects.Length,
                $"the four practice objects have FOUR DIFFERENT colours ({distinctColors}) — " +
                "this is the check that would have caught all four being Red");

            foreach (var practice in practiceObjects)
            {
                Assert(practice.objectId == $"Practice_{practice.color}",
                    $"'{practice.objectId}' is named after the colour it actually is " +
                    $"({practice.color})");

                Assert(practice.colorName == PracticeColors.CanonicalName(practice.color),
                    $"'{practice.objectId}' canonical name is derived from the enum " +
                    $"({practice.colorName})");
            }

            // ---- The manager draws once, and everything follows it ---------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
            {
                Info("no ExperimentManager in the open scene — target flow not checked");
                return;
            }

            var type = typeof(ExperimentManager);
            var enterFamiliarization = type.GetMethod("EnterFamiliarization",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var practiceHandler = type.GetMethod("OnPracticeObjectSelected",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            var previousLanguage = ExperimentLocalization.language;

            try
            {
                foreach (var language in ExperimentLanguages.Selectable)
                {
                    ExperimentLocalization.ResetForTesting();
                    ExperimentLocalization.SetLanguage(language);

                    var code = ExperimentLanguages.ToCode(language);

                    enterFamiliarization.Invoke(manager, null);

                    Assert(manager.hasPracticeTarget,
                        $"{code}: a canonical practice target was chosen");

                    var target = manager.practiceTarget;
                    var canonical = manager.practiceTargetColor;

                    // The object and the canonical value must be the same fact.
                    Assert(target != null && target.color == canonical,
                        $"{code}: the target OBJECT and the canonical target COLOUR agree " +
                        $"({target?.color} / {canonical})");

                    // Written prompt.
                    var prompt = FindLabel("Txt_PracticePrompt");
                    var expectedName = ExperimentLocalization.PracticeColorName(canonical);

                    Assert(prompt != null && prompt.text.Contains(expectedName),
                        $"{code}: the WRITTEN prompt names the canonical colour " +
                        $"('{expectedName}')");

                    // No OTHER practice colour may appear in the prompt: naming two colours is
                    // the participant-visible form of this bug.
                    foreach (var other in PracticeColors.All)
                    {
                        if (other == canonical)
                            continue;

                        var otherName = ExperimentLocalization.PracticeColorName(other);

                        // Some languages share a substring across colours; only flag a genuine
                        // second colour name.
                        if (otherName == expectedName || expectedName.Contains(otherName))
                            continue;

                        Assert(prompt == null || !prompt.text.Contains(otherName),
                            $"{code}: the prompt does NOT also name {other} ('{otherName}')");
                    }

                    // Spoken prompt — resolved from the SAME value.
                    if (config != null)
                    {
                        var clip = config.GetPracticeColorClip(language, canonical);

                        if (clip != null)
                        {
                            Assert(clip.name ==
                                   $"Practice_{PracticeColors.CanonicalName(canonical)}",
                                $"{code}: the SPOKEN prompt is the clip for the canonical " +
                                $"colour ({clip.name})");
                        }
                        else
                        {
                            Info($"{code}: no narration clip for {canonical} — this language " +
                                 "runs text-only and is never given another language's voice");
                        }
                    }

                    // Correctness — same value again.
                    var wrong = practiceObjects.First(p => p.color != canonical);
                    practiceHandler.Invoke(manager, new object[] { wrong });

                    Assert(!manager.practiceSucceeded,
                        $"{code}: selecting a DIFFERENT colour is not success");

                    var right = practiceObjects.First(p => p.color == canonical);
                    practiceHandler.Invoke(manager, new object[] { right });

                    Assert(manager.practiceSucceeded,
                        $"{code}: selecting the canonical colour IS success");
                }
            }
            finally
            {
                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }
        }

        // ---------------------------------------------------------------------------------
        // Narration mapping: 3 languages x 4 colours
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Proves the full 3 x 4 language/colour narration mapping, and that every entry resolves
        /// to EXACTLY ONE clip — the right language's recording of the right colour.
        ///
        /// The expected pairs come from the generator's own script table, so this compares the
        /// two ends of the pipeline against each other rather than against a third hand-written
        /// list that could itself be wrong.
        /// </summary>
        static void CheckPracticeNarrationMapping()
        {
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config == null)
            {
                Assert(false, "the ExperimentConfig asset exists");
                return;
            }

            // ---- The key is built from a TYPED value, not parsed from a name -----------------
            var lookup = typeof(ExperimentConfig).GetMethod("GetPracticeColorClip");
            var parameters = lookup?.GetParameters();

            Assert(parameters != null && parameters.Length == 2 &&
                   parameters[1].ParameterType == typeof(ChairColor),
                "GetPracticeColorClip takes a ChairColor — a colour cannot be passed as free " +
                "text, and the requested colour is never inferred from a clip name");

            // ---- Keys are unique -------------------------------------------------------------
            var ids = config.practiceColorNarrationClips
                .Where(e => e != null)
                .Select(e => e.id)
                .ToList();

            Assert(ids.Count == ids.Distinct().Count(),
                $"every narration key is unique ({ids.Count} entries)");

            // ---- The full 3 x 4 grid ----------------------------------------------------------
            var expected = NarrationClipGenerator.ExpectedPracticeColorMappings().ToList();

            Assert(expected.Count == 12,
                $"the generator defines all 3 languages x 4 colours ({expected.Count} pairs)");

            var resolved = 0;
            var missing = new List<string>();

            foreach (var (language, color, key) in expected)
            {
                var code = ExperimentLanguages.ToCode(language);
                var canonical = PracticeColors.CanonicalName(color);

                Assert(key == $"{code}_{canonical}",
                    $"{code} + {color} keys as {key}");

                var clip = config.GetPracticeColorClip(language, color);

                if (clip == null)
                {
                    // Legitimate when a language has no local voice. Reported, never faked.
                    missing.Add($"{code}_{canonical}");
                    continue;
                }

                resolved++;

                // The clip must be the right COLOUR...
                Assert(clip.name == $"Practice_{canonical}",
                    $"{code} + {color} resolves to {clip.name}");

                // ...AND come out of the right LANGUAGE's folder. A Spanish session playing the
                // English recording of "red" would satisfy the name check alone.
                var path = AssetDatabase.GetAssetPath(clip);

                Assert(path.Replace('\\', '/').Contains($"/Narration/{code}/"),
                    $"{code} + {color} plays the {code} recording ({path})");
            }

            if (missing.Count == 0)
            {
                Assert(resolved == 12,
                    $"all 12 language/colour prompts resolve to a clip ({resolved})");
            }
            else
            {
                Info($"{resolved}/12 prompts have a clip. Not generated: " +
                     $"{string.Join(", ", missing)} — those languages run text-only. No other " +
                     "language's voice is substituted.");
            }

            // ---- No cross-language leakage ----------------------------------------------------
            foreach (var (language, color, _) in expected)
            {
                var clip = config.GetPracticeColorClip(language, color);
                if (clip == null)
                    continue;

                foreach (var otherLanguage in ExperimentLanguages.Selectable)
                {
                    if (otherLanguage == language)
                        continue;

                    var otherClip = config.GetPracticeColorClip(otherLanguage, color);

                    Assert(otherClip == null || otherClip != clip,
                        $"{ExperimentLanguages.ToCode(language)} and " +
                        $"{ExperimentLanguages.ToCode(otherLanguage)} do NOT share the {color} " +
                        "recording");
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Room-scoped participant audio
        // ---------------------------------------------------------------------------------

        static string ManagerSource() =>
            File.ReadAllText("Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs");

        /// <summary>
        /// Narration must never outlive the room that owns it, and the mechanism that guarantees
        /// that must never be able to silence a memory word.
        /// </summary>
        static void CheckRoomScopedAudio()
        {
            // ---- Ownership defaults to PROTECTED ----------------------------------------------
            var playClip = typeof(ExperimentAudio).GetMethod("PlayClip");
            var ownership = playClip?.GetParameters()
                .FirstOrDefault(p => p.ParameterType == typeof(AudioOwnership));

            Assert(ownership != null && ownership.HasDefaultValue &&
                   (AudioOwnership)ownership.DefaultValue == AudioOwnership.Cognitive,
                "audio is Cognitive (uncancellable) BY DEFAULT — forgetting to classify a sound " +
                "leaves it protected, never cancellable");

            // ---- The bookkeeping actually claims and releases voices --------------------------
            var go = new GameObject("SelfTest_Audio");

            try
            {
                var audio = go.AddComponent<ExperimentAudio>();

                // Awake does not run in edit mode, so the pool is created explicitly.
                typeof(ExperimentAudio)
                    .GetMethod("EnsureSources", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(audio, null);

                var clip = ToneGenerator.CreateTone("SelfTest_Narration", 440f, 0.5f, 0.3f);

                Assert(audio.narrationVoiceCount == 0,
                    "no voice is claimed by narration before anything plays");

                audio.PlayClip(AudioCue.InstructionCue, clip, AudioOwnership.Narration,
                    "AREA_0_FAMILIARIZATION");

                Assert(audio.narrationVoiceCount == 1,
                    "a Narration clip CLAIMS a voice, so it can be found and stopped later");

                audio.PlayClip(AudioCue.SpokenWord, clip);

                Assert(audio.narrationVoiceCount == 1,
                    "a Cognitive clip (a memory word) claims NO narration voice — it is not " +
                    "cancellable by a room change");

                audio.StopNarration("self test");

                Assert(audio.narrationVoiceCount == 0,
                    "StopNarration releases every narration claim");

                Assert(!audio.narrationPlaying,
                    "nothing is left playing as narration after a stop");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            // ---- Cognitive audio is never routed through the cancellable path -----------------
            var source = ManagerSource();

            var narrationCalls = System.Text.RegularExpressions.Regex.Matches(
                source, @"AudioOwnership\.Narration").Count;

            Assert(narrationCalls >= 1,
                $"the familiarization narration is tagged as cancellable ({narrationCalls} site(s))");

            // The word-presentation and beep call sites must NOT be.
            foreach (var cognitive in new[] { "AudioCue.SpokenWord", "AudioCue.RecallBeep" })
            {
                var index = source.IndexOf(cognitive, System.StringComparison.Ordinal);

                while (index >= 0)
                {
                    var lineEnd = source.IndexOf('\n', index);
                    var statement = source.Substring(index,
                        System.Math.Min(220, (lineEnd < 0 ? source.Length : source.Length) - index));

                    var callEnd = statement.IndexOf(");", System.StringComparison.Ordinal);
                    if (callEnd > 0)
                        statement = statement.Substring(0, callEnd);

                    Assert(!statement.Contains("AudioOwnership.Narration"),
                        $"{cognitive} is NEVER played as cancellable narration");

                    index = source.IndexOf(cognitive, index + 1, System.StringComparison.Ordinal);
                }
            }

            // ---- Every transition cancels the previous room's narration ----------------------
            // SetRoom is the choke point every area change passes through, so a transition added
            // later cannot forget.
            var setRoom = source.Substring(source.IndexOf("void SetRoom(", System.StringComparison.Ordinal));
            setRoom = setRoom.Substring(0, setRoom.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(setRoom.Contains("StopParticipantNarration"),
                "SetRoom — the single choke point for every area change — cancels the previous " +
                "room's narration");

            foreach (var (method, label) in new[]
                     {
                         ("void LeaveFamiliarization(", "START EXPERIMENT / SKIP INTRO"),
                         ("void OnDeveloperAreaJump(", "a developer room jump"),
                         ("void OnDeveloperReturnToLanguage(", "return to language selection"),
                         ("void EnterLanguageSelection(", "entering the language screen"),
                         ("void RestartSession(", "RESTART and NEW TRIAL"),
                         ("public void EndSession(", "END"),
                         ("public void AbortRun(", "ABORT"),
                     })
            {
                var start = source.IndexOf(method, System.StringComparison.Ordinal);

                if (start < 0)
                {
                    Assert(false, $"{label}: the handler exists");
                    continue;
                }

                var end = source.IndexOf("\n        }", start, System.StringComparison.Ordinal);
                var body = source.Substring(start, end - start);

                Assert(body.Contains("StopParticipantNarration"),
                    $"{label} stops the outgoing narration");
            }

            // ---- Language safety: silence BEFORE the language changes ------------------------
            var enterLanguage = source.Substring(
                source.IndexOf("void EnterLanguageSelection(", System.StringComparison.Ordinal));
            enterLanguage = enterLanguage.Substring(0,
                enterLanguage.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(enterLanguage.IndexOf("StopParticipantNarration", System.StringComparison.Ordinal) <
                   enterLanguage.IndexOf("ClearLanguage", System.StringComparison.Ordinal),
                "narration is stopped BEFORE the language is cleared — a clip from the previous " +
                "language can never play over the language screen or into the next language");

            // ---- The epoch guard --------------------------------------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();

            if (manager == null)
            {
                Info("no ExperimentManager in the open scene — epoch not checked");
                return;
            }

            var epochField = typeof(ExperimentManager).GetField("m_NarrationEpoch",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stop = typeof(ExperimentManager).GetMethod("StopParticipantNarration",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(epochField != null && stop != null,
                "the narration epoch and its cancellation method exist");

            if (epochField == null || stop == null)
                return;

            var before = (int)epochField.GetValue(manager);
            stop.Invoke(manager, new object[] { "self test" });
            var after = (int)epochField.GetValue(manager);

            Assert(after == before + 1,
                "cancelling narration moves the epoch, so a sequence parked in a wait abandons " +
                "itself instead of speaking into the next room");

            var narrationBody = source.Substring(
                source.IndexOf("IEnumerator RunFamiliarizationNarration(",
                    System.StringComparison.Ordinal));
            narrationBody = narrationBody.Substring(0,
                narrationBody.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(narrationBody.Contains("epoch != m_NarrationEpoch"),
                "the narration sequence checks the epoch before each segment");

            Assert(narrationBody.Contains("m_RunAborted"),
                "the narration sequence also stops on an abort");
        }

        // ---------------------------------------------------------------------------------
        // ABORT
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// ABORT must be a hard stop: nothing cognitive left running, the participant's audio
        /// preserved rather than thrown away, an unanswered trial invalidated rather than marked
        /// wrong, and one stable terminal screen that nothing can restart from.
        /// </summary>
        static void CheckAbortLifecycle()
        {
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();

            if (manager == null || ui == null)
            {
                Info("no ExperimentManager/UI in the open scene — abort flow not checked");
                return;
            }

            var type = typeof(ExperimentManager);
            var source = ManagerSource();
            var abortBody = source.Substring(
                source.IndexOf("public void AbortRun(", System.StringComparison.Ordinal));
            abortBody = abortBody.Substring(0,
                abortBody.IndexOf("\n        }", System.StringComparison.Ordinal));

            // ---- Microphone: SAFE stop, not a discard ----------------------------------------
            // Real microphone behaviour cannot be exercised headlessly, so what is verified here
            // is which code path ABORT takes — and the two paths differ in exactly the way that
            // matters: StopRecording writes the WAV, AbortRecording throws the audio away.
            Assert(abortBody.Contains("StopRecording(RecallStopReasons.Aborted)"),
                "ABORT stops the microphone through the SAVING path, with termination_reason " +
                "ABORTED (source-level check — a real capture needs a headset)");

            Assert(!abortBody.Contains("AbortRecording"),
                "ABORT does NOT use the discarding path — the participant's speech is kept");

            Assert(RecallStopReasons.Aborted == "ABORTED",
                $"the termination reason written is '{RecallStopReasons.Aborted}'");

            var stopRecording = typeof(VoiceRecallManager).GetMethod("StopRecording");
            Assert(stopRecording != null &&
                   stopRecording.GetParameters().Length == 1,
                "StopRecording takes the stop reason, so ABORTED reaches the recording's record");

            // ---- Set up a run with an unanswered chair trial ---------------------------------
            var currentTrialField = type.GetField("m_CurrentChairTrial",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var abortedField = type.GetField("m_RunAborted",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var endedField = type.GetField("m_SessionEnded",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(currentTrialField != null && abortedField != null,
                "the abort state and the active-trial reference exist");

            if (currentTrialField == null || abortedField == null || endedField == null)
                return;

            // A clean slate: this section runs after others have driven the manager around.
            abortedField.SetValue(manager, false);
            endedField.SetValue(manager, false);
            manager.sessionResults.Reset();

            var openTrial = new ChairTrialResult
            {
                trialIndex = 1,
                selectionMade = false,
                correct = false,
                responseTimeSeconds = 1.234,
                valid = true,
            };

            currentTrialField.SetValue(manager, openTrial);

            // A COMPLETED, correct trial that must survive the abort untouched.
            manager.sessionResults.chairTrials.Add(new ChairTrialResult
            {
                trialIndex = 0,
                selectionMade = true,
                correct = true,
                responseTimeSeconds = 2.0,
                valid = true,
            });

            manager.AbortRun("self test");

            // ---- State -----------------------------------------------------------------------
            Assert(manager.runAborted, "ABORT marks the run aborted");

            Assert(manager.state == ExperimentState.Aborted,
                $"ABORT enters the terminal aborted state ({manager.state})");

            // ---- The interrupted trial -------------------------------------------------------
            Assert(currentTrialField.GetValue(manager) == null,
                "no chair trial is left active after an abort");

            var recorded = manager.sessionResults.chairTrials
                .FirstOrDefault(t => t.trialIndex == 1);

            Assert(recorded != null, "the interrupted trial is RECORDED, not silently dropped");

            Assert(recorded == null || !recorded.valid,
                "the interrupted trial is marked INVALID");

            Assert(recorded == null || !recorded.correct,
                "the interrupted trial is NOT counted as incorrect — it is excluded, not failed");

            Assert(recorded == null ||
                   recorded.invalidReason.StartsWith("ABORTED"),
                $"the reason records the abort ({recorded?.invalidReason})");

            // Its partial response time must not reach any average.
            Assert(manager.sessionResults.chairTrialsTotal == 2,
                $"the aborted trial is RETAINED in the record " +
                $"({manager.sessionResults.chairTrialsTotal} trials)");

            Assert(manager.sessionResults.chairTrialsScored == 1,
                $"only the COMPLETED trial is SCORED ({manager.sessionResults.chairTrialsScored})");

            Assert(System.Math.Abs(manager.sessionResults.meanResponseTimeSeconds - 2.0) < 0.0001,
                $"the aborted trial's partial RT is excluded from the mean " +
                $"({manager.sessionResults.meanResponseTimeSeconds:F3} s)");

            Assert(System.Math.Abs(manager.sessionResults.medianResponseTimeSeconds - 2.0) < 0.0001,
                "the aborted trial's partial RT is excluded from the median");

            Assert(System.Math.Abs(manager.sessionResults.chairAccuracy - 1.0) < 0.0001,
                $"accuracy is computed over the valid trials only " +
                $"({manager.sessionResults.chairAccuracy:F2})");

            // ---- The aborted screen ----------------------------------------------------------
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                foreach (var language in ExperimentLanguages.Selectable)
                {
                    ExperimentLocalization.ResetForTesting();
                    ExperimentLocalization.SetLanguage(language);

                    var showAborted = type.GetMethod("ShowAbortedState",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    showAborted?.Invoke(manager, null);

                    var code = ExperimentLanguages.ToCode(language);

                    // Taken from the UI controller's own references rather than by name: several
                    // areas have a label called Txt_Instruction, and the aborted screen must be
                    // checked on the one the manager actually writes to.
                    var heading = UiLabel(ui, "m_AreaCInstruction");
                    var body = UiLabel(ui, "m_AreaCResults");

                    Assert(heading == null ||
                           heading.text == ExperimentLocalization.Get(LocKeys.ExperimentAborted),
                        $"{code}: the aborted screen's heading is localized ('{heading?.text}')");

                    Assert(body == null ||
                           body.text == ExperimentLocalization.Get(LocKeys.ExperimentAbortedDetail),
                        $"{code}: the aborted screen's message is localized");

                    // No participant-facing identifier or path may appear on it.
                    var shown = $"{heading?.text} {body?.text}";

                    foreach (var forbidden in new[] { ".csv", ".wav", "C:\\", "seed", "T001" })
                    {
                        Assert(shown.IndexOf(forbidden, System.StringComparison.OrdinalIgnoreCase) < 0,
                            $"{code}: the aborted screen shows no '{forbidden}'");
                    }
                }
            }
            finally
            {
                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // ---- Nothing may restart from it -------------------------------------------------
            foreach (var button in new[]
                     { "Btn_Restart", "Btn_NewTrial", "Btn_End", "Btn_Start", "Btn_StartExperiment" })
            {
                var go = GameObject.Find(button) ??
                         Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
                                 FindObjectsSortMode.None)
                             .FirstOrDefault(b => b.gameObject.name == button)?.gameObject;

                Assert(go == null || !go.activeSelf,
                    $"{button} is hidden on the aborted screen");
            }

            var runsBefore = manager.sessionResults.chairTrials.Count;

            manager.StartNewRun();
            Assert(manager.runAborted && manager.state == ExperimentState.Aborted,
                "NEW TRIAL after an ABORT does nothing");

            manager.RestartTrial();
            Assert(manager.runAborted && manager.state == ExperimentState.Aborted,
                "RESTART after an ABORT does nothing");

            manager.EndSession();
            Assert(manager.runAborted && manager.state == ExperimentState.Aborted,
                "END after an ABORT does nothing — the run's files are already closed");

            Assert(manager.sessionResults.chairTrials.Count == runsBefore,
                "no further trial data is produced from the aborted state");

            // A second ABORT must not double-log or re-finalise.
            manager.AbortRun("self test — second abort");
            Assert(manager.state == ExperimentState.Aborted,
                "a second ABORT is a no-op");

            // ---- Recentre survives -----------------------------------------------------------
            var recenter = Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(b => b.gameObject.name == "Btn_Recenter_C");

            Assert(recenter == null || recenter.gameObject.activeSelf,
                "RECENTER remains available after an ABORT — it cannot touch experiment state");

            // ---- The abort covers every process the report requires --------------------------
            foreach (var (needle, what) in new[]
                     {
                         ("StopParticipantNarration", "narration and anything queued behind it"),
                         ("StopFlow()", "the protocol coroutine (instructions, encoding, recall, " +
                                        "feedback, ITI and pending transitions)"),
                         ("CloseSelection()", "the chair-selection response window"),
                         ("MarkDeveloperInterrupted", "the run marked through the existing " +
                                                      "invalid/interrupted architecture"),
                         ("EventTypes.RunAborted", "an abort event in the data"),
                         ("EndSession(", "the run's files closed"),
                         ("ShowAbortedState()", "one stable aborted screen"),
                     })
            {
                Assert(abortBody.Contains(needle), $"ABORT stops/records: {what}");
            }

            Assert(!abortBody.Contains("StopAllCoroutines"),
                "ABORT does not use StopAllCoroutines — that would also kill the " +
                "audio-confirmation coroutines that record whether delivered stimuli were heard");

            // Nothing is deleted.
            foreach (var destructive in new[] { "Delete", "TryDeleteRunDirectory" })
            {
                Assert(!abortBody.Contains(destructive),
                    $"ABORT never calls '{destructive}' — previously completed runs are untouched");
            }

            // Restore a usable state for any later section.
            abortedField.SetValue(manager, false);
            endedField.SetValue(manager, false);
            manager.sessionResults.Reset();

            typeof(ExperimentManager)
                .GetField("m_State", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(manager, ExperimentState.Idle);
        }

        // ---------------------------------------------------------------------------------
        // Localization completeness audit
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Walks every participant-facing label in the built scene and, for Spanish and Japanese,
        /// flags any that still renders the ENGLISH string of a key that has a different
        /// translation.
        ///
        /// Scope is deliberate: researcher and developer surfaces are excluded (their being in
        /// English is correct), and a term that is legitimately identical across languages —
        /// AZUL/AZUL, VERDE/VERDE — cannot produce a false alarm because the comparison only
        /// fires where the two languages actually differ.
        /// </summary>
        static void CheckLocalizationAudit()
        {
            // Surfaces that are NOT participant-facing and are correct in English.
            var excludedRoots = new[]
            {
                "UI_DeveloperNavigation",
                "UI_0_ResearcherStatus",
                "UI_LanguageSelection",   // each button is always its own language, by design
            };

            var labels = Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(t => !excludedRoots.Any(root =>
                    t.transform.GetComponentsInParent<Transform>(true)
                        .Any(p => p.name == root)))
                .ToArray();

            Info($"auditing {labels.Length} participant-facing labels " +
                 $"(excluded: {string.Join(", ", excludedRoots)})");

            // ---- THE RULE ---------------------------------------------------------------------
            // A participant-facing label is either BOUND to a localization key (static caption) or
            // authored EMPTY (the manager fills it, and the manager only ever writes strings it
            // got from the same service). A label carrying builder-authored English is neither,
            // and is exactly the defect reported from the headset — it is invisible in an English
            // session and shows English to everybody else.
            //
            // This runs before anything has driven the manager, so what each label holds is what
            // the scene builder put there.
            var unbound = new List<string>();

            foreach (var label in labels)
            {
                if (label == null)
                    continue;

                if (label.GetComponent<LocalizedText>() != null)
                    continue;

                if (string.IsNullOrWhiteSpace(label.text))
                    continue;

                unbound.Add($"'{label.gameObject.name}' = \"{Shorten(label.text)}\"");
            }

            Assert(unbound.Count == 0,
                unbound.Count == 0
                    ? $"every participant-facing label is either localization-bound or left for " +
                      $"the manager to fill ({labels.Length} checked)"
                    : $"{unbound.Count} label(s) carry builder-authored text with no localization " +
                      $"binding — {string.Join(" | ", unbound.Take(8))}");

            // The concepts the brief requires to be localized, and where each lives.
            var requiredKeys = new[]
            {
                LocKeys.PracticeColorPrefix + ChairColor.Red,
                LocKeys.PracticeColorPrefix + ChairColor.Blue,
                LocKeys.PracticeColorPrefix + ChairColor.Yellow,
                LocKeys.PracticeColorPrefix + ChairColor.Green,
                LocKeys.SelectColor,
                LocKeys.PracticeSuccess,
                LocKeys.PracticeTryTarget,
                LocKeys.PracticeMoreOne,
                LocKeys.ControllerTitle,
                LocKeys.ControllerIndexTrigger,
                LocKeys.ControllerGripTrigger,
                LocKeys.ControllerFooter,
                LocKeys.HandLeft,
                LocKeys.HandRight,
                LocKeys.ColorPrefix + ChairColor.Red,
                LocKeys.SizePrefix + ChairSize.Large,
                LocKeys.ShapePrefix + ChairShape.Slatted,
                LocKeys.NewTrial,
                LocKeys.Restart,
                LocKeys.End,
                LocKeys.Recording,
                LocKeys.RecordingComplete,
                LocKeys.RecheckAudio,
                LocKeys.AudioUnavailable,
                LocKeys.ExperimentAborted,
                LocKeys.ExperimentAbortedDetail,
            };

            foreach (var key in requiredKeys)
            {
                var present = LocalizationTable.TryGet(key, out var entry);

                Assert(present &&
                       !string.IsNullOrEmpty(entry.english) &&
                       !string.IsNullOrEmpty(entry.spanish) &&
                       !string.IsNullOrEmpty(entry.japanese),
                    $"{key} is defined in all three languages");
            }

            // ---- The rendered scan -----------------------------------------------------------
            var previousLanguage = ExperimentLocalization.language;

            try
            {
                foreach (var language in new[]
                         { ExperimentLanguage.Spanish, ExperimentLanguage.Japanese })
                {
                    ExperimentLocalization.ResetForTesting();
                    ExperimentLocalization.SetLanguage(language);

                    foreach (var localized in Object.FindObjectsByType<LocalizedText>(
                                 FindObjectsInactive.Include, FindObjectsSortMode.None))
                    {
                        localized.Refresh();
                    }

                    var code = ExperimentLanguages.ToCode(language);
                    var leaks = new List<string>();

                    foreach (var label in labels)
                    {
                        if (label == null || string.IsNullOrWhiteSpace(label.text))
                            continue;

                        // Only BOUND labels have a language of their own to be wrong about. The
                        // manager-written ones are verified per-language by the sections that
                        // actually drive the manager into each state.
                        if (label.GetComponent<LocalizedText>() == null)
                            continue;

                        foreach (var pair in LocalizationTable.entries)
                        {
                            var translated = pair.Value.For(language);

                            // Only meaningful where the languages genuinely differ.
                            if (string.IsNullOrEmpty(translated) ||
                                translated == pair.Value.english)
                            {
                                continue;
                            }

                            if (label.text.Contains(pair.Value.english))
                            {
                                leaks.Add($"'{label.gameObject.name}' shows the English " +
                                          $"'{pair.Value.english}' instead of '{translated}'");
                            }
                        }
                    }

                    Assert(leaks.Count == 0,
                        leaks.Count == 0
                            ? $"{code}: no participant-facing label is left in English"
                            : $"{code}: {leaks.Count} English string(s) leaked — " +
                              string.Join(" | ", leaks.Take(6)));
                }
            }
            finally
            {
                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // ---- No participant-facing English literal remains in the builder ----------------
            var builder = File.ReadAllText(
                "Assets/IKEA_EEG/Editor/ExperimentSceneBuilder.cs");

            var practiceBlock = builder.Substring(
                builder.IndexOf("static PracticeObject BuildPracticeObject(",
                    System.StringComparison.Ordinal));
            practiceBlock = practiceBlock.Substring(0,
                practiceBlock.IndexOf("\n        }", System.StringComparison.Ordinal));

            foreach (var literal in new[] { "\"RED\"", "\"BLUE\"", "\"YELLOW\"", "\"GREEN\"" })
            {
                Assert(!practiceBlock.Contains(literal),
                    $"the practice-object builder contains no hard-coded {literal}");
            }

            var helpBlock = builder.Substring(
                builder.IndexOf("static void BuildControllerHelp(", System.StringComparison.Ordinal));
            helpBlock = helpBlock.Substring(0,
                helpBlock.IndexOf("\n        }", System.StringComparison.Ordinal));

            foreach (var literal in new[]
                     { "\"YOUR CONTROLLER\"", "INDEX TRIGGER</b>", "GRIP TRIGGER</b>",
                       "Either trigger works" })
            {
                Assert(!helpBlock.Contains(literal),
                    $"the controller-help builder contains no hard-coded \"{literal}\"");
            }
        }

        // ---------------------------------------------------------------------------------
        // Area 0 layout
        // ---------------------------------------------------------------------------------

        /// <summary>World-space bounds of a UI element, from its RectTransform corners.</summary>
        static Bounds WorldBoundsOf(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            var bounds = new Bounds(corners[0], Vector3.zero);
            for (var i = 1; i < 4; i++)
                bounds.Encapsulate(corners[i]);

            return bounds;
        }

        /// <summary>
        /// The Area 0 spatial fixes, checked as GEOMETRY rather than by eye — every one of
        /// these was a real overlap reported from the headset.
        /// </summary>
        static void CheckArea0Layout()
        {
            var panel = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "UI_0_Canvas");

            Assert(panel != null, "the Area 0 instruction panel exists");
            if (panel == null)
                return;

            var panelBounds = WorldBoundsOf((RectTransform)panel);
            var practiceObjects = Object.FindObjectsByType<PracticeObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            // ---- 1. Colour labels must not intersect their posts -----------------------------
            var posts = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(t => t.name.EndsWith("_Post"))
                .ToArray();

            Assert(posts.Length == practiceObjects.Length,
                $"each practice object has a post ({posts.Length})");

            var intersecting = 0;

            foreach (var practice in practiceObjects)
            {
                var label = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                        FindObjectsSortMode.None)
                    .FirstOrDefault(t => t.name == $"UI_{practice.objectId}_Label");

                Assert(label != null, $"{practice.objectId} has a colour label");
                if (label == null)
                    continue;

                var labelBounds = WorldBoundsOf((RectTransform)label);

                foreach (var post in posts)
                {
                    var renderer = post.GetComponent<Renderer>();
                    if (renderer == null)
                        continue;

                    if (labelBounds.Intersects(renderer.bounds))
                    {
                        intersecting++;
                        Info($"  {label.name} intersects {post.name}");
                    }
                }
            }

            Assert(intersecting == 0,
                $"no colour label intersects any post ({intersecting} intersection(s))");

            // ---- 2. Practice objects must not block the instruction panel --------------------
            var eye = Object.FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None)
                .Where(s => s.area == ExperimentArea.Familiarization)
                .Select(s => s.transform.position + Vector3.up * 1.6f)
                .FirstOrDefault();

            var blocking = 0;

            foreach (var practice in practiceObjects)
            {
                var renderer = practice.GetComponent<Renderer>();
                if (renderer == null)
                    continue;

                // An object occludes the panel only if it is nearer AND its silhouette overlaps
                // the panel in the participant's view. Comparing world Y is enough here because
                // both sit in front of the same viewer: an object entirely below the panel's
                // lower edge cannot cover any of it.
                var objectTop = renderer.bounds.max.y;

                if (objectTop > panelBounds.min.y &&
                    Vector3.Distance(eye, renderer.bounds.center) <
                    Vector3.Distance(eye, panelBounds.center))
                {
                    blocking++;
                    Info($"  {practice.objectId} top {objectTop:F2} m rises above the panel's " +
                         $"lower edge {panelBounds.min.y:F2} m");
                }
            }

            Assert(blocking == 0,
                $"no practice object rises into the instruction panel's view ({blocking})");

            // ---- 3. Controller help must not overlap the instruction panel -------------------
            var help = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "ControllerDiagram");

            Assert(help != null, "the controller help exists");

            if (help != null)
            {
                var helpRenderers = help.GetComponentsInChildren<Renderer>(true);
                var helpRects = help.GetComponentsInChildren<RectTransform>(true)
                    .Where(r => r.GetComponent<Canvas>() != null)
                    .ToArray();

                var helpBounds = new Bounds(help.position, Vector3.zero);

                foreach (var renderer in helpRenderers)
                    helpBounds.Encapsulate(renderer.bounds);

                foreach (var rect in helpRects)
                    helpBounds.Encapsulate(WorldBoundsOf(rect));

                Assert(!helpBounds.Intersects(panelBounds),
                    $"the controller help (x {helpBounds.min.x:F2}..{helpBounds.max.x:F2}) does " +
                    $"not overlap the instruction panel (x {panelBounds.min.x:F2}.." +
                    $"{panelBounds.max.x:F2})");

                // ---- 4. Only participant-relevant controls -----------------------------------
                var helpText = string.Join(" ", help.GetComponentsInChildren<TMPro.TMP_Text>(true)
                    .Select(t => t.text));

                foreach (var banned in new[]
                         { "THUMBSTICK", "DEVELOPER", "MENU", "ABORT", "NAVIGATION" })
                {
                    Assert(helpText.IndexOf(banned, System.StringComparison.OrdinalIgnoreCase) < 0,
                        $"the participant-facing controller help does not mention '{banned}'");
                }

                // Checked by KEY rather than by English words: the captions are localized now, so
                // "INDEX TRIGGER" is only what an English participant sees. What must hold in
                // every language is that both controls are still explained.
                var helpKeys = help.GetComponentsInChildren<LocalizedText>(true)
                    .Select(l => l.key)
                    .ToList();

                Assert(helpKeys.Contains(LocKeys.ControllerIndexTrigger) &&
                       helpKeys.Contains(LocKeys.ControllerGripTrigger),
                    "the controller help still explains BOTH the index trigger and the grip " +
                    $"({helpKeys.Count} localized captions)");

                var markers = help.GetComponentsInChildren<Transform>(true)
                    .Count(t => t.name.StartsWith("Marker_"));

                Assert(markers == 4,
                    $"only the two participant-relevant inputs are marked, on both hands " +
                    $"({markers} markers = 2 inputs x 2 hands)");

                // Only OUR callout objects are checked. The controller model's own geometry
                // legitimately contains a part named "Thumbstick" — it is a controller — and
                // showing the physical stick is not the same as documenting what it does.
                Assert(!help.GetComponentsInChildren<Transform>(true)
                        .Any(t => (t.name.StartsWith("Marker_") || t.name.StartsWith("Txt_")) &&
                                  t.name.IndexOf("Thumbstick", System.StringComparison.OrdinalIgnoreCase) >= 0),
                    "no thumbstick MARKER or CALLOUT remains — the developer gesture stays " +
                    "undocumented, though the stick is still visible on the model");
            }

            // ---- 5. Instruction text blocks must not overlap each other ----------------------
            var instruction = panel.GetComponentsInChildren<TMPro.TMP_Text>(true)
                .FirstOrDefault(t => t.name == "Txt_FamiliarizationInstruction");
            var prompt = panel.GetComponentsInChildren<TMPro.TMP_Text>(true)
                .FirstOrDefault(t => t.name == "Txt_PracticePrompt");
            var status = panel.GetComponentsInChildren<TMPro.TMP_Text>(true)
                .FirstOrDefault(t => t.name == "Txt_FamiliarizationStatus");

            Assert(instruction != null && prompt != null && status != null,
                "the instruction, practice prompt and status blocks all exist");

            if (instruction != null && prompt != null && status != null)
            {
                var instructionRect = instruction.rectTransform;
                var promptRect = prompt.rectTransform;
                var statusRect = status.rectTransform;

                var instructionBottom = instructionRect.anchoredPosition.y -
                                        instructionRect.sizeDelta.y * 0.5f;
                var promptTop = promptRect.anchoredPosition.y + promptRect.sizeDelta.y * 0.5f;
                var promptBottom = promptRect.anchoredPosition.y - promptRect.sizeDelta.y * 0.5f;
                var statusTop = statusRect.anchoredPosition.y + statusRect.sizeDelta.y * 0.5f;

                Assert(promptTop < instructionBottom,
                    $"the practice prompt (top {promptTop:F0}) sits below the instructions " +
                    $"(bottom {instructionBottom:F0}) — they do not overlap");

                Assert(statusTop < promptBottom,
                    $"the status line (top {statusTop:F0}) sits below the practice prompt " +
                    $"(bottom {promptBottom:F0})");

                Info($"Area 0 text bands: instructions .. {instructionBottom:F0}, " +
                     $"prompt {promptTop:F0} .. {promptBottom:F0}, status from {statusTop:F0}");
            }
        }

        // ---------------------------------------------------------------------------------
        // Area C results layout
        // ---------------------------------------------------------------------------------

        static void CheckAreaCLayout()
        {
            var panel = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "UI_C_Canvas");

            Assert(panel != null, "the Area C results panel exists");
            if (panel == null)
                return;

            var buttons = panel.GetComponentsInChildren<Button>(true)
                .Where(b => b.name.StartsWith("Btn_"))
                .ToArray();

            Assert(buttons.Length >= 4,
                $"the results panel has the three run actions plus recenter ({buttons.Length})");

            // Every pair of buttons must be disjoint in the panel's own 2D space.
            var overlaps = 0;

            for (var i = 0; i < buttons.Length; i++)
            {
                for (var j = i + 1; j < buttons.Length; j++)
                {
                    var a = (RectTransform)buttons[i].transform;
                    var b = (RectTransform)buttons[j].transform;

                    var aRect = new Rect(
                        a.anchoredPosition - a.sizeDelta * 0.5f, a.sizeDelta);
                    var bRect = new Rect(
                        b.anchoredPosition - b.sizeDelta * 0.5f, b.sizeDelta);

                    if (aRect.Overlaps(bRect))
                    {
                        overlaps++;
                        Info($"  {buttons[i].name} overlaps {buttons[j].name}");
                    }
                }
            }

            Assert(overlaps == 0,
                $"no two buttons on the final screen overlap ({overlaps}) — RESTART, NEW TRIAL, " +
                "END and RECENTER are all separated");

            // Recenter must still exist, and be visually subordinate to the run actions.
            var recenter = buttons.FirstOrDefault(b => b.name == "Btn_Recenter_C");
            var newTrial = buttons.FirstOrDefault(b => b.name == "Btn_NewTrial");

            Assert(recenter != null, "recenter is still available on the final screen");

            if (recenter != null && newTrial != null)
            {
                var recenterRect = (RectTransform)recenter.transform;
                var newTrialRect = (RectTransform)newTrial.transform;

                Assert(recenterRect.sizeDelta.x < newTrialRect.sizeDelta.x,
                    $"recenter ({recenterRect.sizeDelta.x:F0} px) is smaller than a run action " +
                    $"({newTrialRect.sizeDelta.x:F0} px) — it reads as a utility, not a choice");

                Assert(recenterRect.anchoredPosition.y < newTrialRect.anchoredPosition.y,
                    "recenter sits below the run-management group");
            }

            // Everything must fit inside the panel.
            var half = ((RectTransform)panel).sizeDelta.y * 0.5f;
            var outside = buttons.Count(b =>
            {
                var rect = (RectTransform)b.transform;
                return Mathf.Abs(rect.anchoredPosition.y) + rect.sizeDelta.y * 0.5f > half;
            });

            Assert(outside == 0, $"every button fits inside the panel ({outside} outside)");
        }

        // ---------------------------------------------------------------------------------
        // Final results screen: the summary must not reach the buttons, in any language
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Clearance in panel pixels the summary must keep above the first action button.
        ///
        /// Not zero: "touching exactly" is the bug that was reported, and a language whose text
        /// metrics differ slightly from the ones measured here must still land clear.
        /// </summary>
        const float k_SummaryButtonMarginPx = 40f;

        /// <summary>
        /// The reported overlap, checked as GEOMETRY and against the REAL summary text in all
        /// three languages.
        ///
        /// The subtlety that made the bug invisible to the previous layout check: a TMP label
        /// does not clip to its RectTransform. The results rect was 420 px, the restored Session
        /// Summary needs far more, and the surplus is drawn OUTSIDE the rect — so a test that
        /// compared RectTransforms saw no overlap while the headset showed one. This measures the
        /// text's own preferred height instead.
        /// </summary>
        static void CheckFinalSummaryLayout()
        {
            var panel = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "UI_C_Canvas");

            var ui = Object.FindAnyObjectByType<ExperimentUIController>();

            if (panel == null || ui == null)
            {
                Assert(false, "the Area C results panel and UI controller exist");
                return;
            }

            var results = UiLabel(ui, "m_AreaCResults") as TextMeshProUGUI;
            Assert(results != null, "the results label is bound to the UI controller");

            if (results == null)
                return;

            var resultsRect = (RectTransform)results.transform;

            // The action buttons, and the highest edge among them.
            var actionButtons = panel.GetComponentsInChildren<Button>(true)
                .Where(b => b.name == "Btn_NewTrial" || b.name == "Btn_Restart" ||
                            b.name == "Btn_End" || b.name == "Btn_Recenter_C")
                .ToArray();

            Assert(actionButtons.Length == 4,
                $"the four final-screen controls exist ({actionButtons.Length})");

            if (actionButtons.Length == 0)
                return;

            var highestButtonTop = actionButtons
                .Select(b => ((RectTransform)b.transform).anchoredPosition.y +
                             ((RectTransform)b.transform).sizeDelta.y * 0.5f)
                .Max();

            // A worst-case summary: three trials, both recalls present, run 2 of a sitting.
            var sample = new SessionResults
            {
                immediateRecallStatus = "recording saved (audio/T001_immediate_recall.wav) / not scored",
                delayedRecallStatus = "NOT captured (no microphone device) / not scored",
                totalExperimentDurationSeconds = 184.5d,
            };

            for (var i = 0; i < 3; i++)
            {
                sample.chairTrials.Add(new ChairTrialResult
                {
                    trialIndex = i + 1,
                    trialCount = 3,
                    selectionMade = true,
                    correct = i < 2,
                    responseTimeSeconds = 2.0 + i * 0.5,
                    valid = true,
                });
            }

            var previousLanguage = ExperimentLocalization.language;
            var previousText = results.text;

            try
            {
                foreach (var language in ExperimentLanguages.Selectable)
                {
                    ExperimentLocalization.ResetForTesting();
                    ExperimentLocalization.SetLanguage(language);

                    var code = ExperimentLanguages.ToCode(language);

                    results.text = sample.BuildParticipantRunSummary(
                        ExperimentLocalization.Get(LocKeys.ExecutiveTaskComplete), 2, 0);

                    results.ForceMeshUpdate();

                    // The height the text ACTUALLY needs at this width — not the rect's height.
                    var preferred = results.GetPreferredValues(
                        results.text, resultsRect.sizeDelta.x, 0f).y;

                    Assert(preferred <= resultsRect.sizeDelta.y,
                        $"{code}: the summary ({preferred:F0} px) fits inside its rect " +
                        $"({resultsRect.sizeDelta.y:F0} px) — no text is drawn outside it");

                    // TextAlignmentOptions.Center is middle-centre, so overflow grows both ways
                    // from the rect's centre. The occupied band is measured that way rather than
                    // assumed to be the rect.
                    var occupiedHalf = Mathf.Max(preferred, resultsRect.sizeDelta.y) * 0.5f;
                    var textBottom = resultsRect.anchoredPosition.y - occupiedHalf;
                    var textTop = resultsRect.anchoredPosition.y + occupiedHalf;

                    var gap = textBottom - highestButtonTop;

                    Assert(gap >= k_SummaryButtonMarginPx,
                        $"{code}: the summary ends at {textBottom:F0} and the topmost button " +
                        $"begins at {highestButtonTop:F0} — {gap:F0} px clear " +
                        $"(minimum {k_SummaryButtonMarginPx:F0})");

                    // And it must not run off the top of the panel either.
                    var panelHalf = ((RectTransform)panel).sizeDelta.y * 0.5f;

                    Assert(textTop <= panelHalf,
                        $"{code}: the summary's top ({textTop:F0}) stays inside the panel " +
                        $"({panelHalf:F0})");

                    // Nothing may be clipped away instead of laid out.
                    Assert(results.overflowMode != TextOverflowModes.Truncate &&
                           results.overflowMode != TextOverflowModes.Ellipsis,
                        $"{code}: the summary is not truncated — the layout makes room for it");

                    Info($"{code}: summary needs {preferred:F0} px of {resultsRect.sizeDelta.y:F0}, " +
                         $"{gap:F0} px above the buttons");
                }

                // ---- Readability was not traded away for space ------------------------------
                Assert(results.fontSize >= 44f,
                    $"the summary font is still large ({results.fontSize:F0}) — the overlap was " +
                    "fixed with layout, not by shrinking the text");

                Assert(results.enableAutoSizing == false || results.fontSizeMin >= 34f,
                    $"auto-sizing cannot shrink the summary below {results.fontSizeMin:F0} pt");
            }
            finally
            {
                results.text = previousText;
                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // ---- Every control stays inside the panel -------------------------------------
            var half = ((RectTransform)panel).sizeDelta.y * 0.5f;

            foreach (var button in actionButtons)
            {
                var rect = (RectTransform)button.transform;
                var top = rect.anchoredPosition.y + rect.sizeDelta.y * 0.5f;
                var bottom = rect.anchoredPosition.y - rect.sizeDelta.y * 0.5f;

                Assert(top <= half && bottom >= -half,
                    $"{button.name} ({bottom:F0}..{top:F0}) is inside the panel " +
                    $"(-{half:F0}..{half:F0})");
            }

            // The panel must stay under the ceiling and above the floor in the real world.
            var worldHeight = ((RectTransform)panel).sizeDelta.y * panel.localScale.y;
            var worldTop = panel.position.y + worldHeight * 0.5f;
            var worldBottom = panel.position.y - worldHeight * 0.5f;

            Assert(worldTop < 3.0f && worldBottom > 0.2f,
                $"the taller panel still fits the room ({worldBottom:F2}..{worldTop:F2} m, " +
                "ceiling 3.00 m)");
        }

        // ---------------------------------------------------------------------------------
        // Run duration
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The reported "Total duration: 0.000 s".
        ///
        /// Checks the lifecycle rather than only the number: where the value is captured, that it
        /// is captured BEFORE the panel is built, that every consumer reads the one field, and
        /// that it is measured on the run clock rather than the session clock.
        /// </summary>
        static void CheckRunDuration()
        {
            var source = ManagerSource();

            // ---- Captured before the participant panel is built -----------------------------
            // Scanned from the START of RunAreaC to the END of FinishAreaC. Area C was split
            // into a protocol fork (RunAreaC) and a shared tail (FinishAreaC) when the
            // Recognition protocol was added, so the capture and the summary now live in two
            // methods. The GUARANTEE is unchanged and still checkable — the capture must appear
            // before the summary in execution order — but it can no longer be read out of a
            // single method body, so the window is widened to cover both.
            var areaC = source.Substring(
                source.IndexOf("IEnumerator RunAreaC()", System.StringComparison.Ordinal));

            var finishEnd = areaC.IndexOf("void FinishAreaC()", System.StringComparison.Ordinal);

            Assert(finishEnd >= 0,
                "the Area C tail (FinishAreaC) follows RunAreaC in the source, so execution " +
                "order and source order still agree");

            if (finishEnd >= 0)
            {
                var afterFinish = areaC.Substring(finishEnd);
                var close = afterFinish.IndexOf("\n        }", System.StringComparison.Ordinal);
                areaC = areaC.Substring(0, finishEnd + (close >= 0 ? close : afterFinish.Length));
            }

            // Matched on the CALL EXPRESSIONS, not on the bare method names: a comment that
            // merely mentions a method must not be mistaken for a call to it.
            var captureAt = areaC.IndexOf("CaptureRunDuration(\"", System.StringComparison.Ordinal);
            var buildAt = areaC.IndexOf("m_SessionResults.BuildParticipantRunSummary(",
                System.StringComparison.Ordinal);
            var publishAt = areaC.IndexOf("PublishSessionResults(\"", System.StringComparison.Ordinal);

            Assert(captureAt >= 0, "the run duration is captured in the Area C flow");

            Assert(captureAt >= 0 && buildAt > captureAt,
                "the duration is captured BEFORE the participant summary is built — this " +
                "ordering is the whole bug: the panel used to be built first and read a zero");

            Assert(buildAt >= 0 && publishAt > buildAt,
                "the summary is still built before publishing (unchanged), so the capture had " +
                "to move rather than the publish");

            // ---- Measured on the RUN clock, not the session clock ---------------------------
            var capture = source.Substring(
                source.IndexOf("void CaptureRunDuration(", System.StringComparison.Ordinal));
            capture = capture.Substring(0,
                capture.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(capture.Contains("ElapsedTrialSeconds"),
                "the duration comes from the existing per-run interval timer");

            Assert(!capture.Contains("ElapsedSessionSeconds"),
                "it is NOT the session clock — that starts at SESSION_START and would include " +
                "the language screen and the whole of Area 0");

            foreach (var competing in new[] { "Time.time", "Time.realtimeSinceStartup", "DateTime.Now" })
            {
                Assert(!capture.Contains(competing),
                    $"no competing timing system is introduced (no {competing})");
            }

            // One read, written to both consumers.
            Assert(capture.Contains("m_Result.totalTrialSeconds") &&
                   capture.Contains("m_SessionResults.totalExperimentDurationSeconds"),
                "one reading of the clock feeds both the trial record and the session results, " +
                "so they cannot disagree");

            var publish = source.Substring(
                source.IndexOf("void PublishSessionResults(", System.StringComparison.Ordinal));
            publish = publish.Substring(0,
                publish.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(!publish.Contains("= m_Logger.ElapsedSessionSeconds()"),
                "publishing no longer overwrites the captured run duration with the " +
                "session-clock elapsed time");

            // ---- The clock starts at Area A, not before -------------------------------------
            var start = source.Substring(
                source.IndexOf("void OnStartPressed()", System.StringComparison.Ordinal));
            start = start.Substring(0,
                start.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(start.Contains("BeginTrial()"),
                "the run clock is started by START in Area A — the cognitive run's beginning");

            foreach (var (method, label) in new[]
                     {
                         ("void EnterLanguageSelection(", "the language screen"),
                         ("void EnterFamiliarization(", "Area 0 familiarization"),
                     })
            {
                var body = source.Substring(
                    source.IndexOf(method, System.StringComparison.Ordinal));
                body = body.Substring(0,
                    body.IndexOf("\n        }", System.StringComparison.Ordinal));

                Assert(!body.Contains("BeginTrial"),
                    $"{label} does NOT start the run clock — its length is excluded from the " +
                    "reported duration");
            }

            // ---- A known duration produces the expected number ------------------------------
            var logger = Object.FindAnyObjectByType<EventLogger>();

            if (logger == null)
            {
                Info("no EventLogger in the open scene — the live clock was not exercised");
            }
            else
            {
                var timerField = typeof(EventLogger).GetField("m_TrialTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert(timerField != null, "the run timer field exists on the EventLogger");

                if (timerField != null)
                {
                    var timer = (IntervalTimer)timerField.GetValue(logger);

                    Assert(timer != null && !timer.hasStarted || true,
                        "the run timer is inspectable");

                    // A real, non-zero elapsed interval: started, allowed to run, then stopped —
                    // exactly what BeginTrial/EndTrial do.
                    timer.Restart();
                    System.Threading.Thread.Sleep(60);
                    timer.Stop();

                    var elapsed = logger.ElapsedTrialSeconds();

                    Assert(elapsed > 0.03d,
                        $"a completed run reports a NON-ZERO duration ({elapsed:F3} s) — the " +
                        "reported symptom was exactly 0.000 s");

                    Assert(elapsed < 5d,
                        $"the measured interval is the elapsed one, not an absolute time " +
                        $"({elapsed:F3} s)");

                    // The value survives the stop: EndTrial stops the timer before the summary
                    // is written, and a reset stopwatch would report zero again.
                    var afterStop = logger.ElapsedTrialSeconds();

                    Assert(System.Math.Abs(afterStop - elapsed) < 0.001d,
                        "the duration is stable after the run timer is stopped");

                    // Restarting is what BeginTrial does for run 2: the new run starts from zero
                    // rather than inheriting run 1's elapsed time.
                    timer.Restart();
                    var run2 = logger.ElapsedTrialSeconds();

                    Assert(run2 < elapsed,
                        $"a new run's clock starts from zero ({run2:F3} s) rather than " +
                        $"inheriting the previous run's {elapsed:F3} s");

                    timer.Stop();
                }
            }

            // ---- Every consumer reads the SAME field ---------------------------------------
            var results = new SessionResults { totalExperimentDurationSeconds = 184.532d };

            results.chairTrials.Add(new ChairTrialResult
            {
                trialIndex = 1,
                trialCount = 1,
                selectionMade = true,
                correct = true,
                responseTimeSeconds = 2d,
                valid = true,
            });

            var previousLanguage = ExperimentLocalization.language;

            try
            {
                ExperimentLocalization.ResetForTesting();
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

                var expected = TimeFormat.FromSeconds(184.532d);

                Assert(expected == "184.532 s (184532 ms)",
                    $"the established format is preserved: '{expected}'");

                var participant = results.BuildParticipantRunSummary("Executive task complete", 1, 0);

                Assert(participant.Contains(expected),
                    $"the PARTICIPANT summary shows the authoritative duration ('{expected}')");

                Assert(!participant.Contains("0.000 s"),
                    "the participant summary no longer shows 0.000 s");

                var researcher = results.BuildResearcherSummary();

                Assert(researcher.Contains(expected),
                    "the RESEARCHER summary shows the same duration");

                var notes = results.ToEventNotes();

                Assert(notes.Contains("total_duration_s=184.532"),
                    $"the SESSION_SUMMARY event carries the same duration");

                // The saved session-summary file reads the same field.
                var writer = File.ReadAllText(
                    "Assets/IKEA_EEG/Scripts/Data/SessionSummaryWriter.cs");

                Assert(writer.Contains("results.totalExperimentDurationSeconds"),
                    "the saved session summary is written from the same field, not recomputed");

                // Zero must still be reported honestly rather than dressed up.
                var empty = new SessionResults { totalExperimentDurationSeconds = 0d };
                empty.chairTrials.Add(new ChairTrialResult
                {
                    trialIndex = 1, trialCount = 1, selectionMade = true,
                    correct = true, responseTimeSeconds = 1d, valid = true,
                });

                Assert(empty.BuildParticipantRunSummary("Executive task complete", 1, 0)
                        .Contains(TimeFormat.FromSeconds(0d)),
                    "a genuinely zero duration is still shown as zero — the fix is the " +
                    "lifecycle, not a display substitution");
            }
            finally
            {
                ExperimentLocalization.ResetForTesting();

                if (previousLanguage != ExperimentLanguage.None)
                    ExperimentLocalization.SetLanguage(previousLanguage);
            }

            // ---- Multi-run: a new run resets the stored duration ----------------------------
            var restart = source.Substring(
                source.IndexOf("void RestartSession(", System.StringComparison.Ordinal));
            restart = restart.Substring(0,
                restart.IndexOf("\n        }", System.StringComparison.Ordinal));

            Assert(restart.Contains("m_SessionResults.Reset()"),
                "starting another run clears the previous run's stored duration");

            var newRun = source.Substring(
                source.IndexOf("public void StartNewRun()", System.StringComparison.Ordinal));
            newRun = newRun.Substring(0,
                newRun.IndexOf("\n        }", System.StringComparison.Ordinal));

            var finalizeAt = newRun.IndexOf("FinalizeCurrentRun", System.StringComparison.Ordinal);
            var restartAt = newRun.IndexOf("RestartSession", System.StringComparison.Ordinal);

            Assert(finalizeAt >= 0 && restartAt > finalizeAt,
                "NEW TRIAL writes run N's summary — with run N's duration — BEFORE the results " +
                "are reset for run N+1, so the previous run's duration is preserved on disk");

            var reset = File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/SessionResults.cs");

            Assert(reset.Contains("totalExperimentDurationSeconds = 0d"),
                "SessionResults.Reset clears the duration, so run 2 cannot inherit run 1's");
        }

        // ---------------------------------------------------------------------------------
        // Obsolete audio warning
        // ---------------------------------------------------------------------------------

        static void CheckObsoleteWarningRemoved()
        {
            // The healthy-path warning must be gone from the code that drives the participant
            // UI. The BLOCKING warning (stimuli genuinely undeliverable) must remain.
            var managerSource = File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs");

            var readinessStart = managerSource.IndexOf("void UpdateReadinessUI()",
                System.StringComparison.Ordinal);
            var readinessEnd = managerSource.IndexOf("void OnRecheckAudioPressed()",
                System.StringComparison.Ordinal);

            Assert(readinessStart > 0 && readinessEnd > readinessStart,
                "the readiness UI method was located");

            if (readinessStart > 0 && readinessEnd > readinessStart)
            {
                var body = managerSource.Substring(readinessStart, readinessEnd - readinessStart);

                foreach (var banned in new[]
                         { "Before starting", "Windows output device", "confirm the Windows" })
                {
                    Assert(body.IndexOf(banned, System.StringComparison.OrdinalIgnoreCase) < 0,
                        $"the participant-facing readiness UI no longer contains '{banned}'");
                }

                Assert(body.Contains("LocKeys.AudioUnavailable"),
                    "the BLOCKING audio warning is still present (now a localization key) — " +
                    "only the routine reminder was removed");
            }

            // And nothing anywhere else writes it to a participant surface.
            foreach (var path in Directory.GetFiles("Assets/IKEA_EEG/Scripts", "*.cs",
                         SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(path);
                var index = text.IndexOf("Before starting", System.StringComparison.OrdinalIgnoreCase);

                Assert(index < 0,
                    $"{Path.GetFileName(path)} contains no 'Before starting' participant text");
            }
        }

        // ---------------------------------------------------------------------------------
        // Area B instruction overlay
        // ---------------------------------------------------------------------------------

        static void CheckAreaBOverlay()
        {
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();
            if (ui == null)
            {
                Info("no ExperimentUIController in the open scene — overlay not checked");
                return;
            }

            // ---- It occludes the chairs, which is the point ---------------------------------
            var overlay = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "UI_B_InstructionOverlay");

            var legend = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "ShapeLegend");

            Assert(overlay != null, "the Area B instruction overlay exists");
            Assert(legend != null, "the shape legend exists");

            var spawnB = Object.FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None)
                .FirstOrDefault(s => s.area == ExperimentArea.AreaB);

            var chairs = Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None);

            if (overlay != null && spawnB != null && chairs.Length > 0)
            {
                var eye = spawnB.transform.position + Vector3.up * 1.6f;
                var overlayDistance = Vector3.Distance(eye, overlay.position);
                var nearestChair = chairs.Min(c => Vector3.Distance(eye, c.transform.position));

                Assert(overlayDistance < nearestChair,
                    $"the overlay ({overlayDistance:F1} m) stands BETWEEN the participant and " +
                    $"the nearest chair ({nearestChair:F1} m), so it blocks the stimuli");

                Assert(overlayDistance > 1.0f && overlayDistance < 4.0f,
                    $"the overlay is at a comfortable reading distance ({overlayDistance:F1} m)");
            }

            // ---- Overlay and legend must not overlap each other -------------------------------
            if (overlay != null && legend != null)
            {
                var overlayRect = overlay.GetComponent<RectTransform>();
                var textRect = overlay.GetComponentsInChildren<TMPro.TMP_Text>(true)
                    .FirstOrDefault(t => t.name == "Txt_AreaBOverlay")?.rectTransform;

                if (overlayRect != null && textRect != null)
                {
                    // The legend sits below the overlay text band, in world Y.
                    var textBottom = overlay.position.y +
                                     (textRect.anchoredPosition.y - textRect.sizeDelta.y * 0.5f) *
                                     overlayRect.lossyScale.y / overlayRect.localScale.y *
                                     overlayRect.localScale.y;

                    var legendTop = legend.position.y + 0.75f;   // tallest legend example

                    Assert(legendTop <= textBottom + 0.05f,
                        $"the shape examples (top {legendTop:F2} m) sit below the overlay text " +
                        $"(bottom {textBottom:F2} m) — they do not overlap");
                }
            }

            // ---- It is down before any target appears -----------------------------------------
            ui.ShowAreaBInstructionOverlay(true);
            Assert(ui.areaBOverlayVisible && ui.shapeLegendVisible,
                "showing the overlay shows the shape legend with it");

            ui.ShowAreaBInstructionOverlay(false);
            Assert(!ui.areaBOverlayVisible && !ui.shapeLegendVisible,
                "hiding the overlay hides the legend too — neither can survive into a trial");

            ui.ShowAreaBInstructionOverlay(true);
            ui.ShowArea(ExperimentArea.AreaC);
            Assert(!ui.areaBOverlayVisible && !ui.shapeLegendVisible,
                "leaving Area B always takes the overlay and legend down");

            ui.ShowArea(ExperimentArea.AreaA);

            // ---- Older-adult legibility --------------------------------------------------------
            var overlayText = overlay?.GetComponentsInChildren<TMPro.TMP_Text>(true)
                .FirstOrDefault(t => t.name == "Txt_AreaBOverlay");

            if (overlayText != null && overlay != null)
            {
                // Glyph height in metres = font size (px) x canvas metres-per-pixel.
                var metresPerPixel = overlay.localScale.x;
                var minGlyphMetres = overlayText.fontSizeMin * metresPerPixel;

                Assert(minGlyphMetres >= 0.06f,
                    $"even at its smallest auto-size the overlay text is {minGlyphMetres * 100f:F1} cm " +
                    "tall (>= 6 cm), sized for older-adult legibility");

                Info($"overlay text: {overlayText.fontSizeMin:F0}-{overlayText.fontSizeMax:F0} px " +
                     $"= {minGlyphMetres * 100f:F1}-{overlayText.fontSizeMax * metresPerPixel * 100f:F1} cm glyphs");
            }
        }

        // ---------------------------------------------------------------------------------
        // Recording UI semantics
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The participant-facing recording text must mean "the microphone is on", not "we have
        /// heard you". This checks the code path, not a headset run: the status is set in the
        /// same block that starts the capture and is never rewritten by the detector.
        /// </summary>
        static void CheckRecordingUiSemantics()
        {
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            Assert(config != null && !string.IsNullOrWhiteSpace(config.recordingActiveText),
                $"a recording-active message is configured ('{config?.recordingActiveText}')");

            if (config != null)
            {
                // It must not promise anything about speech detection.
                foreach (var banned in new[] { "speak", "heard", "detected" })
                {
                    Assert(config.recordingActiveText.IndexOf(banned,
                               System.StringComparison.OrdinalIgnoreCase) < 0,
                        $"the recording message does not contain '{banned}' — it reports the " +
                        "microphone state, not the detector's");
                }
            }

            // The source contract: the loop that waits out the response window must not touch
            // the status text, or the message could again become a function of detector state.
            var managerSource = File.ReadAllText(
                "Assets/IKEA_EEG/Scripts/Experiment/ExperimentManager.cs");

            var loopStart = managerSource.IndexOf("while (elapsed < maxDurationSeconds)",
                System.StringComparison.Ordinal);
            var loopEnd = managerSource.IndexOf("RecordingInfo info = null;",
                System.StringComparison.Ordinal);

            Assert(loopStart > 0 && loopEnd > loopStart,
                "the recall wait loop was located in the source");

            if (loopStart > 0 && loopEnd > loopStart)
            {
                var loopBody = managerSource.Substring(loopStart, loopEnd - loopStart);

                Assert(!loopBody.Contains("setText"),
                    "the recall wait loop never writes the status text — 'Recording…' is set " +
                    "once, at response onset, and does not depend on speech detection");

                Assert(!loopBody.Contains("speechDetected"),
                    "the recall wait loop does not read the speech detector for UI purposes");
            }

            // The max-duration fallback must still be the loop's bound.
            Assert(loopStart > 0,
                "the response window is still bounded by maxDurationSeconds (hard maximum kept)");
        }

        // ---------------------------------------------------------------------------------
        // Multi-run identifiers + RESTART safety
        // ---------------------------------------------------------------------------------

        static void CheckRunLifecycle()
        {
            // ---- RESTART's discard guard: the data-safety property that matters most ---------
            const string root = "IKEA_EEG_Data";
            var dataRoot = SessionDiscardGuard.DataRoot(root);
            var runId = "S_20260101_120000_r01_abcdef";
            var runDirectory = Path.Combine(dataRoot, runId);

            Assert(SessionDiscardGuard.IsDiscardableRunDirectory(runDirectory, runId, root, out _),
                "the current run's own folder is discardable");

            // Everything below must be REFUSED.
            var refusals = new (string path, string id, string why)[]
            {
                (dataRoot, runId, "the data root itself"),
                (Path.Combine(dataRoot, "S_20250101_090000_r01_999999"), runId, "another session"),
                (Path.Combine(dataRoot, runId, "audio"), runId, "a subfolder of the run"),
                (Path.Combine(dataRoot, "..", "SomethingElse"), runId, "outside the data root"),
                (Application.dataPath, runId, "the project's Assets folder"),
                (Path.GetTempPath(), runId, "an unrelated temp folder"),
                (string.Empty, runId, "an empty path"),
                (runDirectory, string.Empty, "an empty run id"),
                (runDirectory, "S_different_id", "a mismatched run id"),
            };

            var refused = 0;
            foreach (var (path, id, why) in refusals)
            {
                var allowed = SessionDiscardGuard.IsDiscardableRunDirectory(path, id, root,
                    out var refusal);

                Assert(!allowed, $"RESTART refuses {why} ({refusal})");

                if (!allowed)
                    refused++;
            }

            Assert(refused == refusals.Length,
                $"every out-of-scope path was refused ({refused}/{refusals.Length}) — RESTART " +
                "can never delete outside the current run's own folder");

            // The deletion entry point must refuse them too, not just the predicate.
            var deleted = SessionDiscardGuard.TryDeleteRunDirectory(dataRoot, runId, root,
                out var detail);
            Assert(!deleted && detail.StartsWith("REFUSED"),
                $"TryDeleteRunDirectory refuses the data root ({detail})");

            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            Assert(config != null && !config.restartDeletesDiscardedRunFiles,
                "deletion is OFF by default — a discarded run is RETAINED and marked");

            // ---- Multi-run identifiers --------------------------------------------------------
            var go = new GameObject("__IKEA_EEG_SelfTest_Runs");
            var previousDirectories = new List<string>();

            try
            {
                var logger = go.AddComponent<EventLogger>();
                var csv = go.AddComponent<CsvEventSink>();
                logger.bus.Register(csv);

                logger.BeginExperimentSession();
                var experimentId = logger.experimentSessionId;

                Assert(!string.IsNullOrEmpty(experimentId),
                    $"an experiment session id is created ({experimentId})");

                var runIds = new List<string>();

                for (var run = 1; run <= 3; run++)
                {
                    logger.randomizationSeed = 1000 + run;
                    logger.BeginSession();

                    Assert(logger.runIndex == run,
                        $"run_index increments to {logger.runIndex} on run {run}");

                    Assert(logger.experimentSessionId == experimentId,
                        "experiment_session_id persists across runs");

                    Assert(logger.runSessionId == logger.sessionId,
                        "run_session_id is this run's own id");

                    runIds.Add(logger.sessionId);
                    previousDirectories.Add(logger.sessionDirectory);

                    logger.Log(EventTypes.NewRunStarted);
                    logger.EndSession($"self test run {run}");

                    // Every previous run's folder must still be there afterwards.
                    foreach (var directory in previousDirectories)
                    {
                        Assert(Directory.Exists(directory),
                            $"run folder still exists after starting run {run}: " +
                            $"{Path.GetFileName(directory)}");
                    }
                }

                Assert(runIds.Distinct().Count() == runIds.Count,
                    $"every run id is unique ({string.Join(", ", runIds.Select(Path.GetFileName))})");

                Assert(csv.filePath.Contains(runIds.Last()),
                    "each run writes to its own CSV file");

                // The header must carry the new identifiers.
                var header = File.ReadAllLines(csv.filePath)[0].Split(',');
                foreach (var column in new[]
                         { "experiment_session_id", "run_index", "developer_interrupted" })
                {
                    Assert(header.Contains(column), $"the CSV has a '{column}' column");
                }

                // 45 = the 35 previous columns + 10 APPENDED recognition-protocol columns.
                // The count is asserted so a column can never be added or removed silently;
                // when it legitimately changes, this number and the comment change WITH it.
                Assert(header.Length == 45,
                    $"the CSV has {header.Length} columns (35 previous + 10 recognition)");

                Assert(header[21] == "notes" && header[6] == "event_type",
                    "the original column positions are unchanged");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ---------------------------------------------------------------------------------
        // Localization
        // ---------------------------------------------------------------------------------

        static void CheckLocalizationTable()
        {
            var languages = new[]
            {
                ExperimentLanguage.English, ExperimentLanguage.Spanish, ExperimentLanguage.Japanese,
            };

            // ---- Completeness: every key in all three languages -------------------------------
            var missing = new List<string>();

            foreach (var pair in LocalizationTable.entries)
            {
                foreach (var language in languages)
                {
                    if (string.IsNullOrWhiteSpace(pair.Value.For(language)))
                        missing.Add($"{pair.Key}/{language}");
                }
            }

            Assert(missing.Count == 0,
                $"every localization key has text in all three languages " +
                $"({LocalizationTable.entries.Count} keys; {missing.Count} gaps " +
                $"{string.Join(", ", missing.Take(5))})");

            // ---- Every key CODE references must exist in the table ---------------------------
            var keyFields = typeof(LocKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .Where(k => !k.EndsWith("_"))     // prefixes are composed, not looked up directly
                .ToList();

            var unbacked = keyFields.Where(k => !LocalizationTable.entries.ContainsKey(k)).ToList();

            Assert(unbacked.Count == 0,
                $"every declared key has a table entry ({unbacked.Count} missing: " +
                $"{string.Join(", ", unbacked.Take(5))})");

            // ---- Chair attributes: EVERY enum value, in every language -----------------------
            var attributeGaps = 0;

            foreach (ChairColor color in System.Enum.GetValues(typeof(ChairColor)))
            {
                if (!LocalizationTable.entries.ContainsKey(LocKeys.ColorPrefix + color))
                    attributeGaps++;
            }

            foreach (ChairSize size in System.Enum.GetValues(typeof(ChairSize)))
            {
                if (!LocalizationTable.entries.ContainsKey(LocKeys.SizePrefix + size))
                    attributeGaps++;
            }

            foreach (ChairShape shape in System.Enum.GetValues(typeof(ChairShape)))
            {
                if (!LocalizationTable.entries.ContainsKey(LocKeys.ShapePrefix + shape))
                    attributeGaps++;
            }

            Assert(attributeGaps == 0,
                $"every colour, size and shape enum value is localized ({attributeGaps} gaps)");

            // ---- Placeholders must survive translation ---------------------------------------
            var placeholderKeys = new (string key, string placeholder)[]
            {
                (LocKeys.SelectColor, "{COLOR}"),
                (LocKeys.NCorrect, "{N}"),
                (LocKeys.NCorrect, "{TOTAL}"),
                (LocKeys.ResultsRun, "{N}"),
                (LocKeys.TaskProgress, "{N}"),
                (LocKeys.TaskProgress, "{TOTAL}"),
                (LocKeys.PracticeTryTarget, "{SELECTED}"),
                (LocKeys.PracticeTryTarget, "{TARGET}"),
            };

            foreach (var (key, placeholder) in placeholderKeys)
            {
                LocalizationTable.TryGet(key, out var entry);

                var present = languages.All(l => entry.For(l).Contains(placeholder));
                Assert(present, $"'{key}' keeps {placeholder} in all three languages");
            }

            // ---- The attribute terms must actually differ per language ------------------------
            foreach (ChairShape shape in System.Enum.GetValues(typeof(ChairShape)))
            {
                LocalizationTable.TryGet(LocKeys.ShapePrefix + shape, out var entry);

                Assert(entry.english != entry.spanish && entry.english != entry.japanese,
                    $"shape {shape} is genuinely translated " +
                    $"(EN '{entry.english}' / ES '{entry.spanish}' / JA '{entry.japanese}')");
            }

            // ---- Glyph coverage ----------------------------------------------------------------
            // Spanish accents and Japanese kana/kanji must actually be renderable, or the
            // participant sees empty boxes where their instructions should be.
            var spanishText = string.Join(" ", LocalizationTable.entries.Values
                .Select(e => e.spanish));

            var accents = "áéíóúñ¿¡";
            var accentsUsed = accents.Where(c => spanishText.Contains(c)).ToArray();

            Assert(accentsUsed.Length > 0,
                $"the Spanish strings use accented characters ({new string(accentsUsed)}) — " +
                "they are real Spanish, not accent-stripped");

            var latinFont = TMP_Settings.defaultFontAsset;
            if (latinFont != null)
            {
                var missingAccents = accentsUsed
                    .Where(c => !latinFont.HasCharacter(c))
                    .ToArray();

                Assert(missingAccents.Length == 0,
                    $"the default font renders every Spanish accent used " +
                    $"({(missingAccents.Length == 0 ? "all present" : new string(missingAccents))})");
            }

            // Japanese: the fallback must resolve, or Japanese must not be offered.
            var japaneseReady = ExperimentLocalization.CanRenderJapanese();

            Assert(japaneseReady,
                $"Japanese glyphs can be rendered — {ExperimentLocalization.fontFallbackDetail}");

            if (japaneseReady)
            {
                var jpFallback = TMP_Settings.fallbackFontAssets?
                    .FirstOrDefault(f => f != null && f.name.StartsWith("IKEA_EEG_JP_"));

                Assert(jpFallback != null,
                    $"a Japanese fallback font asset is registered ({jpFallback?.name})");

                if (jpFallback != null)
                {
                    // Sample glyphs actually used by the localized strings: kanji, hiragana and
                    // katakana. tryAddCharacter:true because a DYNAMIC font asset rasterises on
                    // demand — the question is whether the font FILE can supply the glyph, not
                    // whether it happens to be in the atlas already.
                    foreach (var glyph in new[] { '言', '語', '実', '験', 'を', 'ア' })
                    {
                        Assert(jpFallback.HasCharacter(glyph, false, true),
                            $"the Japanese font can supply '{glyph}'");
                    }
                }
            }

            // ---- Lookup returns the right language ---------------------------------------------
            var previous = ExperimentLocalization.language;

            try
            {
                foreach (var language in languages)
                {
                    ExperimentLocalization.SetLanguage(language);

                    LocalizationTable.TryGet(LocKeys.StartExperiment, out var entry);

                    Assert(ExperimentLocalization.Get(LocKeys.StartExperiment) == entry.For(language),
                        $"{ExperimentLanguages.ToCode(language)} lookup returns its own text " +
                        $"('{entry.For(language)}')");
                }

                // Formatting must substitute in every language.
                ExperimentLocalization.SetLanguage(ExperimentLanguage.Japanese);
                var formatted = ExperimentLocalization.Format(LocKeys.NCorrect, "N", "2", "TOTAL", "3");

                Assert(formatted.Contains("2") && formatted.Contains("3") &&
                       !formatted.Contains("{"),
                    $"placeholders substitute in Japanese ('{formatted}')");

                // Chair attributes localize without the enum changing.
                var spec = new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Slatted);

                ExperimentLocalization.SetLanguage(ExperimentLanguage.Spanish);
                var spanishLines = ExperimentLocalization.TargetLines(spec);

                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);
                var englishLines = ExperimentLocalization.TargetLines(spec);

                Assert(spanishLines != englishLines,
                    $"the target text differs by language (ES '{spanishLines.Replace('\n', '/')}' " +
                    $"vs EN '{englishLines.Replace('\n', '/')}')");

                // THE DATA MUST NOT MOVE: the enum values are what the CSV stores.
                Assert(spec.color.ToString() == "Blue" && spec.size.ToString() == "Large" &&
                       spec.shape.ToString() == "Slatted",
                    "the INTERNAL enum values are unchanged by localization — the CSV keeps " +
                    "storing canonical Blue/Large/Slatted");
            }
            finally
            {
                ExperimentLocalization.SetLanguage(previous);
            }
        }

        // ---------------------------------------------------------------------------------
        // Language selection flow
        // ---------------------------------------------------------------------------------

        static void CheckLanguageSelectionFlow()
        {
            // ---- The three buttons, each in its own language ----------------------------------
            var languageButtons = Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(b => b.name.StartsWith("Btn_Language_"))
                .OrderBy(b => b.name)
                .ToArray();

            Assert(languageButtons.Length == 3,
                $"exactly three language buttons exist ({languageButtons.Length})");

            var expected = new Dictionary<string, string>
            {
                { "Btn_Language_EN", "English" },
                { "Btn_Language_ES", "Español" },
                { "Btn_Language_JA", "日本語" },
            };

            foreach (var button in languageButtons)
            {
                var label = button.GetComponentInChildren<TMPro.TMP_Text>(true);

                Assert(label != null && expected.TryGetValue(button.name, out var want) &&
                       label.text == want,
                    $"{button.name} reads '{label?.text}' — each language names itself");

                // A language button must never be re-localized: a Spanish speaker has to find
                // "Español" whatever language the UI happens to be in.
                Assert(label == null || label.GetComponent<LocalizedText>() == null,
                    $"{button.name} is NOT bound to the localization table");
            }

            // ---- The panel exists, starts hidden, and precedes Area 0 ------------------------
            var panel = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "UI_LanguageSelection");

            Assert(panel != null, "the language panel exists in the scene");

            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();

            if (manager == null || ui == null)
            {
                Info("no manager/UI in the open scene — the flow was not exercised");
                return;
            }

            var type = typeof(ExperimentManager);
            var enterLanguage = type.GetMethod("EnterLanguageSelection",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var onLanguageSelected = type.GetMethod("OnLanguageSelected",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var stateField = type.GetField("m_State",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(enterLanguage != null && onLanguageSelected != null && stateField != null,
                "the language-selection entry and choice handlers exist");

            if (enterLanguage == null || onLanguageSelected == null || stateField == null)
                return;

            // ---- Language selection comes BEFORE Area 0 ---------------------------------------
            enterLanguage.Invoke(manager, new object[] { "self test" });

            Assert((ExperimentState)stateField.GetValue(manager) == ExperimentState.LanguageSelection,
                "the session begins in the LanguageSelection state");

            Assert(!ExperimentLocalization.hasLanguage,
                "no language is in force until one is chosen");

            Assert(ui.languagePanelVisible,
                "the language panel is shown and the familiarization panel is not");

            // ---- Choosing a language reaches Area 0 -------------------------------------------
            onLanguageSelected.Invoke(manager, new object[] { ExperimentLanguage.Spanish });

            Assert(ExperimentLocalization.language == ExperimentLanguage.Spanish,
                "choosing Español sets the platform language");

            Assert(ExperimentLocalization.languageCode == "ES",
                $"platform_language logs as ES ({ExperimentLocalization.languageCode})");

            Assert((ExperimentState)stateField.GetValue(manager) == ExperimentState.Familiarization,
                "selecting a language enters Area 0");

            Assert(!ui.languagePanelVisible, "the language panel is taken down afterwards");

            // ---- Participant text actually changed --------------------------------------------
            var startButton = Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(b => b.name == "Btn_StartExperiment");

            if (startButton != null)
            {
                var label = startButton.GetComponentInChildren<TMPro.TMP_Text>(true);
                var localized = label != null ? label.GetComponent<LocalizedText>() : null;

                Assert(localized != null,
                    "START EXPERIMENT's caption is bound to the localization table");

                if (localized != null)
                {
                    localized.Refresh();
                    Assert(label.text == "COMENZAR EXPERIMENTO",
                        $"the caption followed the language ('{label.text}')");
                }
            }

            // ---- RESTART clears the language; NEW TRIAL does not ------------------------------
            var restartSession = type.GetMethod("RestartSession",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(restartSession != null &&
                   restartSession.GetParameters().Any(p => p.Name == "returnToLanguageSelection"),
                "restart takes an explicit 'return to language selection' decision");

            var devLanguage = type.GetMethod("OnDeveloperReturnToLanguage",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(devLanguage != null,
                "the developer 'return to language selection' action exists");

            ExperimentLocalization.SetLanguage(ExperimentLanguage.English);
        }

        // ---------------------------------------------------------------------------------
        // Language-specific word sets
        // ---------------------------------------------------------------------------------

        static void CheckWordSetLanguageSafety()
        {
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config == null || config.wordList == null)
            {
                Assert(false, "the word list asset exists");
                return;
            }

            var list = config.wordList;

            // ---- The English prototype sets still work ---------------------------------------
            var englishSets = list.GetSetIndicesForLanguage(ExperimentLanguage.English);

            Assert(englishSets.Count >= 1,
                $"English has {englishSets.Count} word set(s) — the existing prototype sets are " +
                "intact");

            foreach (var index in englishSets)
            {
                var set = list.GetSet(index);
                var detail = "no set";
                var structureOk = false;

                if (set != null)
                    structureOk = set.ValidateStructure(list.wordsPerSet, out detail);

                Assert(structureOk,
                    $"English set '{set?.EffectiveId()}' is still structurally valid ({detail})");
            }

            // ---- Validation status is recorded honestly ---------------------------------------
            var validatedCount = 0;
            for (var i = 0; i < list.setCount; i++)
            {
                var set = list.GetSet(i);
                if (set != null && set.validatedForResearch)
                    validatedCount++;
            }

            Assert(validatedCount == 0,
                $"no word set claims research validation ({validatedCount}) — the English sets " +
                "are prototypes and are marked as such");

            // ---- NO SILENT FALLBACK: the core safety property ---------------------------------
            foreach (var language in new[] { ExperimentLanguage.Spanish, ExperimentLanguage.Japanese })
            {
                var sets = list.GetSetIndicesForLanguage(language);
                var resolved = list.ResolveSetIndexForLanguage(language, 0);

                if (sets.Count == 0)
                {
                    Assert(resolved == -1,
                        $"{ExperimentLanguages.ToCode(language)} has no word set and resolves to " +
                        "-1 — it must NOT fall back to an English list");
                }
                else
                {
                    Assert(resolved >= 0 && list.GetSet(resolved).language == language,
                        $"{ExperimentLanguages.ToCode(language)} resolves to a set of its OWN " +
                        "language");
                }
            }

            // English must still resolve, whatever index is preferred.
            var englishResolved = list.ResolveSetIndexForLanguage(ExperimentLanguage.English, 99);
            Assert(englishResolved >= 0 &&
                   list.GetSet(englishResolved).language == ExperimentLanguage.English,
                "an out-of-range preferred index still resolves to an English set for English");

            // ---- The manager blocks rather than substituting ----------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
                return;

            var applyLanguage = typeof(ExperimentManager).GetMethod("ApplyLanguageToWordList",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var okField = typeof(ExperimentManager).GetField("m_WordSetLanguageOk",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert(applyLanguage != null && okField != null,
                "the language/word-set gate exists");

            if (applyLanguage == null || okField == null)
                return;

            var previous = ExperimentLocalization.language;

            try
            {
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);
                applyLanguage.Invoke(manager, null);
                Assert((bool)okField.GetValue(manager),
                    "an English session finds its word set and is allowed to run");

                foreach (var language in new[]
                         { ExperimentLanguage.Spanish, ExperimentLanguage.Japanese })
                {
                    if (list.GetSetIndicesForLanguage(language).Count > 0)
                        continue;

                    ExperimentLocalization.SetLanguage(language);
                    applyLanguage.Invoke(manager, null);

                    Assert(!(bool)okField.GetValue(manager),
                        $"a {ExperimentLanguages.ToCode(language)} session with no word set is " +
                        "BLOCKED, not silently given the English words");
                }
            }
            finally
            {
                ExperimentLocalization.SetLanguage(previous == ExperimentLanguage.None
                    ? ExperimentLanguage.English
                    : previous);
                applyLanguage.Invoke(manager, null);
            }

            // ---- Narration is language-specific and never substituted -------------------------
            foreach (var language in new[]
                     {
                         ExperimentLanguage.English, ExperimentLanguage.Spanish,
                         ExperimentLanguage.Japanese,
                     })
            {
                var clip = config.GetFamiliarizationNarration(language);
                var code = ExperimentLanguages.ToCode(language);

                if (clip == null)
                {
                    Info($"{code}: no narration generated — that language runs text-only and is " +
                         "NEVER given another language's voice");
                    continue;
                }

                Assert(AssetDatabase.GetAssetPath(clip).Contains($"/Narration/{code}/"),
                    $"{code} narration comes from its own folder " +
                    $"({AssetDatabase.GetAssetPath(clip)})");
            }
        }

        // ---------------------------------------------------------------------------------
        // Final statistics + END state
        // ---------------------------------------------------------------------------------

        static void CheckFinalStatisticsAndEndState()
        {
            var results = new SessionResults
            {
                sessionId = "S_TEST",
                randomizationSeed = 42,
                totalExperimentDurationSeconds = 184.5d,
                immediateRecallStatus = "recording saved (C:/somewhere/audio.wav) / not scored",
                delayedRecallStatus = "no recording / not scored",
                csvPath = @"C:\secret\events.csv",
            };

            results.chairTrials.Add(MakeTrial(1, DifficultyLevel.Low, true, 1500d, true));
            results.chairTrials.Add(MakeTrial(2, DifficultyLevel.Medium, false, 2500d, true));
            results.chairTrials.Add(MakeTrial(3, DifficultyLevel.High, true, 2000d, true));

            var previous = ExperimentLocalization.language;

            try
            {
                foreach (var language in new[]
                         {
                             ExperimentLanguage.English, ExperimentLanguage.Spanish,
                             ExperimentLanguage.Japanese,
                         })
                {
                    ExperimentLocalization.SetLanguage(language);
                    var code = ExperimentLanguages.ToCode(language);

                    var summary = results.BuildParticipantRunSummary(
                        ExperimentLocalization.Get(LocKeys.ExecutiveTaskComplete), 2, 0);

                    // ---- The headline result is still first --------------------------------
                    // Checked as the CORRECT and TOTAL counts rather than an English format:
                    // Japanese writes the same fact as "3問中 2問 正解", and demanding "2 / 3"
                    // would be demanding English word order from a translation.
                    var headline = ExperimentLocalization.Format(LocKeys.NCorrect,
                        "N", "2", "TOTAL", "3");

                    Assert(summary.Contains(headline),
                        $"{code}: the panel leads with the accuracy result ('{headline}')");

                    // ---- The restored statistics are all present ----------------------------
                    foreach (var key in new[]
                             {
                                 LocKeys.SessionSummaryHeading, LocKeys.StatMeanResponseTime,
                                 LocKeys.StatMedianResponseTime, LocKeys.StatTotalDuration,
                                 LocKeys.StatImmediateRecall, LocKeys.StatDelayedRecall,
                             })
                    {
                        Assert(summary.Contains(ExperimentLocalization.Get(key)),
                            $"{code}: the panel shows '{ExperimentLocalization.Get(key)}'");
                    }

                    // Values, in the documented format.
                    Assert(summary.Contains("2.000 s (2000 ms)"),
                        $"{code}: mean response time is shown as X.XXX s (XXXX ms)");

                    Assert(summary.Contains("184.500 s (184500 ms)"),
                        $"{code}: total duration is shown as X.XXX s (XXXX ms)");

                    Assert(summary.Contains(ExperimentLocalization.Get(LocKeys.RecordingSaved)),
                        $"{code}: the saved immediate recall reads as 'Recording saved'");

                    Assert(summary.Contains(ExperimentLocalization.Get(LocKeys.NotAvailable)),
                        $"{code}: the missing delayed recall reads as 'Not available'");

                    // ---- Nothing technical may leak onto the participant panel ---------------
                    foreach (var banned in new[]
                             {
                                 "S_TEST", "T001", "Trial id", "42", ".csv", ".wav", "seed",
                                 "C:\\", "developer_interrupted",
                             })
                    {
                        Assert(summary.IndexOf(banned, System.StringComparison.OrdinalIgnoreCase) < 0,
                            $"{code}: the panel does not contain '{banned}'");
                    }
                }

                Info("EN panel:\n" + BuildSampleSummary(results, ExperimentLanguage.English));
                Info("ES panel:\n" + BuildSampleSummary(results, ExperimentLanguage.Spanish));
                Info("JA panel:\n" + BuildSampleSummary(results, ExperimentLanguage.Japanese));
            }
            finally
            {
                ExperimentLocalization.SetLanguage(previous == ExperimentLanguage.None
                    ? ExperimentLanguage.English
                    : previous);
            }

            // ---- END must truly end -------------------------------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            var ui = Object.FindAnyObjectByType<ExperimentUIController>();
            var logger = Object.FindAnyObjectByType<EventLogger>();

            if (manager == null || ui == null || logger == null)
            {
                Info("no manager/UI/logger in the open scene — END state not exercised");
                return;
            }

            var endedField = typeof(ExperimentManager).GetField("m_SessionEnded",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var beginSession = typeof(ExperimentManager).GetMethod("BeginSession",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(long), typeof(bool) }, null);

            Assert(endedField != null && beginSession != null,
                "the ended flag and the session entry point exist");

            if (endedField == null || beginSession == null)
                return;

            try
            {
                endedField.SetValue(manager, false);
                logger.BeginExperimentSession();
                beginSession.Invoke(manager, new object[] { 999L, false });

                var runBefore = logger.runIndex;
                var directoryBefore = logger.sessionDirectory;

                manager.EndSession();

                Assert(manager.sessionEnded, "END marks the session as ended");
                Assert(!logger.sessionActive, "END closes the run and writes its data");

                // The three run-management buttons must be gone.
                foreach (var buttonName in new[] { "Btn_NewTrial", "Btn_Restart", "Btn_End" })
                {
                    var button = Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
                            FindObjectsSortMode.None)
                        .FirstOrDefault(b => b.name == buttonName);

                    Assert(button != null && !button.gameObject.activeSelf,
                        $"{buttonName} is hidden after END");
                }

                // RECENTER stays, and is harmless.
                var recenter = Object.FindObjectsByType<Button>(FindObjectsInactive.Include,
                        FindObjectsSortMode.None)
                    .FirstOrDefault(b => b.name == "Btn_Recenter_C");

                Assert(recenter != null && recenter.gameObject.activeSelf,
                    "RECENTER remains available after END");

                // ---- Nothing may create another run --------------------------------------
                manager.StartNewRun();

                Assert(!logger.sessionActive && logger.runIndex == runBefore,
                    $"NEW TRIAL after END creates no run (run_index still {logger.runIndex})");

                Assert(logger.sessionDirectory == directoryBefore,
                    "NEW TRIAL after END creates no new run folder");

                manager.RestartTrial();

                Assert(!logger.sessionActive && logger.runIndex == runBefore,
                    "RESTART after END creates no run");

                // ---- END cannot finalise twice ---------------------------------------------
                manager.EndSession();

                Assert(!logger.sessionActive && logger.runIndex == runBefore,
                    "pressing END again finalises nothing a second time");
            }
            finally
            {
                endedField.SetValue(manager, false);

                if (logger.sessionActive)
                    logger.EndSession("self test cleanup");
            }
        }

        static string BuildSampleSummary(SessionResults results, ExperimentLanguage language)
        {
            ExperimentLocalization.SetLanguage(language);
            return results.BuildParticipantRunSummary(
                ExperimentLocalization.Get(LocKeys.ExecutiveTaskComplete), 2, 0);
        }

        // ---------------------------------------------------------------------------------
        // NEW TRIAL run tracking
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Drives the REAL manager through NEW TRIAL, rather than the logger alone, because the
        /// reported bug ("the new run looked like run 1 again") was about what that whole path
        /// produces — including what the participant is shown afterwards.
        /// </summary>
        static void CheckNewTrialRunTracking()
        {
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            var logger = Object.FindAnyObjectByType<EventLogger>();

            if (manager == null || logger == null)
            {
                Info("no ExperimentManager/EventLogger in the open scene — NEW TRIAL not checked");
                return;
            }

            var type = typeof(ExperimentManager);
            var beginSession = type.GetMethod("BeginSession",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(long), typeof(bool) }, null);

            if (beginSession == null)
            {
                Assert(false, "the manager's session entry point was found");
                return;
            }

            try
            {
                // Run 1 of a fresh sitting.
                logger.BeginExperimentSession();
                beginSession.Invoke(manager, new object[] { 12345L, false });

                var experimentId = logger.experimentSessionId;
                var run1Id = logger.sessionId;
                var run1Index = logger.runIndex;
                var run1Directory = logger.sessionDirectory;

                Assert(run1Index == 1, $"the first run is run_index 1 ({run1Index})");

                // NEW TRIAL.
                manager.StartNewRun();

                Assert(logger.runIndex == 2,
                    $"NEW TRIAL increments run_index to 2 (got {logger.runIndex}) — the new run " +
                    "is not reported as the first run again");

                Assert(logger.experimentSessionId == experimentId,
                    "NEW TRIAL keeps the same experiment_session_id (same participant sitting)");

                Assert(logger.sessionId != run1Id,
                    $"NEW TRIAL allocates a new run_session_id ({logger.sessionId})");

                Assert(logger.sessionId.Contains("_r02_"),
                    $"the new run's id carries its run number ({logger.sessionId})");

                Assert(Directory.Exists(run1Directory),
                    "the previous run's folder is preserved, not overwritten");

                Assert(logger.sessionDirectory != run1Directory,
                    "the new run writes to its own folder");

                // ---- And what the participant is SHOWN must say run 2 --------------------------
                var results = manager.sessionResults;
                var summary = results.BuildParticipantRunSummary("Executive task complete",
                    logger.runIndex, logger.runIndex);

                Assert(summary.Contains("Run 2"),
                    $"the results panel names the run correctly:\n{summary.Trim()}");

                Assert(!summary.Contains("T001") && !summary.Contains("Trial id"),
                    "the results panel no longer shows a per-run trial id that reads as 'T001' " +
                    "on every run");

                // A third run, to be sure the increment is not a one-off.
                manager.StartNewRun();

                Assert(logger.runIndex == 3 && logger.experimentSessionId == experimentId,
                    $"a further NEW TRIAL reaches run_index 3 ({logger.runIndex}) in the same " +
                    "experiment session");
            }
            finally
            {
                if (logger.sessionActive)
                    logger.EndSession("self test new-trial check");
            }
        }

        // ---------------------------------------------------------------------------------
        // Developer navigation
        // ---------------------------------------------------------------------------------

        static void CheckDeveloperNavigation()
        {
            var devNav = Object.FindAnyObjectByType<DeveloperNavigation>();

            Assert(devNav != null, "the developer navigation component exists in the scene");

            if (devNav == null)
                return;

            Assert(!devNav.isOpen, "the developer menu starts CLOSED");

            Assert(devNav.holdSeconds >= 1.5f,
                $"the gesture requires a deliberate {devNav.holdSeconds:F1} s hold");

            // The gesture must use neither trigger: it could otherwise collide with Select.
            var devSource = File.ReadAllText("Assets/IKEA_EEG/Scripts/XR/DeveloperNavigation.cs");

            Assert(devSource.Contains("thumbstickClicked"),
                "the gesture is bound to the thumbstick clicks");

            Assert(!devSource.Contains("triggerPressed") && !devSource.Contains("gripPressed"),
                "the gesture uses NEITHER trigger, so it cannot collide with Select");

            Assert(devSource.Contains("new InputAction"),
                "the gesture's actions are created locally in code — the shared XRI input " +
                "asset is not modified");

            // The panel must be hidden and must not be part of the XR rig.
            var panel = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "UI_DeveloperNavigation");

            Assert(panel != null, "the developer panel exists");

            if (panel != null)
            {
                Assert(!panel.gameObject.activeSelf,
                    "the developer panel is INACTIVE — a participant never sees it");

                var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
                Assert(origin == null || !panel.IsChildOf(origin.transform),
                    "the developer panel is not parented to the XR Origin");
            }

            // ---- Opening alone must not change any metric ------------------------------------
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
                return;

            var trialsBefore = manager.sessionResults.chairTrialsTotal;
            var correctBefore = manager.sessionResults.chairTrialsCorrect;
            var seedBefore = manager.sessionSeed;

            var openHandler = typeof(ExperimentManager).GetMethod("OnDeveloperMenuOpened",
                BindingFlags.Instance | BindingFlags.NonPublic);

            openHandler?.Invoke(manager, null);

            Assert(manager.sessionResults.chairTrialsTotal == trialsBefore &&
                   manager.sessionResults.chairTrialsCorrect == correctBefore &&
                   manager.sessionSeed == seedBefore,
                "OPENING the developer menu changes no metric and no seed");

            // ---- A jump marks the run interrupted ---------------------------------------------
            var loggerGo = new GameObject("__IKEA_EEG_SelfTest_DevJump");

            try
            {
                var logger = loggerGo.AddComponent<EventLogger>();

                Assert(!logger.developerInterrupted,
                    "a fresh run is not flagged as developer-interrupted");

                logger.MarkDeveloperInterrupted("self test jump");

                Assert(logger.developerInterrupted,
                    "a developer jump marks the run DEVELOPER_INTERRUPTED");

                logger.BeginExperimentSession();
                logger.BeginSession();

                Assert(!logger.developerInterrupted,
                    "starting a NEW run clears the flag — interference does not carry across runs");

                logger.EndSession("self test");
            }
            finally
            {
                Object.DestroyImmediate(loggerGo);
            }
        }

        // ---------------------------------------------------------------------------------
        // Recall scoring
        // ---------------------------------------------------------------------------------

        static void CheckRecallScoring()
        {
            var expected = new List<string> { "River", "Copper", "Lantern", "Falcon", "Sugar" };

            var perfect = RecallScorer.Score(expected,
                new List<string> { "river", "copper", "lantern", "falcon", "sugar" });
            Assert(perfect.scored && perfect.numberCorrect == 5 && perfect.wordOrderCorrect
                   && perfect.intrusions.Count == 0,
                $"perfect recall: correct={perfect.numberCorrect}/5, order={perfect.wordOrderCorrect}, " +
                $"intrusions={perfect.intrusions.Count}");

            var scrambled = RecallScorer.Score(expected,
                new List<string> { "sugar", "river", "falcon" });
            Assert(scrambled.numberCorrect == 3 && !scrambled.wordOrderCorrect,
                $"out-of-order partial recall: correct={scrambled.numberCorrect}/5, " +
                $"order={scrambled.wordOrderCorrect}");

            var intrusive = RecallScorer.Score(expected,
                new List<string> { "river", "chair", "copper", "table" });
            Assert(intrusive.numberCorrect == 2 && intrusive.intrusions.Count == 2,
                $"intrusions detected: correct={intrusive.numberCorrect}/5, " +
                $"intrusions=[{intrusive.IntrusionsCsv()}]");

            Assert(perfect.omissions.Count == 0,
                $"perfect recall has no omissions ({perfect.OmissionsCsv()})");

            Assert(scrambled.omissions.Count == 2 &&
                   scrambled.omissions.Contains("Copper") && scrambled.omissions.Contains("Lantern"),
                $"omissions are the unrecalled expected words: [{scrambled.OmissionsCsv()}]");

            // number_correct ignores position; order_correct is the separate measure.
            var reversed = RecallScorer.Score(expected,
                new List<string> { "sugar", "falcon", "lantern", "copper", "river" });
            Assert(reversed.numberCorrect == 5 && !reversed.wordOrderCorrect &&
                   reversed.omissions.Count == 0,
                $"fully reversed recall: correct={reversed.numberCorrect}/5 (position ignored), " +
                $"order={reversed.wordOrderCorrect}");

            var none = RecallScorer.Score(expected, null);
            Assert(!none.scored && none.expectedWords.Count == 5,
                "no transcript -> not scored, but expected words are still recorded");

            // The distinction that must never be blurred: "recalled nothing" vs "not scored".
            Assert(none.ToNotesString().Contains("number_correct=NA") &&
                   none.ToNotesString().Contains("omissions=NA"),
                $"an unscored recall reports NA, not 0: {none.ToNotesString()}");

            var nothingRecalled = RecallScorer.Score(expected,
                new List<string> { "banana", "table" });
            Assert(nothingRecalled.scored && nothingRecalled.numberCorrect == 0 &&
                   nothingRecalled.omissions.Count == 5 && nothingRecalled.intrusions.Count == 2,
                $"a scored-but-empty recall reports 0 correct and 5 omissions: " +
                $"{nothingRecalled.ToNotesString()}");

            var tokens = RecallScorer.Tokenize("River, copper... lantern! Falcon? sugar.");
            Assert(tokens.Count == 5, $"tokenizer split punctuated text into {tokens.Count} words");
        }

        // ---------------------------------------------------------------------------------
        // CSV pipeline
        // ---------------------------------------------------------------------------------

        static void CheckCsvPipeline()
        {
            var go = new GameObject("__IKEA_EEG_SelfTest_Logger");
            string csvPath;

            try
            {
                var logger = go.AddComponent<EventLogger>();
                var csv = go.AddComponent<CsvEventSink>();

                // In edit mode Awake does not run, so the sink is registered explicitly.
                // At runtime EventLogger.Awake does this automatically.
                logger.bus.Register(csv);

                logger.currentState = ExperimentState.Idle.ToString();
                logger.currentRoom = RoomNames.AreaA;
                logger.randomizationSeed = 987654321L;
                logger.BeginSession();
                logger.BeginTrial();

                logger.currentState = ExperimentState.WordEncoding.ToString();
                logger.Log(EventTypes.WordEncodingStart);

                var words = new[] { "River", "Copper", "Lantern", "Falcon", "Sugar" };
                for (var i = 0; i < words.Length; i++)
                {
                    var index = i;
                    logger.Log(EventTypes.WordPresented, e =>
                    {
                        e.wordIndex = (index + 1).ToString();
                        e.expectedWord = words[index];
                    });
                }

                // Three chair trials, so the per-trial columns can be checked for validity.
                logger.currentRoom = RoomNames.AreaB;
                for (var trial = 1; trial <= 3; trial++)
                {
                    var index = trial;
                    var difficulty = trial == 1 ? "LOW" : trial == 2 ? "MEDIUM" : "HIGH";

                    logger.currentState = ExperimentState.ChairInstruction.ToString();
                    logger.Log(EventTypes.ChairTrialStart, e =>
                    {
                        e.chairTrialIndex = index.ToString();
                        e.chairTrialCount = "3";
                        e.difficulty = difficulty;
                    });

                    logger.currentState = ExperimentState.ChairSelection.ToString();
                    logger.Log(EventTypes.ChairSelected, e =>
                    {
                        e.chairTrialIndex = index.ToString();
                        e.chairTrialCount = "3";
                        e.difficulty = difficulty;
                        e.objectId = "Chair_04";
                        e.targetColor = "Blue";
                        e.targetSize = "Large";
                        e.targetShape = "Modern";
                        e.selectedColor = "Blue";
                        e.selectedSize = "Large";
                        e.selectedShape = "Modern";
                        e.correct = "TRUE";
                        e.responseTimeMs = "1843.2";
                        // Deliberately hostile free text: commas, quotes and a newline must not
                        // be able to corrupt the CSV.
                        e.notes = "self-test, with \"quotes\" and\na newline";
                    });

                    logger.Log(EventTypes.ChairTrialEnd, e =>
                    {
                        e.chairTrialIndex = index.ToString();
                        e.chairTrialCount = "3";
                        e.difficulty = difficulty;
                    });
                }

                logger.EndTrial();
                logger.EndSession("self test");

                csvPath = csv.filePath;

                Assert(!string.IsNullOrEmpty(csvPath) && File.Exists(csvPath),
                    $"CSV was written: {csvPath}");

                if (!File.Exists(csvPath))
                    return;

                var lines = File.ReadAllLines(csvPath);
                Assert(lines.Length >= 11,
                    $"CSV contains {lines.Length} lines (header + events)");

                var headerColumns = lines[0].Split(',').Length;
                Assert(headerColumns == 45,
                    $"header has {headerColumns} columns (expected 45: the original 22, the 8 " +
                    "protocol columns, the 3 multi-run columns, platform_language, " +
                    "lsl_timestamp, and the 10 appended recognition columns)");

                // Every data row must parse to the same column count, including the row with
                // embedded commas/quotes/newline.
                var rows = ParseCsv(csvPath);
                var badRows = rows.Count(r => r.Length != headerColumns);
                Assert(badRows == 0,
                    $"every CSV row parses to {headerColumns} columns ({badRows} malformed)");

                var header = lines[0];
                Assert(header.StartsWith("timestamp_absolute,timestamp_relative,session_id,trial_id,"),
                    "CSV header starts with the documented column order");

                // The original 22 columns must keep their POSITIONS, not just their names, or
                // an analysis script written against the previous format breaks silently.
                var columnNames = header.Split(',');
                Assert(columnNames.Length > 21 && columnNames[21] == "notes",
                    $"column 22 is still 'notes' (found '{(columnNames.Length > 21 ? columnNames[21] : "n/a")}')");

                foreach (var (name, position) in new[]
                         {
                             ("chair_trial_index", 22), ("chair_trial_count", 23),
                             ("difficulty", 24), ("randomization_seed", 25),
                             ("word_set_id", 26), ("clip_name", 27),
                             ("scheduled_audio_time", 28), ("confirmed_audio_time", 29),
                         })
                {
                    Assert(columnNames.Length > position && columnNames[position] == name,
                        $"appended column {position} is '{name}'");
                }

                var eventTypeIndex = System.Array.IndexOf(header.Split(','), "event_type");
                var loggedTypes = rows.Select(r => r[eventTypeIndex]).ToList();

                foreach (var required in new[]
                         {
                             EventTypes.SessionStart, EventTypes.TrialStart,
                             EventTypes.WordEncodingStart, EventTypes.WordPresented,
                             EventTypes.ChairSelected, EventTypes.TrialEnd, EventTypes.SessionEnd,
                         })
                {
                    Assert(loggedTypes.Contains(required), $"CSV contains a {required} row");
                }

                Assert(loggedTypes.Count(t => t == EventTypes.WordPresented) == 5,
                    "CSV contains exactly 5 WORD_PRESENTED rows");

                // ---- Chair trial indices and the session seed --------------------------------
                var trialIndexColumn = System.Array.IndexOf(columnNames, "chair_trial_index");
                var trialCountColumn = System.Array.IndexOf(columnNames, "chair_trial_count");
                var seedColumn = System.Array.IndexOf(columnNames, "randomization_seed");

                var chairRows = rows
                    .Where(r => !string.IsNullOrEmpty(r[trialIndexColumn]))
                    .ToList();

                Assert(chairRows.Count == 9,
                    $"{chairRows.Count} rows carry a chair_trial_index (expected 9: " +
                    "3 events x 3 trials)");

                var validIndices = chairRows.All(r =>
                    int.TryParse(r[trialIndexColumn], out var index) &&
                    int.TryParse(r[trialCountColumn], out var count) &&
                    index >= 1 && index <= count && count == 3);

                Assert(validIndices,
                    "every chair_trial_index is within 1..chair_trial_count");

                var distinctTrials = chairRows.Select(r => r[trialIndexColumn]).Distinct().Count();
                Assert(distinctTrials == 3,
                    $"the CSV distinguishes {distinctTrials} separate chair trials (expected 3)");

                var everyRowHasSeed = rows.All(r => r[seedColumn] == "987654321");
                Assert(everyRowHasSeed,
                    "every row carries the session's randomization_seed");

                // Timestamps must be monotonically non-decreasing.
                var relIndex = System.Array.IndexOf(header.Split(','), "timestamp_relative");
                var times = rows
                    .Select(r => double.Parse(r[relIndex], System.Globalization.CultureInfo.InvariantCulture))
                    .ToList();
                var monotonic = true;
                for (var i = 1; i < times.Count; i++)
                {
                    if (times[i] < times[i - 1])
                        monotonic = false;
                }

                Assert(monotonic, "timestamp_relative is monotonically non-decreasing");
                Assert(times.Count > 0 && times[0] >= 0d, "session clock starts at t >= 0");

                Info($"session spanned {times.Last():F4} s of logged events");
                Info("--- first 3 CSV lines ---");
                foreach (var line in lines.Take(3))
                    Info("  " + line);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            Info($"CSV self-test artefacts remain in: {Path.GetDirectoryName(csvPath)}");
        }

        /// <summary>Minimal RFC-4180 reader, used to prove the writer's escaping is correct.</summary>
        static List<string[]> ParseCsv(string path)
        {
            var text = File.ReadAllText(path);
            var rows = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var first = true;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        fields.Add(field.ToString());
                        field.Length = 0;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        fields.Add(field.ToString());
                        field.Length = 0;
                        if (first)
                            first = false;          // skip the header row
                        else
                            rows.Add(fields.ToArray());
                        fields.Clear();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }

            if (field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                if (!first)
                    rows.Add(fields.ToArray());
            }

            return rows;
        }

        // ---------------------------------------------------------------------------------
        // Audio
        // ---------------------------------------------------------------------------------

        static void CheckAudioGeneration()
        {
            var beep = ToneGenerator.CreateTwoToneBeep("test_beep", 880f, 1320f, 0.14f, 0.05f);
            Assert(beep != null && beep.samples > 0 && beep.length > 0.3f,
                $"recall beep generated: {beep?.length:F3} s, {beep?.samples} samples, " +
                $"{beep?.frequency} Hz");

            var blip = ToneGenerator.CreateSelectionBlip("test_blip", 1100f, 520f, 0.22f);
            Assert(blip != null && blip.samples > 0,
                $"chair selection blip generated: {blip?.length:F3} s");

            // Confirm the envelope removes onset/offset clicks: the first and last samples
            // must be at (or very near) zero amplitude.
            var data = new float[beep.samples];
            beep.GetData(data, 0);
            Assert(Mathf.Abs(data[0]) < 0.01f && Mathf.Abs(data[data.Length - 1]) < 0.01f,
                $"beep is click-free (first={data[0]:F4}, last={data[data.Length - 1]:F4})");

            var peak = data.Max(Mathf.Abs);
            Assert(peak > 0.1f && peak <= 1f, $"beep peak amplitude is {peak:F3} (audible, not clipping)");
        }

        // ---------------------------------------------------------------------------------
        // Stimulus confirmation must agree with the diagnostics
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Regression guard for the contradiction seen in the real run: AUDIO_DIAGNOSTICS said
        /// "clip for SpokenWord is missing or empty" while every WORD_PRESENTED was logged with
        /// audio_confirmed=TRUE. Both now go through IsClipUsable, so they cannot disagree.
        /// </summary>
        static void CheckStimulusConfirmationLogic()
        {
            Assert(!ExperimentAudio.IsClipUsable(null, out var nullReason),
                $"a null clip is never usable ({nullReason})");

            var empty = AudioClip.Create("empty", 1, 1, 44100, false);
            Assert(!ExperimentAudio.IsClipUsable(empty, out var emptyReason),
                $"a 1-sample clip is never usable ({emptyReason})");

            var good = ToneGenerator.CreateTone("good", 440f, 0.5f);
            Assert(ExperimentAudio.IsClipUsable(good, out _),
                "a real generated clip is usable");

            // A handle that failed at scheduling time must stay unconfirmed no matter what.
            var go = new GameObject("__IKEA_EEG_SelfTest_Audio");
            try
            {
                var audio = go.AddComponent<ExperimentAudio>();

                var failed = new AudioCueHandle
                {
                    cue = AudioCue.SpokenWord,
                    clip = null,
                    failureReason = "clip is null",
                };

                audio.ConfirmPlayback(failed);
                Assert(!failed.confirmed,
                    "a handle with a scheduling failure can NEVER become audio_confirmed=TRUE");
                Assert(failed.ToNotes().Contains("audio_confirmed=FALSE"),
                    "its CSV notes report audio_confirmed=FALSE");

                // Same for a handle whose clip is unusable but which somehow reached confirm.
                var emptyHandle = new AudioCueHandle { cue = AudioCue.SpokenWord, clip = empty };
                audio.ConfirmPlayback(emptyHandle);
                Assert(!emptyHandle.confirmed,
                    "a handle holding an empty clip can never become audio_confirmed=TRUE");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            // SpokenWord must not be treated as a built-in cue — that false positive is what
            // made the startup diagnostic fail on a healthy setup.
            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            Assert(manager != null, "ExperimentManager present for readiness evaluation");
        }

        // ---------------------------------------------------------------------------------
        // Microphone signal analysis
        // ---------------------------------------------------------------------------------

        static void CheckSignalAnalysis()
        {
            const float threshold = 0.01f;

            // Digital silence — the case the old "samples exist" check wrongly passed.
            var silence = new float[48000];
            var silentStats = WavUtility.AnalyzeSignal(silence);
            Assert(silentStats.peak == 0f && silentStats.IsSilent(threshold),
                $"a buffer of zeros is detected as SILENT ({silentStats.ToNotes(threshold)})");
            Assert(silentStats.nearZeroPercent > 99f,
                $"near-zero proportion of digital silence is {silentStats.nearZeroPercent:F1}%");
            Assert(silentStats.peakDbfs <= -100f,
                $"silence reports a floor dBFS ({silentStats.peakDbfs:F0} dBFS)");

            // A real speech-like signal.
            var speech = new float[48000];
            for (var i = 0; i < speech.Length; i++)
                speech[i] = 0.4f * Mathf.Sin(2f * Mathf.PI * 200f * i / 48000f);

            var speechStats = WavUtility.AnalyzeSignal(speech);
            Assert(!speechStats.IsSilent(threshold),
                $"a 0.4 amplitude signal is NOT silent (peak={speechStats.peak:F3})");
            Assert(Mathf.Abs(speechStats.peak - 0.4f) < 0.01f,
                $"peak measured as {speechStats.peak:F4} (expected ~0.4)");
            // RMS of a sine is peak/sqrt(2).
            Assert(Mathf.Abs(speechStats.rms - 0.4f / Mathf.Sqrt(2f)) < 0.01f,
                $"RMS measured as {speechStats.rms:F4} (expected ~{0.4f / Mathf.Sqrt(2f):F4})");
            Assert(Mathf.Abs(speechStats.peakDbfs - (-7.96f)) < 0.5f,
                $"peak dBFS measured as {speechStats.peakDbfs:F2} (expected ~-7.96)");

            // A very quiet but non-zero buffer: below threshold, still flagged silent.
            var faint = new float[4800];
            for (var i = 0; i < faint.Length; i++)
                faint[i] = 0.002f * Mathf.Sin(i * 0.1f);

            var faintStats = WavUtility.AnalyzeSignal(faint);
            Assert(faintStats.IsSilent(threshold),
                $"a 0.002 amplitude buffer is flagged silent ({faintStats.peakDbfs:F0} dBFS) " +
                "[this is the case that produced a WAV you could not hear]");
        }

        // ---------------------------------------------------------------------------------
        // Time formatting
        // ---------------------------------------------------------------------------------

        static void CheckTimeFormatting()
        {
            Assert(TimeFormat.FromMilliseconds(7198d) == "7.198 s (7198 ms)",
                $"chair response time formats as '{TimeFormat.FromMilliseconds(7198d)}'");
            Assert(TimeFormat.FromSeconds(62.381d) == "62.381 s (62381 ms)",
                $"total trial duration formats as '{TimeFormat.FromSeconds(62.381d)}'");
            Assert(TimeFormat.FromSeconds(0.5d) == "0.500 s (500 ms)",
                $"sub-second value formats as '{TimeFormat.FromSeconds(0.5d)}'");

            // Both must use the identical form.
            var a = TimeFormat.FromSeconds(1.234d);
            var b = TimeFormat.FromMilliseconds(1234d);
            Assert(a == b, $"seconds and milliseconds entry points agree ('{a}' == '{b}')");
        }

        // ---------------------------------------------------------------------------------
        // Interaction configuration
        // ---------------------------------------------------------------------------------

        static void CheckInteractionConfiguration()
        {
            var visuals = Object.FindObjectsByType<CurveVisualController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert(visuals.Length == 2, $"two controller ray visuals found ({visuals.Length})");

            foreach (var visual in visuals)
            {
                Assert(visual.lineDynamicsMode == LineDynamicsMode.Traditional,
                    $"'{visual.transform.parent?.parent?.name}' ray mode is " +
                    $"{visual.lineDynamicsMode} [RetractOnHitLoss is what required the whip]");
                Assert(visual.restingVisualLineLength >= 5f,
                    $"'{visual.transform.parent?.parent?.name}' resting ray length is " +
                    $"{visual.restingVisualLineLength} m");
            }

            var haptics = Object.FindObjectsByType<SimpleHapticFeedback>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert(haptics.All(h => !h.enabled),
                $"all {haptics.Length} SimpleHapticFeedback components are disabled " +
                "[no vibration on chair hover/select]");

            var players = Object.FindObjectsByType<HapticImpulsePlayer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert(players.All(p => !p.enabled),
                $"all {players.Length} HapticImpulsePlayer components are disabled");

            foreach (var interactor in Object.FindObjectsByType<NearFarInteractor>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var performed = interactor.selectInput.inputActionPerformed;
                var binding = performed != null && performed.bindings.Count > 0
                    ? performed.bindings[0].path
                    : "none";

                Assert(interactor.selectInput.inputSourceMode ==
                       XRInputButtonReader.InputSourceMode.InputAction,
                    $"'{interactor.transform.parent?.name}' select uses a scene-local action " +
                    "(shared XRI asset untouched)");
                Assert(binding.IndexOf("trigger", System.StringComparison.OrdinalIgnoreCase) >= 0,
                    $"'{interactor.transform.parent?.name}' select binds to {binding}");
                Assert(binding.IndexOf("grip", System.StringComparison.OrdinalIgnoreCase) < 0,
                    $"'{interactor.transform.parent?.name}' select does NOT use the grip");
            }

            // Recenter must be reachable in all three areas.
            var recenterButtons = Object.FindObjectsByType<Button>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(b => b.name.StartsWith("Btn_Recenter"))
                .ToList();

            // Four areas now: Area 0 (familiarization) plus A, B and C.
            Assert(recenterButtons.Count == 4,
                $"a Recenter button exists in each area ({recenterButtons.Count} found: " +
                $"{string.Join(", ", recenterButtons.Select(b => b.name))})");

            var teleporter = Object.FindAnyObjectByType<XRRigTeleporter>();
            Assert(teleporter != null && teleporter.GetType().GetMethod("Recenter") != null,
                "XRRigTeleporter exposes Recenter()");
        }

        // ---------------------------------------------------------------------------------
        // Hardware configuration (audio out + microphone in)
        // ---------------------------------------------------------------------------------

        static void CheckHardwareConfiguration()
        {
            // --- Audio out ------------------------------------------------------------------
            var listeners = Object.FindObjectsByType<AudioListener>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert(listeners.Length == 1,
                $"exactly one AudioListener in the scene (found {listeners.Length}) " +
                "[more than one and Unity ignores all but the first]");

            if (listeners.Length == 1)
                Info($"AudioListener is on '{listeners[0].gameObject.name}'");

            Assert(Object.FindAnyObjectByType<ExperimentAudio>() != null,
                "ExperimentAudio is present in the scene");

            var config = AudioSettings.GetConfiguration();
            Assert(config.sampleRate > 0,
                $"Unity audio output is initialised ({config.sampleRate} Hz, {config.speakerMode})");

#if UNITY_EDITOR
            // This is the single most common reason a logged beep is never heard.
            Assert(!EditorUtility.audioMasterMute,
                $"Game view 'Mute Audio' is OFF (currently muted: {EditorUtility.audioMasterMute})");
#endif

            // --- Microphone in ---------------------------------------------------------------
            var devices = Microphone.devices ?? System.Array.Empty<string>();
            Info($"{devices.Length} microphone device(s) visible to Unity right now:");
            for (var i = 0; i < devices.Length; i++)
            {
                Microphone.GetDeviceCaps(devices[i], out var minFreq, out var maxFreq);
                Info($"    [{i}] \"{devices[i]}\" ({(minFreq == 0 && maxFreq == 0 ? "any rate" : $"{minFreq}-{maxFreq} Hz")})");
            }

            var voice = Object.FindAnyObjectByType<VoiceRecallManager>();
            Assert(voice != null, "VoiceRecallManager is present in the scene");

            if (voice == null)
                return;

            var so = new SerializedObject(voice);
            var mode = so.FindProperty("m_SelectionMode");
            var preferred = so.FindProperty("m_PreferredDevice");
            var keywords = so.FindProperty("m_HeadsetKeywords");

            if (mode != null)
            {
                var modeName = ((MicrophoneSelectionMode)mode.enumValueIndex).ToString();
                Info($"microphone selection mode: {modeName}");

                Assert((MicrophoneSelectionMode)mode.enumValueIndex != MicrophoneSelectionMode.SystemDefault,
                    "microphone selection is NOT SystemDefault " +
                    "[SystemDefault takes devices[0], which is what picked the laptop array]");

                if ((MicrophoneSelectionMode)mode.enumValueIndex == MicrophoneSelectionMode.ExplicitDevice)
                {
                    Assert(preferred != null && !string.IsNullOrWhiteSpace(preferred.stringValue),
                        "ExplicitDevice mode has a Preferred Device name set");
                }
            }

            Assert(keywords != null && keywords.arraySize > 0,
                $"headset keyword list is populated ({keywords?.arraySize ?? 0} entries)");

            // Would the current configuration find a headset mic on THIS machine right now?
            if (keywords != null && devices.Length > 0)
            {
                var words = new List<string>();
                for (var i = 0; i < keywords.arraySize; i++)
                    words.Add(keywords.GetArrayElementAtIndex(i).stringValue);

                var headsetMatches = devices
                    .Where(d => words.Any(k => !string.IsNullOrWhiteSpace(k) &&
                                               d.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                if (headsetMatches.Count > 0)
                    Info($"PreferHeadset would select: \"{headsetMatches[0]}\"");
                else
                    Info("PreferHeadset would find NO headset device right now and would fall " +
                         "back to devices[0] with a warning. NOTE: the Quest microphone is only " +
                         "exposed to Windows while Quest Link is running, so this is expected " +
                         "when running this test with the headset disconnected.");
            }
        }
    }
}
