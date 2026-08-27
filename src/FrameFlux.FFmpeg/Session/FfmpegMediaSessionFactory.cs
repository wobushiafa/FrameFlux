using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlux.FFmpeg;

internal interface IFfmpegMediaSessionFactory
{
    IFfmpegMediaSession Create(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        IMediaVideoOutput? videoOutput);
}

internal sealed class FfmpegMediaSessionFactory : IFfmpegMediaSessionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SharedMediaSessionPool _sharedPool;

    internal FfmpegMediaSessionFactory(ILoggerFactory? loggerFactory = null)
        : this(loggerFactory ?? NullLoggerFactory.Instance, new SharedMediaSessionPool())
    {
    }

    internal FfmpegMediaSessionFactory(
        ILoggerFactory loggerFactory,
        SharedMediaSessionPool sharedPool)
    {
        _loggerFactory = loggerFactory;
        _sharedPool = sharedPool;
    }

    public IFfmpegMediaSession Create(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        IMediaVideoOutput? videoOutput)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.StreamSharing == MediaStreamSharingMode.Shared)
        {
            return _sharedPool.Acquire(
                source,
                options,
                volume,
                isMuted,
                () => CreateDedicated(source, options, volume, isMuted, videoOutput: null),
                videoOutput);
        }

        return CreateDedicated(source, options, volume, isMuted, videoOutput);
    }

    private IFfmpegMediaSession CreateDedicated(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        IMediaVideoOutput? videoOutput) =>
        new FfmpegMediaSession(
            source,
            options,
            volume,
            isMuted,
            videoOutput,
            _loggerFactory.CreateLogger<FfmpegMediaSession>());
}
