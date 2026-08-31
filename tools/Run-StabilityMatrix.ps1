[CmdletBinding()]
param(
    [ValidateSet("Windows", "Linux", "Android", "All")]
    [string]$Platform = "All",

    [ValidateSet("Automated", "Device", "All")]
    [string]$Phase = "Automated",

    [ValidateRange(1, 100000)]
    [int]$Iterations = 500,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$previousIterations = $env:FRAMEFLUX_STABILITY_ITERATIONS
$env:FRAMEFLUX_STABILITY_ITERATIONS = $Iterations

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-SelectedPlatform {
    param([Parameter(Mandatory)][string]$Name)
    return $Platform -eq "All" -or $Platform -eq $Name
}

try {
    Push-Location $repoRoot
    try {
        if ($Phase -in "Automated", "All") {
            Invoke-DotNet @(
                "test",
                ".\tests\FrameFlux.FFmpeg.Tests\FrameFlux.FFmpeg.Tests.csproj",
                "--configuration", $Configuration,
                "--no-restore")

            if (Test-SelectedPlatform "Android") {
                Invoke-DotNet @(
                    "test",
                    ".\tests\FrameFlux.Avalonia.Android.Tests\FrameFlux.Avalonia.Android.Tests.csproj",
                    "--configuration", $Configuration,
                    "--no-restore")
            }
        }

        if ($Phase -in "Device", "All") {
            if (Test-SelectedPlatform "Windows") {
                Invoke-DotNet @(
                    "build",
                    ".\examples\FrameFlux.Demo.Wpf\FrameFlux.Demo.Wpf.csproj",
                    "--configuration", $Configuration,
                    "--no-restore")
                Write-Host "Windows device gate: run the WPF demo, then verify D3D11 device loss, audio-device switching, reconnect, repeated start/stop, and concurrent streams."
            }

            if (Test-SelectedPlatform "Linux") {
                Invoke-DotNet @(
                    "build",
                    ".\examples\FrameFlux.Demo.Avalonia.Desktop\FrameFlux.Demo.Avalonia.Desktop.csproj",
                    "--configuration", $Configuration,
                    "--no-restore")
                Write-Host "Linux device gate: run the desktop demo, then verify VAAPI/DMA-BUF device loss, audio-device switching, reconnect, repeated start/stop, and concurrent streams."
            }

            if (Test-SelectedPlatform "Android") {
                if (-not (Get-Command adb -ErrorAction SilentlyContinue)) {
                    throw "adb is required for the Android device gate."
                }
                $devices = @(& adb devices | Select-String "\tdevice$")
                if ($devices.Count -eq 0) {
                    throw "No authorized Android device is connected."
                }

                Invoke-DotNet @(
                    "build",
                    ".\examples\FrameFlux.Demo.Avalonia.Android\FrameFlux.Demo.Avalonia.Android.csproj",
                    "--configuration", $Configuration,
                    "--no-restore",
                    "-t:Install")
                & adb shell monkey -p com.frameflux.demo 1 | Out-Host
                if ($LASTEXITCODE -ne 0) {
                    throw "The Android demo could not be launched."
                }
                Write-Host "Android device gate: verify Surface recreation, rotation, background/foreground, overlay fallback, reconnect, long playback, repeated start/stop, and concurrent streams."
            }
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:FRAMEFLUX_STABILITY_ITERATIONS = $previousIterations
}
