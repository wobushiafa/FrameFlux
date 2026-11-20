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
    private readonly Func<int, int, IMediaAudioOutput>? _audioOutputFactory;

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
        _audioOutputFactory = options.AudioOutputFactory;
        _openOperationSemaphore = options.MaximumConcurrentOpenOperations is { } limit
            ? FfmpegOpenStreamLimiter.GetSemaphore(limit)
            : null;
    }

    public IFfmpegMediaSession Create(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        IMediaVideoOutput? videoOutput)
    {
        RuntimeCompat.NotNull(source, nameof(source));
        RuntimeCompat.NotNull(options, nameof(options));
        options.Validate();

        if (options.SessionSharing == MediaSessionSharingMode.Shared)
        {
            if (_audioOutputFactory is not null)
                throw new NotSupportedException("Custom audio outputs require a dedicated media session.");
            return _sharedPool.Acquire(
                source,
                options,
                volume,
                isMuted,
                () => CreateDedicated(
                    source,
                    options,
                    volume,
                    isMuted,
                    videoOutput: null,
                    isSharedPhysicalSession: true),
                videoOutput);
        }

        return CreateDedicated(
            source,
            options,
            volume,
            isMuted,
            videoOutput,
            isSharedPhysicalSession: false);
    }

    private IFfmpegMediaSession CreateDedicated(
        MediaSource source,
        MediaOpenOptions options,
        double volume,
        bool isMuted,
        IMediaVideoOutput? videoOutput,
        bool isSharedPhysicalSession) =>
        new FfmpegMediaSession(
            source,
            options,
            volume,
            isMuted,
            videoOutput,
            _openOperationSemaphore,
            _loggerFactory.CreateLogger<FfmpegMediaSession>(),
            _audioOutputFactory,
            isSharedPhysicalSession);
}
