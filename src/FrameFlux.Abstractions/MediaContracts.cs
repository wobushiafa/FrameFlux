using System.ComponentModel;
using System.Globalization;

namespace FrameFlux;

[TypeConverter(typeof(MediaSourceTypeConverter))]
public sealed record MediaSource
{
    public MediaSource(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("Media source URI must be absolute.", nameof(uri));
        }

        Uri = uri;
    }

    public Uri Uri { get; }

    public static MediaSource FromUri(Uri uri) => new(uri);

    public static MediaSource FromUri(string value) => Parse(value);

    public static MediaSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new MediaSource(new Uri(Path.GetFullPath(path)));
    }

    public static MediaSource Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Path.IsPathFullyQualified(value))
        {
            return FromFile(value);
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? new MediaSource(uri)
            : throw new ArgumentException("Media source must be an absolute URI or file path.", nameof(value));
    }

    public override string ToString() => Uri.ToString();
}

public sealed class MediaSourceTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value) =>
        value is string text
            ? MediaSource.Parse(text)
            : base.ConvertFrom(context, culture, value);
}

public enum MediaPlaybackState
{
    Idle,
    Opening,
    Ready,
    Playing,
    Paused,
    Reconnecting,
    Stopping,
    Stopped,
    Faulted
}

public sealed record MediaPlaybackError(
    string Code,
    string Message,
    bool IsRecoverable,
    Exception? Exception = null);

public sealed class MediaPlaybackStateChangedEventArgs(
    MediaPlaybackState oldState,
    MediaPlaybackState newState) : EventArgs
{
    public MediaPlaybackState OldState { get; } = oldState;

    public MediaPlaybackState NewState { get; } = newState;
}

public sealed class MediaPlaybackErrorEventArgs(MediaPlaybackError error) : EventArgs
{
    public MediaPlaybackError Error { get; } = error;
}
