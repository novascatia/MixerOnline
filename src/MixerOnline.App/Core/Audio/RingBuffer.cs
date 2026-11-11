using System;

namespace MixerOnline.Core.Audio
{
    /// <summary>
    /// Thread-safe circular buffer for float audio samples with low-latency non-blocking read/write operations.
    /// </summary>
    public sealed class RingBuffer
    {
        private readonly float[] _buffer;
        private readonly int _capacity;
        private int _writeIndex;
        private int _readIndex;
        private int _count;
        private readonly object _lock = new();

        public RingBuffer(int capacity = 192000) // Default ~2 seconds at 48kHz stereo (96,000 floats/sec)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

            _capacity = capacity;
            _buffer = new float[capacity];
        }

        public int Capacity => _capacity;

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        public int Write(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return 0;

            lock (_lock)
            {
                int written = 0;
                int toWrite = samples.Length;

                // If overflow occurs, advance read pointer to drop oldest samples
                int availableSpace = _capacity - _count;
                if (toWrite > availableSpace)
                {
                    int overflow = toWrite - availableSpace;
                    _readIndex = (_readIndex + overflow) % _capacity;
                    _count -= overflow;
                }

                while (written < toWrite)
                {
                    int chunk = Math.Min(toWrite - written, _capacity - _writeIndex);
                    samples.Slice(written, chunk).CopyTo(_buffer.AsSpan(_writeIndex, chunk));
                    _writeIndex = (_writeIndex + chunk) % _capacity;
                    written += chunk;
                }

                _count += written;
                return written;
            }
        }

        public int Read(Span<float> destination)
        {
            if (destination.IsEmpty) return 0;

            lock (_lock)
            {
                if (_count == 0)
                {
                    destination.Clear();
                    return 0;
                }

                int toRead = Math.Min(destination.Length, _count);
                int read = 0;

                while (read < toRead)
                {
                    int chunk = Math.Min(toRead - read, _capacity - _readIndex);
                    _buffer.AsSpan(_readIndex, chunk).CopyTo(destination.Slice(read, chunk));
                    _readIndex = (_readIndex + chunk) % _capacity;
                    read += chunk;
                }

                _count -= read;

                // If requested more than available, pad the rest with silence
                if (read < destination.Length)
                {
                    destination.Slice(read).Clear();
                }

                return read;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _writeIndex = 0;
                _readIndex = 0;
                _count = 0;
                Array.Clear(_buffer, 0, _capacity);
            }
        }
    }
}
