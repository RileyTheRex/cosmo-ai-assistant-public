$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $csc)) {
    $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

New-Item -ItemType Directory -Force -Path (Join-Path $root "bin") | Out-Null

$systemSpeech = (Get-ChildItem -Path "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech" -Recurse -Filter "System.Speech.dll" | Select-Object -First 1).FullName

$iconArg = @()
if (Test-Path "$root\icon.ico") {
    $iconArg = @("/win32icon:$root\icon.ico")
}

& $csc `
    /nologo `
    /target:winexe `
    /out:"$root\bin\CosmoAIAssistant.exe" `
    @iconArg `
    /r:"$systemSpeech" `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    /r:System.Runtime.Serialization.dll `
    "$root\Config.cs" `
    "$root\Actions.cs" `
    "$root\AudioDevice.cs" `
    "$root\Hotkey.cs" `
    "$root\RemindHotkey.cs" `
    "$root\WaveInRecorder.cs" `
    "$root\WavWriter.cs" `
    "$root\WhisperEngine.cs" `
    "$root\Tone.cs" `
    "$root\ReminderParser.cs" `
    "$root\Program.cs"

if ($LASTEXITCODE -eq 0) {
    if (Test-Path "$root\icon.ico") {
        Copy-Item "$root\icon.ico" "$root\bin\" -Force
    }
    Write-Host "Build succeeded: $root\bin\CosmoAIAssistant.exe"
} else {
    Write-Host "Build failed with exit code $LASTEXITCODE"
}
