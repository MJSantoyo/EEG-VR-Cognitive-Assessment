using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace IkeaEeg.Core
{
    /// <summary>
    /// The centralised logging layer. EVERY experimental event goes through this component —
    /// no subsystem writes to a file, and no subsystem invents timestamps of its own.
    ///
    /// Responsibilities:
    ///   * owns the session clock (the single time base),
    ///   * owns session_id / trial_id and the elapsed trial timer,
    ///   * stamps the current experiment state and room onto every event,
    ///   * publishes each event to every registered <see cref="IEventSink"/>.
    ///
    /// Sinks are auto-discovered from components on this same GameObject, which is how a
    /// future LSL marker sender gets wired in without touching experiment code.
    /// </summary>
    [DisallowMultipleComponent]
    public class EventLogger : MonoBehaviour
    {
        [Header("Session folder")]
        [Tooltip("Sub-folder created inside Application.persistentDataPath.")]
        [SerializeField] string m_RootFolderName = "IKEA_EEG_Data";

        [Header("Debug")]
        [Tooltip("Also print every event to the Unity Console via the console sink.")]
        [SerializeField] bool m_VerboseConsole = true;

        readonly EventBus m_Bus = new EventBus();
        readonly SessionClock m_Clock = new SessionClock();
        readonly IntervalTimer m_TrialTimer = new IntervalTimer();
        readonly ExperimentEvent m_Scratch = new ExperimentEvent();

        int m_TrialCounter;
        bool m_SessionActive;

        public static EventLogger Instance { get; private set; }

        public EventBus bus => m_Bus;
        public SessionClock clock => m_Clock;

        /// <summary>
        /// Unique id of THIS RUN. One run = one Area A→B→C pass = one CSV = one audio folder.
        /// Also written to the CSV's session_id column, which is what it has always meant.
        /// </summary>
        public string sessionId { get; private set; } = string.Empty;

        /// <summary>
        /// Identifier shared by every run of one participant sitting. NEW TRIAL keeps it; a
        /// fresh application start or a RESTART begins a new one.
        ///
        /// This is what will later let consecutive runs be analysed together — it exists now so
        /// the data being collected already carries the relationship, rather than needing it
        /// reconstructed from timestamps afterwards.
        /// </summary>
        public string experimentSessionId { get; private set; } = string.Empty;

        /// <summary>1-based position of this run within the experiment session. NEW TRIAL increments it.</summary>
        public int runIndex { get; private set; }

        /// <summary>Same value as <see cref="sessionId"/>, under the name the data model uses.</summary>
        public string runSessionId => sessionId;
        public string trialId { get; private set; } = string.Empty;
        public string sessionDirectory { get; private set; } = string.Empty;

        /// <summary>
        /// Name of the data folder inside persistentDataPath. Exposed so the RESTART discard
        /// guard can prove a path is inside the data root before touching it.
        /// </summary>
        public string rootFolderName => m_RootFolderName;
        public bool sessionActive => m_SessionActive;
        public bool verboseConsole => m_VerboseConsole;

        /// <summary>Set by the ExperimentManager whenever the state machine advances.</summary>
        public string currentState { get; set; } = "Idle";

        /// <summary>Set by the ExperimentManager whenever the participant changes area.</summary>
        public string currentRoom { get; set; } = RoomNames.None;

        /// <summary>
        /// Randomisation seed of the current session, stamped onto every row.
        ///
        /// It lives here rather than being passed per call so that no event can ever be written
        /// without it: reproducing a session means reading one cell of any row of its CSV.
        /// Assign it BEFORE <see cref="BeginSession"/> so SESSION_START carries it too.
        /// </summary>
        public long randomizationSeed { get; set; }

        /// <summary>
        /// True once a developer action has interfered with this run — an area jump from the
        /// developer menu, for instance.
        ///
        /// Stamped onto EVERY row from that point on, and never cleared within a run, so a
        /// developer-interrupted run cannot later be mistaken for a clean participant run. Only
        /// starting a new run clears it.
        /// </summary>
        public bool developerInterrupted { get; private set; }

        /// <summary>Marks this run as developer-interrupted. One-way within a run.</summary>
        public void MarkDeveloperInterrupted(string reason)
        {
            if (developerInterrupted)
                return;

            developerInterrupted = true;

            Debug.LogWarning($"[IKEA_EEG] RUN MARKED DEVELOPER_INTERRUPTED — {reason}. " +
                             "This run no longer represents a valid participant protocol run.");
        }

        /// <summary>Seconds since SESSION_START. Used for the session-level duration metric.</summary>
        public double ElapsedSessionSeconds() => m_Clock.RelativeSeconds();

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[IKEA_EEG] A second EventLogger was found and disabled. " +
                                 "There must be exactly one per scene.");
                enabled = false;
                return;
            }

            Instance = this;

            // Any IEventSink sitting on this GameObject is registered automatically.
            // This is the extension point: drop an LslMarkerSink component here and it works.
            var sinkComponents = GetComponents<IEventSink>();
            foreach (var sink in sinkComponents)
                m_Bus.Register(sink);
        }

        void OnDestroy()
        {
            if (Instance == this)
            {
                EndSession("OnDestroy");
                Instance = null;
            }
        }

        void OnApplicationQuit()
        {
            EndSession("OnApplicationQuit");
        }

        // ---------------------------------------------------------------------------------
        // Session lifecycle
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Starts a new EXPERIMENT SESSION: a fresh participant sitting. Resets the run counter
        /// so the next run is run 1. Call before <see cref="BeginSession"/>.
        /// </summary>
        public void BeginExperimentSession()
        {
            experimentSessionId = $"E_{DateTime.Now:yyyyMMdd_HHmmss}_" +
                                  $"{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            runIndex = 0;

            Debug.Log($"[IKEA_EEG] ===== EXPERIMENT SESSION {experimentSessionId} =====\n" +
                      "[IKEA_EEG] Runs started from here share this id; each run still gets its " +
                      "own run_session_id, CSV and audio folder.");
        }

        public void BeginSession()
        {
            if (m_SessionActive)
                return;

            // A run always belongs to an experiment session. If none was opened explicitly,
            // open one now so no data is ever written with a blank experiment_session_id.
            if (string.IsNullOrEmpty(experimentSessionId))
                BeginExperimentSession();

            runIndex++;

            m_Clock.StartSession();
            m_TrialCounter = 0;
            trialId = string.Empty;
            m_TrialTimer.Reset();

            // A new run starts clean: developer interference does not carry across runs.
            developerInterrupted = false;

            // The run id carries its run number, so a folder listing reads in run order and no
            // two runs of one sitting can ever collide on disk.
            sessionId = BuildSessionId(m_Clock.sessionStartLocal, runIndex);
            sessionDirectory = Path.Combine(
                Application.persistentDataPath, m_RootFolderName, sessionId);

            try
            {
                Directory.CreateDirectory(sessionDirectory);
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Could not create session directory " +
                               $"'{sessionDirectory}': {e.Message}");
            }

            var context = new SessionContext
            {
                sessionId = sessionId,
                sessionStartLocal = m_Clock.sessionStartLocal,
                sessionDirectory = sessionDirectory,
            };

            m_SessionActive = true;
            m_Bus.InitializeSinks(context);

            Debug.Log($"[IKEA_EEG] ===== SESSION {sessionId} =====\n" +
                      $"[IKEA_EEG] Data folder: {sessionDirectory}\n" +
                      $"[IKEA_EEG] High-resolution timer available: {SessionClock.isHighResolution}");

            Log(EventTypes.SessionStart, e =>
            {
                e.notes = $"persistentDataPath={Application.persistentDataPath}; " +
                          $"highResTimer={SessionClock.isHighResolution}; " +
                          $"unity={Application.unityVersion}; " +
                          $"randomization_seed={randomizationSeed.ToString(CultureInfo.InvariantCulture)}";
            });
        }

        public void EndSession(string reason = "")
        {
            if (!m_SessionActive)
                return;

            Log(EventTypes.SessionEnd, e => e.notes = reason);

            m_SessionActive = false;
            m_TrialTimer.Stop();
            m_Bus.ShutdownSinks();

            Debug.Log($"[IKEA_EEG] ===== SESSION {sessionId} ENDED ({reason}) =====\n" +
                      $"[IKEA_EEG] CSV written to: {sessionDirectory}");
        }

        // ---------------------------------------------------------------------------------
        // Trial lifecycle
        // ---------------------------------------------------------------------------------

        /// <summary>Allocates the next trial id and restarts the elapsed-trial timer.</summary>
        public void BeginTrial()
        {
            m_TrialCounter++;
            trialId = $"T{m_TrialCounter:D3}";
            m_TrialTimer.Restart();

            Log(EventTypes.TrialStart);
        }

        public void EndTrial()
        {
            Log(EventTypes.TrialEnd, e =>
                e.notes = $"trial_duration_ms={m_TrialTimer.ElapsedMilliseconds().ToString("F1", CultureInfo.InvariantCulture)}");

            m_TrialTimer.Stop();
        }

        /// <summary>Seconds since the current TRIAL_START; 0 if no trial has begun.</summary>
        public double ElapsedTrialSeconds()
        {
            return m_TrialTimer.hasStarted ? m_TrialTimer.ElapsedSeconds() : 0d;
        }

        public double ElapsedTrialMilliseconds()
        {
            return m_TrialTimer.hasStarted ? m_TrialTimer.ElapsedMilliseconds() : 0d;
        }

        // ---------------------------------------------------------------------------------
        // Logging
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Records one experimental event.
        /// </summary>
        /// <param name="eventType">One of the <see cref="EventTypes"/> constants.</param>
        /// <param name="configure">
        /// Optional callback that fills in the columns relevant to this event
        /// (chair attributes, word index, transcript, ...). Everything else stays empty.
        /// </param>
        public void Log(string eventType, Action<ExperimentEvent> configure = null)
        {
            // The scratch instance is reused: sinks must consume the event synchronously
            // (they do — Publish is synchronous) or copy what they need.
            m_Scratch.Clear();

            m_Scratch.eventType = eventType;
            m_Scratch.timestampAbsolute = m_Clock.AbsoluteNowString();
            m_Scratch.timestampRelative = m_Clock.RelativeSeconds();
            m_Scratch.sessionId = sessionId;
            m_Scratch.trialId = trialId;
            m_Scratch.experimentState = currentState;
            m_Scratch.room = currentRoom;
            m_Scratch.elapsedTrialTime = m_TrialTimer.hasStarted
                ? m_TrialTimer.ElapsedSeconds().ToString("F4", CultureInfo.InvariantCulture)
                : string.Empty;
            m_Scratch.randomizationSeed = randomizationSeed.ToString(CultureInfo.InvariantCulture);
            m_Scratch.experimentSessionId = experimentSessionId;
            m_Scratch.runIndex = runIndex > 0
                ? runIndex.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            m_Scratch.developerInterrupted = developerInterrupted ? "TRUE" : "FALSE";

            // The platform language is read from the localization service rather than stored
            // here, so the column can never disagree with what the participant is actually
            // being shown.
            m_Scratch.platformLanguage = Localization.ExperimentLocalization.languageCode;

            // The EEG-alignment timestamp, stamped on EVERY row at the moment the event is
            // logged — the same instant the behavioural timestamp above refers to.
            //
            // Read from liblsl's own clock, which is the clock the EEG samples carry. It is
            // stamped here rather than at each call site so no event can be added later that
            // forgets it, and it is left EMPTY when liblsl is absent rather than filled from
            // any other clock: a number here that did not come from local_clock() would
            // misalign every epoch cut against it.
            m_Scratch.lslTimestamp = Data.LslClock.NowString();

            configure?.Invoke(m_Scratch);

            m_Bus.Publish(m_Scratch);
        }

        static string BuildSessionId(DateTime startLocal, int runIndex)
        {
            // Date-ordered and unique even if two runs start in the same second. The run number
            // is embedded so the folder name alone identifies which run of a sitting it is.
            return $"S_{startLocal:yyyyMMdd_HHmmss}_r{runIndex:D2}_" +
                   $"{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }
    }
}
