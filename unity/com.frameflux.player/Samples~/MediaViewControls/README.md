# MediaView controls

1. Create a Canvas and RawImage. Add `MediaView` and `MediaRawImage` to the RawImage.
2. Optionally add an AspectRatioFitter set to Fit In Parent.
3. Configure Source and Backend on MediaView; enable Auto Play.
4. Add MediaViewControls to a GameObject and assign the MediaView.
5. Bind UI buttons to Play/Pause/Stop; bind a slider to SetVolume and a toggle to SetMuted.

For a scene mesh, add MediaView and MediaMaterial to a Renderer instead. Set
MediaMaterial's texture property to match the shader (`_MainTex` or `_BaseMap`).
Audio is provided by UnityAudioOutput, added automatically for the built-in factories.
The sample uses the managed UPM package; it does not require the NativeAOT prototype.
