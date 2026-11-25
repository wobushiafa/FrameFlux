using Vortice.Direct3D11;

namespace FrameFlux.Rendering.Windows;

internal sealed class WindowsD3D11DeviceSynchronizer : IDisposable
{
    private static readonly object RegistrySync = new();
    private static readonly Dictionary<IntPtr, DeviceLockEntry> DeviceLocks = [];
    private readonly IntPtr _devicePointer;
    private readonly object _managedLock;
    private ID3D11Multithread? _multithread;
    private bool _disposed;

    internal WindowsD3D11DeviceSynchronizer(
        ID3D11Device device,
        ID3D11DeviceContext? immediateContext = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devicePointer = device.NativePointer;
        _managedLock = AcquireDeviceLock(_devicePointer);
        try
        {
            _multithread = device.QueryInterfaceOrNull<ID3D11Multithread>() ??
                immediateContext?.QueryInterfaceOrNull<ID3D11Multithread>();
            _multithread?.SetMultithreadProtected(true);
        }
        catch
        {
            ReleaseDeviceLock(_devicePointer);
            throw;
        }
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
        ReleaseDeviceLock(_devicePointer);
    }

    private static object AcquireDeviceLock(IntPtr devicePointer)
    {
        lock (RegistrySync)
        {
            if (!DeviceLocks.TryGetValue(devicePointer, out var entry))
            {
                entry = new DeviceLockEntry();
                DeviceLocks.Add(devicePointer, entry);
            }

            entry.ReferenceCount++;
            return entry.SyncLock;
        }
    }

    private static void ReleaseDeviceLock(IntPtr devicePointer)
    {
        lock (RegistrySync)
        {
            if (DeviceLocks.TryGetValue(devicePointer, out var entry) &&
                --entry.ReferenceCount <= 0)
            {
                DeviceLocks.Remove(devicePointer);
            }
        }
    }

    private sealed class DeviceLockEntry
    {
        internal readonly object SyncLock = new();
        internal int ReferenceCount;
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
