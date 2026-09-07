using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using IkeaEeg.Core;

namespace IkeaEeg.Data
{
    /// <summary>State of the marker outlet. Written verbatim into the LSL_STATUS event.</summary>
    public enum LslSinkState
    {
        /// <summary>Marker sending is switched off in the Inspector.</summary>
        Disabled,

        /// <summary>Sending is on, but no usable LSL library/outlet exists. Nothing is sent.</summary>
        Unavailable,

        /// <summary>A real outlet exists. Markers pushed through it are genuinely transmitted.</summary>
        Active,
    }

    /// <summary>
    /// Transmits every experimental event as an LSL marker, so an EEG recording can be
    /// aligned to the experiment without post-hoc guesswork.
    ///
    ///     ExperimentManager / ChairSelectionTask / VoiceRecallManager
    ///              |
    ///        EventLogger.Log(EventTypes.X, ...)
    ///              |
    ///          EventBus.Publish
    ///              |
    ///        +-----+-----+--------------------+
    ///        v           v                    v
    ///   CsvEventSink  UnityConsoleEventSink  LslMarkerSink  <-- YOU ARE HERE
    ///
    /// HONESTY CONTRACT — the rules this class is built around:
    ///   * it NEVER simulates LSL. If the library or the native binary is missing, the state is
    ///     Unavailable, <see cref="markersPushed"/> stays 0, and the session log says so;
    ///   * "marker sent" is only ever counted when liblsl actually accepted the sample;
    ///   * an LSL failure can never end a behavioural session. Every call path here is guarded,
    ///     and a push that throws disables further pushes rather than propagating into the
    ///     experiment.
    ///
    /// The marker string always BEGINS with the same <see cref="EventTypes"/> constant that
    /// appears in the CSV's event_type column, so the marker stream and the behavioural file
    /// join on identical labels.
    /// </summary>
    [DisallowMultipleComponent]
    public class LslMarkerSink : MonoBehaviour, IEventSink
    {
        // NOTE: this field must NOT be called "m_Enabled". MonoBehaviour already inherits a
        // serialized "m_Enabled" from Behaviour (the component's own enabled checkbox), and
        // declaring a second one makes Unity log, every time the component is serialized:
        //   "The same field name is serialized multiple times in the class or its parent
        //    class. This is not supported: Base(MonoBehaviour) m_Enabled"
        [Tooltip("Attempt to open a real LSL outlet at session start. With no LSL library " +
                 "present this logs LSL_UNAVAILABLE and the session continues normally.")]
        [SerializeField] bool m_SendMarkers = true;

        [Header("Stream metadata")]
        [Tooltip("Name the outlet advertises. Recorders (LabRecorder) resolve streams by this.")]
        [SerializeField] string m_StreamName = "IKEA_EEG_Markers";

        [Tooltip("LSL stream type. 'Markers' is the conventional value for event markers.")]
        [SerializeField] string m_StreamType = "Markers";

        [Tooltip("Stable source id. Lets a recorder recognise a restarted stream as the same " +
                 "source. The session id is advertised separately, in the marker payloads.")]
        [SerializeField] string m_SourceId = "IKEA_EEG_Unity_Markers";

        [Header("Payload")]
        [Tooltip("Append a compact key=value tail to the event name, e.g. " +
                 "WORD_PRESENTED|word_index=2|word=Copper. The event name itself never changes.")]
        [SerializeField] bool m_IncludeCompactPayload = true;

        [Tooltip("Hard cap on marker length. Long free-text notes are never sent; the CSV is " +
                 "where the detail belongs.")]
        [SerializeField] int m_MaxMarkerLength = 256;

        [Header("Diagnostics")]
        [Tooltip("Print every pushed marker to the Console. Development only — it is one log " +
                 "line per experimental event.")]
        [SerializeField] bool m_LogEachMarker;

