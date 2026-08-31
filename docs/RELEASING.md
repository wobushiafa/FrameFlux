# Releasing FrameFlux

FrameFlux separates managed integration packages from optional native FFmpeg redistribution packages. Release them as two independently reviewed groups.

## Hard gates

All FrameFlux packages must declare the approved MIT license through `PackageLicenseExpression`, and the repository must contain the matching `LICENSE` file.

Do not publish any `FrameFlux.FFmpeg.NativeAssets.*` package until the relevant provenance, license texts, build configuration, and source obligations in `THIRD-PARTY-NOTICES.md` are resolved. Android assets additionally require verified 16 KB ELF LOAD alignment.

## Managed package set

The supported managed package set is explicitly listed in `eng/pack-managed.ps1`. Example applications are not packable and must never appear in release output.

From the repository root, validate and pack the managed packages:

```powershell
dotnet build FrameFlux.slnx -c Release -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
dotnet test FrameFlux.slnx -c Release --no-build -p:FrameFluxAllowUnsupportedAndroidPageAlignment=true
.\eng\pack-managed.ps1
```

The Android alignment override only permits local validation of managed code. It is not approval to publish the current Android native binaries.

## Package inspection

The managed output must contain exactly one `.nupkg` and one `.snupkg` for each project listed by the script. Confirm that:

- No package ID begins with `FrameFlux.Demo`.
- Every package contains its README and repository metadata.
- Internal FrameFlux dependencies use exact versions matching the release version.
- Package contents do not contain repository-local native binaries unless the package is an approved native asset package.

## Consumer validation

Create disposable applications using only the local package directory as a package source and build at least these combinations:

- .NET 8 console application referencing `FrameFlux.FFmpeg`.
- .NET 8 Windows WPF application referencing `FrameFlux.Wpf`.
- .NET 8 Avalonia desktop application referencing the core Avalonia and desktop platform packages.
- .NET 10 Android application referencing `FrameFlux.Avalonia.Android`.

Run real-device smoke tests for each platform backend before promoting a version from prerelease to stable.

## Publication

Review `CHANGELOG.md`, set the release date, and ensure the package version matches the intended tag. Committing, pushing, tagging, and uploading packages are separate operations and require explicit approval.
