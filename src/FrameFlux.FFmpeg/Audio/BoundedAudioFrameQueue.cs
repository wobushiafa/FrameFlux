namespace FrameFlux.FFmpeg;

internal sealed class BoundedAudioFrameQueue
{
    internal const int DefaultCapacity = 32;

    private readonly Queue<NativeAudioFrame> _frames;
    private readonly int _capacity;

    internal BoundedAudioFrameQueue(int capacity = DefaultCapacity)
    {
        RuntimeCompat.AtLeast(capacity, 1, nameof(capacity));
        _capacity = capacity;
        _frames = new Queue<NativeAudioFrame>(capacity);
    }

    internal int Count => _frames.Count;

    internal int DroppedCount { get; private set; }

    internal void Enqueue(NativeAudioFrame frame)
    {
        RuntimeCompat.NotNull(frame, nameof(frame));
        if (_frames.Count == _capacity)
        {
            _frames.Dequeue().Dispose();
            DroppedCount++;
        }

        _frames.Enqueue(frame);
    }

    internal void Clear()
    {
        while (_frames.TryDequeue(out var dropped))
        {
            dropped.Dispose();
        }
    }

    internal bool TryDequeue(out NativeAudioFrame? frame) =>
        _frames.TryDequeue(out frame);
}
