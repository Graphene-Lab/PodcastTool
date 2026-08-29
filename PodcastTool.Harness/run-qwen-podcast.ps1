# run-qwen-podcast.ps1 — full podcast test with the Qwen3-TTS engine (CPU-bound, ~7 h for a
# 30-min episode). Sets PODCAST_TTS_ENGINE=qwen and a STABLE workspace (D:\, NOT %TEMP% —
# Windows Storage Sense wipes %TEMP% during long runs, which destroyed the first 7-hour
# attempt's output), runs the harness end-to-end (Release), logs everything to
# qwen-podcast-run.log next to this script, then opens the produced podcast folder in
# Explorer so the user finds the files immediately.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path $root "qwen-podcast-run.log"
$workspace = "D:\podcasttool-workspace"
$env:PODCAST_TTS_ENGINE = "qwen"
$env:PODCAST_WORKSPACE = $workspace

# The harness ensures kokoro.onnx at startup (even for a Qwen run); reuse the host's copy
# instead of downloading 325 MB again.
$harnessOut = Join-Path $PSScriptRoot "bin\Release\net10.0"
if (-not (Test-Path (Join-Path $harnessOut "kokoro.onnx"))) {
    $hostModel = "C:\Users\andre\OneDrive\Sorgenti\AgentBridge\bin\Release\net10.0\kokoro.onnx"
    if (Test-Path $hostModel) {
        New-Item -ItemType Directory -Force -Path $harnessOut | Out-Null
        Copy-Item $hostModel (Join-Path $harnessOut "kokoro.onnx")
    }
}

$started = Get-Date
"=== Qwen podcast run started $started (PODCAST_TTS_ENGINE=qwen, workspace=$workspace, provider=DeepSeekBridge, Release) ===" | Tee-Object -FilePath $log
& dotnet run --project (Join-Path $root "PodcastTool.Harness") -c Release -- "novità del mondo bitcoin" --language it --name "Bitcoin News" *>> $log
$exit = $LASTEXITCODE
"=== run finished: exit=$exit, elapsed=$([math]::Round(((Get-Date)-$started).TotalMinutes,1)) min ===" | Tee-Object -FilePath $log -Append
$pod = Get-ChildItem (Join-Path $workspace "podcast") -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($pod) { Start-Process explorer.exe $pod.FullName } else { Start-Process explorer.exe $workspace }
