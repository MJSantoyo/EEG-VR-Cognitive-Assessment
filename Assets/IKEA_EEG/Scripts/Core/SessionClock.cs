using System;
using System.Diagnostics;
using System.Globalization;

namespace IkeaEeg.Core
{
    /// <summary>
    /// The single source of time for the whole experiment.
    ///
    /// Uses a <see cref="Stopwatch"/> (monotonic, high resolution) for all relative
    /// timing, anchored once to a wall-clock <see cref="DateTime"/> at session start.
    /// This is what makes the log usable for EEG alignment later: Time.time is frame-quantised
    /// and DateTime.Now drifts/steps, neither is acceptable as a millisecond time base.
    /// </summary>
    public class SessionClock
    {
        const string k_TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

        readonly Stopwatch m_Stopwatch = new Stopwatch();
        DateTime m_AnchorLocal;

        /// <summary>Wall-clock time at which the session clock was started.</summary>
        public DateTime sessionStartLocal => m_AnchorLocal;

        public bool isRunning => m_Stopwatch.IsRunning;

        /// <summary>True when the platform provides a true high-resolution timer.</summary>
        public static bool isHighResolution => Stopwatch.IsHighResolution;

        public void StartSession()
        {
            m_AnchorLocal = DateTime.Now;
            m_Stopwatch.Restart();
        }

        /// <summary>Seconds since <see cref="StartSession"/>, sub-millisecond resolution.</summary>
        public double RelativeSeconds()
        {
            return m_Stopwatch.Elapsed.TotalSeconds;
        }

        /// <summary>Milliseconds since <see cref="StartSession"/>.</summary>
        public double RelativeMilliseconds()
        {
            return m_Stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Current wall-clock time, reconstructed from the anchor + monotonic elapsed time
        /// so that it can never disagree with <see cref="RelativeSeconds"/>.
        /// </summary>
        public DateTime AbsoluteNow()
        {
            return m_AnchorLocal + m_Stopwatch.Elapsed;
        }

        public string AbsoluteNowString()
        {
            return AbsoluteNow().ToString(k_TimestampFormat, CultureInfo.InvariantCulture);
        }

        public static string FormatTimestamp(DateTime value)
        {
            return value.ToString(k_TimestampFormat, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Independent stopwatch used for measuring one interval, e.g. the chair response time
    /// or the elapsed trial time. Kept separate from the session clock so it can be
    /// started/stopped/reset without disturbing the session time base.
    /// </summary>
    public class IntervalTimer
    {
        readonly Stopwatch m_Stopwatch = new Stopwatch();

        public bool isRunning => m_Stopwatch.IsRunning;
        public bool hasStarted { get; private set; }

        public void Restart()
        {
            hasStarted = true;
            m_Stopwatch.Restart();
        }

        public void Stop()
        {
            m_Stopwatch.Stop();
        }

        public void Reset()
        {
            hasStarted = false;
            m_Stopwatch.Reset();
        }

        public double ElapsedMilliseconds()
        {
            return m_Stopwatch.Elapsed.TotalMilliseconds;
        }

        public double ElapsedSeconds()
        {
            return m_Stopwatch.Elapsed.TotalSeconds;
        }
    }
}
