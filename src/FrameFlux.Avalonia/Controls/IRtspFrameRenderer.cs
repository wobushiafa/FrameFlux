using Avalonia;
using Avalonia.Media;

namespace FrameFlux.Avalonia;

internal interface IRtspFrameRenderer : IDisposable
{
    RtspRenderMode Mode { get; }

    void Attach(RtspVideoView owner);

    void Detach();

    void UpdateFrame(IntPtr buffer, int width, int height, int stride);

    void Render(DrawingContext context, Rect bounds, Stretch stretch);
}

internal interface IRtspLeaseFrameRenderer : IRtspFrameRenderer
{
    void UpdateFrameLease(RtspFrameLease lease);
}
