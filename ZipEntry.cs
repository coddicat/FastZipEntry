// -----------------------------------------------------------------------
// <copyright file="ZipArchiveEntry.cs">
// Source: https://github.com/dotnet/runtime/tree/9daa4b41eb9f157e79eaf05e2f7451c9c8f6dbdc/src/libraries/System.IO.Compression/src/System/IO/Compression/
// Original code from Microsoft, modified by Rodion Shlomo Solomonyk
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// -----------------------------------------------------------------------

using FastZipEntry.Deflate64;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;

namespace FastZipEntry
{
    // The disposable fields that this class owns get disposed when the ZipArchive it belongs to gets disposed
    public class ZipEntry
    {
        private readonly ZipEntryAccess _archive;
        private readonly bool _originallyInArchive;
        private readonly uint _diskNumberStart;
        private readonly ZipVersionMadeByPlatform _versionMadeByPlatform;
        private ZipVersionNeededValues _versionMadeBySpecification;
        internal ZipVersionNeededValues _versionToExtract;
        private CompressionMethodValues _storedCompressionMethod;
        private readonly DateTimeOffset _lastModified;
        private readonly long _compressedSize;
        private readonly long _uncompressedSize;
        private readonly long _offsetOfLocalHeader;
        private long? _storedOffsetOfCompressedData;
        private readonly bool _everOpenedForWrite;
        private string _storedEntryName;
        private byte[] _storedEntryNameBytes;

        // Initializes a ZipArchiveEntry instance for an existing archive entry.
        internal ZipEntry(ZipEntryAccess archive, ZipCentralDirectoryFileHeader cd)
        {
            _archive = archive;

            _originallyInArchive = true;

            _diskNumberStart = cd.DiskNumberStart;
            _versionMadeByPlatform = (ZipVersionMadeByPlatform)cd.VersionMadeByCompatibility;
            _versionMadeBySpecification = (ZipVersionNeededValues)cd.VersionMadeBySpecification;
            _versionToExtract = (ZipVersionNeededValues)cd.VersionNeededToExtract;

            CompressionMethod = (CompressionMethodValues)cd.CompressionMethod;
            _lastModified = new DateTimeOffset(ZipHelper.DosTimeToDateTime(cd.LastModified));
            _compressedSize = cd.CompressedSize;
            _uncompressedSize = cd.UncompressedSize;
            _offsetOfLocalHeader = cd.RelativeOffsetOfLocalHeader;
            // we don't know this yet: should be _offsetOfLocalHeader + 30 + _storedEntryNameBytes.Length + extrafieldlength
            // but entryname/extra length could be different in LH
            _storedOffsetOfCompressedData = null;
            
            _everOpenedForWrite = false;

            _storedEntryNameBytes = cd.Filename;
            _storedEntryName = (_archive.EntryNameAndCommentEncoding).GetString(_storedEntryNameBytes);
            DetectEntryNameVersion();
        }

        /// <summary>
        /// The relative path of the entry as stored in the Zip archive. Note that Zip archives allow any string to be the path of the entry, including invalid and absolute paths.
        /// </summary>
        public string FullName
        {
            get
            {
                return _storedEntryName;
            }

            [MemberNotNull(nameof(_storedEntryNameBytes))]
            [MemberNotNull(nameof(_storedEntryName))]
            private set
            {
                ArgumentNullException.ThrowIfNull(value, nameof(FullName));

                _storedEntryNameBytes = ZipHelper.GetEncodedTruncatedBytesFromString(
                    value, _archive.EntryNameAndCommentEncoding, 0 /* No truncation */);

                _storedEntryName = value;

                DetectEntryNameVersion();
            }
        }

        /// <summary>
        /// The last write time of the entry as stored in the Zip archive. When setting this property, the DateTime will be converted to the
        /// Zip timestamp format, which supports a resolution of two seconds. If the data in the last write time field is not a valid Zip timestamp,
        /// an indicator value of 1980 January 1 at midnight will be returned.
        /// </summary>
        /// <exception cref="NotSupportedException">An attempt to set this property was made, but the ZipArchive that this entry belongs to was
        /// opened in read-only mode.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An attempt was made to set this property to a value that cannot be represented in the
        /// Zip timestamp format. The earliest date/time that can be represented is 1980 January 1 0:00:00 (midnight), and the last date/time
        /// that can be represented is 2107 December 31 23:59:58 (one second before midnight).</exception>
        public DateTimeOffset LastWriteTime
        {
            get
            {
                return _lastModified;
            }
            set
            {
                ThrowIfInvalidArchive();
                throw new NotSupportedException("ReadOnlyArchive");
            }
        }

