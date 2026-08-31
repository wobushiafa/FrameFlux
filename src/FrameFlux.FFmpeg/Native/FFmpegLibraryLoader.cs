using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal enum FFmpegLibraryPlatform
{
    Windows,
    Linux,
    MacOS
}

internal static class FFmpegLibraryLoader
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, IntPtr> ComponentHandles = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoadedPaths = new(PathComparer);
    private static readonly string[] RequiredComponents = ["avutil", "swresample", "swscale", "avcodec", "avformat"];
    private static readonly string[] LoadOrder = ["avutil", "swresample", "swscale", "avcodec", "avformat"];
    private static readonly string[] AndroidLoadOrder = ["avcodec", "avutil", "swresample", "swscale", "avformat"];
    private static string? _configuredDirectory;

    internal static void Configure(string? libraryDirectory)
    {
        var normalizedDirectory = NormalizeDirectory(libraryDirectory);
        lock (SyncRoot)
        {
            if (ComponentHandles.Count > 0 && !PathEquals(_configuredDirectory, normalizedDirectory))
            {
                throw new InvalidOperationException(
                    "FFmpeg has already been loaded. Configure its library directory before creating a player or session.");
            }

            if (_configuredDirectory is not null && !PathEquals(_configuredDirectory, normalizedDirectory))
            {
                throw new InvalidOperationException(
                    $"FFmpeg is already configured to use '{_configuredDirectory}'.");
            }

            _configuredDirectory = normalizedDirectory;
        }
    }

    internal static IntPtr GetExport(string component, string exportName)
    {
        EnsureLoaded();
        if (!ComponentHandles.TryGetValue(component, out var handle) ||
            !NativeLibrary.TryGetExport(handle, exportName, out var address))
        {
            throw new EntryPointNotFoundException(
                $"FFmpeg component '{component}' does not export '{exportName}'. " +
                "Verify that all shared libraries come from one compatible FFmpeg build.");
        }

        return address;
    }

    internal static void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (RequiredComponents.All(ComponentHandles.ContainsKey))
            {
                return;
            }

            var directories = _configuredDirectory is null
                ? GetCandidateDirectories(AppContext.BaseDirectory, RuntimeInformation.RuntimeIdentifier)
                : GetCandidateDirectories(_configuredDirectory, RuntimeInformation.RuntimeIdentifier);
            if (_configuredDirectory is not null)
            {
                ValidateFFmpegDirectory(directories);
            }

            LoadComponents(directories);
            var missing = RequiredComponents.Where(component => !ComponentHandles.ContainsKey(component)).ToArray();
            if (missing.Length > 0)
            {
                var location = _configuredDirectory ?? AppContext.BaseDirectory;
                throw new DllNotFoundException(
                    $"Unable to load FFmpeg components from '{location}': {string.Join(", ", missing)}. " +
                    "Call FFmpegHelper.RegisterFFmpeg with the directory containing the current platform's FFmpeg shared libraries.");
            }
        }
    }

    internal static string[] GetCandidateDirectories(string rootDirectory, string runtimeIdentifier)
    {
        var root = Path.GetFullPath(rootDirectory);
        return new[]
            {
                root,
                Path.Combine(root, "native"),
                Path.Combine(root, runtimeIdentifier, "native"),
                Path.Combine(root, "runtimes", runtimeIdentifier, "native")
            }
            .Where(Directory.Exists)
            .Distinct(PathComparer)
            .ToArray();
    }

    internal static string? FindBestLibraryFile(
        IEnumerable<string> directories,
        string component,
        FFmpegLibraryPlatform platform) =>
        directories
            .SelectMany(Directory.EnumerateFiles)
            .Where(path => IsComponentLibrary(Path.GetFileName(path), component, platform))
            .OrderDescending(NaturalFileNameComparer.Instance)
            .FirstOrDefault();

    private static void ValidateFFmpegDirectory(IReadOnlyCollection<string> directories)
    {
        var platform = GetPlatform();
        var missing = RequiredComponents
            .Where(component => FindBestLibraryFile(directories, component, platform) is null)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new DllNotFoundException(
                $"The configured FFmpeg directory '{_configuredDirectory}' is missing shared libraries for: " +
                string.Join(", ", missing) + ".");
        }
    }

    private static void LoadComponents(IReadOnlyCollection<string> directories)
    {
        var platform = GetPlatform();
        if (OperatingSystem.IsAndroid())
        {
            LoadOptional(directories, "libc++_shared.so");
        }

        var selectedFiles = RequiredComponents
            .Select(component => (Component: component, File: FindBestLibraryFile(directories, component, platform)))
            .Where(selection => selection.File is not null)
            .ToDictionary(selection => selection.Component, selection => selection.File!, StringComparer.Ordinal);
        if (selectedFiles.Count is > 0 and < 5)
        {
            var missing = RequiredComponents.Where(component => !selectedFiles.ContainsKey(component));
            throw new DllNotFoundException(
                "A partial FFmpeg library set was found beside the application. " +
                $"Missing: {string.Join(", ", missing)}. " +
                "Ship all five components from one build, or rely entirely on system libraries.");
        }

        ValidateSelectedLibraryVersions(selectedFiles, platform);

        var pendingHandles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var pendingPaths = new List<string>();
        try
        {
            var loadOrder = OperatingSystem.IsAndroid() ? AndroidLoadOrder : LoadOrder;
            foreach (var component in loadOrder)
            {
                if (ComponentHandles.ContainsKey(component))
                {
                    continue;
                }

                if (selectedFiles.TryGetValue(component, out var file))
                {
                    pendingHandles[component] = LoadFile(file, component);
                    pendingPaths.Add(file);
                    continue;
                }

                foreach (var name in GetPlatformLibraryNames(component))
                {
                    if (NativeLibrary.TryLoad(name, out var handle))
                    {
                        pendingHandles[component] = handle;
                        break;
                    }
                }
            }

            var missing = RequiredComponents
                .Where(component => !ComponentHandles.ContainsKey(component) &&
                                    !pendingHandles.ContainsKey(component))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new DllNotFoundException(
                    $"Unable to load FFmpeg components: {string.Join(", ", missing)}.");
            }

            foreach (var (component, handle) in pendingHandles)
            {
                ComponentHandles[component] = handle;
            }

            foreach (var path in pendingPaths)
            {
                LoadedPaths.Add(path);
            }
        }
        catch
        {
            foreach (var handle in pendingHandles.Values)
            {
                NativeLibrary.Free(handle);
            }

            throw;
        }
    }

    internal static void ValidateSelectedLibraryVersions(
        IReadOnlyDictionary<string, string> files,
        FFmpegLibraryPlatform platform)
    {
        if (!files.TryGetValue("avcodec", out var codecFile))
        {
            return;
        }

        var codecMajor = GetLibraryMajorVersion(codecFile, "avcodec", platform);
        if (codecMajor is null)
        {
            return;
        }

        var expected = codecMajor.Value switch
        {
            60 => new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["avcodec"] = 60, ["avformat"] = 60, ["avutil"] = 58,
                ["swscale"] = 7, ["swresample"] = 4
            },
            61 => new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["avcodec"] = 61, ["avformat"] = 61, ["avutil"] = 59,
                ["swscale"] = 8, ["swresample"] = 5
            },
            62 => new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["avcodec"] = 62, ["avformat"] = 62, ["avutil"] = 60,
                ["swscale"] = 9, ["swresample"] = 6
            },
            _ => throw new NotSupportedException(
                $"The selected avcodec library has unsupported major version {codecMajor}.")
        };

        foreach (var (component, file) in files)
        {
            var actualMajor = GetLibraryMajorVersion(file, component, platform);
            if (actualMajor is not null && actualMajor != expected[component])
            {
                throw new NotSupportedException(
                    $"The selected FFmpeg files mix incompatible ABI families: " +
                    $"{Path.GetFileName(codecFile)} requires {component} major {expected[component]}, " +
                    $"but {Path.GetFileName(file)} has major {actualMajor}.");
            }
        }
    }

    private static int? GetLibraryMajorVersion(
        string path,
        string component,
        FFmpegLibraryPlatform platform)
    {
        var fileName = Path.GetFileName(path);
        string? versionText = null;
        if (platform == FFmpegLibraryPlatform.Windows)
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (baseName.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[3..];
            }

            var prefix = component + "-";
            if (baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                versionText = baseName[prefix.Length..];
            }
        }
        else if (platform == FFmpegLibraryPlatform.MacOS)
        {
            var prefix = $"lib{component}.";
            if (fileName.StartsWith(prefix, StringComparison.Ordinal) &&
                !fileName.Equals($"lib{component}.dylib", StringComparison.Ordinal))
            {
                versionText = fileName[prefix.Length..];
            }
        }
        else
        {
            var marker = ".so.";
            var markerIndex = fileName.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                versionText = fileName[(markerIndex + marker.Length)..];
            }
        }

        if (versionText is null)
        {
            return null;
        }

        var digitCount = 0;
        while (digitCount < versionText.Length && char.IsDigit(versionText[digitCount]))
        {
            digitCount++;
        }

        return digitCount > 0 && int.TryParse(versionText[..digitCount], out var major)
            ? major
            : null;
    }

    private static IntPtr LoadFile(string path, string component)
    {
        if (LoadedPaths.Contains(path) && ComponentHandles.TryGetValue(component, out var existing))
        {
            return existing;
        }

        try
        {
            return NativeLibrary.Load(path);
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            throw new DllNotFoundException(
                $"Failed to load FFmpeg component '{path}'. " +
                "Verify that every file uses the current process architecture and comes from the same FFmpeg build.",
                exception);
        }
    }

    private static void LoadOptional(IEnumerable<string> directories, string fileName)
    {
        var path = directories.Select(directory => Path.Combine(directory, fileName)).FirstOrDefault(File.Exists);
        if (path is not null && !LoadedPaths.Contains(path) && NativeLibrary.TryLoad(path, out _))
        {
            LoadedPaths.Add(path);
        }
    }

    private static IEnumerable<string> GetPlatformLibraryNames(string component)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return $"{component}.dll";
            yield return $"lib{component}.dll";
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return $"lib{component}.dylib";
            yield break;
        }

        if (OperatingSystem.IsAndroid() && RuntimeInformation.ProcessArchitecture == Architecture.Arm)
        {
            yield return $"lib{component}_neon.so";
        }

        yield return $"lib{component}.so";
    }

    private static string? NormalizeDirectory(string? libraryDirectory)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(libraryDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The configured FFmpeg library directory does not exist: '{fullPath}'.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsComponentLibrary(string fileName, string component, FFmpegLibraryPlatform platform)
    {
        if (platform == FFmpegLibraryPlatform.Windows)
        {
            if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            return baseName.Equals(component, StringComparison.OrdinalIgnoreCase) ||
                   baseName.Equals($"lib{component}", StringComparison.OrdinalIgnoreCase) ||
                   baseName.StartsWith($"{component}-", StringComparison.OrdinalIgnoreCase) ||
                   baseName.StartsWith($"lib{component}-", StringComparison.OrdinalIgnoreCase);
        }

        var prefix = $"lib{component}";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = fileName[prefix.Length..];
        return platform == FFmpegLibraryPlatform.MacOS
            ? suffix.Equals(".dylib", StringComparison.Ordinal) ||
              suffix.EndsWith(".dylib", StringComparison.Ordinal) && suffix[0] == '.'
            : suffix.Equals(".so", StringComparison.Ordinal) ||
              suffix.StartsWith(".so.", StringComparison.Ordinal) ||
              suffix.StartsWith("_", StringComparison.Ordinal) && suffix.Contains(".so", StringComparison.Ordinal);
    }

    private static FFmpegLibraryPlatform GetPlatform() =>
        OperatingSystem.IsWindows()
            ? FFmpegLibraryPlatform.Windows
            : OperatingSystem.IsMacOS() ? FFmpegLibraryPlatform.MacOS : FFmpegLibraryPlatform.Linux;

    private static bool PathEquals(string? left, string? right) => PathComparer.Equals(left, right);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class NaturalFileNameComparer : IComparer<string>
    {
        internal static readonly NaturalFileNameComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftName = Path.GetFileName(left);
            var rightName = Path.GetFileName(right);
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < leftName.Length && rightIndex < rightName.Length)
            {
                if (char.IsDigit(leftName[leftIndex]) && char.IsDigit(rightName[rightIndex]))
                {
                    var numberResult = ReadNumber(leftName, ref leftIndex).CompareTo(
                        ReadNumber(rightName, ref rightIndex));
                    if (numberResult != 0) return numberResult;
                    continue;
                }

                var characterResult = char.ToUpperInvariant(leftName[leftIndex])
                    .CompareTo(char.ToUpperInvariant(rightName[rightIndex]));
                if (characterResult != 0) return characterResult;
                leftIndex++;
                rightIndex++;
            }

            return leftName.Length.CompareTo(rightName.Length);
        }

        private static ulong ReadNumber(string value, ref int index)
        {
            ulong result = 0;
            while (index < value.Length && char.IsDigit(value[index]))
            {
                result = result * 10 + (uint)(value[index] - '0');
                index++;
            }

            return result;
        }
    }
}
