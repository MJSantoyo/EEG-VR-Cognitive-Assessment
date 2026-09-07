# Publishes a SYNTHETIC multi-channel EEG-shaped LSL stream whose channels are provably
# different from one another, from a process that contains no Unity code.
#
# WHY THIS EXISTS
# ---------------
# When the amplifier is offline, the question "does our extraction preserve per-channel
# differences?" cannot be answered against real hardware. This supplies a KNOWN-GOOD input
# instead: every channel carries a different frequency AND a different amplitude, so if any
# stage of the receiving pipeline collapsed channels together, the collapse would be obvious
# and attributable to the receiver rather than to the sender.
#
# It is the positive control for ProbeAuraChannels.ps1 and for the Unity spectral diagnostic.
#
# WHAT IT IS NOT
# --------------
# NOT a simulation of EEG, and NOT a substitute for a live AURA measurement. It contains no
# noise, no artefacts and no physiology. It proves one thing only: that distinct channels put
# on the wire arrive distinct at the far end. Any result obtained against this stream must be
# reported as SYNTHETIC.
#
# SAFETY: -StreamName defaults to a clearly synthetic name. Naming it AURA (to exercise the
# Unity receiver, which resolves that exact name) is deliberate and opt-in, and the source_id
# always says SYNTHETIC so no recording can later be mistaken for hardware.
#
# USAGE
#   powershell -ExecutionPolicy Bypass -File SyntheticEegOutlet.ps1
#   powershell -ExecutionPolicy Bypass -File SyntheticEegOutlet.ps1 -StreamName AURA -Seconds 45

param(
    [string]$StreamName = "SYNTHETIC_EEG",
    [int]$Channels = 8,
    [double]$RateHz = 250.0,
    [double]$Seconds = 45.0,
    [string]$DllPath = "",

    # Reproduces the FAULT instead of the healthy case: every channel carries the identical
    # signal, exactly as a set of disconnected electrodes all reading the reference would.
    # Used as the negative control for the inter-channel identity quality flag - a check that
    # never fires on the failure it was written for is not a check.
    [switch]$IdenticalChannels,

    # A subtler fault: the channels differ ONLY by a constant offset. Welch removes each
    # segment's mean, so the SAMPLES differ while the SPECTRA come out identical. This is the
    # case a naive sample-only duplicate test would miss.
    [switch]$OffsetOnlyChannels
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($DllPath)) {
    $DllPath = Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) "Assets\Plugins\lib\lsl.dll"
}

if (-not (Test-Path $DllPath)) {
    Write-Output "FAIL - lsl.dll not found at: $DllPath"
    exit 1
}

$source = @"
using System;
using System.Runtime.InteropServices;
public static class SynthOutlet {
  const string D = @"__DLL__";
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_create_streaminfo(string name, string type, int channel_count, double nominal_srate, int channel_format, string source_id);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr lsl_create_outlet(IntPtr info, int chunk_size, int max_buffered);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern void lsl_destroy_outlet(IntPtr outlet);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern void lsl_destroy_streaminfo(IntPtr info);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern int lsl_push_sample_ft(IntPtr outlet, float[] data, double timestamp);
  [DllImport(D, CallingConvention=CallingConvention.Cdecl)] public static extern double lsl_local_clock();
}
"@

$source = $source.Replace("__DLL__", $DllPath)
Add-Type -TypeDefinition $source

# cf_float32 = 1, matching what AURA advertises.
$info = [SynthOutlet]::lsl_create_streaminfo($StreamName, "EEG", $Channels, $RateHz, 1, "SYNTHETIC-CHANNEL-TEST")
$outlet = [SynthOutlet]::lsl_create_outlet($info, 0, 360)

Write-Output "===== SYNTHETIC EEG OUTLET (NOT REAL DATA) ====="
Write-Output ("name={0}  channels={1}  rate={2} Hz  format=float32  source_id=SYNTHETIC-CHANNEL-TEST" -f $StreamName, $Channels, $RateHz)
Write-Output "Every channel carries a DIFFERENT frequency and a DIFFERENT amplitude:"

# Distinct frequency per channel, spanning theta (4-8 Hz) and alpha (8-12 Hz) so that band
# powers also differ, not merely the raw samples. Distinct amplitude makes the band powers
# differ by a second, independent mechanism.
$freqs = New-Object 'double[]' $Channels
$amps = New-Object 'double[]' $Channels
$offsets = New-Object 'double[]' $Channels

for ($c = 0; $c -lt $Channels; $c++) {
    if ($IdenticalChannels -or $OffsetOnlyChannels) {
        $freqs[$c] = 6.0
        $amps[$c] = 1.0
    } else {
        $freqs[$c] = 4.5 + $c * 1.0
        $amps[$c] = 1.0 + $c * 0.5
    }

    if ($OffsetOnlyChannels) { $offsets[$c] = $c * 10.0 } else { $offsets[$c] = 0.0 }
}

if ($IdenticalChannels) {
    Write-Output "  MODE: IDENTICAL CHANNELS (deliberate fault) - every channel is 6.0 Hz, amplitude 1.0."
} elseif ($OffsetOnlyChannels) {
    Write-Output "  MODE: OFFSET-ONLY CHANNELS (deliberate fault) - same 6.0 Hz signal, different DC offset."
    for ($c = 0; $c -lt $Channels; $c++) {
        Write-Output ("    CH{0}  offset={1:F1}" -f ($c + 1), $offsets[$c])
    }
} else {
    for ($c = 0; $c -lt $Channels; $c++) {
        Write-Output ("  CH{0}  freq={1:F1} Hz  amplitude={2:F1}" -f ($c + 1), $freqs[$c], $amps[$c])
    }
}

$total = [int]($RateHz * $Seconds)
$dt = 1.0 / $RateHz
$start = [SynthOutlet]::lsl_local_clock()
$buf = New-Object 'float[]' $Channels
$sw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Output ""
Write-Output ("Pushing {0} samples over {1:F0} s. Ctrl+C to stop early." -f $total, $Seconds)

# PACING. Windows' sleep granularity is ~16 ms, so sleeping between individual samples would
# cap the stream near 56 Hz however small the requested delay. Instead the loop sleeps briefly
# and then pushes every sample whose grid time has already arrived. The AVERAGE rate is
# therefore the advertised one, delivered in small bursts - which is also how the real
# amplifier behaves (it arrives in 9-sample chunks).
#
# Timestamps always come from the exact uniform grid, never from the wall clock, so burstiness
# in delivery never contaminates the times the receiver analyses.
$n = 0

while ($n -lt $total) {
    $due = [int]([math]::Floor($sw.Elapsed.TotalSeconds * $RateHz))
    if ($due -gt $total) { $due = $total }

    while ($n -lt $due) {
        $t = $n * $dt

        for ($c = 0; $c -lt $Channels; $c++) {
            $buf[$c] = [float]($offsets[$c] + $amps[$c] * [math]::Sin(2.0 * [math]::PI * $freqs[$c] * $t))
        }

        [void][SynthOutlet]::lsl_push_sample_ft($outlet, $buf, $start + $t)
        $n++
    }

    if ($n -lt $total) { Start-Sleep -Milliseconds 1 }
}

Write-Output ("Pushed {0} samples in {1:F2} s (effective {2:F1} Hz)." -f $n, $sw.Elapsed.TotalSeconds, ($n / $sw.Elapsed.TotalSeconds))

[SynthOutlet]::lsl_destroy_outlet($outlet)
[SynthOutlet]::lsl_destroy_streaminfo($info)

Write-Output "Done. Outlet closed."
