# Pulls real samples from an LSL stream OUTSIDE Unity and asks one question:
# are the channels actually different from each other?
#
# WHY THIS EXISTS
# ---------------
# A live feature run produced BIT-IDENTICAL theta and alpha on all 8 electrodes. Real EEG
# cannot do that, so exactly one of two things is true:
#
#   * AURA is transmitting identical channels   -> an ACQUISITION problem
#                                                  (electrodes, reference, test pattern)
#   * our extraction collapses them somewhere   -> a CODE problem in the Unity path
#
# Those need opposite fixes, and no amount of staring at Unity's output can tell them apart,
# because Unity is the thing under suspicion. This script is the control: it loads the same
# native liblsl, opens its own inlet and pulls its own samples, with NO Unity code anywhere
# in the path. What it reports is what the amplifier actually puts on the wire.
#
# READ-ONLY. It opens an inlet and pulls; it sends nothing, writes no project file and changes
# no setting. Opening a second inlet is safe: LSL is multi-consumer by design.
#
# HOW TO READ THE RESULT
#   distinct = 8/8 on every sample   -> the wire carries 8 different channels. If Unity still
#                                       shows identical values, the bug is OURS.
#   distinct = 1/8                   -> the amplifier sends one signal on 8 channels. The bug
#                                       is in ACQUISITION. Check electrodes / reference.
#   identical pairs listed           -> those specific channels duplicate each other.
#
# USAGE
#   powershell -ExecutionPolicy Bypass -File ProbeAuraChannels.ps1
#   powershell -ExecutionPolicy Bypass -File ProbeAuraChannels.ps1 -StreamName AURA -Samples 2000

param(
    [string]$StreamName = "AURA",
    [int]$Samples = 1000,
    [double]$Timeout = 5.0,
    [string]$DllPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($DllPath)) {
    $DllPath = Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) "Assets\Plugins\lib\lsl.dll"
}

if (-not (Test-Path $DllPath)) {
    Write-Output "FAIL - lsl.dll not found at: $DllPath"
    exit 1
}

Write-Output "===== OUT-OF-PROCESS AURA CHANNEL PROBE ====="
Write-Output "lsl.dll:  $DllPath"
Write-Output "host:     $env:COMPUTERNAME"
Write-Output "stream:   $StreamName"
Write-Output "NO Unity code is in this path."
Write-Output ""

