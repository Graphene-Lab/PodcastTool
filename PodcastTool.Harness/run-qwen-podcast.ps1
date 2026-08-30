# run-qwen-podcast.ps1 — full podcast test with the Qwen3-TTS engine (CPU-bound, ~7 h for a
# 30-min episode). The harness runs in DEBUG on purpose: the Qwen3-TTS release gate (≥16 GB
# VRAM) is disabled in debug builds — this 4 GB laptop is exactly the "developer testing"
# case the gate allows. Sets PODCAST_TTS_ENGINE=qwen and a STABLE workspace (D:\, NOT
# %TEMP% — Windows Storage Sense wipes %TEMP% during long runs), logs everything to
# qwen-podcast-run.log next to this script, then opens the produced podcast folder in
# Explorer so the user finds the files immediately.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path $root "qwen-podcast-run.log"
$workspace = "D:\podcasttool-workspace"
$config = "Debug"
$env:PODCAST_TTS_ENGINE = "qwen"
$env:PODCAST_WORKSPACE = $workspace

# The harness ensures kokoro.onnx at startup (even for a Qwen run) and the Kokoro FALLBACK
# needs the voices too: reuse the host's copies instead of downloading 325 MB again.
$harnessOut = Join-Path $PSScriptRoot "bin\$config\net10.0"
if (-not (Test-Path (Join-Path $harnessOut "kokoro.onnx")) -or -not (Test-Path (Join-Path $harnessOut "voices"))) {
    $hostBin = "C:\Users\andre\OneDrive\Sorgenti\AgentBridge\bin\Release\net10.0"
    New-Item -ItemType Directory -Force -Path $harnessOut | Out-Null
    if (-not (Test-Path (Join-Path $harnessOut "kokoro.onnx"))) { Copy-Item (Join-Path $hostBin "kokoro.onnx") (Join-Path $harnessOut "kokoro.onnx") }
    if (-not (Test-Path (Join-Path $harnessOut "voices"))) { Copy-Item (Join-Path $hostBin "voices") (Join-Path $harnessOut "voices") -Recurse }
}

$started = Get-Date
"=== Qwen podcast run started $started (PODCAST_TTS_ENGINE=qwen, workspace=$workspace, config=$config — debug: Qwen gate off, provider=DeepSeekBridge) ===" | Tee-Object -FilePath $log
& dotnet run --project (Join-Path $root "PodcastTool.Harness") -c $config -- "novità del mondo bitcoin" --language it --name "Bitcoin News" *>> $log
$exit = $LASTEXITCODE
"=== run finished: exit=$exit, elapsed=$([math]::Round(((Get-Date)-$started).TotalMinutes,1)) min ===" | Tee-Object -FilePath $log -Append
$pod = Get-ChildItem (Join-Path $workspace "podcast") -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($pod) { Start-Process explorer.exe $pod.FullName } else { Start-Process explorer.exe $workspace }
