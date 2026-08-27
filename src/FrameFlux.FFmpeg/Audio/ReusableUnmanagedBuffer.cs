using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class ReusableUnmanagedBuffer : IDisposable
{
    private IntPtr _pointer;
    private int _capacity;
    private bool _disposed;

    internal IntPtr Pointer => _pointer;

    internal int Capacity => _capacity;

    internal void EnsureCapacity(int requiredCapacity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredCapacity, 1);
        if (requiredCapacity <= _capacity)
        {
            return;
        }

        var doubledCapacity = _capacity <= int.MaxValue / 2
            ? _capacity * 2
            : int.MaxValue;
        var newCapacity = Math.Max(requiredCapacity, Math.Max(4096, doubledCapacity));
        _pointer = _pointer == IntPtr.Zero
            ? Marshal.AllocHGlobal(newCapacity)
            : Marshal.ReAllocHGlobal(_pointer, (IntPtr)newCapacity);
        _capacity = newCapacity;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pointer);
            _pointer = IntPtr.Zero;
            _capacity = 0;
        }
    }
}
