# Generates spoken-word WAV files for the IKEA_EEG verbal memory task.
#
# Uses the LOCAL Windows Speech API (System.Speech). This runs OFFLINE on this machine:
# no cloud service, no API key, no network call. It is an AUTHORING-TIME tool — the WAV
# files it produces are committed as normal project assets, so the running experiment has
# no TTS dependency at all and will still work in a standalone Quest build.
#
# Invoked by the Unity Editor menu: IKEA_EEG > Generate Spoken Word Clips (local TTS)
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File GenerateWordClips.ps1 \
#       -OutputDir "<folder>" -Words "River,Copper,Lantern,Falcon,Sugar" \
#       [-VoiceName "Microsoft Zira Desktop"] [-Rate -1] [-SampleRate 44100]

param(
    [Parameter(Mandatory = $true)][string]$OutputDir,

    # Comma-separated words. Safe for ASCII only — the console codepage mangles accented and
    # Japanese text on its way through a command line, so non-English sets are passed with
    # -WordsFile instead.
    [string]$Words = "",

    # UTF-8 file, one word per line. Preferred for every language, required for ES/JA.
    [string]$WordsFile = "",

    [string]$VoiceName = "Microsoft Zira Desktop",

    # Culture the voice MUST belong to, e.g. "en", "es", "ja". A voice from another culture is
    # never substituted: an English engine reading Japanese produces confident nonsense, and a
    # participant would be tested on a stimulus nobody could recognise.
    [string]$CulturePrefix = "en",

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

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer

# Pick the requested voice, but ONLY if it belongs to the required culture; otherwise any
# enabled voice OF THAT CULTURE.
#
# The fallback is culture-scoped on purpose. It never drops back to the system default or to
# English: a Spanish or Japanese word read by an English engine is not a degraded stimulus,
# it is a different one, and the resulting recall data would be uninterpretable. If the
# culture has no voice on this machine, this fails loudly and generates nothing.
$selected = $null
foreach ($v in $synth.GetInstalledVoices()) {
    if ($v.Enabled -and $v.VoiceInfo.Name -eq $VoiceName -and
        $v.VoiceInfo.Culture.Name.StartsWith($CulturePrefix)) {
        $selected = $v.VoiceInfo.Name
        break
    }
}

if (-not $selected) {
    foreach ($v in $synth.GetInstalledVoices()) {
        if ($v.Enabled -and $v.VoiceInfo.Culture.Name.StartsWith($CulturePrefix)) {
            $selected = $v.VoiceInfo.Name
            Write-Output "WARN: voice '$VoiceName' not found; using '$selected' ($CulturePrefix) instead."
            break
        }
    }
}

if (-not $selected) {
    Write-Error "No enabled '$CulturePrefix-*' voice is installed. Install one via Windows Settings > Time and Language > Speech. NO other language's voice is substituted."
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

# The file form is preferred: it is read as UTF-8, so accented Spanish and Japanese words
# arrive intact. A command-line string goes through the console codepage and does not.
if (-not [string]::IsNullOrWhiteSpace($WordsFile)) {
    if (-not (Test-Path $WordsFile)) {
        Write-Error "WordsFile not found: $WordsFile"
        exit 5
    }

    $wordArray = [System.IO.File]::ReadAllLines($WordsFile, [System.Text.Encoding]::UTF8)
}
else {
    $wordArray = $Words.Split(",")
}

$index = 0

foreach ($rawWord in $wordArray) {
    $word = $rawWord.Trim()
    if ([string]::IsNullOrWhiteSpace($word)) { continue }

    $index++

    # Unicode letters and digits, not just ASCII. This MUST agree with the C# side's
    # char.IsLetterOrDigit filter, or the generator writes 01_.wav while Unity looks for
    # 01_大根.wav and every Japanese clip silently goes missing.
    $safe = ($word -replace '[^\p{L}\p{N}]', '')
    $path = Join-Path $OutputDir ("{0:D2}_{1}.wav" -f $index, $safe)

    $synth.SetOutputToWaveFile($path, $format)
    $synth.Speak($word)
    $synth.SetOutputToNull()

    if (Test-Path $path) {
        $size = (Get-Item $path).Length
        Write-Output "CLIP=$index|$word|$path|$size"
    }
    else {
        Write-Error "Failed to write $path"
        exit 4
    }
}

$synth.Dispose()
Write-Output "DONE=$index"
