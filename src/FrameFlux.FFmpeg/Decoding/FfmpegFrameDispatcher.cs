using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal readonly record struct FfmpegFrameDispatchMetrics(
    long ConvertTicks,
    long DispatchTicks);

internal sealed class FfmpegFrameDispatcher
{
    private readonly UnmanagedFrameBufferPool _frameBufferPool = new();
    private volatile bool _isEnabled = true;
    private IntPtr _bgraBuffer;
    private int _bufferSize;

    internal bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    internal void BeginSession() => _frameBufferPool.StartAcceptingReturns();

    internal void StopAcceptingReturns() => _frameBufferPool.StopAcceptingReturns();

    internal void EndSession()
    {
        if (_bgraBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bgraBuffer);
            _bgraBuffer = IntPtr.Zero;
            _bufferSize = 0;
        }

        _frameBufferPool.StopAcceptingReturns();
    }

    internal FfmpegFrameDispatchMetrics Dispatch(
        NativeDecodedFrame frame,
        FfmpegDecoder decoder,
        FfmpegPlaybackOptions options,
        FrameReceivedHandler? frameReceived,
        FrameLeaseReceivedHandler? frameLeaseReceived,
        FrameLeaseReceivedHandler? snapshotFrameLeaseReceived)
    {
        var outputSize = FfmpegPlaybackPolicy.CalculateOutputSize(
            frame.Info.Width,
            frame.Info.Height,
            options.MaxVideoWidth,
            options.MaxVideoHeight);
        var useLeasedFrameDelivery =
            frameLeaseReceived is not null ||
            snapshotFrameLeaseReceived is not null;
        FfmpegMediaFrameLease? frameLease = null;

        try
        {
            if (useLeasedFrameDelivery &&
                (options.FrameDeliveryMode is FfmpegFrameDeliveryMode.D3D11Texture or
                    FfmpegFrameDeliveryMode.DmaBuf) &&
                decoder.TryGetNativePixelFormat(frame, out var nativePixelFormat) &&
                FfmpegPlaybackPolicy.NativeFrameMatchesDeliveryMode(
                    options.FrameDeliveryMode,
                    nativePixelFormat))
            {
                return DispatchNativeFrame(
                    frame,
                    decoder,
                    options,
                    outputSize,
                    nativePixelFormat,
                    frameLeaseReceived,
                    snapshotFrameLeaseReceived,
                    ref frameLease);
            }

            var stride = outputSize.Width * 4;
            var requiredBufferSize = stride * outputSize.Height;
            IntPtr targetBuffer;
            if (useLeasedFrameDelivery)
            {
                frameLease = RentFrameLease(requiredBufferSize);
                targetBuffer = frameLease.Buffer;
            }
            else
            {
                targetBuffer = EnsureBgraBuffer(requiredBufferSize);
            }

            var convertStart = Stopwatch.GetTimestamp();
            decoder.ConvertFrameToBgra(
                frame,
                targetBuffer,
                outputSize.Width,
                outputSize.Height,
                stride);
            var convertTicks = Stopwatch.GetTimestamp() - convertStart;

            var dispatchStart = Stopwatch.GetTimestamp();
            if (frameLease is not null)
            {
                frameLease.ResetBgra(outputSize.Width, outputSize.Height, stride);
                if (frameLeaseReceived is null)
                {
                    frameLease.Dispose();
                }
                else
                {
                    frameLeaseReceived.Invoke(frameLease);
                }

                frameLease = null;
            }
            else
            {
                frameReceived?.Invoke(
                    targetBuffer,
                    outputSize.Width,
                    outputSize.Height,
                    stride);
            }

            return new FfmpegFrameDispatchMetrics(
                convertTicks,
                Stopwatch.GetTimestamp() - dispatchStart);
        }
        finally
        {
            frameLease?.Dispose();
        }
    }

    private FfmpegFrameDispatchMetrics DispatchNativeFrame(
        NativeDecodedFrame frame,
        FfmpegDecoder decoder,
        FfmpegPlaybackOptions options,
        (int Width, int Height) outputSize,
        FfmpegNativePixelFormat nativePixelFormat,
        FrameLeaseReceivedHandler? frameLeaseReceived,
        FrameLeaseReceivedHandler? snapshotFrameLeaseReceived,
        ref FfmpegMediaFrameLease? frameLease)
    {
        var convertStart = Stopwatch.GetTimestamp();
        if (options.CreateSnapshotFrames && snapshotFrameLeaseReceived is not null)
        {
            DispatchSnapshot(
                frame,
                decoder,
                outputSize,
                snapshotFrameLeaseReceived);
        }

        frameLease = decoder.CreateNativeFrameLease(frame, nativePixelFormat);
        var convertTicks = Stopwatch.GetTimestamp() - convertStart;
        var dispatchStart = Stopwatch.GetTimestamp();
        if (frameLeaseReceived is not null)
        {
            frameLeaseReceived.Invoke(frameLease);
            frameLease = null;
        }

        return new FfmpegFrameDispatchMetrics(
            convertTicks,
            Stopwatch.GetTimestamp() - dispatchStart);
    }

    private void DispatchSnapshot(
        NativeDecodedFrame frame,
        FfmpegDecoder decoder,
        (int Width, int Height) outputSize,
        FrameLeaseReceivedHandler snapshotFrameLeaseReceived)
    {
        var stride = outputSize.Width * 4;
        var size = stride * outputSize.Height;
        FfmpegMediaFrameLease? snapshotLease = RentFrameLease(size);
        try
        {
            decoder.ConvertFrameToBgra(
                frame,
                snapshotLease.Buffer,
                outputSize.Width,
                outputSize.Height,
                stride);
            snapshotLease.ResetBgra(
                outputSize.Width,
                outputSize.Height,
                stride);
            snapshotFrameLeaseReceived.Invoke(snapshotLease);
            snapshotLease = null;
        }
        finally
        {
            snapshotLease?.Dispose();
        }
    }

    private IntPtr EnsureBgraBuffer(int requiredBufferSize)
    {
        if (_bgraBuffer != IntPtr.Zero && _bufferSize == requiredBufferSize)
        {
            return _bgraBuffer;
        }

        if (_bgraBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bgraBuffer);
        }

        _bgraBuffer = Marshal.AllocHGlobal(requiredBufferSize);
        _bufferSize = requiredBufferSize;
        return _bgraBuffer;
    }

    private FfmpegMediaFrameLease RentFrameLease(int requiredSize)
    {
        var buffer = _frameBufferPool.Rent(requiredSize);
        return new FfmpegMediaFrameLease(buffer, requiredSize, ReturnFrameLease);
    }

    private void ReturnFrameLease(FfmpegMediaFrameLease lease)
    {
        _frameBufferPool.Return(lease.Buffer, lease.Size);
    }
}
