using System.Text.Json.Serialization;

namespace FrameFlux.WebRtc;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Go2RtcWebSocketSignaling.Go2RtcSignalingMessage))]
internal partial class WebRtcJsonSerializerContext : JsonSerializerContext
{
}
