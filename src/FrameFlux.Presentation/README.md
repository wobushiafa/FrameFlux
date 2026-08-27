# FrameFlux.Presentation

Shared playback lifecycle orchestration, presentation-mode policy, and
native-to-software frame fallback used by the FrameFlux Avalonia and WPF
controls. Framework-specific output controls, visibility, stretch, overlay,
and disposal stay in platform presentation coordinators rather than their
public `MediaView` controls.

GPU outputs rebuild their device-dependent resources after presentation
errors. Three consecutive failures exhaust the retry budget and cause the
platform coordinator to restart playback with software bitmap presentation.
One successful GPU frame resets the failure budget.

Application code normally references a UI package and supplies an `IMediaPlayerFactory`; it does not use this package directly.
