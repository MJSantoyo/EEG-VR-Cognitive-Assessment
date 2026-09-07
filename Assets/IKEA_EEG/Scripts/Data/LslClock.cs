using System;
using System.Globalization;
using System.Reflection;

namespace IkeaEeg.Data
{
    /// <summary>
    /// liblsl's clock, cached for use on every logged event.
    ///
    /// WHY IT EXISTS SEPARATELY FROM <see cref="LslBinding"/>: this is called once per
    /// experiment event, so the reflection lookup is resolved ONCE and reused. Re-resolving a
    /// MethodInfo on every row would put reflection cost on the logging path for no reason.
    ///
    /// WHAT IT IS FOR: the single time base shared by the experiment and the EEG. LSL stamps
    /// every EEG sample with local_clock(); stamping events with the same function is what makes
    /// "the 400 ms after WORD_PRESENTED" a range that can actually be cut out of the signal.
    ///
    /// WHAT IT IS NOT: a replacement for anything. The session clock still drives every
    /// behavioural timestamp and every reaction time, unchanged. This is an ADDITIONAL reading,
    /// and when liblsl is absent it returns "unavailable" rather than substituting Time.time or
    /// DateTime.Now — a plausible number from the wrong clock would misalign every epoch while
    /// looking perfectly healthy.
    /// </summary>
    public static class LslClock
    {
        static bool s_Resolved;
        static MethodInfo s_LocalClock;

        /// <summary>True when liblsl's clock is available on this machine.</summary>
        public static bool isAvailable
        {
            get
            {
                Resolve();
                return s_LocalClock != null;
            }
        }

        /// <summary>
        /// The current LSL time in seconds, or NaN when liblsl is not available.
        ///
        /// NaN rather than 0: zero is a value the clock could in principle report, and it would
        /// be indistinguishable from "we could not read it".
        /// </summary>
        public static double Now()
        {
            Resolve();

            if (s_LocalClock == null)
                return double.NaN;

            try
            {
                return s_LocalClock.Invoke(null, Array.Empty<object>()) is double now
                    ? now
                    : double.NaN;
            }
            catch
            {
                return double.NaN;
            }
        }

        /// <summary>
        /// The current LSL time formatted for the CSV, or EMPTY when unavailable.
        ///
        /// Six decimals: LSL timestamps are seconds with sub-millisecond resolution, and the
        /// EEG intervals this will be compared against are a few milliseconds apart.
        /// </summary>
        public static string NowString()
        {
            var now = Now();

            return double.IsNaN(now)
                ? string.Empty
                : now.ToString("F6", CultureInfo.InvariantCulture);
        }

        static void Resolve()
        {
            if (s_Resolved)
                return;

            s_Resolved = true;

            if (!LslBinding.isAvailable)
                return;

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;

                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        types = Array.FindAll(e.Types, t => t != null);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var type in types)
                    {
                        if (type.Name != "LSL")
                            continue;

                        var method = type.GetMethod("local_clock",
                            BindingFlags.Public | BindingFlags.Static);

                        if (method != null && method.ReturnType == typeof(double) &&
                            method.GetParameters().Length == 0)
                        {
                            s_LocalClock = method;
                            return;
                        }
                    }
                }
            }
            catch
            {
                s_LocalClock = null;
            }
        }

        /// <summary>Test hook: forces the next call to re-resolve.</summary>
        public static void ResetForTesting()
        {
            s_Resolved = false;
            s_LocalClock = null;
        }
    }
}
