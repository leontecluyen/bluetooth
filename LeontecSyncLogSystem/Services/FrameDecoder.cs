using System.Text;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Stateful STX/ETX frame extractor for a byte stream.
    ///
    /// Bytes arrive in arbitrary chunks from the serial port; a single logical frame
    /// may be split across several DataReceived events, and a single read may contain
    /// several frames. Feed every received byte through <see cref="Push(byte)"/>; each
    /// completed frame (the bytes between STX 0x02 and ETX 0x03, exclusive) is returned
    /// as a decoded, trimmed UTF-8 string.
    ///
    /// Isolated from <see cref="SerialPortListener"/> so the framing rules can be unit
    /// tested without any serial hardware.
    /// </summary>
    public sealed class FrameDecoder
    {
        public const byte STX = 0x02;
        public const byte ETX = 0x03;

        private const int MaxFrameBytes = 64 * 1024;

        private readonly List<byte> _buffer = new(256);
        private bool _inFrame;

        /// <summary>
        /// Feed one byte. Returns the completed frame text when an ETX closes a frame,
        /// otherwise null. Empty frames (STX immediately followed by ETX) return null.
        /// </summary>
        public string? Push(byte b)
        {
            switch (b)
            {
                case STX:
                    // A new STX always restarts framing, discarding any partial frame.
                    _buffer.Clear();
                    _inFrame = true;
                    return null;

                case ETX when _inFrame:
                    string? result = null;
                    if (_buffer.Count > 0)
                    {
                        var text = Encoding.UTF8.GetString(_buffer.ToArray()).Trim();
                        if (text.Length > 0)
                            result = text;
                    }
                    _buffer.Clear();
                    _inFrame = false;
                    return result;

                default:
                    if (_inFrame)
                    {
                        if (_buffer.Count < MaxFrameBytes)
                        {
                            _buffer.Add(b);
                        }
                        else
                        {
                            // Runaway frame (missing ETX): drop it to bound memory.
                            _buffer.Clear();
                            _inFrame = false;
                        }
                    }
                    // Bytes outside a frame (e.g. line noise, keep-alives) are ignored.
                    return null;
            }
        }

        /// <summary>Feed a chunk; returns every frame completed within it, in order.</summary>
        public IEnumerable<string> Push(byte[] data, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var frame = Push(data[i]);
                if (frame is not null)
                    yield return frame;
            }
        }

        public void Reset()
        {
            _buffer.Clear();
            _inFrame = false;
        }
    }
}
