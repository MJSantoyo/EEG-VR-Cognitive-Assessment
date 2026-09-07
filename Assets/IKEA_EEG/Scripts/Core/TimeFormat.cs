using System.Globalization;

namespace IkeaEeg.Core
{
    /// <summary>
    /// One place that decides how a duration is shown to the participant/researcher.
    ///
    /// Every on-screen time uses the same form — "X.XXX s (XXXX ms)" — so the chair response
    /// time and the total trial duration are directly comparable instead of being reported in
    /// different units. This is presentation only: the CSV keeps full raw precision
    /// (response_time_ms to 0.1 ms, timestamps to 1 µs) and never goes through here.
    /// </summary>
    public static class TimeFormat
    {
        /// <summary>Formats a duration given in SECONDS as "X.XXX s (XXXX ms)".</summary>
        public static string FromSeconds(double seconds)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:F3} s ({1:F0} ms)", seconds, seconds * 1000d);
        }

        /// <summary>Formats a duration given in MILLISECONDS as "X.XXX s (XXXX ms)".</summary>
        public static string FromMilliseconds(double milliseconds)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:F3} s ({1:F0} ms)", milliseconds / 1000d, milliseconds);
        }
    }
}
