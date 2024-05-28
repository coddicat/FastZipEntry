// -----------------------------------------------------------------------
// <copyright file="ZipBlocks.cs">
// Source: https://github.com/dotnet/runtime/tree/9daa4b41eb9f157e79eaf05e2f7451c9c8f6dbdc/src/libraries/System.IO.Compression/src/System/IO/Compression/
// Original code from Microsoft, modified by Rodion Shlomo Solomonyk
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// -----------------------------------------------------------------------

namespace FastZipEntry
{
    internal struct ZipGenericExtraField
    {
        private ushort _tag;
        private ushort _size;
        private byte[] _data;

        public readonly ushort Tag => _tag;
        // returns size of data, not of the entire block
        public readonly ushort Size => _size;
        public readonly byte[] Data => _data;

        // shouldn't ever read the byte at position endExtraField
        // assumes we are positioned at the beginning of an extra field subfield
        public static bool TryReadBlock(BinaryReader reader, long endExtraField, out ZipGenericExtraField field)
        {
            field = default;

            // not enough bytes to read tag + size
            if (endExtraField - reader.BaseStream.Position < 4)
                return false;

            field._tag = reader.ReadUInt16();
            field._size = reader.ReadUInt16();

            // not enough bytes to read the data
            if (endExtraField - reader.BaseStream.Position < field._size)
                return false;

            field._data = reader.ReadBytes(field._size);
            return true;
        }

        // shouldn't ever read the byte at position endExtraField
        public static List<ZipGenericExtraField> ParseExtraField(Stream extraFieldData)
        {
            List<ZipGenericExtraField> extraFields = [];

            using (BinaryReader reader = new(extraFieldData))
            {
                while (TryReadBlock(reader, extraFieldData.Length, out ZipGenericExtraField field))
                {
                    extraFields.Add(field);
                }
            }

            return extraFields;
        }
    }

    internal struct Zip64ExtraField
    {
        // Size is size of the record not including the tag or size fields
        // If the extra field is going in the local header, it cannot include only
        // one of uncompressed/compressed size

        public const int OffsetToFirstField = 4;
        private const ushort TagConstant = 1;

        private ushort _size;
        private long? _uncompressedSize;
        private long? _compressedSize;
        private long? _localHeaderOffset;
        private uint? _startDiskNumber;

        public long? UncompressedSize
        {
            readonly get { return _uncompressedSize; }
            set { _uncompressedSize = value; UpdateSize(); }
        }
        public long? CompressedSize
        {
            readonly get { return _compressedSize; }
            set { _compressedSize = value; UpdateSize(); }
        }
        public long? LocalHeaderOffset
        {
            readonly get { return _localHeaderOffset; }
            set { _localHeaderOffset = value; UpdateSize(); }
        }
        public readonly uint? StartDiskNumber => _startDiskNumber;

        private void UpdateSize()
        {
            _size = 0;
            if (_uncompressedSize != null) _size += 8;
            if (_compressedSize != null) _size += 8;
            if (_localHeaderOffset != null) _size += 8;
            if (_startDiskNumber != null) _size += 4;
        }

