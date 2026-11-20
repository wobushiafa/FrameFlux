param(
    [string]$OutputDirectory = '',
    [string]$Configuration = 'Release',
    [switch]$IncludeNativeAotPrototype
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts/unity-managed' }
$publishDirectory = Join-Path $OutputDirectory 'native'
$packageDirectory = Join-Path $OutputDirectory 'com.frameflux.player'
$pluginDirectory = Join-Path $packageDirectory 'Runtime/Plugins/x86_64'
$managedPublishDirectory = Join-Path $OutputDirectory 'managed'
$managedDirectory = Join-Path $packageDirectory 'Runtime/Plugins/Managed'
$project = Join-Path $repositoryRoot 'unity/FrameFlux.Unity.Dependencies.csproj'
& dotnet build $project -c $Configuration -p:TargetFrameworks=netstandard2.1 -p:TargetFramework=netstandard2.1 -o $managedPublishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Unity managed dependency build failed.' }
if ($IncludeNativeAotPrototype) {
$nativeProject = Join-Path $repositoryRoot 'src/FrameFlux.Unity.Native/FrameFlux.Unity.Native.csproj'

# TargetFrameworks limits the FFmpeg project reference to desktop during restore.
& dotnet publish $nativeProject -c $Configuration -r win-x64 `
    -p:FrameFluxUnityPublishAot=true -p:TargetFrameworks=net8.0 -p:TargetFramework=net8.0 -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'NativeAOT publish failed. See the compiler output above.' }

# Never package a managed .NET DLL if the AOT build targets were not imported.
$publishedLibrary = Join-Path $publishDirectory 'FrameFluxUnity.dll'
$stream = [System.IO.File]::OpenRead($publishedLibrary)
$reader = New-Object System.IO.BinaryReader($stream)
try {
    $stream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $stream.Position = $peOffset + 24
    if ($reader.ReadUInt16() -ne 0x20b) { throw 'Expected a Windows x64 PE32+ native DLL.' }
    $stream.Position = $peOffset + 24 + 112 + 14 * 8
    if ($reader.ReadUInt32() -ne 0) { throw 'The published DLL is managed; NativeAOT compilation did not run.' }
} finally { $reader.Dispose(); $stream.Dispose() }

 }

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $repositoryRoot 'unity/com.frameflux.player/*') -Destination $packageDirectory -Recurse -Force
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
if ($IncludeNativeAotPrototype) { Copy-Item -LiteralPath (Join-Path $publishDirectory 'FrameFluxUnity.dll') -Destination $pluginDirectory -Force }
Copy-Item -Path (Join-Path $repositoryRoot 'native/artifacts/runtimes/win-x64/native/*.dll') -Destination $pluginDirectory -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $packageDirectory 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $packageDirectory -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native/licenses') -Destination (Join-Path $packageDirectory 'ThirdPartyLicenses') -Recurse -Force

New-Item -ItemType Directory -Path $managedDirectory -Force | Out-Null
# BCL reference/facade assemblies already supplied by Unity's .NET Standard 2.1
# profile must not be imported a second time. Retain actual extension packages.
$extensions = @('System.Diagnostics.DiagnosticSource.dll', 'System.IO.Pipelines.dll',
    'System.Net.IPNetwork.dll', 'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encodings.Web.dll', 'System.Text.Json.dll', 'System.Threading.Channels.dll')
foreach ($library in Get-ChildItem -LiteralPath $managedPublishDirectory -Filter '*.dll') {
    if ($library.Name -eq 'FrameFlux.Unity.Dependencies.dll') { continue }
    if ($library.Name.StartsWith('System.') -and $library.Name -notin $extensions) { continue }
    if ($library.Name -in @('Microsoft.CSharp.dll', 'Microsoft.Win32.Registry.dll')) { continue }
    Copy-Item -LiteralPath $library.FullName -Destination $managedDirectory -Force
}

# Makaretu's Windows mDNS path never invokes libc, but its assembly metadata
# references Tmds.LibC. IL2CPP needs that assembly even on Windows.
$assets = Get-Content -LiteralPath (Join-Path $repositoryRoot 'unity/obj/project.assets.json') -Raw | ConvertFrom-Json
$libcPackage = $assets.libraries.PSObject.Properties | Where-Object { $_.Name.StartsWith('Tmds.LibC/') } | Select-Object -First 1
if ($libcPackage) {
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $libcLibrary = Join-Path $folder ($libcPackage.Value.path + '/runtimes/linux-x64/lib/netstandard2.0/Tmds.LibC.dll')
        if (Test-Path -LiteralPath $libcLibrary) { Copy-Item -LiteralPath $libcLibrary -Destination $managedDirectory -Force; break }
    }
    if (!(Test-Path -LiteralPath (Join-Path $managedDirectory 'Tmds.LibC.dll'))) { throw 'The mDNS metadata dependency Tmds.LibC.dll is missing.' }
}

# Preserve each bundled managed dependency's package metadata and supplied license texts.
# Older packages without embedded licenses retain their upstream URL in the inventory.
$managedNotices = Join-Path $packageDirectory 'ThirdPartyLicenses/Managed'
New-Item -ItemType Directory -Path $managedNotices -Force | Out-Null
$bundledNames = @(Get-ChildItem -LiteralPath $managedDirectory -Filter '*.dll' | Select-Object -ExpandProperty Name)
$targetProperty = $assets.targets.PSObject.Properties |
    Where-Object { $_.Name -in @('netstandard2.1', '.NETStandard,Version=v2.1') } |
    Select-Object -First 1
if (!$targetProperty) { throw 'Missing netstandard2.1 target in project.assets.json.' }
$targetLibraries = $targetProperty.Value
$dependencyInventory = @()
foreach ($entry in $assets.libraries.PSObject.Properties) {
    if ($entry.Value.type -ne 'package') { continue }
    $resolvedProperty = $targetLibraries.PSObject.Properties[$entry.Name]
    $resolvedAssets = if ($resolvedProperty) { $resolvedProperty.Value } else { $null }
    $runtimeProperty = if ($resolvedAssets) { $resolvedAssets.PSObject.Properties['runtime'] } else { $null }
    $runtimeNames = if ($runtimeProperty -and $runtimeProperty.Value) {
        @($runtimeProperty.Value.PSObject.Properties.Name | ForEach-Object { [IO.Path]::GetFileName($_) })
    } else {
        @()
    }
    if ($entry.Name.StartsWith('Tmds.LibC/')) { $runtimeNames += 'Tmds.LibC.dll' }
    $includedNames = @($runtimeNames | Where-Object { $_ -in $bundledNames })
    if (!$includedNames.Count) { continue }
    $cacheDirectory = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidateDirectory = Join-Path $folder $entry.Value.path
        if (Test-Path -LiteralPath $candidateDirectory) { $cacheDirectory = $candidateDirectory; break }
    }
    if (!$cacheDirectory) { throw "Missing package metadata for $($entry.Name)." }
    $noticeDirectory = Join-Path $managedNotices ($entry.Name.Replace('/', '-'))
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
    $nuspec = Get-ChildItem -LiteralPath $cacheDirectory -Filter '*.nuspec' | Select-Object -First 1
    if (!$nuspec) { throw "Missing nuspec for $($entry.Name)." }
    Copy-Item -LiteralPath $nuspec.FullName -Destination $noticeDirectory -Force
    [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
    $licenseNode = $metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
    $urlNode = $metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='projectUrl']")
    foreach ($notice in Get-ChildItem -LiteralPath $cacheDirectory -Recurse -File | Where-Object { $_.Name -match '(?i)license|notice|copying' }) {
        $noticeRelative = $notice.FullName.Substring($cacheDirectory.Length + 1)
        $destination = Join-Path $noticeDirectory $noticeRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $notice.FullName -Destination $destination -Force
    }
    $dependencyInventory += [ordered]@{
        package = $entry.Name; assemblies = $includedNames
        license = if ($licenseNode) { $licenseNode.InnerText } else { 'See upstream package metadata' }
        licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { '' }
        projectUrl = if ($urlNode) { $urlNode.InnerText } else { '' }
    }
}
$dependencyInventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageDirectory 'ManagedDependencies.json') -Encoding utf8
$assemblyInventory = @(Get-ChildItem -LiteralPath $managedDirectory -Filter '*.dll' | ForEach-Object {
    [ordered]@{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$assemblyInventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageDirectory 'AssemblyInventory.json') -Encoding utf8

# Restrict every native plugin to Windows x64 and the Windows Editor.
foreach ($library in Get-ChildItem -LiteralPath $pluginDirectory -Filter '*.dll') {
    $hasher = [System.Security.Cryptography.MD5]::Create()
    try {
        $guid = [BitConverter]::ToString($hasher.ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes('FrameFlux/plugin/' + $library.Name))).Replace('-', '').ToLowerInvariant()
    } finally { $hasher.Dispose() }
    @"
fileFormatVersion: 2
guid: $guid
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        OS: Windows
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: x86_64
  userData:
  assetBundleName:
  assetBundleVariant:
"@ | Set-Content -LiteralPath ($library.FullName + '.meta') -Encoding utf8
}
Write-Host "Unity package: $packageDirectory"
Write-Host "In Unity Package Manager choose Add package from disk and select package.json."

foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -Recurse -File) {
    if ($file.Extension -eq '.meta') { continue }
    $managedLibrary = $file.Extension -eq '.dll' -and $file.DirectoryName -eq $managedDirectory
    if (!$managedLibrary -and (Test-Path -LiteralPath ($file.FullName + '.meta'))) { continue }
    $relative = $file.FullName.Substring($packageDirectory.Length).Replace('\', '/')
    $hasher = [System.Security.Cryptography.MD5]::Create()
    try { $guid = [BitConverter]::ToString($hasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes('FrameFlux/package' + $relative))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    if ($file.Extension -eq '.dll') {
        # These assemblies supply dependency metadata; exposing their BCL types
        # to Unity's C# compiler would duplicate the .NET Standard profile.
        $explicit = if ($file.Name -in @('Microsoft.Bcl.AsyncInterfaces.dll', 'Tmds.LibC.dll')) { 1 } else { 0 }
        @"
fileFormatVersion: 2
guid: $guid
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: $explicit
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
        OS: Windows
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
"@ | Set-Content -LiteralPath ($file.FullName + '.meta') -Encoding utf8
    } else {
        "fileFormatVersion: 2`nguid: $guid`n" | Set-Content -LiteralPath ($file.FullName + '.meta') -Encoding utf8
    }
}

foreach ($directory in Get-ChildItem -LiteralPath $packageDirectory -Recurse -Directory) {
    $metaPath = $directory.FullName + '.meta'
    if (Test-Path -LiteralPath $metaPath) { continue }
    $relative = $directory.FullName.Substring($packageDirectory.Length).Replace('\', '/')
    $hasher = [System.Security.Cryptography.MD5]::Create()
    try { $guid = [BitConverter]::ToString($hasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes('FrameFlux/package' + $relative))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    "fileFormatVersion: 2`nguid: $guid`nfolderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n" | Set-Content -LiteralPath $metaPath -Encoding utf8
}