        object m_Outlet;
        readonly string[] m_Sample = new string[1];
        readonly StringBuilder m_Builder = new StringBuilder(128);

        LslSinkState m_State = LslSinkState.Disabled;
        string m_StateDetail = "not initialised";
        int m_MarkersPushed;
        int m_MarkersFailed;
        bool m_PushDisabledAfterFailure;
        string m_SessionId = string.Empty;

        public string streamName => m_StreamName;
        public string streamType => m_StreamType;
        public string sourceId => m_SourceId;

        /// <summary>Current state. Never Active unless a real outlet was created.</summary>
        public LslSinkState state => m_State;

        /// <summary>Why the sink is in its current state — library detail, or a native error.</summary>
        public string stateDetail => m_StateDetail;

        /// <summary>Markers liblsl actually accepted. Stays 0 when LSL is unavailable.</summary>
        public int markersPushed => m_MarkersPushed;

        public int markersFailed => m_MarkersFailed;

        /// <summary>True only when a real outlet exists.</summary>
        public bool isTransmitting => m_State == LslSinkState.Active && m_Outlet != null;

        public void Initialize(SessionContext context)
        {
            Shutdown();

            m_SessionId = context != null ? context.sessionId : string.Empty;
            m_MarkersPushed = 0;
            m_MarkersFailed = 0;
            m_PushDisabledAfterFailure = false;

            if (!m_SendMarkers)
            {
                m_State = LslSinkState.Disabled;
                m_StateDetail = "Send Markers is off in the Inspector";
                Debug.Log($"[IKEA_EEG] LSL marker sink DISABLED — {m_StateDetail}.");
                return;
            }

            m_Outlet = LslBinding.CreateOutlet(m_StreamName, m_StreamType, m_SourceId,
                out var detail);

            if (m_Outlet == null)
            {
                m_State = LslSinkState.Unavailable;
                m_StateDetail = detail;

                // A warning, not an error: this is an expected, survivable configuration.
                Debug.LogWarning(
                    "[IKEA_EEG] LSL_UNAVAILABLE — no markers will be transmitted this session.\n" +
                    $"    reason: {detail}\n" +
                    "    The experiment continues normally and CSV/WAV data is unaffected.\n" +
                    "    To enable LSL see Assets/IKEA_EEG/Documentation/EEG_LSL_PIPELINE.md");
                return;
            }

            m_State = LslSinkState.Active;
            m_StateDetail = detail;

            Debug.Log(
                "[IKEA_EEG] ===== LSL OUTLET CREATED =====\n" +
                $"    stream name:   {m_StreamName}\n" +
                $"    stream type:   {m_StreamType}\n" +
                $"    source id:     {m_SourceId}\n" +
                $"    channels:      1 (string)\n" +
                $"    sampling rate: irregular ({LslBinding.IrregularRate})\n" +
                $"    library:       {LslBinding.boundAssembly}\n" +
                $"    session:       {m_SessionId}\n" +
                "===============================");
        }

        public void OnEvent(ExperimentEvent evt)
        {
            if (!isTransmitting || m_PushDisabledAfterFailure || evt == null)
                return;

            m_Sample[0] = BuildMarker(evt);

            if (LslBinding.PushSample(m_Outlet, m_Sample, out var error))
            {
                m_MarkersPushed++;

                if (m_LogEachMarker)
                    Debug.Log($"[IKEA_EEG] LSL marker #{m_MarkersPushed}: {m_Sample[0]}");

                return;
            }

            m_MarkersFailed++;

            // One failure is reported; after that the sink goes quiet rather than spamming the
            // Console once per event for the rest of the session.
            if (!m_PushDisabledAfterFailure)
            {
                m_PushDisabledAfterFailure = true;
                m_State = LslSinkState.Unavailable;
                m_StateDetail = $"push failed after {m_MarkersPushed} marker(s): {error}";

                Debug.LogWarning($"[IKEA_EEG] LSL push FAILED — marker transmission stopped for " +
                                 $"this session. {m_StateDetail}. The behavioural session continues.");
            }
        }

