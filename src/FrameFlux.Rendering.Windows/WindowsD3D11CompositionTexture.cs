using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameFlux.Rendering.Windows;

internal readonly record struct WindowsD3D11CompositionFrame(
    IntPtr SharedHandle,
    int Width,
    int Height,
    long Generation);

internal enum WindowsD3D11CompositionSynchronization
{
    KeyedMutex,
    Shared
}

internal sealed class WindowsD3D11CompositionTexture : IDisposable
{
    private readonly WindowsD3D11CompositionSynchronization _synchronization;
    private readonly Dictionary<int, ID3D11VideoProcessorInputView> _inputViews = [];
    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1];
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private WindowsD3D11DeviceSynchronizer? _deviceSynchronizer;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _sourceTexture;
    private Texture2DDescription _sourceDescription;
    private ID3D11Texture2D? _videoOutputTexture;
    private ID3D11Texture2D? _outputTexture;
    private ID3D11VideoProcessorOutputView? _outputView;
    private ID3D11Query? _completionQuery;
    private IDXGIKeyedMutex? _keyedMutex;
    private IntPtr _sharedHandle;
    private IntPtr _sourceTexturePointer;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _width;
    private int _height;
    private long _generation;
    private bool _disposed;

    internal WindowsD3D11CompositionTexture(
        WindowsD3D11CompositionSynchronization synchronization =
            WindowsD3D11CompositionSynchronization.KeyedMutex)
    {
        _synchronization = synchronization;
    }

    internal bool RequiresReset(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        MediaD3D11TextureBuffer frame)
    {
        if (_outputTexture is null)
        {
            return false;
        }

        if (_sourceWidth != sourceWidth ||
            _sourceHeight != sourceHeight ||
            _width != outputWidth ||
            _height != outputHeight ||
            frame.Texture == IntPtr.Zero)
        {
            return true;
        }

        if (_sourceTexturePointer == frame.Texture)
        {
            return false;
        }

        Marshal.AddRef(frame.Texture);
        using var sourceTexture = new ID3D11Texture2D(frame.Texture);
        using var sourceDevice = sourceTexture.Device;
        return _device is null || _device.NativePointer != sourceDevice.NativePointer;
    }

    internal bool TryPresent(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        MediaD3D11TextureBuffer frame,
        out WindowsD3D11CompositionFrame compositionFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        compositionFrame = default;
        if (frame.Texture == IntPtr.Zero ||
            sourceWidth <= 0 ||
            sourceHeight <= 0 ||
            outputWidth <= 0 ||
            outputHeight <= 0)
        {
            return false;
        }

        var sourceTexture = EnsureDevice(frame.Texture);
        using (_deviceSynchronizer!.EnterScope())
        {
            EnsureOutputResources(sourceWidth, sourceHeight, outputWidth, outputHeight);
        }

        if (_keyedMutex is not null)
        {
            _keyedMutex.AcquireSync(0, 100);
        }

        var rendered = false;
        try
        {
            using (_deviceSynchronizer.EnterScope())
            {
                if (CanDirectCopy(
                        sourceWidth,
                        sourceHeight,
                        outputWidth,
                        outputHeight,
                        frame.ArraySlice))
                {
                    var sourceSubresource = _sourceDescription.MipLevels * checked((uint)frame.ArraySlice);
                    if (_sourceDescription.Width == (uint)sourceWidth &&
                        _sourceDescription.Height == (uint)sourceHeight)
                    {
                        _deviceContext!.CopySubresourceRegion(
                            _outputTexture!,
                            0,
                            0,
                            0,
                            0,
                            sourceTexture,
                            sourceSubresource);
                    }
                    else
                    {
                        _deviceContext!.CopySubresourceRegion(
                            _outputTexture!,
                            0,
                            0,
                            0,
                            0,
                            sourceTexture,
                            sourceSubresource,
                            new Vortice.Mathematics.Box(0, 0, 0, sourceWidth, sourceHeight, 1));
                    }
                }
                else
                {
                    EnsureVideoProcessorPipeline(sourceWidth, sourceHeight, outputWidth, outputHeight);
                    var inputView = GetOrCreateInputView(sourceTexture, frame.ArraySlice);
                    _streams[0] = new VideoProcessorStream
                    {
                        Enable = true,
                        InputSurface = inputView
                    };
                    _videoContext!.VideoProcessorBlt(
                        _processor!,
                        _outputView!,
                        0,
                        1,
                        _streams).CheckError();
                    if (_videoOutputTexture is not null)
                    {
                        _deviceContext!.CopySubresourceRegion(
                            _outputTexture!,
                            0,
                            0,
                            0,
                            0,
                            _videoOutputTexture,
                            0);
                    }
                }

                if (_keyedMutex is null)
                {
                    if (_completionQuery is not null)
                    {
                        _deviceContext!.End(_completionQuery);
                    }
                    _deviceContext!.Flush();
                }
            }

            if (_keyedMutex is null && _completionQuery is not null)
            {
                WaitForGpuCompletion();
            }

            rendered = true;
        }
        finally
        {
            if (_keyedMutex is not null)
            {
                _keyedMutex.ReleaseSync(rendered ? 1UL : 0UL);
            }
        }

        compositionFrame = new WindowsD3D11CompositionFrame(
            _sharedHandle,
            _width,
            _height,
            _generation);
        return true;
    }

    internal void Reset()
    {
        DisposePipeline();
        _sourceWidth = 0;
        _sourceHeight = 0;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reset();
    }

    private ID3D11Texture2D EnsureDevice(IntPtr sourceTexturePointer)
    {
        if (_device is not null &&
            _sourceTexture is not null &&
            _sourceTexturePointer == sourceTexturePointer)
        {
            return _sourceTexture;
        }

        Marshal.AddRef(sourceTexturePointer);
        var sourceTexture = new ID3D11Texture2D(sourceTexturePointer);
        try
        {
            using var sourceDevice = sourceTexture.Device;
            if (_device is not null &&
                _device.NativePointer == sourceDevice.NativePointer)
            {
                using (_deviceSynchronizer!.EnterScope())
                {
                    DisposeInputViews();
                    _sourceTexture?.Dispose();
                }
            }
            else
            {
                DisposePipeline();
                _device = sourceDevice.QueryInterface<ID3D11Device>();
                _videoDevice = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
                _deviceContext = _device.ImmediateContext;
                _videoContext = _deviceContext.QueryInterfaceOrNull<ID3D11VideoContext>();
                _deviceSynchronizer = new WindowsD3D11DeviceSynchronizer(
                    _device,
                    _deviceContext);
            }

            _sourceTexture = sourceTexture;
            _sourceTexturePointer = sourceTexturePointer;
            _sourceDescription = sourceTexture.Description;
            return sourceTexture;
        }
        catch
        {
            sourceTexture.Dispose();
            DisposePipeline();
            throw;
        }
    }

    private ID3D11VideoProcessorInputView GetOrCreateInputView(
        ID3D11Texture2D sourceTexture,
        int arraySlice)
    {
        if (_inputViews.TryGetValue(arraySlice, out var inputView))
        {
            return inputView;
        }

        var inputDescription = new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView
            {
                MipSlice = 0,
                ArraySlice = checked((uint)arraySlice)
            }
        };
        _videoDevice!.CreateVideoProcessorInputView(
            sourceTexture,
            _enumerator!,
            inputDescription,
            out inputView).CheckError();
        _inputViews.Add(arraySlice, inputView);
        return inputView;
    }

    private void DisposeInputViews()
    {
        _streams[0] = default;
        foreach (var inputView in _inputViews.Values)
        {
            inputView.Dispose();
        }

        _inputViews.Clear();
    }

    private bool CanDirectCopy(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        int arraySlice) =>
        sourceWidth == outputWidth &&
        sourceHeight == outputHeight &&
        _sourceDescription.Format == Format.B8G8R8A8_UNorm &&
        _sourceDescription.SampleDescription.Count == 1 &&
        _sourceDescription.Width >= (uint)sourceWidth &&
        _sourceDescription.Height >= (uint)sourceHeight &&
        arraySlice >= 0 &&
        (uint)arraySlice < _sourceDescription.ArraySize;

    private void EnsureOutputResources(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight)
    {
        if (_outputTexture is not null &&
            _sourceWidth == sourceWidth &&
            _sourceHeight == sourceHeight &&
            _width == outputWidth &&
            _height == outputHeight)
        {
            return;
        }

        DisposePresentationResources();
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _width = outputWidth;
        _height = outputHeight;

        var textureDescription = new Texture2DDescription
        {
            Width = (uint)outputWidth,
            Height = (uint)outputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = _synchronization ==
                WindowsD3D11CompositionSynchronization.KeyedMutex
                ? ResourceOptionFlags.SharedKeyedMutex
                : ResourceOptionFlags.Shared
        };
        _outputTexture = _device!.CreateTexture2D(textureDescription);
        if (_synchronization ==
            WindowsD3D11CompositionSynchronization.KeyedMutex)
        {
            _keyedMutex = _outputTexture.QueryInterface<IDXGIKeyedMutex>();
        }
        using (var resource = _outputTexture.QueryInterface<IDXGIResource>())
        {
            _sharedHandle = resource.SharedHandle;
        }

        if (_synchronization == WindowsD3D11CompositionSynchronization.Shared)
        {
            _completionQuery = _device.CreateQuery(QueryType.Event, QueryFlags.None);
        }
        _generation++;
    }

    private void EnsureVideoProcessorPipeline(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight)
    {
        if (_outputView is not null &&
            _processor is not null &&
            _enumerator is not null)
        {
            return;
        }

        var videoDevice = _videoDevice ??
            throw new InvalidOperationException("D3D11 video device interface is unavailable.");
        var videoContext = _videoContext ??
            throw new InvalidOperationException("D3D11 video context interface is unavailable.");

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1),
            InputWidth = (uint)sourceWidth,
            InputHeight = (uint)sourceHeight,
            OutputFrameRate = new Rational(30, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.PlaybackNormal
        };
        videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();
        videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        var sourceBounds = new RawRect(0, 0, sourceWidth, sourceHeight);
        var outputBounds = new RawRect(0, 0, outputWidth, outputHeight);
        videoContext.VideoProcessorSetStreamSourceRect(
            _processor,
            0,
            true,
            sourceBounds);
        videoContext.VideoProcessorSetStreamDestRect(
            _processor,
            0,
            true,
            outputBounds);
        videoContext.VideoProcessorSetOutputTargetRect(
            _processor,
            true,
            outputBounds);

        ID3D11Texture2D targetTexture;
        if (_synchronization == WindowsD3D11CompositionSynchronization.Shared)
        {
            var privateDescription = new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            _videoOutputTexture = _device!.CreateTexture2D(privateDescription);
            targetTexture = _videoOutputTexture;
        }
        else
        {
            targetTexture = _outputTexture!;
        }

        var outputDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
        };
        videoDevice.CreateVideoProcessorOutputView(
            targetTexture,
            _enumerator,
            outputDescription,
            out _outputView).CheckError();
    }

    private void WaitForGpuCompletion()
    {
        var query = _completionQuery;
        var context = _deviceContext;
        var synchronizer = _deviceSynchronizer;
        if (query is null || context is null || synchronizer is null)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var timeoutTicks = Stopwatch.Frequency / 10; // 100 ms guard
        var iteration = 0;
        while (true)
        {
            bool completed;
            using (synchronizer.EnterScope())
            {
                completed = context.GetData(query, AsyncGetDataFlags.DoNotFlush, out int value) &&
                            value != 0;
            }

            if (completed || Stopwatch.GetTimestamp() - start >= timeoutTicks)
            {
                return;
            }

            if (iteration++ < 64)
            {
                Thread.SpinWait(32);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    private void DisposePresentationResources()
    {
        DisposeInputViews();
        _completionQuery?.Dispose();
        _completionQuery = null;
        _keyedMutex?.Dispose();
        _keyedMutex = null;
        _outputView?.Dispose();
        _outputView = null;
        _videoOutputTexture?.Dispose();
        _videoOutputTexture = null;
        _outputTexture?.Dispose();
        _outputTexture = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _sharedHandle = IntPtr.Zero;
    }

    private void DisposePipeline()
    {
        DisposePresentationResources();
        _sourceTexture?.Dispose();
        _sourceTexture = null;
        _sourceDescription = default;
        _sourceTexturePointer = IntPtr.Zero;
        _videoContext?.Dispose();
        _videoContext = null;
        _deviceContext?.Dispose();
        _deviceContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _deviceSynchronizer?.Dispose();
        _deviceSynchronizer = null;
        _device?.Dispose();
        _device = null;
    }
}
