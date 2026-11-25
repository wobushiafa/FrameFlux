using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameFlux.Rendering.Windows;

internal enum MediaStretchMode
{
    None,
    Fill,
    Uniform,
    UniformToFill
}

internal sealed class WindowsD3D11Presenter : IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;

    private readonly Dictionary<int, ID3D11VideoProcessorInputView> _inputViews = [];
    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1];
    private IntPtr _window;
    private ID3D11Device? _device;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private WindowsD3D11DeviceSynchronizer? _deviceSynchronizer;
    private ID3D11Texture2D? _sourceTexture;
    private IntPtr _sourceTexturePointer;
    private IDXGISwapChain1? _swapChain;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorOutputView? _outputView;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _outputWidth;
    private int _outputHeight;
    private RawRect _lastSourceRect;
    private RawRect _lastDestRect;
    private RawRect _lastTargetRect;
    private bool _rectsInitialized;
    private bool _disposed;

    internal IntPtr CreateWindow(IntPtr parent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("D3D11 presentation is only available on Windows.");
        }

        _window = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings,
            0,
            0,
            1,
            1,
            parent,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Unable to create the D3D11 child window ({Marshal.GetLastWin32Error()}).");
        }

        return _window;
    }

    internal void Present(
        int sourceWidth,
        int sourceHeight,
        MediaD3D11TextureBuffer frame,
        int outputWidth,
        int outputHeight,
        MediaStretchMode stretch)
    {
        if (_window == IntPtr.Zero || frame.Texture == IntPtr.Zero)
        {
            return;
        }

        var texture = EnsureDevice(frame.Texture);
        var clampedOutputWidth = Math.Max(1, outputWidth);
        var clampedOutputHeight = Math.Max(1, outputHeight);
        using (_deviceSynchronizer!.EnterScope())
        {
            EnsurePipeline(
                sourceWidth,
                sourceHeight,
                clampedOutputWidth,
                clampedOutputHeight);

            var inputView = GetOrCreateInputView(texture, frame.ArraySlice);
            var sourceRect = new RawRect(0, 0, sourceWidth, sourceHeight);
            var destinationRect = CalculateDestinationRect(
                sourceWidth,
                sourceHeight,
                _outputWidth,
                _outputHeight,
                stretch);
            var targetRect = new RawRect(0, 0, _outputWidth, _outputHeight);

            if (!_rectsInitialized || !RectsEqual(_lastSourceRect, sourceRect))
            {
                _videoContext!.VideoProcessorSetStreamSourceRect(_processor!, 0, true, sourceRect);
                _lastSourceRect = sourceRect;
            }
            if (!_rectsInitialized || !RectsEqual(_lastDestRect, destinationRect))
            {
                _videoContext!.VideoProcessorSetStreamDestRect(_processor!, 0, true, destinationRect);
                _lastDestRect = destinationRect;
            }
            if (!_rectsInitialized || !RectsEqual(_lastTargetRect, targetRect))
            {
                _videoContext!.VideoProcessorSetOutputTargetRect(_processor!, true, targetRect);
                _lastTargetRect = targetRect;
            }
            _rectsInitialized = true;

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
        }

        _swapChain!.Present(0, PresentFlags.None).CheckError();
    }

    internal void DestroyWindow()
    {
        DisposePipeline();
        if (_window != IntPtr.Zero)
        {
            _ = DestroyWindowNative(_window);
            _window = IntPtr.Zero;
        }
    }

    internal void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DisposePipeline();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyWindow();
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
            if (_device is not null && _device.NativePointer == sourceDevice.NativePointer)
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
                _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
                using var immediateContext = _device.ImmediateContext;
                _videoContext = immediateContext.QueryInterface<ID3D11VideoContext>();
                _deviceSynchronizer = new WindowsD3D11DeviceSynchronizer(
                    _device,
                    immediateContext);
            }

            _sourceTexture = sourceTexture;
            _sourceTexturePointer = sourceTexturePointer;
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
        ID3D11Texture2D texture,
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
            texture,
            _enumerator!,
            inputDescription,
            out inputView).CheckError();
        _inputViews.Add(arraySlice, inputView);
        return inputView;
    }

    private void EnsurePipeline(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight)
    {
        if (_swapChain is not null &&
            _sourceWidth == sourceWidth &&
            _sourceHeight == sourceHeight)
        {
            if (_outputWidth == outputWidth && _outputHeight == outputHeight)
            {
                return;
            }

            // Fast resize path: resize existing swapchain buffers without recreating swapchain or video processor.
            _outputView?.Dispose();
            _outputView = null;
            _swapChain.ResizeBuffers(
                0,
                (uint)outputWidth,
                (uint)outputHeight,
                Format.Unknown,
                SwapChainFlags.None).CheckError();
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;
            _rectsInitialized = false;
            CreateOutputView();
            return;
        }

        DisposePresentationResources();
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _rectsInitialized = false;

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter).CheckError();
        using (adapter)
        {
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var description = new SwapChainDescription1(
                (uint)outputWidth,
                (uint)outputHeight,
                Format.B8G8R8A8_UNorm,
                false,
                Usage.RenderTargetOutput,
                2,
                Scaling.Stretch,
                SwapEffect.FlipDiscard,
                AlphaMode.Ignore);
            _swapChain = factory.CreateSwapChainForHwnd(
                _device,
                _window,
                description,
                null,
                null);
        }

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
        _videoDevice!.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();
        _videoContext!.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);

        CreateOutputView();
    }

    private void CreateOutputView()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        var outputDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
        };
        _videoDevice!.CreateVideoProcessorOutputView(
            backBuffer,
            _enumerator!,
            outputDescription,
            out _outputView).CheckError();
    }

    private static bool RectsEqual(RawRect left, RawRect right) =>
        left.Left == right.Left &&
        left.Top == right.Top &&
        left.Right == right.Right &&
        left.Bottom == right.Bottom;

    private static RawRect CalculateDestinationRect(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        MediaStretchMode stretch)
    {
        if (stretch == MediaStretchMode.Fill)
        {
            return new RawRect(0, 0, outputWidth, outputHeight);
        }

        var scaleX = outputWidth / (double)sourceWidth;
        var scaleY = outputHeight / (double)sourceHeight;
        var scale = stretch switch
        {
            MediaStretchMode.None => 1d,
            MediaStretchMode.UniformToFill => Math.Max(scaleX, scaleY),
            _ => Math.Min(scaleX, scaleY)
        };
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var left = (outputWidth - width) / 2;
        var top = (outputHeight - height) / 2;
        return new RawRect(left, top, left + width, top + height);
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

    private void DisposePresentationResources()
    {
        DisposeInputViews();
        _outputView?.Dispose();
        _outputView = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _rectsInitialized = false;
    }

    private void DisposePipeline()
    {
        DisposePresentationResources();
        _sourceTexture?.Dispose();
        _sourceTexture = null;
        _sourceTexturePointer = IntPtr.Zero;
        _videoContext?.Dispose();
        _videoContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _deviceSynchronizer?.Dispose();
        _deviceSynchronizer = null;
        _device?.Dispose();
        _device = null;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindowNative(IntPtr window);
}