        /// <summary>
        /// The uncompressed size of the entry. This property is not valid in Create mode, and it is only valid in Update mode if the entry has not been opened.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is not available because the entry has been written to or modified.</exception>
        public long Length
        {
            get
            {
                if (_everOpenedForWrite)
                    throw new InvalidOperationException("LengthAfterWrite");
                return _uncompressedSize;
            }
        }

        /// <summary>
        /// The filename of the entry. This is equivalent to the substring of Fullname that follows the final directory separator character.
        /// </summary>
        public string Name => ParseFileName(FullName, _versionMadeByPlatform);

        internal static string ParseFileName(string path, ZipVersionMadeByPlatform madeByPlatform) =>
           madeByPlatform == ZipVersionMadeByPlatform.Unix ? GetFileName_Unix(path) : GetFileName_Windows(path);

        /// <summary>
        /// Opens the entry. If the archive that the entry belongs to was opened in Read mode, the returned stream will be readable, and it may or may not be seekable. If Create mode, the returned stream will be writable and not seekable. If Update mode, the returned stream will be readable, writable, seekable, and support SetLength.
        /// </summary>
        /// <returns>A Stream that represents the contents of the entry.</returns>
        /// <exception cref="IOException">The entry is already currently open for writing. -or- The entry has been deleted from the archive. -or- The archive that this entry belongs to was opened in ZipArchiveMode.Create, and this entry has already been written to once.</exception>
        /// <exception cref="InvalidDataException">The entry is missing from the archive or is corrupt and cannot be read. -or- The entry has been compressed using a compression method that is not supported.</exception>
        /// <exception cref="ObjectDisposedException">The ZipArchive that this entry belongs to has been disposed.</exception>
        public Stream Open()
        {
            ThrowIfInvalidArchive();

            return OpenInReadMode(checkOpenable: true);
        }

        // Only allow opening ZipArchives with large ZipArchiveEntries in update mode when running in a 64-bit process.
        // This is for compatibility with old behavior that threw an exception for all process bitnesses, because this
        // will not work in a 32-bit process.
        private static readonly bool s_allowLargeZipArchiveEntriesInUpdateMode = IntPtr.Size > 4;

        private long OffsetOfCompressedData
        {
            get
            {
                if (_storedOffsetOfCompressedData == null)
                {
                    Debug.Assert(_archive.ArchiveReader != null);
                    _archive.ArchiveStream.Seek(_offsetOfLocalHeader, SeekOrigin.Begin);
                    // by calling this, we are using local header _storedEntryNameBytes.Length and extraFieldLength
                    // to find start of data, but still using central directory size information
                    if (!ZipLocalFileHeader.TrySkipBlock(_archive.ArchiveReader))
                        throw new InvalidDataException("LocalFileHeaderCorrupt");
                    _storedOffsetOfCompressedData = _archive.ArchiveStream.Position;
                }
                return _storedOffsetOfCompressedData.Value;
            }
        }

        private CompressionMethodValues CompressionMethod
        {
            get { return _storedCompressionMethod; }
            set
            {
                if (value == CompressionMethodValues.Deflate)
                    VersionToExtractAtLeast(ZipVersionNeededValues.Deflate);
                else if (value == CompressionMethodValues.Deflate64)
                    VersionToExtractAtLeast(ZipVersionNeededValues.Deflate64);
                _storedCompressionMethod = value;
            }
        }

        internal void ThrowIfNotOpenable(bool needToUncompress, bool needToLoadIntoMemory)
        {
            if (!IsOpenable(needToUncompress, needToLoadIntoMemory, out string? message))
                throw new InvalidDataException(message);
        }

        private void DetectEntryNameVersion()
        {
            if (ParseFileName(_storedEntryName, _versionMadeByPlatform) == "")
            {
                VersionToExtractAtLeast(ZipVersionNeededValues.ExplicitDirectory);
            }
        }

