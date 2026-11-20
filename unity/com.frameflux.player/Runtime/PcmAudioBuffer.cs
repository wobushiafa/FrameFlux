using System;

namespace FrameFlux.Unity
{
    // Bounded stereo ring. Producer and Unity's audio callback share this lock;
    // neither path allocates per sample or calls Unity APIs.
    internal sealed class PcmAudioBuffer
    {
        private readonly object sync = new object();
        private readonly float[] samples;
        private int head, count;
        private long played;
        private long outputPhase, inputPhase;
        private int inputRate = 48000, inputChannels = 2;
        private float gain = 1;
        private bool paused;
        internal PcmAudioBuffer(int frames = 24000)
        {
            if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
            samples = new float[checked(frames * 2)];
        }
        internal long PlayedFrames { get { lock (sync) return played; } }
        internal int QueuedFrames { get { lock (sync) return count / 2; } }
        internal void Configure(int rate, int channels)
        {
            if (rate < 4000 || channels < 1 || channels > 2) throw new ArgumentOutOfRangeException(nameof(rate));
            lock (sync)
            {
                if (inputRate == rate && inputChannels == channels) return;
                inputRate = rate; inputChannels = channels;
                head = count = 0; inputPhase = outputPhase = 0;
            }
        }
        internal void SetVolume(double volume, bool muted) { lock (sync) gain = muted ? 0 : (float)Math.Max(0, Math.Min(1, volume)); }
        internal void SetPaused(bool value) { lock (sync) paused = value; }
        internal void Reset() { lock (sync) { head = count = 0; inputPhase = outputPhase = 0; } }

        internal void Write(ReadOnlySpan<short> pcm)
        {
            lock (sync)
            {
                int frames = pcm.Length / inputChannels;
                long limit = frames * 48000L;
                while (inputPhase < limit)
                {
                    int source = (int)(inputPhase / 48000) * inputChannels;
                    float left = pcm[source] / 32768f;
                    float right = inputChannels == 1 ? left : pcm[source + 1] / 32768f;
                    if (count == samples.Length) { head = (head + 2) % samples.Length; count -= 2; }
                    int tail = (head + count) % samples.Length;
                    samples[tail] = left; samples[(tail + 1) % samples.Length] = right;
                    count += 2; inputPhase += inputRate;
                }
                inputPhase -= limit;
            }
        }

        internal void Read(float[] data, int channels, int outputRate)
        {
            Array.Clear(data, 0, data.Length);
            lock (sync)
            {
                if (paused || channels <= 0 || outputRate <= 0) return;
                for (int destination = 0; destination + channels <= data.Length; destination += channels)
                {
                    if (count < 2) { outputPhase = 0; break; }
                    float left = samples[head] * gain, right = samples[(head + 1) % samples.Length] * gain;
                    data[destination] = channels == 1 ? (left + right) * 0.5f : left;
                    if (channels > 1) data[destination + 1] = right;
                    outputPhase += 48000;
                    int consume = Math.Min(count / 2, (int)(outputPhase / outputRate));
                    head = (head + consume * 2) % samples.Length; count -= consume * 2;
                    played += consume; outputPhase -= consume * (long)outputRate;
                }
            }
        }
    }
}
