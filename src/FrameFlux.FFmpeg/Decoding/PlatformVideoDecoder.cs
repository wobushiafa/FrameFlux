namespace FrameFlux.FFmpeg;

internal interface IPlatformVideoDecoderFactory
{
    bool CanCreate(IMediaVideoOutput? output, FfmpegPlaybackOptions options);

    IPlatformVideoDecoder Create(
        string url,
        FfmpegPlaybackOptions options,
        IMediaVideoOutput output,
        CancellationToken cancellationToken);
}

internal interface IPlatformVideoDecoder : IDisposable
{
    bool HasAudio { get; }

    bool IsHardwareVideoDecodingActive { get; }

    string VideoDecoderDiagnostics { get; }

    long LastReadTicks { get; }

    long LastCodecTicks { get; }

    bool TryDecodeNextFrame(out IPlatformDecodedVideoFrame? frame);

    bool TryDequeueAudioFrame(out NativeAudioFrame? frame);
}

internal interface IPlatformDecodedVideoFrame : IDisposable
{
    int Width { get; }

    int Height { get; }

    double? PresentationSeconds { get; }

    void Present();
}

internal static class PlatformVideoDecoderRegistry
{
    private static IPlatformVideoDecoderFactory? _androidFactory;

    internal static void RegisterAndroidFactory(IPlatformVideoDecoderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var existing = Interlocked.CompareExchange(ref _androidFactory, factory, null);
        if (existing is not null && existing.GetType() != factory.GetType())
        {
            throw new InvalidOperationException(
                "A FrameFlux Android video decoder backend is already registered.");
        }
    }

    internal static IPlatformVideoDecoder? TryCreate(
        string url,
        FfmpegPlaybackOptions options,
        IMediaVideoOutput? output,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroid() ||
            output is null ||
            !FfmpegPlaybackConfiguration.UsesHardwareDecoding(options.VideoDecodingMode))
        {
            return null;
        }

        var factory = Volatile.Read(ref _androidFactory);
        return factory?.CanCreate(output, options) == true
            ? factory.Create(url, options, output, cancellationToken)
            : null;
    }
}
