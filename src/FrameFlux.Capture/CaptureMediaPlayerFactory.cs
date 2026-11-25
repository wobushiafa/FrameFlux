namespace FrameFlux.Capture;

/// <summary>
/// Factory for creating <see cref="CaptureMediaPlayer"/> instances.
/// </summary>
public sealed class CaptureMediaPlayerFactory : IMediaPlayerFactory
{
    private readonly CapturePlayerOptions _options;

    public static CaptureMediaPlayerFactory Instance { get; } = new();

    public CaptureMediaPlayerFactory(CapturePlayerOptions? options = null)
    {
        _options = options ?? new CapturePlayerOptions();
        _options.Validate();
    }

    public IMediaPlayer Create() => new CaptureMediaPlayer(_options);
}