$source = @"
using System;
using System.Runtime.InteropServices;
public static class AuraProbe {
  const string D = @"__DLL__";
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_resolve_byprop(IntPtr[] b, uint n, string prop, string val, int min, double wait);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_get_name(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_get_hostname(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_get_channel_count(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern double lsl_get_nominal_srate(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_get_channel_format(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern void lsl_destroy_streaminfo(IntPtr i);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_create_inlet(IntPtr info, int max_buflen, int max_chunklen, int recover);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern void lsl_open_stream(IntPtr inlet, double timeout, ref int ec);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern void lsl_close_stream(IntPtr inlet);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern void lsl_destroy_inlet(IntPtr inlet);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern double lsl_pull_sample_f(IntPtr inlet, float[] buffer, int buffer_elements, double timeout, ref int ec);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern double lsl_pull_sample_d(IntPtr inlet, double[] buffer, int buffer_elements, double timeout, ref int ec);
}
"@

$source = $source.Replace("__DLL__", $DllPath)
Add-Type -TypeDefinition $source

function Text([IntPtr]$p) {
    if ($p -eq [IntPtr]::Zero) { return "" }
    return [Runtime.InteropServices.Marshal]::PtrToStringAnsi($p)
}

# ---- resolve ------------------------------------------------------------------------------

$found = New-Object IntPtr[] 16
$n = [AuraProbe]::lsl_resolve_byprop($found, 16, "name", $StreamName, 1, $Timeout)

if ($n -le 0) {
    Write-Output "RESULT: STREAM NOT FOUND - it is not on the network."
    Write-Output "No live measurement is possible. Nothing about the channels can be concluded."
    exit 2
}

$info = $found[0]
$channels = [AuraProbe]::lsl_get_channel_count($info)
$rate = [AuraProbe]::lsl_get_nominal_srate($info)
$fmt = [AuraProbe]::lsl_get_channel_format($info)
$srcHost = Text ([AuraProbe]::lsl_get_hostname($info))

Write-Output ("FOUND  channels={0}  rate={1} Hz  format_code={2}  host={3}" -f $channels, $rate, $fmt, $srcHost)
Write-Output ""

if ($channels -lt 2) {
    Write-Output "Stream has fewer than 2 channels; an inter-channel comparison is meaningless."
    exit 3
}

# ---- open inlet and pull ------------------------------------------------------------------

$ec = 0
$inlet = [AuraProbe]::lsl_create_inlet($info, 360, 0, 1)
[AuraProbe]::lsl_open_stream($inlet, $Timeout, [ref]$ec)

if ($ec -ne 0) {
    Write-Output "FAIL - could not open the stream (error code $ec)."
    [AuraProbe]::lsl_destroy_inlet($inlet)
    exit 4
}

Write-Output "Pulling up to $Samples samples ..."

# Everything below is computed from the values exactly as pulled. Nothing is transformed.
$sums = New-Object 'double[]' $channels
$sumSquares = New-Object 'double[]' $channels
$mins = New-Object 'double[]' $channels
$maxs = New-Object 'double[]' $channels
for ($c = 0; $c -lt $channels; $c++) { $mins[$c] = [double]::MaxValue; $maxs[$c] = [double]::MinValue }

# pairEqual[i,j] counts the samples on which channels i and j held exactly the same value.
$pairEqual = New-Object 'int[,]' $channels,$channels
$distinctHistogram = New-Object 'int[]' ($channels + 1)

$pulled = 0
$firstRow = $null
$useDouble = ($fmt -eq 2)

$bufF = New-Object 'float[]' $channels
$bufD = New-Object 'double[]' $channels

while ($pulled -lt $Samples) {
    if ($useDouble) {
        $ts = [AuraProbe]::lsl_pull_sample_d($inlet, $bufD, $channels, 2.0, [ref]$ec)
    } else {
        $ts = [AuraProbe]::lsl_pull_sample_f($inlet, $bufF, $channels, 2.0, [ref]$ec)
    }

    if ($ec -ne 0) { Write-Output "  pull error code $ec - stopping."; break }
    if ($ts -eq 0.0) { Write-Output "  timed out waiting for a sample - stopping."; break }

    $row = New-Object 'double[]' $channels
    for ($c = 0; $c -lt $channels; $c++) {
        if ($useDouble) { $row[$c] = $bufD[$c] } else { $row[$c] = [double]$bufF[$c] }
    }

    if ($null -eq $firstRow) { $firstRow = $row }

    $seen = New-Object 'System.Collections.Generic.HashSet[double]'
    for ($c = 0; $c -lt $channels; $c++) {
        $v = $row[$c]
        [void]$seen.Add($v)
        $sums[$c] += $v
        $sumSquares[$c] += $v * $v
        if ($v -lt $mins[$c]) { $mins[$c] = $v }
        if ($v -gt $maxs[$c]) { $maxs[$c] = $v }
    }

    $distinctHistogram[$seen.Count]++

    for ($i = 0; $i -lt $channels; $i++) {
        for ($j = $i + 1; $j -lt $channels; $j++) {
            if ($row[$i] -eq $row[$j]) { $pairEqual[$i,$j]++ }
        }
    }

    $pulled++
}

[AuraProbe]::lsl_close_stream($inlet)
[AuraProbe]::lsl_destroy_inlet($inlet)
[AuraProbe]::lsl_destroy_streaminfo($info)

Write-Output "Samples pulled: $pulled"
Write-Output ""

if ($pulled -eq 0) {
    Write-Output "RESULT: THE STREAM IS ADVERTISING BUT NOT TRANSMITTING."
    Write-Output "The inlet opened and no sample arrived. Nothing about the channels can be concluded."
    exit 5
}

# ---- report -------------------------------------------------------------------------------

Write-Output "--- distinct values per sample (across the channels) ---"
for ($d = 1; $d -le $channels; $d++) {
    if ($distinctHistogram[$d] -gt 0) {
        $pct = 100.0 * $distinctHistogram[$d] / $pulled
        Write-Output ("  distinct={0}/{1}  on {2} samples ({3:F1}%)" -f $d, $channels, $distinctHistogram[$d], $pct)
    }
}

Write-Output ""
Write-Output "--- per channel, as pulled (amplifier native units) ---"
for ($c = 0; $c -lt $channels; $c++) {
    $mean = $sums[$c] / $pulled
    $var = ($sumSquares[$c] / $pulled) - ($mean * $mean)
    if ($var -lt 0) { $var = 0 }
    $sd = [math]::Sqrt($var)
    Write-Output ("  CH{0}  mean={1,14:E6}  sd={2,14:E6}  min={3,14:E6}  max={4,14:E6}" -f ($c + 1), $mean, $sd, $mins[$c], $maxs[$c])
}

Write-Output ""
Write-Output "--- first sample, raw ---"
$parts = @()
for ($c = 0; $c -lt $channels; $c++) { $parts += ("CH{0}={1:E6}" -f ($c + 1), $firstRow[$c]) }
Write-Output ("  " + ($parts -join "  "))

Write-Output ""
Write-Output "--- channel pairs that were IDENTICAL on every sample ---"
$identicalPairs = 0
for ($i = 0; $i -lt $channels; $i++) {
    for ($j = $i + 1; $j -lt $channels; $j++) {
        if ($pairEqual[$i,$j] -eq $pulled) {
            Write-Output ("  CH{0} == CH{1}  on all {2} samples" -f ($i + 1), ($j + 1), $pulled)
            $identicalPairs++
        }
    }
}
if ($identicalPairs -eq 0) { Write-Output "  (none - every pair differed on at least one sample)" }

$allDistinct = $distinctHistogram[$channels]
$totalPairs = $channels * ($channels - 1) / 2

Write-Output ""
Write-Output "===== VERDICT ====="

if ($identicalPairs -eq $totalPairs) {
    Write-Output "THE AMPLIFIER IS SENDING THE SAME SIGNAL ON EVERY CHANNEL."
    Write-Output "All channel pairs were bit-identical on all pulled samples, measured with no"
    Write-Output "Unity code in the path. This is an ACQUISITION problem, not a code problem:"
    Write-Output "check electrodes, impedance, reference/ground and any test-signal mode."
} elseif ($identicalPairs -gt 0) {
    Write-Output "SOME CHANNELS ARE DUPLICATES ($identicalPairs of $totalPairs pairs bit-identical)."
    Write-Output "Listed above. This is an acquisition/montage problem for those channels."
} elseif ($allDistinct -eq $pulled) {
    Write-Output "THE WIRE CARRIES GENUINELY DIFFERENT CHANNELS."
    Write-Output "Every pulled sample held a distinct value on every channel. If the Unity"
    Write-Output "pipeline reports identical per-channel features against this same stream, the"
    Write-Output "fault is in OUR EXTRACTION, not in the amplifier."
} else {
    Write-Output "CHANNELS DIFFER, but some samples had repeated values across channels."
    Write-Output "See the histogram above. Occasional coincidence is normal; a dominant low"
    Write-Output "distinct-count is not."
}