        // There is a small chance that something very weird could happen here. The code calling into this function
        // will ask for a value from the extra field if the field was masked with FF's. It's theoretically possible
        // that a field was FF's legitimately, and the writer didn't decide to write the corresponding extra field.
        // Also, at the same time, other fields were masked with FF's to indicate looking in the zip64 record.
        // Then, the search for the zip64 record will fail because the expected size is wrong,
        // and a nulled out Zip64ExtraField will be returned. Thus, even though there was Zip64 data,
        // it will not be used. It is questionable whether this situation is possible to detect
        // unlike the other functions that have try-pattern semantics, these functions always return a
        // Zip64ExtraField. If a Zip64 extra field actually doesn't exist, all of the fields in the
        // returned struct will be null
        //
        // If there are more than one Zip64 extra fields, we take the first one that has the expected size
        //
        public static Zip64ExtraField GetJustZip64Block(Stream extraFieldStream,
            bool readUncompressedSize, bool readCompressedSize,
            bool readLocalHeaderOffset, bool readStartDiskNumber)
        {
            Zip64ExtraField zip64Field;
            using (BinaryReader reader = new(extraFieldStream))
            {
                while (ZipGenericExtraField.TryReadBlock(reader, extraFieldStream.Length, out ZipGenericExtraField currentExtraField))
                {
                    if (TryGetZip64BlockFromGenericExtraField(currentExtraField, readUncompressedSize,
                                readCompressedSize, readLocalHeaderOffset, readStartDiskNumber, out zip64Field))
                    {
                        return zip64Field;
                    }
                }
            }

            zip64Field = default;

            zip64Field._compressedSize = null;
            zip64Field._uncompressedSize = null;
            zip64Field._localHeaderOffset = null;
            zip64Field._startDiskNumber = null;

            return zip64Field;
        }

        private static bool TryGetZip64BlockFromGenericExtraField(ZipGenericExtraField extraField,
            bool readUncompressedSize, bool readCompressedSize,
            bool readLocalHeaderOffset, bool readStartDiskNumber,
            out Zip64ExtraField zip64Block)
        {
            zip64Block = default;

            zip64Block._compressedSize = null;
            zip64Block._uncompressedSize = null;
            zip64Block._localHeaderOffset = null;
            zip64Block._startDiskNumber = null;

            if (extraField.Tag != TagConstant)
                return false;

            zip64Block._size = extraField.Size;

            using MemoryStream ms = new(extraField.Data);
            using BinaryReader reader = new(ms);
            // The spec section 4.5.3:
            //      The order of the fields in the zip64 extended
            //      information record is fixed, but the fields MUST
            //      only appear if the corresponding Local or Central
            //      directory record field is set to 0xFFFF or 0xFFFFFFFF.
            // However tools commonly write the fields anyway; the prevailing convention
            // is to respect the size, but only actually use the values if their 32 bit
            // values were all 0xFF.

            if (extraField.Size < sizeof(long))
                return true;

            // Advancing the stream (by reading from it) is possible only when:
            // 1. There is an explicit ask to do that (valid files, corresponding boolean flag(s) set to true).
            // 2. When the size indicates that all the information is available ("slightly invalid files").
            bool readAllFields = extraField.Size >= sizeof(long) + sizeof(long) + sizeof(long) + sizeof(int);

            if (readUncompressedSize)
            {
                zip64Block._uncompressedSize = reader.ReadInt64();
            }
            else if (readAllFields)
            {
                _ = reader.ReadInt64();
            }

            if (ms.Position > extraField.Size - sizeof(long))
                return true;

            if (readCompressedSize)
            {
                zip64Block._compressedSize = reader.ReadInt64();
            }
            else if (readAllFields)
            {
                _ = reader.ReadInt64();
            }

            if (ms.Position > extraField.Size - sizeof(long))
                return true;

            if (readLocalHeaderOffset)
            {
                zip64Block._localHeaderOffset = reader.ReadInt64();
            }
            else if (readAllFields)
            {
                _ = reader.ReadInt64();
            }

            if (ms.Position > extraField.Size - sizeof(int))
                return true;

            if (readStartDiskNumber)
            {
                zip64Block._startDiskNumber = reader.ReadUInt32();
            }
            else if (readAllFields)
            {
                _ = reader.ReadInt32();
            }

            // original values are unsigned, so implies value is too big to fit in signed integer
            if (zip64Block._uncompressedSize < 0) throw new InvalidDataException("FieldTooBigUncompressedSize");
            if (zip64Block._compressedSize < 0) throw new InvalidDataException("FieldTooBigCompressedSize");
            if (zip64Block._localHeaderOffset < 0) throw new InvalidDataException("FieldTooBigLocalHeaderOffset");

            return true;
        }

