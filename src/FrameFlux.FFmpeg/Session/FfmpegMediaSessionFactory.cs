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
    private readonly SemaphoreSlim? _openOperationSemaphore;

    internal FfmpegMediaSessionFactory(
        ILoggerFactory? loggerFactory = null,
        FfmpegMediaPlayerFactoryOptions? options = null)
        : this(
            loggerFactory ?? NullLoggerFactory.Instance,
            new SharedMediaSessionPool(),
            options ?? new FfmpegMediaPlayerFactoryOptions())
    {
    }

    internal FfmpegMediaSessionFactory(
        ILoggerFactory loggerFactory,
        SharedMediaSessionPool sharedPool,
        FfmpegMediaPlayerFactoryOptions? options = null)
    {
        options ??= new FfmpegMediaPlayerFactoryOptions();
        options.Validate();
        _loggerFactory = loggerFactory;
        _sharedPool = sharedPool;
        _openOperationSemaphore = options.MaximumConcurrentOpenOperations is { } limit
            ? new SemaphoreSlim(limit, limit)
            : null;
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

        if (options.SessionSharing == MediaSessionSharingMode.Shared)
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
            _openOperationSemaphore,
            _loggerFactory.CreateLogger<FfmpegMediaSession>());
}
