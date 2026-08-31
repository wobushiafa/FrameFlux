namespace FrameFlux.FFmpeg;

internal sealed class BoundedAudioFrameQueue
{
    internal const int DefaultCapacity = 32;

    private readonly Queue<NativeAudioFrame> _frames;
    private readonly int _capacity;

    internal BoundedAudioFrameQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _frames = new Queue<NativeAudioFrame>(capacity);
    }

    internal int Count => _frames.Count;

    internal int DroppedCount { get; private set; }

    internal void Enqueue(NativeAudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_frames.Count == _capacity)
        {
            _frames.Dequeue();
            DroppedCount++;
        }

        _frames.Enqueue(frame);
    }

    internal void Clear() => _frames.Clear();

    internal bool TryDequeue(out NativeAudioFrame? frame) =>
        _frames.TryDequeue(out frame);
}
