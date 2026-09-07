# Generates spoken NARRATION WAV files for the IKEA_EEG familiarization room (Area 0).
#
# Same offline approach as GenerateWordClips.ps1: the LOCAL Windows Speech API, no cloud
# service, no API key, no network call. This is an AUTHORING-TIME tool — the WAV it produces
# is committed as a normal project asset, so the running experiment has no TTS dependency and
# still works in a standalone Quest build.
#
# SEPARATE FROM THE WORD CLIPS ON PURPOSE. The verbal-memory word audio is experimental
# stimulus material and its generator is left completely untouched; this writes instructional
# narration only, to its own folder. It also takes its lines from a FILE rather than a
# comma-separated argument, because narration sentences contain commas.
#
# Invoked by: IKEA_EEG > Generate Familiarization Narration (local TTS)
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File GenerateNarrationClips.ps1 \
#       -OutputDir "<folder>" -LinesFile "<file>" [-VoiceName "..."] [-Rate -1] [-SampleRate 44100]
#
# LinesFile format, one clip per line:
#   <clipId>|<text to speak>

param(
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [Parameter(Mandatory = $true)][string]$LinesFile,
    [string]$VoiceName = "Microsoft Zira Desktop",
    [int]$Rate = -1,
    [int]$SampleRate = 44100
)

$ErrorActionPreference = "Stop"

try {
    Add-Type -AssemblyName System.Speech
}
catch {
    Write-Error "System.Speech is not available on this machine: $($_.Exception.Message)"
    exit 2
}

if (-not (Test-Path $LinesFile)) {
    Write-Error "Lines file not found: $LinesFile"
    exit 5
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer

# Pick the requested voice; fall back to any enabled en-* voice rather than to whatever the
# system default happens to be.
$selected = $null
foreach ($v in $synth.GetInstalledVoices()) {
    if ($v.Enabled -and $v.VoiceInfo.Name -eq $VoiceName) { $selected = $v.VoiceInfo.Name; break }
}

if (-not $selected) {
    foreach ($v in $synth.GetInstalledVoices()) {
        if ($v.Enabled -and $v.VoiceInfo.Culture.Name.StartsWith("en")) {
            $selected = $v.VoiceInfo.Name
            Write-Output "WARN: voice '$VoiceName' not found; using '$selected' instead."
            break
        }
    }
}

if (-not $selected) {
    Write-Error "No enabled English voice is installed. Install one via Windows Settings > Time & Language > Speech."
    exit 3
}

$synth.SelectVoice($selected)
$synth.Rate = $Rate
$synth.Volume = 100

Write-Output "VOICE=$selected"
Write-Output "RATE=$Rate"

$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    $SampleRate,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
    [System.Speech.AudioFormat.AudioChannel]::Mono)

$lines = Get-Content -Path $LinesFile -Encoding UTF8
$index = 0

foreach ($rawLine in $lines) {
    if ([string]::IsNullOrWhiteSpace($rawLine)) { continue }
    if ($rawLine.StartsWith("#")) { continue }

    $separator = $rawLine.IndexOf("|")
    if ($separator -lt 1) {
        Write-Error "Malformed line (expected '<clipId>|<text>'): $rawLine"
        exit 6
    }

    $clipId = $rawLine.Substring(0, $separator).Trim()
    $text = $rawLine.Substring($separator + 1).Trim()

    if ([string]::IsNullOrWhiteSpace($clipId) -or [string]::IsNullOrWhiteSpace($text)) { continue }

    $index++
    $safe = ($clipId -replace '[^A-Za-z0-9_]', '')
    $path = Join-Path $OutputDir ("{0}.wav" -f $safe)

    $synth.SetOutputToWaveFile($path, $format)
    $synth.Speak($text)
    $synth.SetOutputToNull()

    if (Test-Path $path) {
        $size = (Get-Item $path).Length
        Write-Output "CLIP=$index|$clipId|$path|$size"
    }
    else {
        Write-Error "Failed to write $path"
        exit 4
    }
}

$synth.Dispose()
Write-Output "DONE=$index"
