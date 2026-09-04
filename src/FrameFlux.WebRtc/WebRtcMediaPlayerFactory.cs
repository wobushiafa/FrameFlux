namespace FrameFlux.WebRtc;

/// <summary>
/// Factory for creating <see cref="WebRtcMediaPlayer"/> instances.
/// Implements <see cref="IMediaPlayerFactory"/> for direct injection into Avalonia and WPF MediaView controls.
/// </summary>
public sealed class WebRtcMediaPlayerFactory : IMediaPlayerFactory
{
    private readonly WebRtcPlayerOptions _options;

    /// <summary>
    /// Default singleton instance for quick assignment: <c>mediaView.PlayerFactory = WebRtcMediaPlayerFactory.Instance;</c>
    /// </summary>
    public static WebRtcMediaPlayerFactory Instance { get; } = new();

    public WebRtcMediaPlayerFactory(WebRtcPlayerOptions? options = null)
    {
        _options = options ?? new WebRtcPlayerOptions();
    }

    /// <summary>
    /// Creates a new <see cref="WebRtcMediaPlayer"/> instance configured with the factory's options.
    /// </summary>
    public IMediaPlayer Create()
    {
        return new WebRtcMediaPlayer(_options);
    }
}
