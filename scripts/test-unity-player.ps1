param(
    [Parameter(Mandatory=$true)][string]$UnityEditorPath,
    [Parameter(Mandatory=$true)][string]$Source,
    [ValidateSet('FFmpeg','WebRtc')][string]$Backend = 'FFmpeg',
    [switch]$Preview,
    [ValidateSet('Editor','Mono','Il2Cpp')][string[]]$Targets = @('Editor','Mono','Il2Cpp'),
    [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = if ($Preview) { Join-Path $repository '.verify/unity-playback-preview' } else { Join-Path $repository '.verify/unity-player-tests' }
$package = Join-Path $repository 'artifacts/unity-managed/com.frameflux.player'
if (!(Test-Path -LiteralPath $UnityEditorPath)) { throw 'Unity editor executable not found.' }
if (!(Test-Path -LiteralPath (Join-Path $package 'package.json'))) { throw 'Run build-unity-package.ps1 first.' }
New-Item -ItemType Directory -Force -Path "$project/Assets/Editor", "$project/Packages", "$project/ProjectSettings" | Out-Null
$projectVersionFile = Join-Path $project 'ProjectSettings/ProjectVersion.txt'
if (!(Test-Path -LiteralPath $projectVersionFile) -or ((Get-Content -LiteralPath $projectVersionFile -Raw) -match 'UnknownUnityVersion')) {
    $editorRelease = (Get-Item -LiteralPath $UnityEditorPath).VersionInfo.ProductVersion.Split('_')[0]
    if ($editorRelease -notmatch '^\d{4}\.\d+\.\d+[abfp]\d+(?:c\d+)?$') { throw 'Could not determine the installed Unity release.' }
    "m_EditorVersion: $editorRelease" | Set-Content -LiteralPath $projectVersionFile -Encoding utf8
}
Copy-Item -Path (Join-Path $repository 'unity/Tests~/Runtime/*.cs') -Destination "$project/Assets" -Force
Copy-Item -Path (Join-Path $repository 'unity/Tests~/Editor/*.cs') -Destination "$project/Assets/Editor" -Force
$manifest = @{ dependencies = @{
    'com.frameflux.player' = ('file:' + $package.Replace('\','/'))
    'com.unity.ugui' = '1.0.0'; 'com.unity.modules.audio' = '1.0.0'
    'com.unity.modules.ui' = '1.0.0'; 'com.unity.modules.imgui' = '1.0.0'
    'com.unity.modules.jsonserialize' = '1.0.0'
} }
$manifest | ConvertTo-Json -Depth 5 | Set-Content "$project/Packages/manifest.json" -Encoding utf8

if ($Preview) {
    $previewLog = Join-Path $project ('Preview-' + [Guid]::NewGuid().ToString('N') + '.log')
    $previewArguments = @('-projectPath', $project, '-logFile', $previewLog,
        '-executeMethod', 'PreviewBuilder.Run', '-framefluxSource', $Source, '-framefluxBackend', $Backend)
    $previewQuoted = ($previewArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $previewProcess = Start-Process -FilePath $UnityEditorPath -ArgumentList $previewQuoted -WindowStyle Normal -PassThru
    Write-Host "Visible Unity playback preview launched (PID $($previewProcess.Id))."
    Write-Host "Use the Game window to watch video and control playback. Close Unity when finished."
    Write-Host "Preview log: $previewLog"
    return
}

function Invoke-SmokeProcess([string]$Executable, [string[]]$Arguments, [string]$Log) {
    $argumentsQuoted = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Executable -ArgumentList $argumentsQuoted -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (!$process.WaitForExit(1000)) {
        if ((Get-Date) -ge $deadline) {
            $process.Kill()
            throw "Unity check timed out. Log: $Log"
        }
    }
    if ($process.ExitCode -ne 0) { throw "Unity check exited with code $($process.ExitCode). Log: $Log" }
}
foreach ($target in $Targets) {
    $runId = [Guid]::NewGuid().ToString('N')
    $log = Join-Path $project ($target + '-' + $runId + '.log')
    $result = Join-Path $project ($target + '-' + $runId + '.result.txt')
    $common = @('-projectPath', $project, '-logFile', $log)
    $test = @('-framefluxSource', $Source, '-framefluxResult', $result, '-framefluxBackend', $Backend)
    if ($target -eq 'Editor') {
        Write-Host 'Checking Editor playback...'
        Invoke-SmokeProcess $UnityEditorPath (@('-batchmode','-executeMethod','SmokeBuilder.RunEditor') + $common + $test) $log
    } else {
        $player = Join-Path $project ("Builds/$target/FrameFluxSmoke.exe")
        Write-Host "Building $target Player..."
        Invoke-SmokeProcess $UnityEditorPath (@('-batchmode','-executeMethod',"SmokeBuilder.Build$target",'-framefluxBuild',$player) + $common) $log
        Write-Host "Checking $target Player playback..."
        # A normal hidden Player keeps real graphics and audio available.
        Invoke-SmokeProcess $player (@('-logFile', $log) + $test) $log
    }
    if (!(Test-Path -LiteralPath $result)) { throw "Unity produced no test result for $target. Log: $log" }
    Get-Content -LiteralPath $result
}
