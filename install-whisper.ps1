param(
    [string]$Tag = "b5130",
    [string]$Model = "ggml-base.en.bin"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$whisperDir = Join-Path $root "whisper"
$modelsDir = Join-Path $whisperDir "models"
$tmpDir = Join-Path $env:TEMP "cosmo-whisper-install"

New-Item -ItemType Directory -Force -Path $whisperDir | Out-Null
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

$zipUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/$Tag/whisper-bin-x64.zip"
$zipPath = Join-Path $tmpDir "whisper-bin-x64.zip"
$extractDir = Join-Path $tmpDir "extract"

Write-Host "Downloading whisper.cpp server ($Tag, CPU x64 build)..."
Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UserAgent "cosmo-install"

if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
Expand-Archive -Path $zipPath -DestinationPath $extractDir
$binSrc = Join-Path $extractDir "Release"

$files = @("whisper-server.exe", "ggml.dll", "ggml-base.dll", "whisper.dll")
$files += (Get-ChildItem "$binSrc\ggml-cpu-*.dll" | Select-Object -ExpandProperty Name)
foreach ($f in $files) {
    Copy-Item (Join-Path $binSrc $f) (Join-Path $whisperDir $f) -Force
}

$modelPath = Join-Path $modelsDir $Model
if (Test-Path $modelPath) {
    Write-Host "Model already present at $modelPath, skipping download."
} else {
    Write-Host "Downloading Whisper model ($Model)..."
    Invoke-WebRequest -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$Model" -OutFile $modelPath -UserAgent "cosmo-install"
}

Remove-Item -Recurse -Force $tmpDir

Write-Host "whisper.cpp installed at $whisperDir"
Write-Host "Model ready at $modelPath"
