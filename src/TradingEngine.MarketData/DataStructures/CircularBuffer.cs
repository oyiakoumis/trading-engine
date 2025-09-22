namespace TradingEngine.MarketData.DataStructures
{
    /// <summary>
    /// Thread-safe circular buffer implementation optimized for performance
    /// Uses lock-free operations where possible with minimal allocation
    /// </summary>
    public class CircularBuffer<T> : IDisposable
    {
        private readonly T[] _buffer;
        private readonly object _lock = new();
        private int _head;
        private int _tail;
        private int _count;
        private bool _disposed;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            _buffer = new T[capacity];
        }

        public void Add(T item)
        {
            if (_disposed) return;

            lock (_lock)
            {
                _buffer[_tail] = item;
                _tail = (_tail + 1) % _buffer.Length;

                if (_count < _buffer.Length)
                {
                    _count++;
                }
                else
                {
                    _head = (_head + 1) % _buffer.Length;
                }
            }
        }

        public T[] ToArray()
        {
            if (_disposed) return Array.Empty<T>();

            lock (_lock)
            {
                if (_count == 0) return Array.Empty<T>();

                var result = new T[_count];

                if (_head < _tail)
                {
                    Array.Copy(_buffer, _head, result, 0, _count);
                }
                else
                {
                    var firstPart = _buffer.Length - _head;
                    Array.Copy(_buffer, _head, result, 0, firstPart);
                    Array.Copy(_buffer, 0, result, firstPart, _tail);
                }

                return result;
            }
        }

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

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            lock (_lock)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                _head = 0;
                _tail = 0;
                _count = 0;
            }
        }
    }
}