        public static Zip64ExtraField GetAndRemoveZip64Block(List<ZipGenericExtraField> extraFields,
            bool readUncompressedSize, bool readCompressedSize,
            bool readLocalHeaderOffset, bool readStartDiskNumber)
        {
            Zip64ExtraField zip64Field = default;

            zip64Field._compressedSize = null;
            zip64Field._uncompressedSize = null;
            zip64Field._localHeaderOffset = null;
            zip64Field._startDiskNumber = null;

            List<ZipGenericExtraField> markedForDelete = [];
            bool zip64FieldFound = false;

            foreach (ZipGenericExtraField ef in extraFields)
            {
                if (ef.Tag == TagConstant)
                {
                    markedForDelete.Add(ef);
                    if (!zip64FieldFound)
                    {
                        if (TryGetZip64BlockFromGenericExtraField(ef, readUncompressedSize, readCompressedSize,
                                    readLocalHeaderOffset, readStartDiskNumber, out zip64Field))
                        {
                            zip64FieldFound = true;
                        }
                    }
                }
            }

            foreach (ZipGenericExtraField ef in markedForDelete)
                extraFields.Remove(ef);

            return zip64Field;
        }
    }

    internal struct Zip64EndOfCentralDirectoryLocator
    {
        public const uint SignatureConstant = 0x07064B50;
        public const int SignatureSize = sizeof(uint);

        public const int SizeOfBlockWithoutSignature = 16;

        public uint NumberOfDiskWithZip64EOCD;
        public ulong OffsetOfZip64EOCD;
        public uint TotalNumberOfDisks;

        public static bool TryReadBlock(BinaryReader reader, out Zip64EndOfCentralDirectoryLocator zip64EOCDLocator)
        {
            zip64EOCDLocator = default;

            if (reader.ReadUInt32() != SignatureConstant)
                return false;

            zip64EOCDLocator.NumberOfDiskWithZip64EOCD = reader.ReadUInt32();
            zip64EOCDLocator.OffsetOfZip64EOCD = reader.ReadUInt64();
            zip64EOCDLocator.TotalNumberOfDisks = reader.ReadUInt32();
            return true;
        }
    }

    internal struct Zip64EndOfCentralDirectoryRecord
    {
        private const uint SignatureConstant = 0x06064B50;

        public ulong SizeOfThisRecord;
        public ushort VersionMadeBy;
        public ushort VersionNeededToExtract;
        public uint NumberOfThisDisk;
        public uint NumberOfDiskWithStartOfCD;
        public ulong NumberOfEntriesOnThisDisk;
        public ulong NumberOfEntriesTotal;
        public ulong SizeOfCentralDirectory;
        public ulong OffsetOfCentralDirectory;

        public static bool TryReadBlock(BinaryReader reader, out Zip64EndOfCentralDirectoryRecord zip64EOCDRecord)
        {
            zip64EOCDRecord = default;

            if (reader.ReadUInt32() != SignatureConstant)
                return false;

            zip64EOCDRecord.SizeOfThisRecord = reader.ReadUInt64();
            zip64EOCDRecord.VersionMadeBy = reader.ReadUInt16();
            zip64EOCDRecord.VersionNeededToExtract = reader.ReadUInt16();
            zip64EOCDRecord.NumberOfThisDisk = reader.ReadUInt32();
            zip64EOCDRecord.NumberOfDiskWithStartOfCD = reader.ReadUInt32();
            zip64EOCDRecord.NumberOfEntriesOnThisDisk = reader.ReadUInt64();
            zip64EOCDRecord.NumberOfEntriesTotal = reader.ReadUInt64();
            zip64EOCDRecord.SizeOfCentralDirectory = reader.ReadUInt64();
            zip64EOCDRecord.OffsetOfCentralDirectory = reader.ReadUInt64();

            return true;
        }
    }