        public void Shutdown()
        {
            if (m_Outlet != null)
            {
                Debug.Log($"[IKEA_EEG] LSL outlet closed. {m_MarkersPushed} marker(s) transmitted, " +
                          $"{m_MarkersFailed} failed.");

                LslBinding.Dispose(m_Outlet);
                m_Outlet = null;
            }

            if (m_State == LslSinkState.Active)
                m_State = LslSinkState.Disabled;
        }

        // ---------------------------------------------------------------------------------
        // Marker format
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Builds the marker string.
        ///
        /// FORMAT:  EVENT_TYPE[|key=value]...
        ///
        /// The first token is ALWAYS the unmodified EventTypes constant, so a recording can be
        /// segmented on event names alone and stays readable in LabRecorder. The optional tail
        /// carries only the few identifiers that make a marker self-describing — trial index,
        /// word index, chair, correctness — never free-text notes, which belong in the CSV.
        ///
        /// Examples:
        ///     SESSION_START|seed=1234567
        ///     WORD_PRESENTED|word_index=2|word=Copper
        ///     CHAIR_SELECTED|trial=2|chair=Chair_04|correct=1
        /// </summary>
        public string BuildMarker(ExperimentEvent evt)
        {
            if (!m_IncludeCompactPayload)
                return evt.eventType;

            m_Builder.Clear();
            m_Builder.Append(evt.eventType);

            AppendField("trial", evt.chairTrialIndex);
            AppendField("diff", ShortDifficulty(evt.difficulty));
            AppendField("word_index", evt.wordIndex);
            AppendField("word", evt.expectedWord);
            AppendField("phase", evt.recallPhase);
            AppendField("chair", evt.objectId);

            if (!string.IsNullOrEmpty(evt.correct))
                AppendField("correct", evt.correct == "TRUE" ? "1" : "0");

            if (!string.IsNullOrEmpty(evt.responseTimeMs))
                AppendField("rt_ms", evt.responseTimeMs);

            if (evt.eventType == EventTypes.SessionStart)
                AppendField("seed", evt.randomizationSeed);

            var marker = m_Builder.ToString();

            return marker.Length <= m_MaxMarkerLength
                ? marker
                : marker.Substring(0, m_MaxMarkerLength);
        }

        void AppendField(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            m_Builder.Append('|').Append(key).Append('=');

            // '|' and '=' are the payload's only structural characters, so they are the only
            // ones that need neutralising. Everything else is passed through unchanged.
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                m_Builder.Append(c == '|' || c == '=' ? '_' : c);
            }
        }

        static string ShortDifficulty(string difficulty)
        {
            return string.IsNullOrEmpty(difficulty)
                ? string.Empty
                : difficulty.ToUpperInvariant();
        }

        /// <summary>Status line for the LSL_STATUS event and the researcher summary.</summary>
        public string DescribeStatus()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "lsl_state={0}; stream_name={1}; stream_type={2}; source_id={3}; " +
                "channel_count=1; channel_format=string; nominal_srate=0; library={4}; " +
                "markers_pushed={5}; detail={6}",
                m_State.ToString().ToUpperInvariant(), m_StreamName, m_StreamType, m_SourceId,
                string.IsNullOrEmpty(LslBinding.boundAssembly) ? "none" : LslBinding.boundAssembly,
                m_MarkersPushed, m_StateDetail);
        }

        /// <summary>Used by the scene builder and the self test.</summary>
        public void Configure(bool sendMarkers, string streamName, string streamType, string sourceId)
        {
            m_SendMarkers = sendMarkers;
            m_StreamName = streamName;
            m_StreamType = streamType;
            m_SourceId = sourceId;
        }

        void OnDestroy()
        {
            Shutdown();
        }
    }
}
