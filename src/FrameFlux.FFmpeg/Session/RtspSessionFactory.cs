using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FrameFlux;

namespace FrameFlux.FFmpeg;

public sealed class RtspSessionFactory(ILoggerFactory? loggerFactory = null) : IRtspSessionFactory
{
    private static readonly SharedRtspSessionPool DefaultSharedPool = new();
    private readonly IRtspSessionFactory _dedicatedFactory =
        new DedicatedRtspSessionFactory(loggerFactory ?? NullLoggerFactory.Instance);
    private readonly SharedRtspSessionPool _sharedPool = DefaultSharedPool;

    internal RtspSessionFactory(
        IRtspSessionFactory dedicatedFactory,
        SharedRtspSessionPool sharedPool) : this()
    {
        _dedicatedFactory = dedicatedFactory;
        _sharedPool = sharedPool;
    }

    public IRtspSession Create(RtspSource source, RtspSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolvedOptions = options ?? new RtspSessionOptions();
        resolvedOptions.Validate();
        return resolvedOptions.StreamSharing == RtspStreamSharingMode.Shared
            ? _sharedPool.Acquire(
                source,
                resolvedOptions,
                () => _dedicatedFactory.Create(source, resolvedOptions))
            : _dedicatedFactory.Create(source, resolvedOptions);
    }
}

internal sealed class DedicatedRtspSessionFactory(ILoggerFactory loggerFactory) : IRtspSessionFactory
{
    public IRtspSession Create(RtspSource source, RtspSessionOptions? options = null) =>
        new FfmpegRtspSession(
            source,
            options ?? new RtspSessionOptions(),
            loggerFactory.CreateLogger<FfmpegRtspSession>());
}