    internal readonly struct ZipLocalFileHeader
    {
        public const uint DataDescriptorSignature = 0x08074B50;
        public const uint SignatureConstant = 0x04034B50;
        public const int OffsetToCrcFromHeaderStart = 14;
        public const int OffsetToVersionFromHeaderStart = 4;
        public const int OffsetToBitFlagFromHeaderStart = 6;
        public const int SizeOfLocalHeader = 30;

        // will not throw end of stream exception
        public static bool TrySkipBlock(BinaryReader reader)
        {
            const int OffsetToFilenameLength = 22; // from the point after the signature

            if (reader.ReadUInt32() != SignatureConstant)
                return false;


            if (reader.BaseStream.Length < reader.BaseStream.Position + OffsetToFilenameLength)
                return false;

            reader.BaseStream.Seek(OffsetToFilenameLength, SeekOrigin.Current);

            ushort filenameLength = reader.ReadUInt16();
            ushort extraFieldLength = reader.ReadUInt16();

            if (reader.BaseStream.Length < reader.BaseStream.Position + filenameLength + extraFieldLength)
                return false;

            reader.BaseStream.Seek(filenameLength + extraFieldLength, SeekOrigin.Current);

            return true;
        }
    }

    internal struct ZipCentralDirectoryFileHeader
    {
        public const uint SignatureConstant = 0x02014B50;
        public byte VersionMadeByCompatibility;
        public byte VersionMadeBySpecification;
        public ushort VersionNeededToExtract;
        public ushort GeneralPurposeBitFlag;
        public ushort CompressionMethod;
        public uint LastModified; // convert this on the fly
        public uint Crc32;
        public long CompressedSize;
        public long UncompressedSize;
        public ushort FilenameLength;
        public ushort ExtraFieldLength;
        public ushort FileCommentLength;
        public uint DiskNumberStart;
        public ushort InternalFileAttributes;
        public uint ExternalFileAttributes;
        public long RelativeOffsetOfLocalHeader;

        public byte[] Filename;
        public byte[] FileComment;
        public List<ZipGenericExtraField>? ExtraFields;

        // if saveExtraFieldsAndComments is false, FileComment and ExtraFields will be null
        // in either case, the zip64 extra field info will be incorporated into other fields
        public static bool TryReadBlock(BinaryReader reader, out ZipCentralDirectoryFileHeader header)
        {
            header = default;

            if (reader.ReadUInt32() != SignatureConstant)
                return false;
            header.VersionMadeBySpecification = reader.ReadByte();
            header.VersionMadeByCompatibility = reader.ReadByte();
            header.VersionNeededToExtract = reader.ReadUInt16();
            header.GeneralPurposeBitFlag = reader.ReadUInt16();
            header.CompressionMethod = reader.ReadUInt16();
            header.LastModified = reader.ReadUInt32();
            header.Crc32 = reader.ReadUInt32();
            uint compressedSizeSmall = reader.ReadUInt32();
            uint uncompressedSizeSmall = reader.ReadUInt32();
            header.FilenameLength = reader.ReadUInt16();
            header.ExtraFieldLength = reader.ReadUInt16();
            header.FileCommentLength = reader.ReadUInt16();
            ushort diskNumberStartSmall = reader.ReadUInt16();
            header.InternalFileAttributes = reader.ReadUInt16();
            header.ExternalFileAttributes = reader.ReadUInt32();
            uint relativeOffsetOfLocalHeaderSmall = reader.ReadUInt32();

            header.Filename = reader.ReadBytes(header.FilenameLength);

            bool uncompressedSizeInZip64 = uncompressedSizeSmall == ZipHelper.Mask32Bit;
            bool compressedSizeInZip64 = compressedSizeSmall == ZipHelper.Mask32Bit;
            bool relativeOffsetInZip64 = relativeOffsetOfLocalHeaderSmall == ZipHelper.Mask32Bit;
            bool diskNumberStartInZip64 = diskNumberStartSmall == ZipHelper.Mask16Bit;

            Zip64ExtraField zip64;

            long endExtraFields = reader.BaseStream.Position + header.ExtraFieldLength;
            using (Stream str = new SubReadStream(reader.BaseStream, reader.BaseStream.Position, header.ExtraFieldLength))
            {
                header.ExtraFields = null;
                zip64 = Zip64ExtraField.GetJustZip64Block(str,
                        uncompressedSizeInZip64, compressedSizeInZip64,
                        relativeOffsetInZip64, diskNumberStartInZip64);
            }

            // There are zip files that have malformed ExtraField blocks in which GetJustZip64Block() silently bails out without reading all the way to the end
            // of the ExtraField block. Thus we must force the stream's position to the proper place.
            reader.BaseStream.AdvanceToPosition(endExtraFields);

            header.FileComment = reader.ReadBytes(header.FileCommentLength);

            header.UncompressedSize = zip64.UncompressedSize == null
                                                    ? uncompressedSizeSmall
                                                    : zip64.UncompressedSize.Value;
            header.CompressedSize = zip64.CompressedSize == null
                                                    ? compressedSizeSmall
                                                    : zip64.CompressedSize.Value;
            header.RelativeOffsetOfLocalHeader = zip64.LocalHeaderOffset == null
                                                    ? relativeOffsetOfLocalHeaderSmall
                                                    : zip64.LocalHeaderOffset.Value;
            header.DiskNumberStart = zip64.StartDiskNumber == null
                                                    ? diskNumberStartSmall
                                                    : zip64.StartDiskNumber.Value;

            return true;
        }
    }

