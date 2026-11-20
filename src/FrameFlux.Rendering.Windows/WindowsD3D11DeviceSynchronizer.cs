using System.Collections.Concurrent;
using Vortice.Direct3D11;

namespace FrameFlux.Rendering.Windows;

internal sealed class WindowsD3D11DeviceSynchronizer : IDisposable
{
    private static readonly ConcurrentDictionary<IntPtr, object> DeviceLocks = new();
    private readonly object _managedLock;
    private ID3D11Multithread? _multithread;
    private bool _disposed;

    internal WindowsD3D11DeviceSynchronizer(
        ID3D11Device device,
        ID3D11DeviceContext? immediateContext = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _managedLock = DeviceLocks.GetOrAdd(device.NativePointer, static _ => new object());
        _multithread = device.QueryInterfaceOrNull<ID3D11Multithread>() ??
            immediateContext?.QueryInterfaceOrNull<ID3D11Multithread>();
        _multithread?.SetMultithreadProtected(true);
    }

    internal Scope EnterScope()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Scope(_managedLock, _multithread);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _multithread?.Dispose();
        _multithread = null;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly object _managedLock;
        private readonly ID3D11Multithread? _multithread;
        private readonly bool _entered;

        internal Scope(object managedLock, ID3D11Multithread? multithread)
        {
            _managedLock = managedLock;
            _multithread = multithread;
            Monitor.Enter(managedLock);
            try
            {
                multithread?.Enter();
                _entered = true;
            }
            catch
            {
                Monitor.Exit(managedLock);
                throw;
            }
        }

        public void Dispose()
        {
            if (!_entered)
            {
                return;
            }

            try
            {
                _multithread?.Leave();
            }
            finally
            {
                Monitor.Exit(_managedLock);
            }
        }
    }
}
