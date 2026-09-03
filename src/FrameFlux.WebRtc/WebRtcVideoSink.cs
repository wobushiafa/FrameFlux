using System.Net;
using SIPSorceryMedia.Abstractions;

namespace FrameFlux.WebRtc;

/// <summary>
/// Implements SIPSorcery's <see cref="IVideoSink"/> to bridge decoded video frames
/// directly into the FrameFlux presentation and rendering pipeline.
/// </summary>
public sealed class WebRtcVideoSink : IVideoSink, IDisposable
{
    private readonly List<VideoFormat> _supportedFormats =
    [
        new VideoFormat(VideoCodecsEnum.H264, 96),
        new VideoFormat(VideoCodecsEnum.H265, 97),
        new VideoFormat(VideoCodecsEnum.VP8, 98),
        new VideoFormat(VideoCodecsEnum.VP9, 99),
        new VideoFormat(VideoCodecsEnum.AV1, 100),
        new VideoFormat(VideoCodecsEnum.JPEG, 26)
    ];

    private readonly object _sync = new();
    private readonly Action<RawImage>? _onRawImage;
    private readonly Action<byte[], uint, uint, int, VideoPixelFormatsEnum>? _onSample;
    private readonly Action<IPEndPoint, uint, byte[], VideoFormat>? _onFrame;
    private VideoFormat? _currentFormat;
    private bool _isStarted;
    private bool _isPaused;
    private bool _disposed;

    public WebRtcVideoSink(
        Action<RawImage>? onRawImage = null,
        Action<byte[], uint, uint, int, VideoPixelFormatsEnum>? onSample = null,
        Action<IPEndPoint, uint, byte[], VideoFormat>? onFrame = null)
    {
        _onRawImage = onRawImage;
        _onSample = onSample;
        _onFrame = onFrame;
    }

    public event VideoSinkSampleDecodedDelegate? OnVideoSinkDecodedSample;

    public event VideoSinkSampleDecodedFasterDelegate? OnVideoSinkDecodedSampleFaster;

    public List<VideoFormat> GetVideoSinkFormats()
    {
        lock (_sync)
        {
            return new List<VideoFormat>(_supportedFormats);
        }
    }

    public void SetVideoSinkFormat(VideoFormat videoFormat)
    {
        lock (_sync)
        {
            _currentFormat = videoFormat;
        }
    }

    public void RestrictFormats(Func<VideoFormat, bool> filter)
    {
        lock (_sync)
        {
            _supportedFormats.RemoveAll(f => !filter(f));
        }
    }

    public void GotVideoRtp(
        IPEndPoint endpoint,
        uint ssrc,
        uint seqnum,
        uint timestamp,
        int payloadType,
        bool marker,
        byte[] payload)
    {
        // RTP packet handling if passed directly
    }

    public void GotVideoFrame(IPEndPoint endpoint, uint timestamp, byte[] payload, VideoFormat format)
    {
        if (_disposed || !_isStarted || _isPaused)
        {
            return;
        }

        _onFrame?.Invoke(endpoint, timestamp, payload, format);
    }

    /// <summary>
    /// Delivers a decoded <see cref="RawImage"/> directly into the video sink.
    /// </summary>
    public void DeliverDecodedSample(RawImage rawImage)
    {
        if (_disposed || !_isStarted || _isPaused)
        {
            return;
        }

        _onRawImage?.Invoke(rawImage);
        OnVideoSinkDecodedSampleFaster?.Invoke(rawImage);
    }

    /// <summary>
    /// Delivers a decoded byte buffer sample directly into the video sink.
    /// </summary>
    public void DeliverDecodedSample(byte[] sample, uint width, uint height, int stride, VideoPixelFormatsEnum pixelFormat)
    {
        if (_disposed || !_isStarted || _isPaused)
        {
            return;
        }

        _onSample?.Invoke(sample, width, height, stride, pixelFormat);
        OnVideoSinkDecodedSample?.Invoke(sample, width, height, stride, pixelFormat);
    }

    public Task PauseVideoSink()
    {
        _isPaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeVideoSink()
    {
        _isPaused = false;
        return Task.CompletedTask;
    }

    public Task StartVideoSink()
    {
        _isStarted = true;
        _isPaused = false;
        return Task.CompletedTask;
    }

    public Task CloseVideoSink()
    {
        _isStarted = false;
        _isPaused = true;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isStarted = false;
            _isPaused = true;
        }
    }
}
