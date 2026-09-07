using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;
using Debug = UnityEngine.Debug;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Why a stream that demonstrably exists on another machine is not visible here.
    ///
    /// TRANSPORT AND DISCOVERY ONLY. It reads no EEG, processes no signal and touches no
    /// experiment state; it answers the single question "can this process see LSL traffic from
    /// the network, and if not, what is stopping it".
    ///
    /// Everything it prints is measured. Where a fact cannot be measured — liblsl exposes no API
    /// to ask which config file it parsed — it says so and names the documented way to find out
    /// instead of guessing.
    /// </summary>
    public static class LslNetworkDiagnostics
    {
        /// <summary>
        /// liblsl's DOCUMENTED configuration search order.
        ///
        /// From the liblsl network-connectivity documentation: the LSLAPICFG environment
        /// variable, then the process's working directory, then the user's home, then the
        /// machine-wide location. Listed here so the report can show which of them exist and, as
        /// importantly, which one liblsl would have reached FIRST.
        /// </summary>
        static IEnumerable<(string label, string path)> ConfigSearchPaths()
        {
            var env = Environment.GetEnvironmentVariable("LSLAPICFG");

            yield return ("1. LSLAPICFG environment variable",
                string.IsNullOrEmpty(env) ? string.Empty : env);

            yield return ("2. working directory", Path.Combine(
                Directory.GetCurrentDirectory(), "lsl_api.cfg"));

            yield return ("3. user home", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "lsl_api", "lsl_api.cfg"));

            yield return ("4. machine-wide", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "lsl_api", "lsl_api.cfg"));
        }

        [MenuItem("IKEA_EEG/LSL/Diagnose Network", false, 123)]
        public static void DiagnoseMenu()
        {
            Debug.Log(Report());
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void DiagnoseFromCommandLine()
        {
            Debug.Log(Report());
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== LSL NETWORK DIAGNOSTIC =====");
            sb.AppendLine("  Transport and discovery only. No EEG is read and no signal is processed.");

            AppendHost(sb);
            AppendInterfaces(sb);
            AppendLibrary(sb);
            AppendConfig(sb);
            AppendFirewall(sb);
            AppendDiscovery(sb);

            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------
        // Host
        // ---------------------------------------------------------------------------------

        static void AppendHost(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("  ---- HOST ----");

            try
            {
                sb.AppendLine($"  hostname: {Dns.GetHostName()}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"  hostname: unavailable ({e.GetType().Name})");
            }

            sb.AppendLine($"  process:  {Process.GetCurrentProcess().ProcessName} " +
                          $"(pid {Process.GetCurrentProcess().Id})");
        }

        // ---------------------------------------------------------------------------------
        // Interfaces
        // ---------------------------------------------------------------------------------

        static void AppendInterfaces(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("  ---- IPv4 INTERFACES VISIBLE TO THIS PROCESS ----");
            sb.AppendLine("  (LSL discovery goes out of these; an extra or wrongly-ordered one");
            sb.AppendLine("   can send the query somewhere the other machine will never hear it.)");

            NetworkInterface[] interfaces;

            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception e)
            {
                sb.AppendLine($"  could not enumerate interfaces: {e.Message}");
                return;
            }

            var routable = 0;
            var linkLocal = 0;
            var virtualUp = new List<string>();

            foreach (var nic in interfaces)
            {
                IPInterfaceProperties properties;

                try
                {
                    properties = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var addresses = properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily ==
                                System.Net.Sockets.AddressFamily.InterNetwork)
                    .ToArray();

                if (addresses.Length == 0)
                    continue;

                foreach (var address in addresses)
                {
                    var ip = address.Address.ToString();

                    // 169.254.x.x is APIPA: an interface that never got an address. liblsl may
                    // still enumerate it, and a query sent there reaches nothing.
                    var isLinkLocal = ip.StartsWith("169.254.", StringComparison.Ordinal);
                    var isLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;

                    if (isLinkLocal)
                        linkLocal++;
                    else if (!isLoopback)
                        routable++;

                    var note = isLoopback ? "  [loopback]"
                        : isLinkLocal ? "  [LINK-LOCAL / no DHCP lease — cannot reach the LAN]"
                        : string.Empty;

                    sb.AppendLine($"    {ip,-16} {nic.OperationalStatus,-12} " +
                                  $"{Describe(nic.Name)}{note}");

                    if (nic.OperationalStatus == OperationalStatus.Up && IsVirtual(nic) &&
                        !isLoopback)
                    {
                        virtualUp.Add($"{Describe(nic.Name)} ({ip})");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine($"  routable IPv4 interfaces: {routable}");
            sb.AppendLine($"  link-local (169.254.x.x): {linkLocal}");

            sb.AppendLine(virtualUp.Count == 0
                ? "  virtual/VPN adapters UP: none"
                : $"  virtual/VPN adapters UP: {string.Join(", ", virtualUp)}  <-- these can " +
                  "capture LSL discovery; disable any not in use");
        }

        static bool IsVirtual(NetworkInterface nic)
        {
            var text = $"{nic.Name} {nic.Description}";

            foreach (var marker in new[]
                     { "Radmin", "VPN", "Virtual", "Hyper-V", "VMware", "VirtualBox", "TAP",
                       "Loopback Adapter", "Hamachi", "ZeroTier", "Tailscale" })
            {
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>Interface names can be non-ASCII; keep them printable in the log.</summary>
        static string Describe(string name) => string.IsNullOrEmpty(name) ? "(unnamed)" : name;

        // ---------------------------------------------------------------------------------
        // Library
        // ---------------------------------------------------------------------------------

        static void AppendLibrary(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("  ---- LIBLSL ----");
            sb.AppendLine($"  managed binding: {LslBinding.boundAssembly} " +
                          $"({LslBinding.detail})");

            var library = LslBinding.libraryVersion;
            var protocol = LslBinding.protocolVersion;

            sb.AppendLine($"  library version:  {LslBinding.FormatVersion(library)} ({library})");
            sb.AppendLine($"  protocol version: {LslBinding.FormatVersion(protocol)} ({protocol})");
            sb.AppendLine("  NOTE: two liblsl builds interoperate when the PROTOCOL versions");
            sb.AppendLine("        match; the library versions may differ freely.");

            var nativeOk = LslBinding.TryNativeCheck(out var nativeDetail);
            sb.AppendLine($"  native lsl.dll:   {(nativeOk ? "OK" : "NOT USABLE")} — {nativeDetail}");
        }

        // ---------------------------------------------------------------------------------
        // Config
        // ---------------------------------------------------------------------------------

        static void AppendConfig(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("  ---- lsl_api.cfg ----");
            sb.AppendLine("  Documented search order; liblsl uses the FIRST one that exists.");

            var firstFound = string.Empty;

            foreach (var (label, path) in ConfigSearchPaths())
            {
                if (string.IsNullOrEmpty(path))
                {
                    sb.AppendLine($"    {label,-38} (not set)");
                    continue;
                }

                var exists = File.Exists(path);
                sb.AppendLine($"    {label,-38} {(exists ? "FOUND  " : "absent ")} {path}");

                if (exists && string.IsNullOrEmpty(firstFound))
                    firstFound = path;
            }

            if (string.IsNullOrEmpty(firstFound))
            {
                sb.AppendLine();
                sb.AppendLine("  No configuration file exists on any search path — liblsl is");
                sb.AppendLine("  running on its defaults (multicast/broadcast discovery only).");
                return;
            }

            sb.AppendLine();
            sb.AppendLine($"  liblsl would read: {firstFound}");
            sb.AppendLine("  contents:");

            try
            {
                foreach (var line in File.ReadAllLines(firstFound))
                    sb.AppendLine($"    | {line}");

                var text = File.ReadAllText(firstFound);

                sb.AppendLine();
                sb.AppendLine($"  KnownPeers present in the file: " +
                              $"{text.IndexOf("KnownPeers", StringComparison.OrdinalIgnoreCase) >= 0}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"    could not be read: {e.Message}");
            }

            sb.AppendLine();
            sb.AppendLine("  HONEST LIMIT: liblsl exposes no API to ask which config it parsed,");
            sb.AppendLine("  so the file EXISTING is not proof that it was LOADED. To confirm,");
            sb.AppendLine("  add a [log] section with 'level = 5' to the file above and re-run —");
            sb.AppendLine("  liblsl then prints its configuration and resolver activity into the");
            sb.AppendLine("  Editor log. That is liblsl's own documented mechanism.");
        }

        // ---------------------------------------------------------------------------------
        // Firewall
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Asks Windows Firewall whether anything BLOCKS inbound traffic to this process.
        ///
        /// This is the check that is easiest to get wrong by eye: Windows evaluates BLOCK rules
        /// before ALLOW rules, so a program-scoped block on Unity.exe silently defeats any
        /// port-scoped allow rule for LSL, no matter how correct that rule is. Outbound still
        /// works, and ICMP still works, so ping and "the config looks right" both mislead.
        /// </summary>
        static void AppendFirewall(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("  ---- WINDOWS FIREWALL (inbound, this executable) ----");

            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                sb.AppendLine("  not a Windows editor — skipped");
                return;
            }

            var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

            if (string.IsNullOrEmpty(exe))
            {
                sb.AppendLine("  could not determine this process's executable path");
                return;
            }

            sb.AppendLine($"  executable: {exe}");

            var script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "Get-NetConnectionProfile | ForEach-Object { 'PROFILE|' + $_.InterfaceAlias + '|' + $_.NetworkCategory };" +
                "Get-NetFirewallRule -Direction Inbound -Enabled True | ForEach-Object {" +
                "  $r=$_; $a=$r | Get-NetFirewallApplicationFilter;" +
                "  if ($a.Program -and $a.Program -ne 'Any') {" +
                "    'RULE|' + $r.Action + '|' + $r.Profile + '|' + $a.Program + '|' + $r.DisplayName } }";

            var output = RunPowerShell(script, out var error);

            if (string.IsNullOrWhiteSpace(output))
            {
                sb.AppendLine($"  could not query the firewall ({error})");
                return;
            }

            var activeProfiles = new List<string>();
            var blocking = new List<string>();
            var allowing = new List<string>();

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split('|');

                if (parts.Length >= 3 && parts[0] == "PROFILE")
                {
                    activeProfiles.Add(parts[2]);
                    sb.AppendLine($"  active network profile: {parts[2]} (on {parts[1]})");
                    continue;
                }

                if (parts.Length < 5 || parts[0] != "RULE")
                    continue;

                // Only rules naming THIS executable.
                if (!string.Equals(parts[3].Trim(), exe, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entry = $"{parts[1]} on [{parts[2]}] — {parts[4]}";

                if (parts[1].Trim().Equals("Block", StringComparison.OrdinalIgnoreCase))
                    blocking.Add(entry);
                else
                    allowing.Add(entry);
            }

            sb.AppendLine();

            foreach (var rule in allowing)
                sb.AppendLine($"    ALLOW  {rule}");

            foreach (var rule in blocking)
                sb.AppendLine($"    BLOCK  {rule}");

            if (allowing.Count == 0 && blocking.Count == 0)
            {
                sb.AppendLine("    no inbound rule names this executable");
                return;
            }

            // The verdict: a block rule covering an ACTIVE profile is decisive.
            var fatal = blocking
                .Where(rule => activeProfiles.Any(profile =>
                    rule.IndexOf(profile, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToArray();

            sb.AppendLine();

            if (fatal.Length > 0)
            {
                sb.AppendLine("  *** INBOUND TRAFFIC TO THIS PROCESS IS BLOCKED ***");
                sb.AppendLine();
                sb.AppendLine("  A BLOCK rule for this executable covers the ACTIVE network");
                sb.AppendLine("  profile. Windows Firewall evaluates block rules BEFORE allow");
                sb.AppendLine("  rules, so this defeats any port-based LSL allow rule.");
                sb.AppendLine();
                sb.AppendLine("  LSL discovery needs inbound UDP: this process sends the query,");
                sb.AppendLine("  and the provider REPLIES to it. The reply is what is dropped —");
                sb.AppendLine("  which is why ping works, why the other machine can resolve its");
                sb.AppendLine("  own stream locally, and why this process sees nothing at all.");
            }
            else if (blocking.Count > 0)
            {
                sb.AppendLine("  A block rule exists but does not cover the active profile.");
            }
            else
            {
                sb.AppendLine("  No inbound block rule applies to this process.");
            }
        }

        static string RunPowerShell(string script, out string error)
        {
            error = string.Empty;

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "could not start powershell.exe";
                        return string.Empty;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit(20000);
                    return output;
                }
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return string.Empty;
            }
        }

        // ---------------------------------------------------------------------------------
        // Discovery
        // ---------------------------------------------------------------------------------

        static void AppendDiscovery(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("  ---- DISCOVERY ----");

            if (!LslBinding.CanReceiveStreams)
            {
                sb.AppendLine("  the binding exposes no resolver — nothing to test");
                return;
            }

            var clockOk = LslBinding.TryNativeCheck(out var clockDetail);
            sb.AppendLine($"  local_clock: {(clockOk ? clockDetail : "unavailable — " + clockDetail)}");

            // Everything on the network, then the specific stream.
            var all = LslBinding.ResolveAllStreams(5.0, out var allDetail);

            sb.AppendLine();
            sb.AppendLine($"  resolve_streams(5.0): {allDetail}");

            foreach (var stream in all)
                sb.AppendLine($"    {stream}");

            if (all.Length == 0)
                sb.AppendLine("    (none)");

            var info = LslBinding.ResolveStreamInfo(AuraLslReceiver.StreamName, 5.0,
                out var auraDetail);

            sb.AppendLine();
            sb.AppendLine($"  resolve_stream(\"name\", \"{AuraLslReceiver.StreamName}\", 5.0): " +
                          $"{auraDetail}");

            if (info != null)
            {
                if (LslBinding.TryReadStreamInfo(info, out var meta, out _))
                    sb.AppendLine($"    {meta}");

                LslBinding.Dispose(info);
            }

            sb.AppendLine();

            if (all.Length == 0)
            {
                sb.AppendLine("  VERDICT: this process receives NO LSL traffic from any source.");
                sb.AppendLine("  That is a transport problem, not a stream-name problem — a wrong");
                sb.AppendLine("  name would still show the other streams on the network.");
            }
            else if (info == null)
            {
                sb.AppendLine("  VERDICT: LSL traffic IS reaching this process, but no stream is");
                sb.AppendLine($"  named exactly '{AuraLslReceiver.StreamName}'. Compare the list above with what");
                sb.AppendLine("  the sending application says it publishes.");
            }
            else
            {
                sb.AppendLine("  VERDICT: the stream is discoverable from this process.");
            }
        }
    }
}
