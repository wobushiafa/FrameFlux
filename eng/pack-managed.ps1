[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory = 'artifacts/packages/managed',

    [string] $Version
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))

$projects = @(
    'src/FrameFlux.Abstractions/FrameFlux.Abstractions.csproj'
    'src/FrameFlux.FFmpeg/FrameFlux.FFmpeg.csproj'
    'src/FrameFlux.FFmpeg.Android/FrameFlux.FFmpeg.Android.csproj'
    'src/FrameFlux.Presentation/FrameFlux.Presentation.csproj'
    'src/FrameFlux.Rendering.Windows/FrameFlux.Rendering.Windows.csproj'
    'src/FrameFlux.Wpf/FrameFlux.Wpf.csproj'
    'src/FrameFlux.Avalonia/FrameFlux.Avalonia.csproj'
    'src/FrameFlux.Avalonia.Windows/FrameFlux.Avalonia.Windows.csproj'
    'src/FrameFlux.Avalonia.Linux/FrameFlux.Avalonia.Linux.csproj'
    'src/FrameFlux.Avalonia.Android/FrameFlux.Avalonia.Android.csproj'
)

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

foreach ($project in $projects) {
    $projectPath = Join-Path $repositoryRoot $project
    $arguments = @(
        'pack'
        $projectPath
        '--configuration'
        $Configuration
        '--output'
        $outputPath
        '-p:FrameFluxAllowUnsupportedAndroidPageAlignment=true'
    )

    if ($Version) {
        $arguments += "-p:Version=$Version"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Packing failed for $project."
    }
}

Write-Host "Managed packages written to $outputPath"
