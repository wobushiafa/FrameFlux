namespace FrameFlux;

public sealed record RtspSource
{
    public const string RtspScheme = "rtsp";

    public RtspSource(Uri uri, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            (!string.Equals(uri.Scheme, RtspScheme, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, "rtsps", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The source must use the rtsp or rtsps scheme.", nameof(uri));
        }

        Uri = uri;
        DisplayName = displayName;
    }

    public Uri Uri { get; }

    public string? DisplayName { get; }

    public static RtspSource Parse(string value, string? displayName = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The RTSP source URI is invalid.", nameof(value));
        }

        return new RtspSource(uri, displayName);
    }

    public override string ToString() => Uri.ToString();
}
