[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repositoryRoot 'artifacts/sources'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$sources = @(
    @{
        Name = 'ffmpeg-9.0.1.tar.gz'
        Url = 'https://codeload.github.com/arthenica/FFmpeg/tar.gz/bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa'
        Sha256 = 'fb1931fd4eb29297ee1c1017a24f800c4d8fbea35b4f2aaeb28308a48a9149b4'
    }
    @{
        Name = 'ffmpeg-kit-next-9.0.0.tar.gz'
        Url = 'https://codeload.github.com/arthenica/ffmpeg-kit-next/tar.gz/5e51b2da4c3593c0f2f9b49f53eeb497d93e39d3'
        Sha256 = '4eb50b840334b22e72b3d02fa72c9b39cf014372bae163896e8bdab9cc0e4b7d'
    }
    @{
        Name = 'cpu-features-0.11.0.tar.gz'
        Url = 'https://codeload.github.com/arthenica/cpu_features/tar.gz/refs/tags/v0.11.0'
        Sha256 = 'ab2463f2d38fcaff1ce806be8e4c91333449931f5e02009d543b2569a3fa471a'
    }
)
foreach ($source in $sources) {
    $path = Join-Path $outputDirectory $source.Name
    if (-not (Test-Path -LiteralPath $path)) {
        Invoke-WebRequest -Uri $source.Url -OutFile $path -MaximumRetryCount 3
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $source.Sha256) {
        throw "Source archive checksum mismatch: $($source.Name)"
    }
    Write-Host "Verified $($source.Name)"
}