    internal struct ZipEndOfCentralDirectoryBlock
    {
        public const uint SignatureConstant = 0x06054B50;
        public const int SignatureSize = sizeof(uint);

        // This is the minimum possible size, assuming the zip file comments variable section is empty
        public const int SizeOfBlockWithoutSignature = 18;

        // The end of central directory can have a variable size zip file comment at the end, but its max length can be 64K
        // The Zip File Format Specification does not explicitly mention a max size for this field, but we are assuming this
        // max size because that is the maximum value an ushort can hold.
        public const int ZipFileCommentMaxLength = ushort.MaxValue;

        public uint Signature;
        public ushort NumberOfThisDisk;
        public ushort NumberOfTheDiskWithTheStartOfTheCentralDirectory;
        public ushort NumberOfEntriesInTheCentralDirectoryOnThisDisk;
        public ushort NumberOfEntriesInTheCentralDirectory;
        public uint SizeOfCentralDirectory;
        public uint OffsetOfStartOfCentralDirectoryWithRespectToTheStartingDiskNumber;
        public byte[] ArchiveComment;

        public static bool TryReadBlock(BinaryReader reader, out ZipEndOfCentralDirectoryBlock eocdBlock)
        {
            eocdBlock = default;
            if (reader.ReadUInt32() != SignatureConstant)
                return false;

            eocdBlock.Signature = SignatureConstant;
            eocdBlock.NumberOfThisDisk = reader.ReadUInt16();
            eocdBlock.NumberOfTheDiskWithTheStartOfTheCentralDirectory = reader.ReadUInt16();
            eocdBlock.NumberOfEntriesInTheCentralDirectoryOnThisDisk = reader.ReadUInt16();
            eocdBlock.NumberOfEntriesInTheCentralDirectory = reader.ReadUInt16();
            eocdBlock.SizeOfCentralDirectory = reader.ReadUInt32();
            eocdBlock.OffsetOfStartOfCentralDirectoryWithRespectToTheStartingDiskNumber = reader.ReadUInt32();

            ushort commentLength = reader.ReadUInt16();
            eocdBlock.ArchiveComment = reader.ReadBytes(commentLength);

            return true;
        }
    }
}