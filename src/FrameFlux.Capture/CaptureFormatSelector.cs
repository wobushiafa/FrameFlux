namespace FrameFlux.Capture;

internal static class CaptureFormatSelector
{
    private const double PreferredMinimumFps = 24.5d;
    private const long StandardFullHdPixels = 1920L * 1080L;

    internal static MediaCaptureVideoFormat? SelectBestFormat(
        IReadOnlyList<MediaCaptureVideoFormat> available,
        int? requestedWidth,
        int? requestedHeight,
        double? requestedFps,
        MediaCapturePixelFormat? requestedFormat,
        MediaVideoOptions? videoOptions,
        CapturePlayerOptions? playerOptions = null)
    {
        if (available is null || available.Count == 0)
        {
            return null;
        }

        var valid = available
            .Where(static f => f.Width > 0 && f.Height > 0)
            .ToList();
        if (valid.Count == 0)
        {
            return null;
        }

        var maxWidth = videoOptions?.MaximumWidth;
        var maxHeight = videoOptions?.MaximumHeight;
        var maxFps = videoOptions?.MaximumFrameRate;

        var constrained = valid.Where(f =>
            (!maxWidth.HasValue || f.Width <= maxWidth.Value) &&
            (!maxHeight.HasValue || f.Height <= maxHeight.Value) &&
            (!maxFps.HasValue || f.FrameRate <= 0 || f.FrameRate <= maxFps.Value + 0.5d))
            .ToList();

        var candidates = constrained.Count > 0
            ? constrained
            : [valid.OrderBy(static f => (long)f.Width * f.Height).ThenByDescending(static f => f.FrameRate).First()];

        var targetWidth = requestedWidth ?? playerOptions?.PreferredWidth;
        var targetHeight = requestedHeight ?? playerOptions?.PreferredHeight;
        var targetFps = requestedFps ?? playerOptions?.PreferredFrameRate;
        var targetFormat = requestedFormat ?? playerOptions?.PreferredPixelFormat;

        return candidates
            .OrderByDescending(f => ComputeScore(f, targetWidth, targetHeight, targetFps, targetFormat))
            .ThenByDescending(static f => GetPixelFormatPriority(f.PixelFormat))
            .ThenByDescending(static f => f.FrameRate)
            .ThenByDescending(static f => (long)f.Width * f.Height)
            .First();
    }

    private static (int FormatMatch, int FpsTier, int ResolutionScore, double FpsScore, int PixelRank) ComputeScore(
        MediaCaptureVideoFormat candidate,
        int? targetWidth,
        int? targetHeight,
        double? targetFps,
        MediaCapturePixelFormat? targetFormat)
    {
        var formatMatch = targetFormat.HasValue && targetFormat.Value != MediaCapturePixelFormat.Unknown
            ? (candidate.PixelFormat == targetFormat.Value ? 1 : 0)
            : 0;

        var fps = candidate.FrameRate > 0 ? candidate.FrameRate : 30d;
        var fpsTier = targetFps.HasValue
            ? (Math.Abs(fps - targetFps.Value) <= 1.0d ? 2 : fps >= targetFps.Value ? 1 : 0)
            : (fps >= PreferredMinimumFps ? 1 : 0);

        var area = (long)candidate.Width * candidate.Height;
        int resolutionScore;
        if (targetWidth.HasValue || targetHeight.HasValue)
        {
            var reqW = targetWidth ?? candidate.Width;
            var reqH = targetHeight ?? candidate.Height;
            if (candidate.Width == reqW && candidate.Height == reqH)
            {
                resolutionScore = int.MaxValue;
            }
            else
            {
                var diff = Math.Abs(candidate.Width - reqW) + Math.Abs(candidate.Height - reqH);
                var coversTarget = candidate.Width >= reqW && candidate.Height >= reqH ? 1_000_000 : 0;
                resolutionScore = coversTarget - diff;
            }
        }
        else
        {
            // When no explicit resolution is given, prefer standard Full HD (1080p) or below over ultra-high-res low-fps modes.
            resolutionScore = area <= StandardFullHdPixels
                ? (int)area
                : (int)(StandardFullHdPixels - Math.Min(StandardFullHdPixels - 1, (area - StandardFullHdPixels) / 4));
        }

        var fpsScore = targetFps.HasValue
            ? -Math.Abs(fps - targetFps.Value)
            : Math.Min(fps, 60d);

        return (formatMatch, fpsTier, resolutionScore, fpsScore, GetPixelFormatPriority(candidate.PixelFormat));
    }

    internal static int GetPixelFormatPriority(MediaCapturePixelFormat format) =>
        format switch
        {
            MediaCapturePixelFormat.Nv12 => 7,
            MediaCapturePixelFormat.H264 => 6,
            MediaCapturePixelFormat.Hevc => 5,
            MediaCapturePixelFormat.Mjpeg => 4,
            MediaCapturePixelFormat.Yuy2 => 3,
            MediaCapturePixelFormat.Yuv420P => 2,
            MediaCapturePixelFormat.Bgra32 => 1,
            _ => 0
        };
}
