# FrameFlux.Presentation

Shared playback lifecycle orchestration, presentation-mode policy, and
native-to-software frame fallback used by the FrameFlux Avalonia and WPF
controls. Framework-specific output controls, visibility, stretch, overlay,
and disposal stay in platform presentation coordinators rather than their
public `MediaView` controls.

Application code normally references a UI package and supplies an `IMediaPlayerFactory`; it does not use this package directly.
