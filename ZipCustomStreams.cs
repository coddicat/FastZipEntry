// -----------------------------------------------------------------------
// <copyright file="ZipCustomStreams.cs">
// Source: https://github.com/dotnet/runtime/tree/9daa4b41eb9f157e79eaf05e2f7451c9c8f6dbdc/src/libraries/System.IO.Compression/src/System/IO/Compression/
// Original code from Microsoft, modified by Rodion Shlomo Solomonyk
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// -----------------------------------------------------------------------

using System.Diagnostics;

namespace FastZipEntry
{
    internal sealed class SubReadStream(Stream superStream, long startPosition, long maxLength) : Stream
    {
        private readonly long _startInSuperStream = startPosition;
        private long _positionInSuperStream = startPosition;
        private readonly long _endInSuperStream = startPosition + maxLength;
        private readonly Stream _superStream = superStream;
        private bool _canRead = true;
        private bool _isDisposed = false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();

                return _endInSuperStream - _startInSuperStream;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();

                return _positionInSuperStream - _startInSuperStream;
            }
            set
            {
                ThrowIfDisposed();

                throw new NotSupportedException("SeekingNotSupported");
            }
        }

        public override bool CanRead => _superStream.CanRead && _canRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().ToString(), "HiddenStreamName");
        }

        private void ThrowIfCantRead()
        {
            if (!CanRead)
                throw new NotSupportedException("ReadingNotSupported");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // parameter validation sent to _superStream.Read
            int origCount = count;

            ThrowIfDisposed();
            ThrowIfCantRead();

            if (_superStream.Position != _positionInSuperStream)
                _superStream.Seek(_positionInSuperStream, SeekOrigin.Begin);
            if (_positionInSuperStream + count > _endInSuperStream)
                count = (int)(_endInSuperStream - _positionInSuperStream);

            Debug.Assert(count >= 0);
            Debug.Assert(count <= origCount);

            int ret = _superStream.Read(buffer, offset, count);

            _positionInSuperStream += ret;
            return ret;
        }

        public override int Read(Span<byte> destination)
        {
            // parameter validation sent to _superStream.Read
            int origCount = destination.Length;
            int count = destination.Length;

            ThrowIfDisposed();
            ThrowIfCantRead();

            if (_superStream.Position != _positionInSuperStream)
                _superStream.Seek(_positionInSuperStream, SeekOrigin.Begin);
            if (_positionInSuperStream + count > _endInSuperStream)
                count = (int)(_endInSuperStream - _positionInSuperStream);

            Debug.Assert(count >= 0);
            Debug.Assert(count <= origCount);

            int ret = _superStream.Read(destination[..count]);

            _positionInSuperStream += ret;
            return ret;
        }

        public override int ReadByte()
        {
            byte b = default;
            return Read(new Span<byte>(ref b)) == 1 ? b : -1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ThrowIfCantRead();
            return Core(buffer, cancellationToken);

            async ValueTask<int> Core(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_superStream.Position != _positionInSuperStream)
                {
                    _superStream.Seek(_positionInSuperStream, SeekOrigin.Begin);
                }

                if (_positionInSuperStream > _endInSuperStream - buffer.Length)
                {
                    buffer = buffer[..(int)(_endInSuperStream - _positionInSuperStream)];
                }

                int ret = await _superStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                _positionInSuperStream += ret;
                return ret;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            throw new NotSupportedException("SeekingNotSupported");
        }

        public override void SetLength(long value)
        {
            ThrowIfDisposed();
            throw new NotSupportedException("SetLengthRequiresSeekingAndWriting");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            throw new NotSupportedException("WritingNotSupported");
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            throw new NotSupportedException("WritingNotSupported");
        }

        // Close the stream for reading.  Note that this does NOT close the superStream (since
        // the substream is just 'a chunk' of the super-stream
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _canRead = false;
                _isDisposed = true;
            }
            base.Dispose(disposing);
        }
    }
}