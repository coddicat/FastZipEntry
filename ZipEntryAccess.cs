// -----------------------------------------------------------------------
// <copyright file="ZipArchive.cs">
// Source: https://github.com/dotnet/runtime/tree/9daa4b41eb9f157e79eaf05e2f7451c9c8f6dbdc/src/libraries/System.IO.Compression/src/System/IO/Compression/
// Original code from Microsoft, modified by Rodion Shlomo Solomonyk
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// -----------------------------------------------------------------------

using System.Diagnostics;
using System.Text;

namespace FastZipEntry
{
    /// <summary>
    /// Provides functionality to retrieve specific entries from a ZIP archive efficiently without extracting the entire archive or iterating through all entries.
    /// </summary>
    public class ZipEntryAccess : IDisposable
    {
        private bool _isDisposed;
        private readonly Stream _archiveStream;
        private readonly BinaryReader _archiveReader;
        private readonly Stream? _backingStream;
        private uint _numberOfThisDisk; //only valid after ReadCentralDirectory
        private long _centralDirectoryStart; //only valid after ReadCentralDirectory
        private long _expectedNumberOfEntries;
        private readonly Encoding _entryNameAndCommentEncoding;

        internal Encoding EntryNameAndCommentEncoding => _entryNameAndCommentEncoding;
        internal uint NumberOfThisDisk => _numberOfThisDisk;
        internal BinaryReader? ArchiveReader => _archiveReader;
        internal Stream ArchiveStream => _archiveStream;

        public ZipEntryAccess(Stream stream, Encoding? entryNameAndCommentEncoding = null)
        {
            ArgumentNullException.ThrowIfNull(stream);

            _entryNameAndCommentEncoding = entryNameAndCommentEncoding ?? Encoding.UTF8;

            if (!stream.CanRead)
                throw new ArgumentException("ReadModeCapabilities");
            
            if (!stream.CanSeek)
            {
                _backingStream = stream;
                stream = new MemoryStream();
                _backingStream.CopyTo(stream);
                stream.Seek(0, SeekOrigin.Begin);
            }

            _archiveStream = stream;
            _archiveReader = new (_archiveStream, Encoding.UTF8, leaveOpen: true);
            ReadEndOfCentralDirectory();
        }

        internal void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }

