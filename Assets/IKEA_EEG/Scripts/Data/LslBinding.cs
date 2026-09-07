using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Late-bound bridge to the Lab Streaming Layer C# API.
    ///
    /// WHY REFLECTION AND NOT A DIRECT REFERENCE:
    /// This project must compile, build and run in the headset whether or not liblsl is
    /// installed — a missing EEG library may not be allowed to stop a behavioural session. A
    /// direct `using LSL;` would make the whole assembly fail to compile until someone imports
    /// the package, and would put an unconditional native dependency into the Quest build.
    /// Binding by reflection at run time means:
    ///
    ///   * with liblsl absent  -> the project compiles, the sink reports LSL_UNAVAILABLE, and
    ///                            the experiment runs and saves CSV/WAV exactly as before;
    ///   * with liblsl present -> a REAL outlet is created and real markers are pushed, with
    ///                            no code change and no rebuild of anything else.
    ///
    /// It deliberately supports both layouts the C# binding has shipped in:
    ///   LSL.StreamInfo / LSL.StreamOutlet          (liblsl-Csharp 1.13+, LSL4Unity current)
    ///   LSL.liblsl.StreamInfo / .StreamOutlet      (older LSL4Unity bundles)
    ///
    /// NOTHING HERE SIMULATES LSL. Every method returns an honest failure when the library is
    /// not there; no code path fabricates a transmission or a timestamp.
    /// </summary>
    public static class LslBinding
    {
        /// <summary>LSL's constant for a stream with no fixed sampling rate.</summary>
        public const double IrregularRate = 0.0;

        static bool s_Attempted;
        static bool s_Available;
        static string s_Detail = "not probed yet";

        static Type s_StreamInfoType;
        static Type s_StreamOutletType;
        static Type s_StreamInletType;
        static Type s_ChannelFormatType;
        static object s_ChannelFormatString;
        static ConstructorInfo s_StreamInfoCtor;
        static ConstructorInfo s_StreamOutletCtor;
        static ConstructorInfo s_StreamInletCtor;
        static MethodInfo s_PushSample;
        static MethodInfo s_PullSample;
        static MethodInfo s_ResolveStream;
        static MethodInfo s_HaveConsumers;

        /// <summary>True when a usable LSL C# API was found in the loaded assemblies.</summary>
        public static bool isAvailable
        {
            get
            {
                Probe();
                return s_Available;
            }
        }

        /// <summary>Human-readable outcome of the probe, for the log and the CSV.</summary>
        public static string detail
        {
            get
            {
                Probe();
                return s_Detail;
            }
        }

        /// <summary>Assembly-qualified name of the API that was bound, or empty.</summary>
        public static string boundAssembly =>
            isAvailable && s_StreamOutletType != null
                ? s_StreamOutletType.Assembly.GetName().Name
                : string.Empty;

        /// <summary>Re-runs the probe. Only useful in the Editor after importing the package.</summary>
        public static void ResetProbe()
        {
            s_Attempted = false;
            s_Available = false;
            s_Detail = "not probed yet";
        }

        static void Probe()
        {
            if (s_Attempted)
                return;

            s_Attempted = true;

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
                        // A half-loadable assembly must not abort the search.
                        types = e.Types.Where(t => t != null).ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    var outlet = types.FirstOrDefault(t =>
                        t.Name == "StreamOutlet" &&
                        t.GetMethods().Any(m => m.Name == "push_sample"));

                    if (outlet == null)
                        continue;

                    if (TryBindTypes(types, outlet, out var problem))
                        return;

                    s_Detail = $"found {outlet.FullName} in {assembly.GetName().Name} but " +
                               $"could not bind it: {problem}";
                    return;
                }

                s_Detail = "no LSL C# API found in the loaded assemblies " +
                           "(expected a type named StreamOutlet with a push_sample method)";
            }
            catch (Exception e)
            {
                s_Detail = $"probe threw {e.GetType().Name}: {e.Message}";
            }
        }

        static bool TryBindTypes(Type[] types, Type outletType, out string problem)
        {
            s_StreamOutletType = outletType;
            s_StreamInfoType = types.FirstOrDefault(t => t.Name == "StreamInfo");
            s_StreamInletType = types.FirstOrDefault(t => t.Name == "StreamInlet");
            s_ChannelFormatType = types.FirstOrDefault(t => t.IsEnum && t.Name == "channel_format_t");

            if (s_StreamInfoType == null)
            {
                problem = "no StreamInfo type alongside it";
                return false;
            }

            if (s_ChannelFormatType == null)
            {
                problem = "no channel_format_t enum alongside it";
                return false;
            }

            if (!Enum.GetNames(s_ChannelFormatType).Contains("cf_string"))
            {
                problem = "channel_format_t has no cf_string value";
                return false;
            }

            s_ChannelFormatString = Enum.Parse(s_ChannelFormatType, "cf_string");

            // StreamInfo(name, type, channel_count, nominal_srate, channel_format, source_id)
            s_StreamInfoCtor = s_StreamInfoType.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length >= 6 &&
                       p[0].ParameterType == typeof(string) &&
                       p[1].ParameterType == typeof(string) &&
                       p[2].ParameterType == typeof(int) &&
                       p[3].ParameterType == typeof(double) &&
                       p[4].ParameterType == s_ChannelFormatType &&
                       p[5].ParameterType == typeof(string);
            });

            if (s_StreamInfoCtor == null)
            {
                problem = "StreamInfo has no (name, type, channels, rate, format, source_id) constructor";
                return false;
            }

            s_StreamOutletCtor = s_StreamOutletType.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length >= 1 && p[0].ParameterType == s_StreamInfoType;
            });

            if (s_StreamOutletCtor == null)
            {
                problem = "StreamOutlet has no (StreamInfo) constructor";
                return false;
            }

            // push_sample(string[] data, ...) — the STRING-MARKER overload, whatever else it
            // takes after that.
            //
            // Selected on its FIRST parameter only. The official liblsl-Csharp declares
            //     push_sample(string[] data, double timestamp = 0.0, bool pushthrough = true)
            // and this used to demand an exact one-parameter method, so a perfectly good
            // installation bound as "no push_sample(string[]) overload". Optional parameters are
            // a compile-time convenience that does not exist at the reflection layer: C# fills
            // them in at the call site, so a late-bound caller must supply them itself.
            //
            // The declared defaults are exactly what we want: timestamp 0.0 means "stamp it now,
            // inside liblsl", and pushthrough true means "send it immediately rather than
            // buffering". Both are supplied from ParameterInfo.DefaultValue rather than typed in
            // here, so this cannot drift from whatever the installed binding actually declares.
            s_PushSample = ChooseOverload(s_StreamOutletType, "push_sample", typeof(string[]));

            if (s_PushSample == null)
            {
                var seen = s_StreamOutletType.GetMethods()
                    .Where(m => m.Name == "push_sample")
                    .Select(DescribeMethod);

                problem = "StreamOutlet has no push_sample overload taking string[] as its first " +
                          $"parameter. Overloads present: {string.Join(", ", seen)}";
                return false;
            }

            s_HaveConsumers = s_StreamOutletType.GetMethods()
                .FirstOrDefault(m => m.Name == "have_consumers" && m.GetParameters().Length == 0);

            // Optional: only the editor loopback test needs the inlet side.
            if (s_StreamInletType != null)
            {
                s_StreamInletCtor = s_StreamInletType.GetConstructors().FirstOrDefault(c =>
                {
                    var p = c.GetParameters();
                    return p.Length >= 1 && p[0].ParameterType == s_StreamInfoType;
                });

                // Same rule as push_sample: matched on the FIRST parameter, with any further
                // parameters supplied from their declared defaults. The official binding is
                //     double pull_sample(string[] sample, double timeout = LSL.FOREVER)
                // which the old exact-arity test happened to accept — but only by luck, and a
                // future binding that adds a processing flag would have silently un-bound the
                // inlet while leaving the outlet working.
                s_PullSample = ChooseOverload(s_StreamInletType, "pull_sample", typeof(string[]));
            }

            s_ResolveStream = types
                .SelectMany(t =>
                {
                    try
                    {
                        return t.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    }
                    catch
                    {
                        return Array.Empty<MethodInfo>();
                    }
                })
                .Where(m =>
                {
                    if (m.Name != "resolve_stream")
                        return false;

                    // The BY-PROPERTY overload: resolve_stream("name", <stream name>, ...).
                    // The official binding also offers resolve_stream(pred, minimum, timeout),
                    // whose second parameter is an int — requiring TWO leading strings is what
                    // separates them, and the return type check makes sure we did not pick up
                    // some unrelated static method of the same name.
                    var p = m.GetParameters();
                    return p.Length >= 2 &&
                           p[0].ParameterType == typeof(string) &&
                           p[1].ParameterType == typeof(string) &&
                           m.ReturnType.IsArray &&
                           m.ReturnType.GetElementType() == s_StreamInfoType;
                })
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();

            // The native library is NOT probed here. Binding the managed API and loading
            // lsl.dll are two different facts, and conflating them is how a build ends up
            // reporting "LSL active" while nothing can actually be transmitted. The native
            // side is exercised for real when an outlet is created — see CreateOutlet, which
            // returns null with the DllNotFoundException if the binary is missing — and can be
            // checked deliberately with TryNativeCheck.

            s_Available = true;
            s_Detail = $"bound {s_StreamOutletType.FullName} from " +
                       $"{s_StreamOutletType.Assembly.GetName().Name}";
            problem = string.Empty;
            return true;
        }

        // ---------------------------------------------------------------------------------
        // Native library
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Calls into the NATIVE library and reports whether it answered.
        ///
        /// Separate from <see cref="isAvailable"/> on purpose. Binding the managed API proves
        /// only that LSL.cs is compiled into the project; it says nothing about whether lsl.dll
        /// is present and loadable for this architecture. Reporting "available" as though it
        /// meant "will transmit" is exactly the false-positive this experiment cannot afford.
        ///
        /// local_clock() is used because it is the cheapest call that must cross into the
        /// native binary: if the DLL is missing or is the wrong bitness, this throws
        /// DllNotFoundException or BadImageFormatException and the failure is reported, not
        /// swallowed.
        /// </summary>
        public static bool TryNativeCheck(out string detail)
        {
            Probe();

            if (!s_Available)
            {
                detail = s_Detail;
                return false;
            }

            var lslType = s_StreamOutletType.Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "LSL" &&
                                     t.GetMethod("local_clock",
                                         BindingFlags.Public | BindingFlags.Static) != null);

            var localClock = lslType?.GetMethod("local_clock",
                BindingFlags.Public | BindingFlags.Static);

            if (localClock == null)
            {
                detail = "the binding exposes no local_clock(); the native library was not probed";
                return false;
            }

            try
            {
                var now = localClock.Invoke(null, Array.Empty<object>());

                if (!(now is double seconds) || seconds <= 0d)
                {
                    detail = $"local_clock() returned {now ?? "null"} — the native library " +
                             "answered but not with a usable clock";
                    return false;
                }

                detail = $"native liblsl responded: local_clock()={seconds:F3}";
                return true;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                detail = $"native library unusable — {e.InnerException.GetType().Name}: " +
                         $"{e.InnerException.Message}";
                return false;
            }
            catch (Exception e)
            {
                detail = $"native library unusable — {e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        // ---------------------------------------------------------------------------------
        // Outlet
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Creates a real LSL outlet. Returns null (with a reason) if the library is missing or
        /// the native liblsl binary cannot be loaded — never a stand-in object.
        /// </summary>
        public static object CreateOutlet(string streamName, string streamType, string sourceId,
            out string detail)
        {
            if (!isAvailable)
            {
                detail = LslBinding.detail;
                return null;
            }

            try
            {
                var infoArgs = BuildArgs(s_StreamInfoCtor, new object[]
                    { streamName, streamType, 1, IrregularRate, s_ChannelFormatString, sourceId });

                var info = s_StreamInfoCtor.Invoke(infoArgs);
                var outlet = s_StreamOutletCtor.Invoke(BuildArgs(s_StreamOutletCtor, new object[] { info }));

                detail = $"outlet created via {s_StreamOutletType.FullName}";
                return outlet;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                // A missing native liblsl.dll surfaces here as DllNotFoundException.
                detail = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return null;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        /// <summary>
        /// Pushes one marker. Returns true ONLY when the library actually accepted the sample —
        /// the caller uses this to decide whether it may claim a marker was sent.
        /// </summary>
        public static bool PushSample(object outlet, string[] sample, out string error)
        {
            if (outlet == null || s_PushSample == null)
            {
                error = "no outlet";
                return false;
            }

            try
            {
                // The sample, then every remaining parameter from its declared default —
                // timestamp 0.0 ("stamp it now, inside liblsl") and pushthrough true ("send it
                // immediately"). Passing only the sample is what used to be impossible.
                s_PushSample.Invoke(outlet, BuildArgs(s_PushSample, new object[] { sample }));
                error = string.Empty;
                return true;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                error = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return false;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>True when at least one recorder is subscribed. False if unknown.</summary>
        public static bool HaveConsumers(object outlet)
        {
            if (outlet == null || s_HaveConsumers == null)
                return false;

            try
            {
                return s_HaveConsumers.Invoke(outlet, Array.Empty<object>()) is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        public static void Dispose(object instance)
        {
            if (instance is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Closing an outlet must never be able to take the session down with it.
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Inlet — the marker loopback test and the raw-EEG receiver
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Everything needed to RESOLVE a stream and open an inlet on it.
        ///
        /// Separate from <see cref="CanRunLoopbackTest"/>, which additionally needs the STRING
        /// pull_sample: a numeric EEG stream is received through a different overload, and must
        /// not be gated on the marker one being present.
        /// </summary>
        public static bool CanReceiveStreams =>
            isAvailable && s_StreamInletCtor != null && s_ResolveStream != null;

        public static bool CanRunLoopbackTest =>
            CanReceiveStreams && s_PullSample != null;

        /// <summary>
        /// What a stream ADVERTISES about itself. Every value is read from the live StreamInfo —
        /// nothing here is assumed, defaulted or inferred from the device's name.
        /// </summary>
        public struct StreamMetadata
        {
            public string name;
            public string type;
            public int channelCount;
            public double nominalSrate;

            /// <summary>The channel_format_t value's NAME, e.g. "cf_float32".</summary>
            public string channelFormat;

            /// <summary>The channel_format_t value as its underlying int, e.g. 1 for cf_float32.</summary>
            public int channelFormatValue;

            public string sourceId;

            /// <summary>True when the stream reports a regular sampling rate.</summary>
            public bool hasRegularRate => nominalSrate > 0d;

            public override string ToString()
            {
                var rate = hasRegularRate
                    ? $"{nominalSrate.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} Hz"
                    : "irregular";

                return $"name={name}; type={type}; channels={channelCount}; rate={rate}; " +
                       $"format={channelFormat}; source_id=" +
                       $"{(string.IsNullOrEmpty(sourceId) ? "(none)" : sourceId)}";
            }
        }

        /// <summary>
        /// Reads a resolved stream's REAL metadata.
        ///
        /// Every field comes from the StreamInfo the sender published. Channel count, sampling
        /// rate and sample format are properties of the amplifier and its configuration, and
        /// guessing any of them would silently mis-shape every buffer downstream.
        /// </summary>
        public static bool TryReadStreamInfo(object streamInfo, out StreamMetadata metadata,
            out string detail)
        {
            metadata = default;

            if (streamInfo == null)
            {
                detail = "no stream info";
                return false;
            }

            try
            {
                var type = streamInfo.GetType();

                metadata.name = InvokeGetter(streamInfo, type, "name") as string ?? string.Empty;
                metadata.type = InvokeGetter(streamInfo, type, "type") as string ?? string.Empty;
                metadata.sourceId =
                    InvokeGetter(streamInfo, type, "source_id") as string ?? string.Empty;

                var channels = InvokeGetter(streamInfo, type, "channel_count");
                var rate = InvokeGetter(streamInfo, type, "nominal_srate");
                var format = InvokeGetter(streamInfo, type, "channel_format");

                metadata.channelCount = channels is int c ? c : 0;
                metadata.nominalSrate = rate is double r ? r : 0d;
                metadata.channelFormat = format != null ? format.ToString() : "unknown";
                metadata.channelFormatValue = format != null
                    ? Convert.ToInt32(format, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;

                if (metadata.channelCount <= 0)
                {
                    detail = $"the stream advertises {metadata.channelCount} channels";
                    return false;
                }

                detail = metadata.ToString();
                return true;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                detail = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return false;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        static object InvokeGetter(object instance, Type type, string methodName)
        {
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == methodName &&
                                     m.GetParameters().All(p => p.HasDefaultValue));

            return method?.Invoke(instance, BuildArgs(method, Array.Empty<object>()));
        }

        /// <summary>
        /// The C# array element type that carries a given channel_format_t, or null when the
        /// format cannot be received through a numeric pull_sample.
        ///
        /// The mapping is liblsl's own: cf_float32 -> float, cf_double64 -> double,
        /// cf_int32 -> int, cf_int16 -> short. cf_string is a marker stream, not a signal, and
        /// cf_int8 / cf_int64 have no numeric pull_sample overload in this binding — all three
        /// are reported as unsupported rather than coerced into a type that would misread the
        /// samples.
        /// </summary>
        public static Type ChannelElementType(int channelFormatValue)
        {
            switch (channelFormatValue)
            {
                case 1: return typeof(float);    // cf_float32
                case 2: return typeof(double);   // cf_double64
                case 4: return typeof(int);      // cf_int32
                case 5: return typeof(short);    // cf_int16
                default: return null;            // cf_string / cf_int8 / cf_int64 / cf_undefined
            }
        }

        /// <summary>
        /// Pulls ONE numeric sample of <paramref name="channelCount"/> channels.
        ///
        /// The buffer is allocated for the element type the STREAM advertises and sized from the
        /// channel count the STREAM advertises, and the pull_sample overload is chosen to match
        /// — so nothing here depends on any assumption about the amplifier.
        ///
        /// TIMEOUT: pass 0 for a non-blocking poll (the frame-safe form used from Update) or a
        /// small positive number for a diagnostic wait. There is deliberately NO way to request
        /// an indefinite wait through this method: LSL's FOREVER on the main thread would hang
        /// the Editor whenever a stream went quiet.
        ///
        /// Returns false — with <paramref name="timestamp"/> 0 — when no sample was available.
        /// That is a normal outcome for a poll, not an error.
        /// </summary>
        public static bool TryPullNumericSample(object inlet, Type elementType, int channelCount,
            double timeoutSeconds, double[] destination, out double timestamp, out string error)
        {
            timestamp = 0d;

            if (inlet == null)
            {
                error = "no inlet";
                return false;
            }

            if (elementType == null)
            {
                error = "unsupported channel format";
                return false;
            }

            if (destination == null || destination.Length < channelCount)
            {
                error = $"destination buffer holds {destination?.Length ?? 0} of {channelCount} channels";
                return false;
            }

            var pull = ChooseOverload(s_StreamInletType, "pull_sample", elementType.MakeArrayType());

            if (pull == null)
            {
                error = $"the installed StreamInlet has no pull_sample({elementType.Name}[]) overload";
                return false;
            }

            // Never FOREVER, and never negative: a poll is 0, a diagnostic wait is small.
            var timeout = Math.Max(0d, timeoutSeconds);

            try
            {
                var buffer = Array.CreateInstance(elementType, channelCount);
                var args = BuildArgs(pull, new object[] { buffer, timeout });

                var result = pull.Invoke(inlet, args);

                // pull_sample fills the array it was handed; some bindings hand it back.
                if (args[0] is Array returned && returned.Length == channelCount)
                    buffer = returned;

                timestamp = result is double stamp ? stamp : 0d;

                if (timestamp == 0d)
                {
                    // No sample within the timeout. Normal for a poll; the caller decides
                    // whether that matters.
                    error = string.Empty;
                    return false;
                }

                for (var i = 0; i < channelCount; i++)
                {
                    destination[i] = Convert.ToDouble(buffer.GetValue(i),
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                error = string.Empty;
                return true;
            }
            catch (TargetInvocationException e) when (
                e.InnerException != null && e.InnerException is TimeoutException)
            {
                // liblsl signals "nothing arrived" as a timeout on some builds. Same meaning as
                // a zero timestamp, and not a failure of the stream.
                error = string.Empty;
                return false;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                error = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return false;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>
        /// liblsl's clock-offset estimate for an inlet.
        ///
        /// THE DOCUMENTED SEMANTICS, quoted from the installed binding:
        ///   time_correction() returns "the number that needs to be added to a time stamp that
        ///   was remotely generated via lsl_local_clock() to map it into the local clock domain
        ///   of this machine", and pull_sample returns "the capture time of the sample ON THE
        ///   REMOTE MACHINE ... To remap this time stamp to the local clock, ADD the value
        ///   returned by .time_correction() to it."
        ///
        /// So the relationship is    local = remote + correction    — an addition, taken from
        /// the API's own documentation rather than assumed from the sign of an observed
        /// difference.
        ///
        /// The first call blocks for a few milliseconds while liblsl establishes the estimate;
        /// later calls are documented as instantaneous, served from a background update. The
        /// timeout is therefore always finite here and never LSL's FOREVER.
        /// </summary>
        public static bool TryGetTimeCorrection(object inlet, double timeoutSeconds,
            out double correction, out string error)
        {
            correction = 0d;

            if (inlet == null)
            {
                error = "no inlet";
                return false;
            }

            var method = ChooseOverload(s_StreamInletType, "time_correction", typeof(double));

            // The overload takes only optional parameters, so the first-parameter rule above
            // does not match it; look it up directly.
            if (method == null)
            {
                method = s_StreamInletType?
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "time_correction" &&
                                         m.ReturnType == typeof(double));
            }

            if (method == null)
            {
                error = "the installed StreamInlet exposes no time_correction()";
                return false;
            }

            try
            {
                var parameters = method.GetParameters();
                var args = parameters.Length > 0
                    ? BuildArgs(method, new object[] { Math.Max(0.001d, timeoutSeconds) })
                    : Array.Empty<object>();

                if (method.Invoke(inlet, args) is double value)
                {
                    correction = value;
                    error = string.Empty;
                    return true;
                }

                error = "time_correction() did not return a number";
                return false;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                // A timeout here is ordinary: the estimate is simply not ready yet.
                error = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return false;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>The signature of the bound time_correction, for the diagnostic report.</summary>
        public static string boundTimeCorrection
        {
            get
            {
                Probe();

                var method = s_StreamInletType?
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "time_correction" &&
                                         m.ReturnType == typeof(double));

                return method != null ? DescribeMethod(method) : string.Empty;
            }
        }

        /// <summary>
        /// Creates a NUMERIC outlet — a stream of numbers rather than markers.
        ///
        /// VALIDATION SUPPORT ONLY. The experiment publishes markers and nothing else; this
        /// exists so the raw-EEG receiver can be exercised end to end in-process, against a
        /// stream whose channel count, rate and format are known exactly. Without it, the
        /// receiver could only ever be tested when an amplifier happened to be on the network.
        /// </summary>
        public static object CreateNumericOutlet(string streamName, string streamType,
            string sourceId, int channelCount, double nominalSrate, string channelFormatName,
            out string detail)
        {
            if (!isAvailable)
            {
                detail = LslBinding.detail;
                return null;
            }

            try
            {
                if (!Enum.GetNames(s_ChannelFormatType).Contains(channelFormatName))
                {
                    detail = $"channel_format_t has no value '{channelFormatName}'";
                    return null;
                }

                var format = Enum.Parse(s_ChannelFormatType, channelFormatName);

                var info = s_StreamInfoCtor.Invoke(BuildArgs(s_StreamInfoCtor, new object[]
                    { streamName, streamType, channelCount, nominalSrate, format, sourceId }));

                var outlet = s_StreamOutletCtor.Invoke(
                    BuildArgs(s_StreamOutletCtor, new object[] { info }));

                detail = $"numeric outlet created ({channelCount} ch, {nominalSrate} Hz, " +
                         $"{channelFormatName})";
                return outlet;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                detail = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return null;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        /// <summary>
        /// Pushes one numeric sample. VALIDATION SUPPORT ONLY — see
        /// <see cref="CreateNumericOutlet"/>. The experiment itself never publishes numbers.
        /// </summary>
        public static bool TryPushNumericSample(object outlet, Type elementType, double[] values,
            out string error)
        {
            if (outlet == null || elementType == null || values == null)
            {
                error = "no outlet, element type or values";
                return false;
            }

            var push = ChooseOverload(s_StreamOutletType, "push_sample", elementType.MakeArrayType());

            if (push == null)
            {
                error = $"no push_sample({elementType.Name}[]) overload";
                return false;
            }

            try
            {
                var buffer = Array.CreateInstance(elementType, values.Length);

                for (var i = 0; i < values.Length; i++)
                {
                    buffer.SetValue(
                        Convert.ChangeType(values[i], elementType,
                            System.Globalization.CultureInfo.InvariantCulture), i);
                }

                push.Invoke(outlet, BuildArgs(push, new object[] { buffer }));
                error = string.Empty;
                return true;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                error = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return false;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>
        /// EVERY stream currently visible on the network, for diagnostics.
        ///
        /// Exists so "AURA was not found" can be reported alongside what WAS found — the
        /// difference between "AURA is not streaming", "AURA publishes under another name" and
        /// "this machine sees nothing at all" is the whole of the troubleshooting, and guessing
        /// between them wastes a session.
        /// </summary>
        public static StreamMetadata[] ResolveAllStreams(double timeoutSeconds, out string detail)
        {
            Probe();

            if (!s_Available)
            {
                detail = s_Detail;
                return Array.Empty<StreamMetadata>();
            }

            var resolveAll = s_StreamOutletType.Assembly.GetTypes()
                .Where(t => t.Name == "LSL")
                .SelectMany(t =>
                {
                    try
                    {
                        return t.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    }
                    catch
                    {
                        return Array.Empty<MethodInfo>();
                    }
                })
                .FirstOrDefault(m => m.Name == "resolve_streams" &&
                                     m.ReturnType.IsArray &&
                                     m.ReturnType.GetElementType() == s_StreamInfoType);

            if (resolveAll == null)
            {
                detail = "the installed binding exposes no resolve_streams()";
                return Array.Empty<StreamMetadata>();
            }

            try
            {
                // Finite wait, supplied explicitly rather than left to the default.
                var args = BuildArgs(resolveAll, new object[] { Math.Max(0.5d, timeoutSeconds) });

                if (!(resolveAll.Invoke(null, args) is Array results))
                {
                    detail = "resolve_streams() returned nothing";
                    return Array.Empty<StreamMetadata>();
                }

                var found = new List<StreamMetadata>();

                foreach (var info in results)
                {
                    if (TryReadStreamInfo(info, out var meta, out _))
                        found.Add(meta);

                    Dispose(info);
                }

                detail = $"{found.Count} stream(s) visible";
                return found.ToArray();
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                detail = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return Array.Empty<StreamMetadata>();
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return Array.Empty<StreamMetadata>();
            }
        }

        /// <summary>Resolves the first stream with the given name, or null on timeout.</summary>
        public static object ResolveStreamInfo(string streamName, double timeoutSeconds,
            out string detail)
        {
            if (!CanReceiveStreams)
            {
                detail = "inlet API not bound";
                return null;
            }

            try
            {
                var parameters = s_ResolveStream.GetParameters();
                var args = new object[parameters.Length];
                args[0] = "name";
                args[1] = streamName;

                for (var i = 2; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType == typeof(int))
                        args[i] = 1;
                    else if (parameters[i].ParameterType == typeof(double))
                        args[i] = timeoutSeconds;
                    else
                        args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                }

                if (!(s_ResolveStream.Invoke(null, args) is Array results) || results.Length == 0)
                {
                    detail = $"no stream named '{streamName}' resolved within {timeoutSeconds:F1} s";
                    return null;
                }

                detail = $"resolved {results.Length} stream(s) named '{streamName}'";
                return results.GetValue(0);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                detail = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return null;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        public static object CreateInlet(object streamInfo, out string detail)
        {
            if (!CanReceiveStreams || streamInfo == null)
            {
                detail = "inlet API not bound";
                return null;
            }

            try
            {
                var inlet = s_StreamInletCtor.Invoke(BuildArgs(s_StreamInletCtor, new object[] { streamInfo }));

                // open_stream is optional in most bindings; pull_sample opens implicitly.
                var open = s_StreamInletType.GetMethods()
                    .FirstOrDefault(m => m.Name == "open_stream");
                if (open != null)
                {
                    var openArgs = open.GetParameters()
                        .Select(p => p.HasDefaultValue ? p.DefaultValue : (object)0.0)
                        .ToArray();
                    open.Invoke(inlet, openArgs);
                }

                detail = "inlet created";
                return inlet;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                detail = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return null;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        /// <summary>Pulls one marker. Returns the received string, or null on timeout.</summary>
        public static string PullSample(object inlet, double timeoutSeconds, out string detail)
        {
            if (inlet == null || s_PullSample == null)
            {
                detail = "no inlet";
                return null;
            }

            try
            {
                var sample = new string[1];
                var args = BuildArgs(s_PullSample, new object[] { sample, timeoutSeconds });
                var timestamp = s_PullSample.Invoke(inlet, args);

                // pull_sample writes into the array it was given; some bindings pass it back.
                if (args[0] is string[] returned && returned.Length > 0)
                    sample = returned;

                if (timestamp is double stamp && stamp == 0d && string.IsNullOrEmpty(sample[0]))
                {
                    detail = $"timed out after {timeoutSeconds:F1} s";
                    return null;
                }

                detail = $"received at lsl_time={timestamp}";
                return sample[0];
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                detail = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
                return null;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        // ---------------------------------------------------------------------------------
        // Late-binding helpers
        //
        // The single reason this file needed changing: C# optional parameters do not exist at
        // the reflection layer. `push_sample(string[] data, double timestamp = 0.0, bool
        // pushthrough = true)` is ONE method with THREE parameters; the compiler inserts the
        // defaults at each call site. A reflective caller sees all three and must pass all
        // three — so matching on exact arity rejects the very API it is meant to bind.
        //
        // Everything below therefore matches on the parameters that carry MEANING (the first
        // one) and fills the rest from the binding's own declared defaults.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The overload of <paramref name="name"/> whose FIRST parameter is
        /// <paramref name="firstParameterType"/>, preferring the one with the fewest parameters
        /// when several match.
        ///
        /// Fewest-first is deliberate: if a binding ever offers both a plain and an extended
        /// form, the plain one has fewer knobs to get wrong.
        /// </summary>
        static MethodInfo ChooseOverload(Type type, string name, Type firstParameterType)
        {
            if (type == null)
                return null;

            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m =>
                {
                    if (m.Name != name)
                        return false;

                    var p = m.GetParameters();
                    if (p.Length < 1 || p[0].ParameterType != firstParameterType)
                        return false;

                    // Every parameter after the first must be one we can supply honestly:
                    // either the binding declares a default, or the type has a meaningful
                    // zero value. A required reference parameter we would have to pass null
                    // for is NOT bindable — guessing there could push a malformed sample.
                    for (var i = 1; i < p.Length; i++)
                    {
                        if (!p[i].HasDefaultValue && !p[i].ParameterType.IsValueType)
                            return false;
                    }

                    return true;
                })
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        /// <summary>Readable signature, so a binding failure names what WAS found.</summary>
        static string DescribeMethod(MethodInfo method)
        {
            var p = method.GetParameters()
                .Select(x => x.HasDefaultValue
                    ? $"{FriendlyName(x.ParameterType)} {x.Name} = ..."
                    : $"{FriendlyName(x.ParameterType)} {x.Name}");

            return $"{method.Name}({string.Join(", ", p)})";
        }

        static string FriendlyName(Type type)
        {
            return type.IsArray ? FriendlyName(type.GetElementType()) + "[]" : type.Name;
        }

        /// <summary>
        /// Every public overload of a method, for the diagnostic report. Used by the editor's
        /// availability check so a future incompatibility can be read off the log instead of
        /// guessed at.
        /// </summary>
        public static string[] DescribeOverloads(string typeName, string methodName)
        {
            Probe();

            Type type = typeName == "StreamOutlet" ? s_StreamOutletType
                : typeName == "StreamInlet" ? s_StreamInletType
                : null;

            if (type == null)
                return Array.Empty<string>();

            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == methodName)
                .Select(DescribeMethod)
                .ToArray();
        }

        /// <summary>
        /// The NATIVE library's version, as liblsl reports it: major*100 + minor. Returns -1 when
        /// it cannot be read.
        ///
        /// Read from the native binary, not from the C# file's own version: the managed binding
        /// and lsl.dll are shipped separately and can disagree.
        /// </summary>
        public static int libraryVersion => InvokeLslStatic("library_version");

        /// <summary>
        /// The wire PROTOCOL version. Two liblsl builds interoperate when this matches; the
        /// library version may differ freely. This is the number that matters when a sender and
        /// a receiver cannot see each other.
        /// </summary>
        public static int protocolVersion => InvokeLslStatic("protocol_version");

        static int InvokeLslStatic(string methodName)
        {
            Probe();

            if (!s_Available)
                return -1;

            try
            {
                var method = s_StreamOutletType.Assembly.GetTypes()
                    .Where(t => t.Name == "LSL")
                    .Select(t => t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static))
                    .FirstOrDefault(m => m != null && m.ReturnType == typeof(int) &&
                                         m.GetParameters().Length == 0);

                return method?.Invoke(null, Array.Empty<object>()) is int value ? value : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Formats a liblsl version integer as "major.minor".</summary>
        public static string FormatVersion(int version)
        {
            return version < 0
                ? "unknown"
                : $"{version / 100}.{version % 100}";
        }

        /// <summary>The signature this binding actually invokes, for the report. Empty if unbound.</summary>
        public static string boundPushSample =>
            isAvailable && s_PushSample != null ? DescribeMethod(s_PushSample) : string.Empty;

        public static string boundPullSample =>
            isAvailable && s_PullSample != null ? DescribeMethod(s_PullSample) : string.Empty;

        public static string boundResolveStream =>
            isAvailable && s_ResolveStream != null
                ? $"{s_ResolveStream.DeclaringType?.FullName}.{DescribeMethod(s_ResolveStream)}"
                : string.Empty;

        // NOTE: `leading` is deliberately NOT a params array. `string[]` is assignable to
        // `object[]` by array covariance, so `BuildArgs(method, sample)` with a string[] sample
        // bound the SAMPLE ITSELF as the params array and passed its first string as argument
        // zero — which liblsl rejected with "String cannot be converted to String[]". Requiring
        // an explicit array at every call site makes that mistake impossible to write.

        /// <summary>Fills optional trailing constructor parameters with their defaults.</summary>
        static object[] BuildArgs(ConstructorInfo ctor, object[] leading)
        {
            return BuildArgs(ctor.GetParameters(), leading);
        }

        /// <summary>Fills optional trailing METHOD parameters with their defaults.</summary>
        static object[] BuildArgs(MethodInfo method, object[] leading)
        {
            return BuildArgs(method.GetParameters(), leading);
        }

        /// <summary>
        /// Supplies the leading arguments we mean, and every remaining parameter from the
        /// binding's OWN declared default.
        ///
        /// Taking the value from ParameterInfo.DefaultValue rather than writing 0.0 and true in
        /// here means the call always matches what the installed binding documents, and cannot
        /// drift if a future version changes a default.
        /// </summary>
        static object[] BuildArgs(ParameterInfo[] parameters, object[] leading)
        {
            var args = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                if (i < leading.Length)
                {
                    args[i] = leading[i];
                    continue;
                }

                args[i] = parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : GetTypeDefault(parameters[i].ParameterType);
            }

            return args;
        }

        static object GetTypeDefault(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