        private Stream GetDataDecompressor(Stream compressedStreamToRead)
        {
            Stream? uncompressedStream;
            switch (CompressionMethod)
            {
                case CompressionMethodValues.Deflate:
                    uncompressedStream = new DeflateStream(compressedStreamToRead, CompressionMode.Decompress);
                    break;
                case CompressionMethodValues.Deflate64:
                    uncompressedStream = new Deflate64Stream(compressedStreamToRead, CompressionMethodValues.Deflate64, _uncompressedSize);
                    break;
                case CompressionMethodValues.Stored:
                default:
                    // we can assume that only deflate/deflate64/stored are allowed because we assume that
                    // IsOpenable is checked before this function is called
                    Debug.Assert(CompressionMethod == CompressionMethodValues.Stored);

                    uncompressedStream = compressedStreamToRead;
                    break;
            }

            return uncompressedStream;
        }

        private Stream OpenInReadMode(bool checkOpenable)
        {
            if (checkOpenable)
                ThrowIfNotOpenable(needToUncompress: true, needToLoadIntoMemory: false);

            Stream compressedStream = new SubReadStream(_archive.ArchiveStream, OffsetOfCompressedData, _compressedSize);
            return GetDataDecompressor(compressedStream);
        }
       
        private bool IsOpenable(bool needToUncompress, bool needToLoadIntoMemory, out string? message)
        {
            message = null;

            if (_originallyInArchive)
            {
                if (needToUncompress)
                {
                    if (CompressionMethod != CompressionMethodValues.Stored &&
                        CompressionMethod != CompressionMethodValues.Deflate &&
                        CompressionMethod != CompressionMethodValues.Deflate64)
                    {
                        switch (CompressionMethod)
                        {
                            case CompressionMethodValues.BZip2:
                            case CompressionMethodValues.LZMA:
                                message = $"UnsupportedCompressionMethod { CompressionMethod }";
                                break;
                            default:
                                message = "UnsupportedCompression";
                                break;
                        }
                        return false;
                    }
                }
                if (_diskNumberStart != _archive.NumberOfThisDisk)
                {
                    message = "SplitSpanned";
                    return false;
                }
                if (_offsetOfLocalHeader > _archive.ArchiveStream.Length)
                {
                    message = "LocalFileHeaderCorrupt";
                    return false;
                }
                Debug.Assert(_archive.ArchiveReader != null);
                _archive.ArchiveStream.Seek(_offsetOfLocalHeader, SeekOrigin.Begin);
                if (!ZipLocalFileHeader.TrySkipBlock(_archive.ArchiveReader))
                {
                    message = "LocalFileHeaderCorrupt";
                    return false;
                }
                // when this property gets called, some duplicated work
                if (OffsetOfCompressedData + _compressedSize > _archive.ArchiveStream.Length)
                {
                    message = "LocalFileHeaderCorrupt";
                    return false;
                }
                // This limitation originally existed because a) it is unreasonable to load > 4GB into memory
                // but also because the stream reading functions make it hard.  This has been updated to handle
                // this scenario in a 64-bit process using multiple buffers, delivered first as an OOB for
                // compatibility.
                if (needToLoadIntoMemory)
                {
                    if (_compressedSize > int.MaxValue)
                    {
                        if (!s_allowLargeZipArchiveEntriesInUpdateMode)
                        {
                            message = "EntryTooLarge";
                            return false;
                        }
                    }
                }
            }

            return true;
        }
                
        private void VersionToExtractAtLeast(ZipVersionNeededValues value)
        {
            if (_versionToExtract < value)
            {
                _versionToExtract = value;
            }
            if (_versionMadeBySpecification < value)
            {
                _versionMadeBySpecification = value;
            }
        }

        private void ThrowIfInvalidArchive()
        {
            if (_archive == null)
                throw new InvalidOperationException("DeletedEntry");
            _archive.ThrowIfDisposed();
        }

        /// <summary>
        /// Gets the file name of the path based on Windows path separator characters
        /// </summary>
        private static string GetFileName_Windows(string path)
        {
            int i = path.AsSpan().LastIndexOfAny('\\', '/', ':');
            return i >= 0 ?
                path[(i + 1)..] :
                path;
        }

        /// <summary>
        /// Gets the file name of the path based on Unix path separator characters
        /// </summary>
        private static string GetFileName_Unix(string path)
        {
            int i = path.LastIndexOf('/');
            return i >= 0 ?
                path[(i + 1)..] :
                path;
        }               

        internal enum CompressionMethodValues : ushort
        {
            Stored = 0x0,
            Deflate = 0x8,
            Deflate64 = 0x9,
            BZip2 = 0xC,
            LZMA = 0xE
        }
    }
}