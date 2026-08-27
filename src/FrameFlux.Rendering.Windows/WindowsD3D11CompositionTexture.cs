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
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _outputTexture;
    private ID3D11VideoProcessorOutputView? _outputView;
    private IDXGIKeyedMutex? _keyedMutex;
    private IntPtr _sharedHandle;
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
        int width,
        int height,
        MediaD3D11TextureBuffer frame)
    {
        if (_outputTexture is null)
        {
            return false;
        }

        if (_width != width || _height != height || frame.Texture == IntPtr.Zero)
        {
            return true;
        }

        Marshal.AddRef(frame.Texture);
        using var sourceTexture = new ID3D11Texture2D(frame.Texture);
        using var sourceDevice = sourceTexture.Device;
        return _device is null || _device.NativePointer != sourceDevice.NativePointer;
    }

    internal bool TryPresent(
        int width,
        int height,
        MediaD3D11TextureBuffer frame,
        out WindowsD3D11CompositionFrame compositionFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        compositionFrame = default;
        if (frame.Texture == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return false;
        }

        Marshal.AddRef(frame.Texture);
        using var sourceTexture = new ID3D11Texture2D(frame.Texture);
        EnsureDevice(sourceTexture);
        EnsurePipeline(width, height);

        if (_keyedMutex is not null)
        {
            _keyedMutex.AcquireSync(0, 0);
        }

        var rendered = false;
        try
        {
            var inputDescription = new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView
                {
                    MipSlice = 0,
                    ArraySlice = checked((uint)frame.ArraySlice)
                }
            };
            _videoDevice!.CreateVideoProcessorInputView(
                sourceTexture,
                _enumerator!,
                inputDescription,
                out var inputView).CheckError();
            using (inputView)
            {
                var bounds = new RawRect(0, 0, width, height);
                _videoContext!.VideoProcessorSetStreamSourceRect(_processor!, 0, true, bounds);
                _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, bounds);
                _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, bounds);
                var stream = new VideoProcessorStream
                {
                    Enable = true,
                    InputSurface = inputView
                };
                _videoContext.VideoProcessorBlt(
                    _processor!,
                    _outputView!,
                    0,
                    1,
                    [stream]).CheckError();
            }

            rendered = true;
        }
        finally
        {
            if (_keyedMutex is not null)
            {
                _keyedMutex.ReleaseSync(rendered ? 1UL : 0UL);
            }
            else if (rendered)
            {
                _deviceContext!.Flush();
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

    private void EnsureDevice(ID3D11Texture2D sourceTexture)
    {
        using var sourceDevice = sourceTexture.Device;
        if (_device is not null && _device.NativePointer == sourceDevice.NativePointer)
        {
            return;
        }

        DisposePipeline();
        _device = sourceDevice.QueryInterface<ID3D11Device>();
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _deviceContext = _device.ImmediateContext;
        _videoContext = _deviceContext.QueryInterface<ID3D11VideoContext>();
    }

    private void EnsurePipeline(int width, int height)
    {
        if (_outputTexture is not null && _width == width && _height == height)
        {
            return;
        }

        DisposePresentationResources();
        _width = width;
        _height = height;

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational(30, 1),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal
        };
        _videoDevice!.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();

        var textureDescription = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
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

        var outputDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
        };
        _videoDevice.CreateVideoProcessorOutputView(
            _outputTexture,
            _enumerator,
            outputDescription,
            out _outputView).CheckError();
        _generation++;
    }

    private void DisposePresentationResources()
    {
        _keyedMutex?.Dispose();
        _keyedMutex = null;
        _outputView?.Dispose();
        _outputView = null;
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
        _videoContext?.Dispose();
        _videoContext = null;
        _deviceContext?.Dispose();
        _deviceContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _device?.Dispose();
        _device = null;
    }
}
