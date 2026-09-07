using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace IkeaEeg.Audio
{
    /// <summary>The auditory stimuli and feedback sounds the experiment can produce.</summary>
    public enum AudioCue
    {
        RecallBeep,
        WordCue,
        InstructionCue,
        ChairSelection,

        /// <summary>
        /// A spoken target word. The clip is supplied per word by the WordListDefinition
        /// rather than held on this component, because the word list is configurable.
        /// </summary>
        SpokenWord,

        /// <summary>
        /// The short beep marking the change to the next VISUAL encoding word.
        ///
        /// A transition cue, not a stimulus: it says "a new word is on screen now" and carries
        /// no content. Kept deliberately brief and quiet — it fires once per word, and anything
        /// longer would either mask the next transition or become the salient event instead of
        /// the word. It must never be confused with RecallBeep, which is a protocol instruction
        /// meaning "start speaking".
        /// </summary>
        StimulusTransition,

        /// <summary>
        /// Confirmation that a recognition response was registered.
        ///
        /// REGISTRATION ONLY, NEVER CORRECTNESS. It is identical for every answer, so it cannot
        /// tell the participant whether they were right. A correctness cue here would turn a
        /// memory test into a learning trial and contaminate the delayed phase, so the sound is
        /// deliberately the same for hits, misses, correct rejections and false alarms.
        /// </summary>
        ResponseConfirm,
    }

    /// <summary>
    /// Who owns a sound, and therefore what is allowed to stop it.
    ///
    /// This is the whole of the cancellation policy. A room change, a RESTART or an ABORT stops
    /// the sounds that BELONG to the phase being left — never a blanket stop of every
    /// AudioSource, which would be able to cut a memory word in half mid-encoding.
    /// </summary>
    public enum AudioOwnership
    {
        /// <summary>
        /// EXPERIMENT-CRITICAL. Memory words, the recall beep, selection feedback, the
        /// instruction cue. These carry the protocol itself and are never cancelled by a room
        /// change. Only tearing the run down (ABORT / RESTART / END) reaches them, and then only
        /// because the run itself is over.
        /// </summary>
        Cognitive,

        /// <summary>
        /// ROOM/PHASE-OWNED NARRATION. The spoken familiarization instructions, the practice
        /// lead-in and the practice colour prompt. Purely instructional, carries no stimulus, and
        /// must stop the instant the participant leaves the phase that was speaking.
        /// </summary>
        Narration,
    }

    /// <summary>
    /// A scheduled auditory stimulus, and whether it actually reached the participant.
    ///
    /// This exists because "we called Play()" and "the participant heard it" are NOT the same
    /// claim. A muted Game view, a zero-volume or missing AudioListener, or an output device
    /// that is not the headset all produce a perfectly happy AudioSource and total silence in
    /// the headset. For an EEG-bound experiment, logging a stimulus onset that never occurred
    /// is worse than logging nothing.
    /// </summary>
    public class AudioCueHandle
    {
        public AudioCue cue;
        public AudioSource source;
        public AudioClip clip;

        /// <summary>Absolute DSP time at which playback was scheduled to begin.</summary>
        public double scheduledDspTime;

        /// <summary>DSP time at which onset was actually observed.</summary>
        public double confirmedDspTime;

        /// <summary>True only when playback was verified to be audible.</summary>
        public bool confirmed;

        /// <summary>Populated when <see cref="confirmed"/> is false.</summary>
        public string failureReason = string.Empty;

        public float clipLength => clip != null ? clip.length : 0f;

        public string ToNotes()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "cue={0}; audio_confirmed={1}; clip={2}; clip_samples={3}; clip_rate={4}; " +
                "dsp_scheduled={5:F4}; dsp_confirmed={6:F4}; clip_length_s={7:F3}; " +
                "source={8}; source_volume={9:F2}; source_spatialBlend={10:F2}; source_mute={11}; " +
                "unity_playback_only=TRUE{12}",
                cue,
                confirmed ? "TRUE" : "FALSE",
                clip != null ? clip.name : "NULL",
                clip != null ? clip.samples : 0,
                clip != null ? clip.frequency : 0,
                scheduledDspTime,
                confirmedDspTime,
                clipLength,
                source != null ? source.gameObject.name : "NULL",
                source != null ? source.volume : 0f,
                source != null ? source.spatialBlend : -1f,
                source != null && source.mute,
                string.IsNullOrEmpty(failureReason) ? string.Empty : $"; reason={failureReason}");
        }
    }

    /// <summary>
    /// The experiment's auditory layer.
    ///
    /// Two responsibilities beyond "play a sound":
    ///   1. schedule cues on the DSP clock so stimulus onset is sample-accurate rather than
    ///      frame-quantised;
    ///   2. verify that the output path is actually capable of delivering sound, and report
    ///      loudly when it is not.
    ///
    /// Clips left unassigned are generated procedurally (see <see cref="ToneGenerator"/>).
    /// Assigning a real recorded/TTS clip in the Inspector replaces a generated one without
    /// any call-site change.
    /// </summary>
    [DisallowMultipleComponent]
    public class ExperimentAudio : MonoBehaviour
    {
        [Header("Sources")]
        [Tooltip("Voices used for scheduled cues. More than one so overlapping cues never cut " +
                 "each other off. 2D (spatialBlend 0) so a cue is identical wherever the " +
                 "participant stands.")]
        [SerializeField] int m_SourcePoolSize = 3;

        [Header("Clips (leave empty to generate procedurally)")]
        [SerializeField] AudioClip m_RecallBeep;
        [SerializeField] AudioClip m_WordCue;
        [SerializeField] AudioClip m_ChairSelectionFeedback;
        [SerializeField] AudioClip m_InstructionCue;

        [Tooltip("Short beep marking the change to the next VISUAL encoding word. Generated if left empty.")]
        [SerializeField] AudioClip m_StimulusTransition;

        [Tooltip("Confirms a recognition response was registered. The SAME sound for every answer - it must never signal correctness. Generated if left empty.")]
        [SerializeField] AudioClip m_ResponseConfirm;

        [Header("Volumes")]
        [Range(0f, 1f)][SerializeField] float m_CueVolume = 0.85f;
        [Range(0f, 1f)][SerializeField] float m_FeedbackVolume = 0.75f;

        [Tooltip("Level for the spoken target words. These carry the task content, so they " +
                 "are played at full level.")]
        [Range(0f, 1f)][SerializeField] float m_SpokenWordVolume = 1f;

        [Header("Scheduling")]
        [Tooltip("Lead time between scheduling a cue and its onset. Must exceed one audio " +
                 "buffer or the scheduled start is missed and playback begins late.")]
        [SerializeField] double m_ScheduleLeadSeconds = 0.08;

        [Header("Integrity")]
        [Tooltip("Log an error and mark stimuli unheard when the output path looks silent.")]
        [SerializeField] bool m_VerifyPlayback = true;

        readonly List<AudioSource> m_Sources = new List<AudioSource>();
        int m_NextSource;

        /// <summary>
        /// The voices currently carrying phase-owned narration, and which phase owns each.
        ///
        /// Only these can be stopped by <see cref="StopNarration"/>. A voice playing a memory
        /// word or a beep never appears here, which is what makes the cancellation
        /// ownership-aware rather than a blanket Stop().
        /// </summary>
        readonly List<(AudioSource source, AudioClip clip, string owner)> m_NarrationVoices =
            new List<(AudioSource, AudioClip, string)>();

        string m_LastHealthProblem = string.Empty;
        AudioListener m_CachedListener;

        /// <summary>False when the audio output path cannot deliver sound to the participant.</summary>
        public bool outputHealthy { get; private set; } = true;

        /// <summary>Human-readable description of the audio path, for the log and the CSV.</summary>
        public string diagnosticsSummary { get; private set; } = string.Empty;

        public AudioClip recallBeep => m_RecallBeep;

        void Awake()
        {
            EnsureSources();
            EnsureClips();
        }

        void Start()
        {
            // Deferred to Start so the XR rig (and therefore its AudioListener) exists.
            // Console reporting is left to the ExperimentManager so the message appears once,
            // alongside the microphone report, and reaches the CSV.
            RunDiagnostics(logToConsole: false);
        }

        // ---------------------------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------------------------

        void EnsureSources()
        {
            var count = Mathf.Max(1, m_SourcePoolSize);
            for (var i = 0; i < count; i++)
            {
                var go = new GameObject($"Cue Source {i + 1}");
                go.transform.SetParent(transform, false);

                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.volume = 1f;
                source.mute = false;
                source.bypassEffects = true;
                source.bypassListenerEffects = true;
                source.bypassReverbZones = true;
                source.priority = 0;   // stimuli must never be voice-stolen

                m_Sources.Add(source);
            }
        }

        void EnsureClips()
        {
            if (m_RecallBeep == null)
                m_RecallBeep = ToneGenerator.CreateTwoToneBeep("IKEA_RecallBeep", 880f, 1320f, 0.14f, 0.05f);

            if (m_WordCue == null)
                m_WordCue = ToneGenerator.CreateTone("IKEA_WordCue", 660f, 0.09f, 0.35f);

            if (m_ChairSelectionFeedback == null)
                m_ChairSelectionFeedback = ToneGenerator.CreateSelectionBlip(
                    "IKEA_ChairSelect", 1100f, 520f, 0.22f);

            if (m_InstructionCue == null)
                m_InstructionCue = ToneGenerator.CreateTone("IKEA_InstructionCue", 520f, 0.12f, 0.3f);

            // Deliberately shorter and quieter than every other cue. It fires once per encoding
            // word, so length here is a cost paid 15 times: 60 ms is enough to be noticed as a
            // transition and short enough never to overlap the next one or compete with the
            // word itself for attention. A higher pitch than the instruction cue keeps the two
            // distinguishable without being sharp.
            if (m_StimulusTransition == null)
                m_StimulusTransition = ToneGenerator.CreateTone(
                    "IKEA_StimulusTransition", 990f, 0.06f, 0.22f);

            // A single soft downward blip. IDENTICAL for every response, by design: this
            // confirms registration and must not leak correctness (see AudioCue.ResponseConfirm).
            if (m_ResponseConfirm == null)
                m_ResponseConfirm = ToneGenerator.CreateSelectionBlip(
                    "IKEA_ResponseConfirm", 880f, 620f, 0.10f);
        }

        AudioClip ClipFor(AudioCue cue)
        {
            switch (cue)
            {
                case AudioCue.RecallBeep: return m_RecallBeep;
                case AudioCue.WordCue: return m_WordCue;
                case AudioCue.InstructionCue: return m_InstructionCue;
                case AudioCue.ChairSelection: return m_ChairSelectionFeedback;
                case AudioCue.StimulusTransition: return m_StimulusTransition;
                case AudioCue.ResponseConfirm: return m_ResponseConfirm;
                default: return null;
            }
        }

        /// <summary>
        /// True for cues whose clip lives on THIS component.
        ///
        /// SpokenWord is excluded: its clip is supplied per call from the configurable
        /// WordListDefinition. Treating it as a built-in cue is what previously made the
        /// startup diagnostic report "clip for SpokenWord is missing or empty" on a perfectly
        /// healthy setup — ClipFor(SpokenWord) is null BY DESIGN.
        /// </summary>
        static bool IsBuiltInCue(AudioCue cue)
        {
            return cue != AudioCue.SpokenWord;
        }

        /// <summary>
        /// The single definition of "this clip can actually be heard". Used by the startup
        /// inventory, by the diagnostics and by playback confirmation, so those three can
        /// never disagree with each other.
        /// </summary>
        public static bool IsClipUsable(AudioClip clip, out string reason)
        {
            if (clip == null)
            {
                reason = "clip is null";
                return false;
            }

            if (clip.samples <= 0)
            {
                reason = $"clip '{clip.name}' has 0 samples";
                return false;
            }

            if (clip.length <= 0.01f)
            {
                reason = $"clip '{clip.name}' is {clip.length:F3} s long";
                return false;
            }

            if (clip.loadState == AudioDataLoadState.Failed)
            {
                reason = $"clip '{clip.name}' failed to load";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>Full description of a clip, for the startup inventory log.</summary>
        public static string DescribeClip(AudioClip clip)
        {
            if (clip == null)
                return "NULL";

            return $"'{clip.name}' length={clip.length:F3}s samples={clip.samples} " +
                   $"rate={clip.frequency}Hz channels={clip.channels} loadState={clip.loadState}";
        }

        /// <summary>Full description of the voice a cue will be played on.</summary>
        public string DescribeSource(int index = 0)
        {
            if (m_Sources.Count == 0)
                return "no cue AudioSource exists";

            var source = m_Sources[Mathf.Clamp(index, 0, m_Sources.Count - 1)];
            if (source == null)
                return "cue AudioSource is null";

            return $"source='{source.gameObject.name}' volume={source.volume:F2} " +
                   $"mute={source.mute} spatialBlend={source.spatialBlend:F2} " +
                   $"enabled={source.isActiveAndEnabled} priority={source.priority} " +
                   $"outputMixer={(source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "none")}";
        }

        float VolumeFor(AudioCue cue)
        {
            switch (cue)
            {
                case AudioCue.ChairSelection: return m_FeedbackVolume;

                // Spoken words carry the task content and must be comfortably intelligible,
                // so they get their own level rather than sharing the beep's.
                case AudioCue.SpokenWord: return m_SpokenWordVolume;

                default: return m_CueVolume;
            }
        }

        // ---------------------------------------------------------------------------------
        // Output-path diagnostics
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Inspects every link in the chain
        /// AudioClip → AudioSource → AudioListener → Unity output device → Windows/Link.
        ///
        /// Unity can only see up to its own output device: whether Windows is routing that
        /// device to the headset or to the laptop speakers is outside the process. That last
        /// hop is called out explicitly in the report rather than silently assumed.
        /// </summary>
        public bool RunDiagnostics(bool logToConsole)
        {
            var sb = new StringBuilder();
            var problems = new List<string>();

            // --- AudioListener -------------------------------------------------------------
            var listeners = FindObjectsByType<AudioListener>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var activeListeners = 0;
            foreach (var l in listeners)
            {
                if (!l.isActiveAndEnabled)
                    continue;

                activeListeners++;
                m_CachedListener = l;
            }

            sb.Append($"AudioListener: {listeners.Length} in scene, {activeListeners} active. ");

            if (activeListeners == 0)
                problems.Add("no active AudioListener (nothing can be heard)");
            else if (activeListeners > 1)
                problems.Add($"{activeListeners} active AudioListeners (Unity uses one and warns)");

            if (AudioListener.volume <= 0.0001f)
                problems.Add($"AudioListener.volume is {AudioListener.volume}");

            if (AudioListener.pause)
                problems.Add("AudioListener.pause is true");

            sb.Append($"listenerVolume={AudioListener.volume:F2}, paused={AudioListener.pause}. ");

            // --- Unity audio output ---------------------------------------------------------
            var config = AudioSettings.GetConfiguration();
            sb.Append($"output: {config.sampleRate} Hz, {config.speakerMode}, " +
                      $"dspBuffer={config.dspBufferSize}, realVoices={config.numRealVoices}. ");

            if (config.sampleRate <= 0)
                problems.Add("Unity audio output is not initialised (sampleRate 0) — audio may be disabled in Project Settings ▸ Audio");

            if (AudioSettings.speakerMode == AudioSpeakerMode.Mono && config.sampleRate <= 0)
                problems.Add("no usable speaker mode");

            // A DSP clock that never advances means the audio thread is not running at all.
            sb.Append($"dspTime={AudioSettings.dspTime:F3}. ");

            // --- Editor mute ----------------------------------------------------------------
#if UNITY_EDITOR
            if (UnityEditor.EditorUtility.audioMasterMute)
            {
                problems.Add("the Game view 'Mute Audio' toggle is ON — Unity is muting all " +
                             "audio in Play mode (this is the single most common cause of " +
                             "'the beep is logged but I hear nothing')");
            }

            sb.Append($"editorMuteAudio={UnityEditor.EditorUtility.audioMasterMute}. ");
#endif

            // --- Sources and clips ----------------------------------------------------------
            var badSources = 0;
            foreach (var s in m_Sources)
            {
                if (s == null || !s.isActiveAndEnabled || s.mute || s.volume <= 0.0001f)
                    badSources++;
            }

            if (badSources > 0)
                problems.Add($"{badSources} cue AudioSource(s) are muted, silent or inactive");

            sb.Append($"cueSources={m_Sources.Count}. ");

            foreach (AudioCue cue in System.Enum.GetValues(typeof(AudioCue)))
            {
                // Only the cues this component owns. SpokenWord clips come from the word list
                // and are inventoried separately by the ExperimentManager.
                if (!IsBuiltInCue(cue))
                    continue;

                if (!IsClipUsable(ClipFor(cue), out var clipReason))
                    problems.Add($"cue {cue}: {clipReason}");
            }

            // Per-source detail, so a muted or misconfigured voice is visible in the log.
            for (var i = 0; i < m_Sources.Count; i++)
                sb.Append(DescribeSource(i)).Append(". ");

            outputHealthy = problems.Count == 0;
            m_LastHealthProblem = problems.Count > 0 ? string.Join("; ", problems) : string.Empty;

            if (!outputHealthy)
                sb.Append($"PROBLEMS: {m_LastHealthProblem}");
            else
                sb.Append("no problems detected inside Unity.");

            diagnosticsSummary = sb.ToString();

            if (logToConsole)
            {
                if (outputHealthy)
                {
                    Debug.Log($"[IKEA_EEG] Audio path OK. {diagnosticsSummary}\n" +
                              "[IKEA_EEG] NOTE: Unity cannot see past its own output device. " +
                              "If you still hear nothing in the headset, check that the Windows " +
                              "default playback device is the Quest/Link headset and not the " +
                              "laptop speakers.");
                }
                else
                {
                    Debug.LogError($"[IKEA_EEG] AUDIO OUTPUT PROBLEM — auditory stimuli will " +
                                   $"NOT reach the participant.\n{diagnosticsSummary}");
                }
            }

            return outputHealthy;
        }

        // ---------------------------------------------------------------------------------
        // Scheduled playback
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Schedules a cue on the DSP clock and returns a handle whose onset can be awaited
        /// and verified. Always returns a handle, even on failure.
        /// </summary>
        public AudioCueHandle PlayCue(AudioCue cue)
        {
            return PlayClip(cue, ClipFor(cue));
        }

        /// <summary>
        /// Schedules an explicitly supplied clip — used for the spoken target words, whose
        /// audio lives in the configurable WordListDefinition, not on this component.
        /// Goes through exactly the same scheduling and confirmation path as a built-in cue,
        /// so a spoken word gets the same onset guarantees as a beep.
        /// </summary>
        /// <param name="ownership">
        /// Whether this sound belongs to the protocol (default, never cancelled by a room
        /// change) or to a room/phase's instructional narration. Defaulting to Cognitive is
        /// deliberate: forgetting to classify a sound must leave it PROTECTED, never
        /// cancellable.
        /// </param>
        /// <param name="owner">
        /// The phase that owns a Narration sound, for the log — e.g. "AREA_0_FAMILIARIZATION".
        /// </param>
        public AudioCueHandle PlayClip(AudioCue cue, AudioClip clip,
            AudioOwnership ownership = AudioOwnership.Cognitive, string owner = "")
        {
            var handle = new AudioCueHandle
            {
                cue = cue,
                clip = clip,
            };

            // Hard gate: an unusable clip fails here and the handle can never be confirmed
            // later, no matter what the AudioSource reports.
            if (!IsClipUsable(clip, out var clipReason))
            {
                handle.failureReason = cue == AudioCue.SpokenWord
                    ? $"{clipReason} — no spoken recording for this word. Run " +
                      "IKEA_EEG > Generate Spoken Word Clips (local TTS)"
                    : clipReason;
                handle.scheduledDspTime = AudioSettings.dspTime;
                Debug.LogError($"[IKEA_EEG] Cannot play {cue}: {handle.failureReason}.");
                return handle;
            }

            var source = NextSource();
            if (source == null)
            {
                handle.failureReason = "no AudioSource available";
                handle.scheduledDspTime = AudioSettings.dspTime;
                Debug.LogError($"[IKEA_EEG] Cannot play {cue}: {handle.failureReason}.");
                return handle;
            }

            handle.source = source;
            source.clip = handle.clip;
            source.volume = VolumeFor(cue);

            // PlayScheduled rather than Play/PlayOneShot: onset is placed on the audio thread's
            // own clock, so it is not quantised to the frame in which we happened to call it.
            handle.scheduledDspTime = AudioSettings.dspTime + m_ScheduleLeadSeconds;
            source.PlayScheduled(handle.scheduledDspTime);

            if (ownership == AudioOwnership.Narration)
                RegisterNarrationVoice(source, handle.clip, owner);

            return handle;
        }

        // ---------------------------------------------------------------------------------
        // Narration ownership and cancellation
        // ---------------------------------------------------------------------------------

        void RegisterNarrationVoice(AudioSource source, AudioClip clip, string owner)
        {
            // A voice is round-robined, so the same AudioSource can be handed out again for a
            // later segment. One entry per source: the newest claim wins.
            for (var i = m_NarrationVoices.Count - 1; i >= 0; i--)
            {
                if (m_NarrationVoices[i].source == source)
                    m_NarrationVoices.RemoveAt(i);
            }

            m_NarrationVoices.Add((source, clip,
                string.IsNullOrEmpty(owner) ? "unspecified" : owner));
        }

        /// <summary>
        /// How many voices are currently CLAIMED by narration.
        ///
        /// Distinct from <see cref="narrationPlaying"/>, which asks whether sound is actually
        /// coming out: this is the ownership bookkeeping, and it is what the headless self test
        /// can verify (an AudioSource does not really play outside Play mode, but the claim and
        /// its release are ordinary logic).
        /// </summary>
        public int narrationVoiceCount => m_NarrationVoices.Count;

        /// <summary>True while a phase-owned narration clip is audible.</summary>
        public bool narrationPlaying
        {
            get
            {
                for (var i = 0; i < m_NarrationVoices.Count; i++)
                {
                    var entry = m_NarrationVoices[i];
                    if (entry.source != null && entry.source.isPlaying &&
                        entry.source.clip == entry.clip)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Stops every phase-owned narration voice IMMEDIATELY and returns how many were
        /// actually silenced.
        ///
        /// WHAT IT DOES NOT TOUCH: any voice not registered as narration. A memory word, the
        /// recall beep and the chair-selection feedback are Cognitive and are never in this
        /// list, so normal Area A encoding and the recall beep are unaffected no matter how
        /// often this is called. That is the point of the ownership tag — a blanket
        /// AudioSource.Stop() over the pool would cut a stimulus in half.
        ///
        /// A voice that has been reused for a non-narration cue since it was registered is
        /// detected by the clip check and left alone.
        /// </summary>
        public int StopNarration(string reason)
        {
            var stopped = 0;
            var owners = new List<string>();

            for (var i = 0; i < m_NarrationVoices.Count; i++)
            {
                var entry = m_NarrationVoices[i];

                if (entry.source == null)
                    continue;

                // The voice was recycled for something else; it is no longer ours to stop.
                if (entry.source.clip != entry.clip)
                    continue;

                if (entry.source.isPlaying)
                {
                    stopped++;
                    owners.Add(entry.owner);
                }

                // Unconditional: Stop() also cancels a PlayScheduled that has not begun yet,
                // which is the window in which "ABORT silenced nothing, then it started talking"
                // would otherwise be possible.
                entry.source.Stop();
            }

            m_NarrationVoices.Clear();

            if (stopped > 0)
            {
                Debug.Log($"[IKEA_EEG] Narration stopped ({reason}): {stopped} clip(s) from " +
                          $"{string.Join(", ", owners)}. Experiment-critical audio was not " +
                          "touched.");
            }

            return stopped;
        }

        /// <summary>
        /// Allocation-free health check used at stimulus onset.
        ///
        /// The full <see cref="RunDiagnostics"/> does a scene-wide FindObjectsByType, which is
        /// exactly the kind of frame spike that must not happen in the moments around a
        /// stimulus marker. This variant only touches cached references and static state.
        /// </summary>
        bool QuickHealthCheck(out string problem)
        {
            if (m_CachedListener == null || !m_CachedListener.isActiveAndEnabled)
            {
                problem = "no active AudioListener";
                return false;
            }

            if (AudioListener.volume <= 0.0001f)
            {
                problem = $"AudioListener.volume is {AudioListener.volume}";
                return false;
            }

            if (AudioListener.pause)
            {
                problem = "AudioListener.pause is true";
                return false;
            }

#if UNITY_EDITOR
            if (UnityEditor.EditorUtility.audioMasterMute)
            {
                problem = "the Game view 'Mute Audio' toggle is ON";
                return false;
            }
#endif

            problem = string.Empty;
            return true;
        }

        /// <summary>True once the scheduled onset time has passed on the DSP clock.</summary>
        public bool HasOnsetPassed(AudioCueHandle handle)
        {
            return handle != null && AudioSettings.dspTime >= handle.scheduledDspTime;
        }

        /// <summary>
        /// Decides whether the cue actually reached the participant. Call once, just after
        /// <see cref="HasOnsetPassed"/> becomes true.
        /// </summary>
        public void ConfirmPlayback(AudioCueHandle handle)
        {
            if (handle == null)
                return;

            handle.confirmedDspTime = AudioSettings.dspTime;

            // Any failure recorded at scheduling time is permanent. This is the guard that
            // makes it impossible for a missing clip to end up logged as audio_confirmed=TRUE.
            if (!string.IsNullOrEmpty(handle.failureReason))
            {
                handle.confirmed = false;
                return;
            }

            // Re-validate the clip itself, not just the output path: same source of truth as
            // the startup inventory and the diagnostics.
            if (!IsClipUsable(handle.clip, out var clipReason))
            {
                handle.confirmed = false;
                handle.failureReason = clipReason;
                Debug.LogError($"[IKEA_EEG] AUDITORY STIMULUS NOT DELIVERED ({handle.cue}): {clipReason}.");
                return;
            }

            if (!m_VerifyPlayback)
            {
                handle.confirmed = true;
                return;
            }

            // Re-check the output path at the moment of the stimulus: the researcher can mute
            // the Game view or unplug a device mid-session.
            if (!QuickHealthCheck(out var problem))
            {
                handle.confirmed = false;
                handle.failureReason = problem;
                outputHealthy = false;
                m_LastHealthProblem = problem;
            }
            else if (handle.source == null || !handle.source.isPlaying)
            {
                // The clip is long enough that it must still be playing one frame after onset;
                // if it is not, the scheduled start was missed or the voice was stolen.
                handle.confirmed = false;
                handle.failureReason = "AudioSource is not playing after the scheduled onset";
            }
            else if (handle.source.clip != handle.clip)
            {
                // The voice was reused for a different cue before this one was confirmed.
                handle.confirmed = false;
                handle.failureReason = $"AudioSource is playing '{handle.source.clip?.name}' " +
                                       $"instead of '{handle.clip.name}'";
            }
            else
            {
                handle.confirmed = true;
            }

            if (!handle.confirmed)
            {
                Debug.LogError($"[IKEA_EEG] AUDITORY STIMULUS NOT DELIVERED ({handle.cue}): " +
                               $"{handle.failureReason}. The corresponding onset event is " +
                               "logged with audio_confirmed=FALSE — treat this trial as " +
                               "compromised for any auditory analysis.");
            }
        }

        /// <summary>Length of the recall beep, so the manager can wait it out precisely.</summary>
        public float RecallBeepLength()
        {
            return m_RecallBeep != null ? m_RecallBeep.length : 0.35f;
        }

        public double scheduleLeadSeconds => m_ScheduleLeadSeconds;

        AudioSource NextSource()
        {
            if (m_Sources.Count == 0)
                return null;

            // Round-robin, preferring a source that is not currently busy.
            for (var i = 0; i < m_Sources.Count; i++)
            {
                var candidate = m_Sources[(m_NextSource + i) % m_Sources.Count];
                if (candidate != null && !candidate.isPlaying)
                {
                    m_NextSource = (m_NextSource + i + 1) % m_Sources.Count;
                    return candidate;
                }
            }

            var fallback = m_Sources[m_NextSource];
            m_NextSource = (m_NextSource + 1) % m_Sources.Count;
            return fallback;
        }
    }
}
