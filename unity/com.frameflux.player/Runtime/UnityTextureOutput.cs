using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FrameFlux.Unity
{
    /// <summary>Owns the latest CPU frame and uploads it on the Unity main thread.</summary>
    public sealed class UnityTextureOutput : IMediaVideoOutput, IDisposable
    {
        private readonly object sync = new object();
        private IMediaFrameLease pending;
        private byte[] pixels;
        private byte[] row;
        private bool disposed;
        public Texture2D Texture { get; private set; }
        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;
        public bool Supports(MediaFrameStorageKind storage, MediaPixelFormat format) =>
            storage == MediaFrameStorageKind.CpuMemory && format == MediaPixelFormat.Bgra32;

        public bool TryPresent(IMediaFrameLease frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (!Supports(frame.StorageKind, frame.PixelFormat)) return false;
            IMediaFrameLease previous;
            lock (sync)
            {
                if (disposed) return false;
                previous = pending;
                pending = frame;
            }
            previous?.Dispose();
            return true;
        }

        /// <summary>Call from Update. Returns true when the texture changed.</summary>
        public bool UpdateTexture()
        {
            IMediaFrameLease frame;
            lock (sync) { frame = pending; pending = null; }
            if (frame == null) return false;
            try
            {
                if (!frame.TryGetCpuBuffer(out var buffer)) return false;
                int width = frame.Width, height = frame.Height;
                if (width <= 0 || height <= 0) throw new ArgumentException("Invalid video dimensions.");
                int rowBytes = checked(width * 4), size = checked(rowBytes * height);
                if (buffer.Plane0 == IntPtr.Zero || Math.Abs((long)buffer.Plane0Stride) < rowBytes)
                    throw new ArgumentException("Invalid video stride.");
                long first = buffer.Plane0.ToInt64() - buffer.Buffer.ToInt64();
                long last = first + (long)(height - 1) * buffer.Plane0Stride;
                if (buffer.Buffer == IntPtr.Zero || Math.Min(first, last) < 0 || Math.Max(first, last) + rowBytes > buffer.Size)
                    throw new ArgumentException("Video plane is outside its buffer.");
                if (pixels == null || pixels.Length != size) pixels = new byte[size];
                if (row == null || row.Length != rowBytes) row = new byte[rowBytes];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(new IntPtr(buffer.Plane0.ToInt64() + (long)y * buffer.Plane0Stride), row, 0, rowBytes);
                    int destination = (height - 1 - y) * rowBytes;
                    for (int x = 0; x < rowBytes; x += 4)
                    {
                        pixels[destination + x] = row[x + 2];
                        pixels[destination + x + 1] = row[x + 1];
                        pixels[destination + x + 2] = row[x];
                        pixels[destination + x + 3] = row[x + 3];
                    }
                }
                if (Texture == null || Texture.width != width || Texture.height != height)
                {
                    if (Texture != null) UnityEngine.Object.Destroy(Texture);
                    Texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
                    { name = "FrameFlux Video", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                }
                Texture.LoadRawTextureData(pixels);
                Texture.Apply(false, false);
                return true;
            }
            finally { frame.Dispose(); }
        }

        /// <summary>Flush before a source switch, stop, or seek. Call on the main thread.</summary>
        public void Clear()
        {
            IMediaFrameLease previous;
            lock (sync) { previous = pending; pending = null; }
            previous?.Dispose();
            if (Texture != null) UnityEngine.Object.Destroy(Texture);
            Texture = null;
        }

        /// <summary>Call on the main thread; subsequent incoming frames are rejected.</summary>
        public void Dispose()
        {
            lock (sync) { disposed = true; }
            Clear();
            pixels = null;
            row = null;
        }
    }
}
