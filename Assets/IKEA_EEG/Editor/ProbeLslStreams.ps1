# Resolves LSL streams from OUTSIDE Unity, using the project's own lsl.dll.
#
# WHY THIS EXISTS
# ---------------
# When Unity sees no LSL streams, there are two very different explanations and they need
# opposite fixes:
#
#   * the machine or the network cannot see the streams at all   -> a network/config problem
#   * the machine CAN see them but the Unity process cannot      -> a per-process problem,
#                                                                   almost always a Windows
#                                                                   Firewall rule naming
#                                                                   Unity.exe
#
# This script is the control experiment that tells them apart. It loads the SAME native
# liblsl the Unity project loads, reads the SAME lsl_api.cfg, and runs on the SAME machine —
# the only thing that differs is the executable doing the asking. If this finds streams and
# Unity does not, the difference is the process, not the network.
#
# It is READ-ONLY: it resolves and prints, and never opens an inlet, pulls data, sends
# anything, or changes any setting.
#
# USAGE
#   powershell -ExecutionPolicy Bypass -File ProbeLslStreams.ps1
#   powershell -ExecutionPolicy Bypass -File ProbeLslStreams.ps1 -StreamName AURA -Timeout 5
#
# liblsl prints its own diagnostics to stderr, including the line
#   "Configuration loaded from <path>"
# which is the only authoritative confirmation of WHICH lsl_api.cfg was actually read.

param(
    [string]$StreamName = "AURA",
    [double]$Timeout = 5.0,
    [string]$DllPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($DllPath)) {
    # Default: the copy inside this project.
    $DllPath = Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) "Assets\Plugins\lib\lsl.dll"
}

if (-not (Test-Path $DllPath)) {
    Write-Output "FAIL - lsl.dll not found at: $DllPath"
    Write-Output "Pass the correct path with -DllPath."
    exit 1
}

Write-Output "===== OUT-OF-PROCESS LSL PROBE ====="
Write-Output "lsl.dll:  $DllPath"
Write-Output "host:     $env:COMPUTERNAME"
Write-Output ""

# DllImport needs a literal path, so the source is built with the resolved one baked in.
$source = @"
using System;
using System.Runtime.InteropServices;
public static class LslProbe {
  const string D = @"__DLL__";
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_library_version();
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_protocol_version();
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_resolve_all(IntPtr[] b, uint n, double wait);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_resolve_byprop(IntPtr[] b, uint n, string prop, string val, int min, double wait);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_get_name(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_get_type(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_get_source_id(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_get_hostname(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_get_channel_count(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern double lsl_get_nominal_srate(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_get_channel_format(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern void lsl_destroy_streaminfo(IntPtr i);
}
"@

$source = $source.Replace("__DLL__", $DllPath)
Add-Type -TypeDefinition $source

function Text([IntPtr]$p) {
    if ($p -eq [IntPtr]::Zero) { return "" }
    return [Runtime.InteropServices.Marshal]::PtrToStringAnsi($p)
}

function FormatName([int]$f) {
    switch ($f) {
        1 { "cf_float32" }
        2 { "cf_double64" }
        3 { "cf_string" }
        4 { "cf_int32" }
        5 { "cf_int16" }
        6 { "cf_int8" }
        7 { "cf_int64" }
        default { "cf_undefined($f)" }
    }
}

$lib = [LslProbe]::lsl_library_version()
$proto = [LslProbe]::lsl_protocol_version()

Write-Output ("liblsl library version:  {0}.{1}" -f [math]::Floor($lib / 100), ($lib % 100))
Write-Output ("liblsl protocol version: {0}.{1}" -f [math]::Floor($proto / 100), ($proto % 100))
Write-Output ""
Write-Output "--- resolve_all (every stream on the network) ---"

$buffer = New-Object IntPtr[] 128
$count = [LslProbe]::lsl_resolve_all($buffer, 128, $Timeout)

if ($count -le 0) {
    Write-Output "  (none)"
} else {
    for ($i = 0; $i -lt $count; $i++) {
        $info = $buffer[$i]
        $name = Text ([LslProbe]::lsl_get_name($info))
        $type = Text ([LslProbe]::lsl_get_type($info))
        $src = Text ([LslProbe]::lsl_get_source_id($info))
        $hostName = Text ([LslProbe]::lsl_get_hostname($info))
        $ch = [LslProbe]::lsl_get_channel_count($info)
        $sr = [LslProbe]::lsl_get_nominal_srate($info)
        $fmt = FormatName ([LslProbe]::lsl_get_channel_format($info))

        Write-Output ("  name={0}  type={1}  channels={2}  rate={3} Hz  format={4}  host={5}  source_id={6}" -f $name, $type, $ch, $sr, $fmt, $hostName, $src)
        [LslProbe]::lsl_destroy_streaminfo($info) | Out-Null
    }
}

Write-Output ""
Write-Output "--- resolve_byprop name = $StreamName ---"

$one = New-Object IntPtr[] 16
$found = [LslProbe]::lsl_resolve_byprop($one, 16, "name", $StreamName, 1, $Timeout)

if ($found -le 0) {
    Write-Output "  NOT FOUND"
} else {
    for ($i = 0; $i -lt $found; $i++) {
        $info = $one[$i]
        Write-Output ("  FOUND  name={0}  channels={1}  rate={2} Hz  format={3}" -f (Text ([LslProbe]::lsl_get_name($info))), [LslProbe]::lsl_get_channel_count($info), [LslProbe]::lsl_get_nominal_srate($info), (FormatName ([LslProbe]::lsl_get_channel_format($info))))
        [LslProbe]::lsl_destroy_streaminfo($info) | Out-Null
    }
}

Write-Output ""
Write-Output "===== INTERPRETATION ====="
Write-Output "If streams are listed here but Unity's 'Check AURA Stream' finds none, the"
Write-Output "network, the config and liblsl are all fine and the blocker is specific to the"
Write-Output "Unity PROCESS - check for a Windows Firewall inbound rule naming Unity.exe."
Write-Output "If nothing is listed here either, the problem is the network or the sender."
