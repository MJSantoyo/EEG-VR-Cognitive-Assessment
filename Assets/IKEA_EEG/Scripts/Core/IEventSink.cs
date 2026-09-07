using System;

namespace IkeaEeg.Core
{
    /// <summary>
    /// Metadata handed to every sink when a session begins.
    /// </summary>
    public class SessionContext
    {
        public string sessionId;
        public DateTime sessionStartLocal;
        public string sessionDirectory;   // absolute path, already created on disk
    }

    /// <summary>
    /// ======================= THE LSL INTEGRATION POINT =======================
    ///
    /// Every experimental event is published to every registered IEventSink.
    /// The CSV writer and the Console logger are just two sinks.
    ///
    /// To add EEG/LSL support later you write ONE new class:
    ///
    ///     public class LslMarkerSink : MonoBehaviour, IEventSink
    ///     {
    ///         StreamOutlet m_Outlet;
    ///         public void Initialize(SessionContext ctx) { ...create LSL outlet... }
    ///         public void OnEvent(ExperimentEvent e)     { m_Outlet.push_sample(new[]{ e.eventType }); }
    ///         public void Shutdown()                     { ...dispose... }
    ///     }
    ///
    /// ...add it as a component on the EventLogger GameObject, and it is picked up
    /// automatically. NO experiment code changes. The marker strings pushed to LSL are the
    /// same <see cref="EventTypes"/> constants that appear in the CSV, so the CSV and the
    /// EEG stream align on identical labels.
    /// ========================================================================
    /// </summary>
    public interface IEventSink
    {
        /// <summary>Called once at SESSION_START, before any event is delivered.</summary>
        void Initialize(SessionContext context);

        /// <summary>Called for every experimental event, in publication order.</summary>
        void OnEvent(ExperimentEvent evt);

        /// <summary>Called at SESSION_END and on application quit. Must be idempotent.</summary>
        void Shutdown();
    }
}
