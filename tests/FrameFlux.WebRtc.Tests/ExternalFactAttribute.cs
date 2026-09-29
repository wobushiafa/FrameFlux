using Xunit;

namespace FrameFlux.WebRtc.Tests;

internal sealed class ExternalFactAttribute : FactAttribute
{
    public ExternalFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FRAMEFLUX_GO2RTC_URL")))
        {
            Skip = "Set FRAMEFLUX_GO2RTC_URL to run tests against a go2rtc stream.html endpoint.";
        }
    }
}