        private void ReadEndOfCentralDirectory()
        {
            try
            {
                // This seeks backwards almost to the beginning of the EOCD, one byte after where the signature would be
                // located if the EOCD had the minimum possible size (no file zip comment)
                _archiveStream.Seek(-ZipEndOfCentralDirectoryBlock.SizeOfBlockWithoutSignature, SeekOrigin.End);

                // If the EOCD has the minimum possible size (no zip file comment), then exactly the previous 4 bytes will contain the signature
                // But if the EOCD has max possible size, the signature should be found somewhere in the previous 64K + 4 bytes
                if (!ZipHelper.SeekBackwardsToSignature(_archiveStream,
                        ZipEndOfCentralDirectoryBlock.SignatureConstant,
                        ZipEndOfCentralDirectoryBlock.ZipFileCommentMaxLength + ZipEndOfCentralDirectoryBlock.SignatureSize))
                    throw new InvalidDataException("EOCDNotFound");

                long eocdStart = _archiveStream.Position;

                Debug.Assert(_archiveReader != null);
                // read the EOCD
                bool eocdProper = ZipEndOfCentralDirectoryBlock.TryReadBlock(_archiveReader, out ZipEndOfCentralDirectoryBlock eocd);
                Debug.Assert(eocdProper); // we just found this using the signature finder, so it should be okay

                if (eocd.NumberOfThisDisk != eocd.NumberOfTheDiskWithTheStartOfTheCentralDirectory)
                    throw new InvalidDataException("SplitSpanned");

                _numberOfThisDisk = eocd.NumberOfThisDisk;
                _centralDirectoryStart = eocd.OffsetOfStartOfCentralDirectoryWithRespectToTheStartingDiskNumber;

                if (eocd.NumberOfEntriesInTheCentralDirectory != eocd.NumberOfEntriesInTheCentralDirectoryOnThisDisk)
                    throw new InvalidDataException("SplitSpanned");

                _expectedNumberOfEntries = eocd.NumberOfEntriesInTheCentralDirectory;

                TryReadZip64EndOfCentralDirectory(eocd, eocdStart);

                if (_centralDirectoryStart > _archiveStream.Length)
                {
                    throw new InvalidDataException("FieldTooBigOffsetToCD");
                }
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("CDCorrupt", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidDataException("CDCorrupt", ex);
            }
        }

        private void TryReadZip64EndOfCentralDirectory(ZipEndOfCentralDirectoryBlock eocd, long eocdStart)
        {
            // Only bother looking for the Zip64-EOCD stuff if we suspect it is needed because some value is FFFFFFFFF
            // because these are the only two values we need, we only worry about these
            // if we don't find the Zip64-EOCD, we just give up and try to use the original values
            if (eocd.NumberOfThisDisk == ZipHelper.Mask16Bit ||
                eocd.OffsetOfStartOfCentralDirectoryWithRespectToTheStartingDiskNumber == ZipHelper.Mask32Bit ||
                eocd.NumberOfEntriesInTheCentralDirectory == ZipHelper.Mask16Bit)
            {
                // Read Zip64 End of Central Directory Locator

                // This seeks forwards almost to the beginning of the Zip64-EOCDL, one byte after where the signature would be located
                _archiveStream.Seek(eocdStart - Zip64EndOfCentralDirectoryLocator.SizeOfBlockWithoutSignature, SeekOrigin.Begin);

                // Exactly the previous 4 bytes should contain the Zip64-EOCDL signature
                // if we don't find it, assume it doesn't exist and use data from normal EOCD
                if (ZipHelper.SeekBackwardsToSignature(_archiveStream,
                        Zip64EndOfCentralDirectoryLocator.SignatureConstant,
                        Zip64EndOfCentralDirectoryLocator.SignatureSize))
                {
                    Debug.Assert(_archiveReader != null);

                    // use locator to get to Zip64-EOCD
                    bool zip64eocdLocatorProper = Zip64EndOfCentralDirectoryLocator.TryReadBlock(_archiveReader, out Zip64EndOfCentralDirectoryLocator locator);
                    Debug.Assert(zip64eocdLocatorProper); // we just found this using the signature finder, so it should be okay

                    if (locator.OffsetOfZip64EOCD > long.MaxValue)
                        throw new InvalidDataException("FieldTooBigOffsetToZip64EOCD");

                    long zip64EOCDOffset = (long)locator.OffsetOfZip64EOCD;

                    _archiveStream.Seek(zip64EOCDOffset, SeekOrigin.Begin);

                    // Read Zip64 End of Central Directory Record

                    if (!Zip64EndOfCentralDirectoryRecord.TryReadBlock(_archiveReader, out Zip64EndOfCentralDirectoryRecord record))
                        throw new InvalidDataException("Zip64EOCDNotWhereExpected");

                    _numberOfThisDisk = record.NumberOfThisDisk;

                    if (record.NumberOfEntriesTotal > long.MaxValue)
                        throw new InvalidDataException("FieldTooBigNumEntries");

                    if (record.OffsetOfCentralDirectory > long.MaxValue)
                        throw new InvalidDataException("FieldTooBigOffsetToCD");

                    if (record.NumberOfEntriesTotal != record.NumberOfEntriesOnThisDisk)
                        throw new InvalidDataException("SplitSpanned");

                    _expectedNumberOfEntries = (long)record.NumberOfEntriesTotal;
                    _centralDirectoryStart = (long)record.OffsetOfCentralDirectory;
                }
            }
        }

        private void CloseStreams()
        {
            _archiveReader?.Dispose();
            _archiveStream?.Dispose();
        }

        /// <summary>
        /// Retrieves a specific entry from a ZIP archive.
        /// </summary>
        /// <param name="entryName">The name of the entry to retrieve.</param>
        public ZipEntry? RetrieveZipEntry(string entryname, StringComparison stringComparison = StringComparison.CurrentCulture)
        {
            try
            {
                // assume ReadEndOfCentralDirectory has been called and has populated _centralDirectoryStart

                _archiveStream.Seek(_centralDirectoryStart, SeekOrigin.Begin);

                long numberOfEntries = 0;

                Debug.Assert(_archiveReader != null);
                //read the central directory                
                while (ZipCentralDirectoryFileHeader.TryReadBlock(_archiveReader, out ZipCentralDirectoryFileHeader currentHeader))
                {
                    string fullname = Encoding.UTF8.GetString(currentHeader.Filename);
                    string name = ZipEntry.ParseFileName(fullname, (ZipVersionMadeByPlatform)currentHeader.VersionMadeByCompatibility);

                    if (string.Equals(name, entryname, stringComparison))
                    {
                        return new ZipEntry(this, currentHeader);
                    }
                    numberOfEntries++;
                }

                if (numberOfEntries != _expectedNumberOfEntries)
                    throw new InvalidDataException("NumEntriesWrong");

                return null;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException(ex.Message);
            }
        }

        /// <summary>
        /// Finishes writing the archive and releases all resources used by the ZipArchive object, unless the object was constructed with leaveOpen as true. Any streams from opened entries in the ZipArchive still open will throw exceptions on subsequent writes, as the underlying streams will have been closed.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by ZipArchive and optionally finishes writing the archive and releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to finish writing the archive and release unmanaged and managed resources, false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                CloseStreams();
                _isDisposed = true;
            }
        }
    }
}
