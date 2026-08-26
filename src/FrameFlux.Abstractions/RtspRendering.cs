namespace FrameFlux;

public sealed record RtspPlatformCapabilities(
    string Platform,
    bool SupportsHardwareDecoding,
    IReadOnlySet<RtspRenderPreference> SupportedRenderPreferences)
{
    public static RtspPlatformCapabilities DetectCurrent()
    {
        var platform = OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsLinux() ? "Linux" :
            OperatingSystem.IsAndroid() ? "Android" :
            OperatingSystem.IsIOS() ? "iOS" :
            OperatingSystem.IsMacOS() ? "macOS" : "Unknown";

        var nativeSurfaceSupported =
            OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsAndroid() ||
            OperatingSystem.IsMacOS() ||
            OperatingSystem.IsIOS();

        var preferences = new HashSet<RtspRenderPreference>
        {
            RtspRenderPreference.Software
        };
        if (nativeSurfaceSupported)
        {
            preferences.Add(RtspRenderPreference.NativeSurface);
        }

        return new RtspPlatformCapabilities(
            platform,
            nativeSurfaceSupported || OperatingSystem.IsWindows(),
            preferences);
    }
}

public interface IRtspRendererBackend
{
    string Id { get; }

    RtspRenderPreference Preference { get; }

    int Priority { get; }

    bool IsSupported(RtspPlatformCapabilities capabilities);
}

public sealed class RtspRendererBackendRegistry
{
    private readonly List<IRtspRendererBackend> _backends = [];

    public IReadOnlyList<IRtspRendererBackend> Backends => _backends;

    public void Register(IRtspRendererBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (_backends.Any(candidate => string.Equals(candidate.Id, backend.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"A renderer backend named '{backend.Id}' is already registered.");
        }

        _backends.Add(backend);
    }

    public IRtspRendererBackend? Select(
        RtspRenderPreference requestedPreference,
        RtspPlatformCapabilities? capabilities = null)
    {
        capabilities ??= RtspPlatformCapabilities.DetectCurrent();
        var compatible = _backends.Where(backend => backend.IsSupported(capabilities));
        if (requestedPreference != RtspRenderPreference.Auto)
        {
            compatible = compatible.Where(backend => backend.Preference == requestedPreference);
        }

        return compatible
            .OrderByDescending(backend => backend.Priority)
            .FirstOrDefault();
    }
}

public sealed record RtspVideoTransform(
    int RotationDegrees = 0,
    bool MirrorHorizontally = false,
    bool MirrorVertically = false)
{
    public int NormalizedRotationDegrees
    {
        get
        {
            var normalized = RotationDegrees % 360;
            return normalized < 0 ? normalized + 360 : normalized;
        }
    }
}
