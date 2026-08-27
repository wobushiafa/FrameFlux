# FrameFlux.Rendering.Windows

Shared Windows D3D11 presentation used by the FrameFlux Avalonia and WPF
controls. The package keeps two distinct outputs:

- a Win32 child-window and DXGI swap-chain presenter for minimum latency;
- a keyed-mutex shared BGRA texture producer for UI-framework GPU composition.

Both consume decoder-owned D3D11 textures without CPU readback.

Application code normally references a FrameFlux UI package and does not use
this package directly.
