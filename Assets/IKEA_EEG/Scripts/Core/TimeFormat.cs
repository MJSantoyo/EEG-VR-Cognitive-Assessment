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

        /// <summary>
        /// A LONG duration, for a person: "7 min 24 s".
        ///
        /// WHY THIS IS SEPARATE. The form above is right for a reaction time — milliseconds are
        /// the unit that measurement is made and reported in. It is wrong for a whole session:
        /// "444.512 s (444512 ms)" asks the reader to do arithmetic to learn that the test took
        /// about seven and a half minutes, and the millisecond figure carries no information at
        /// that scale.
        ///
        /// PRESENTATION ONLY. Nothing that is measured, logged or analysed passes through here.
        /// Reaction times, timestamps and every CSV value keep their existing units and
        /// precision untouched — this formats one line on one screen.
        /// </summary>
        public static string MinutesAndSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0d)
                return "0 min 0 s";

            // TRUNCATED, not rounded. 444.512 s is "7 min 24 s": a duration that has not yet
            // reached its 25th second should not be reported as having done so, and rounding can
            // push a value across a minute boundary it never crossed.
            var whole = (int)seconds;
            var minutes = whole / 60;
            var remainder = whole % 60;

            // Under a minute there is nothing to gain from a leading "0 min".
            return minutes <= 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} s", remainder)
                : string.Format(CultureInfo.InvariantCulture, "{0} min {1} s", minutes, remainder);
        }

        /// <summary>Formats a duration given in MILLISECONDS as "X.XXX s (XXXX ms)".</summary>
        public static string FromMilliseconds(double milliseconds)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:F3} s ({1:F0} ms)", milliseconds / 1000d, milliseconds);
        }
    }
}
