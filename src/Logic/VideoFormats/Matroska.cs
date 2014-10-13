using Nikse.SubtitleEdit.Logic.VideoFormats.Ebml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nikse.SubtitleEdit.Logic.VideoFormats
{
    public class SubtitleSequence
    {
        public long StartMilliseconds { get; set; }
        public long EndMilliseconds { get; set; }
        public byte[] BinaryData { get; set; }

        public SubtitleSequence(byte[] data, long startMilliseconds, long endMilliseconds)
        {
            BinaryData = data;
            StartMilliseconds = startMilliseconds;
            EndMilliseconds = endMilliseconds;
        }

        public string Text
        {
            get
            {
                if (BinaryData != null)
                    return System.Text.Encoding.UTF8.GetString(BinaryData).Replace("\\N", Environment.NewLine);
                return string.Empty;
            }
        }
    }

    public class MatroskaSubtitleInfo
    {
        public long TrackNumber { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public string CodecId { get; set; }
        public string CodecPrivate { get; set; }
        public int ContentCompressionAlgorithm { get; set; }
        public int ContentEncodingType { get; set; }
    }

    public class MatroskaTrackInfo
    {
        public int TrackNumber { get; set; }
        public string Uid { get; set; }
        public bool IsVideo { get; set; }
        public bool IsAudio { get; set; }
        public bool IsSubtitle { get; set; }
        public string CodecId { get; set; }
        public string CodecPrivate { get; set; }
        public int DefaultDuration { get; set; }
        public string Language { get; set; }
    }

    public class Matroska : IDisposable
    {
        public delegate void LoadMatroskaCallback(long position, long total);

        private readonly string _fileName;
        private readonly FileStream _stream;
        private readonly long _streamLength;
        private readonly bool _valid;
        private int _pixelWidth, _pixelHeight;
        private double _frameRate;
        private string _videoCodecId;
        private double _durationInMilliseconds;

        private List<MatroskaSubtitleInfo> _subtitleList;
        private int _subtitleRipTrackNumber;
        private List<SubtitleSequence> _subtitleRip = new List<SubtitleSequence>();
        private long _timecodeScale = 1000000; // Timestamp scale in nanoseconds (1.000.000 means all timestamps in the segment are expressed in milliseconds).
        private List<MatroskaTrackInfo> _tracks;

        private readonly Element _headerElement;
        private readonly Element _segmentElement;

        public Matroska(string fileName)
        {
            _fileName = fileName;
            _stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _streamLength = _stream.Length;

            // read header
            _headerElement = ReadElement();
            if (_headerElement != null && _headerElement.Id == ElementId.Ebml)
            {
                // read segment
                _stream.Seek(_headerElement.DataSize, SeekOrigin.Current);
                _segmentElement = ReadElement();
                if (_segmentElement != null && _segmentElement.Id == ElementId.Segment)
                {
                    _valid = true; // matroska file must start with ebml header and segment
                }
            }
        }

        public bool IsValid
        {
            get
            {
                return _valid;
            }
        }

        public string FileName
        {
            get
            {
                return _fileName;
            }
        }

        public List<MatroskaTrackInfo> GetTrackInfo()
        {
            ReadFile(true, null);
            return _tracks;
        }

        /// <summary>
        /// Get first time of track
        /// </summary>
        /// <param name="trackNumber">Track number</param>
        /// <returns>Start time in milliseconds</returns>
        public long GetTrackStartTime(int trackNumber)
        {
            // go to segment
            _stream.Seek(_segmentElement.DataPosition, SeekOrigin.Begin);

            Element element;
            while (_stream.Position < _streamLength && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.Info:
                        AnalyzeMatroskaSegmentInformation(element);
                        break;
                    case ElementId.Tracks:
                        AnalyzeMatroskaTracks(element);
                        break;
                    case ElementId.Cluster:
                        return FindTrackStartInCluster(element, trackNumber);
                }
                _stream.Seek(element.EndPosition, SeekOrigin.Begin);
            }

            return 0;
        }

        private long FindTrackStartInCluster(Element cluster, int targetTrackNumber)
        {
            long clusterTimeCode = 0;
            int trackStartTime = -1;

            Element element;
            while (_stream.Position < cluster.EndPosition && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.Timecode:
                        // Absolute timestamp of the cluster (based on TimecodeScale)
                        clusterTimeCode = (long)ReadUInt((int)element.DataSize);
                        break;
                    case ElementId.BlockGroup:
                        AnalyzeMatroskaBlock(clusterTimeCode);
                        break;
                    case ElementId.SimpleBlock:
                        var trackNumber = (int)ReadVariableLengthUInt();
                        if (trackNumber == targetTrackNumber)
                        {
                            // Timecode (relative to Cluster timecode, signed int16)
                            trackStartTime = ReadInt16();
                        }
                        break;
                }
                _stream.Seek(element.EndPosition, SeekOrigin.Begin);
            }

            return (clusterTimeCode + trackStartTime) * _timecodeScale / 1000000;
        }

        private void AnalyzeMatroskaTrackVideo(Element videoElement)
        {
            Element element;
            while (_stream.Position < videoElement.EndPosition && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.PixelWidth:
                        _pixelWidth = (int)ReadUInt((int)element.DataSize);
                        break;
                    case ElementId.PixelHeight:
                        _pixelHeight = (int)ReadUInt((int)element.DataSize);
                        break;
                    default:
                        _stream.Seek(element.DataSize, SeekOrigin.Current);
                        break;
                }
            }
        }

        private void AnalyzeMatroskaTrackEntry(Element trackEntryElement)
        {
            long defaultDuration = 0;
            bool isVideo = false;
            bool isAudio = false;
            bool isSubtitle = false;
            long trackNumber = 0;
            string name = string.Empty;
            string language = string.Empty;
            string codecId = string.Empty;
            string codecPrivate = string.Empty;
            //var biCompression = string.Empty;
            int contentCompressionAlgorithm = -1;
            int contentEncodingType = -1;

            Element element;
            while (_stream.Position < trackEntryElement.EndPosition && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.DefaultDuration:
                        defaultDuration = (int)ReadUInt((int)element.DataSize);
                        break;
                    case ElementId.Video:
                        AnalyzeMatroskaTrackVideo(element);
                        isVideo = true;
                        break;
                    case ElementId.Audio:
                        isAudio = true;
                        break;
                    case ElementId.TrackNumber:
                        trackNumber = (long)ReadUInt((int)element.DataSize);
                        break;
                    case ElementId.Name:
                        name = ReadString((int)element.DataSize, Encoding.UTF8);
                        break;
                    case ElementId.Language:
                        language = ReadString((int)element.DataSize, Encoding.ASCII);
                        break;
                    case ElementId.CodecId:
                        codecId = ReadString((int)element.DataSize, Encoding.ASCII);
                        break;
                    case ElementId.TrackType:
                        switch (_stream.ReadByte())
                        {
                            case 1:
                                isVideo = true;
                                break;
                            case 2:
                                isAudio = true;
                                break;
                            case 17:
                                isSubtitle = true;
                                break;
                        }
                        break;
                    case ElementId.CodecPrivate:
                        codecPrivate = ReadString((int)element.DataSize, Encoding.UTF8);
                        //if (codecPrivate.Length > 20)
                        //    biCompression = codecPrivate.Substring(16, 4);
                        break;
                    case ElementId.ContentEncodings:
                        contentCompressionAlgorithm = 0; // default value
                        contentEncodingType = 0; // default value

                        var contentEncodingElement = ReadElement();
                        if (contentEncodingElement != null && contentEncodingElement.Id == ElementId.ContentEncoding)
                        {
                            AnalyzeMatroskaContentEncoding(element, ref contentCompressionAlgorithm, ref contentEncodingType);
                        }
                        break;
                }
                _stream.Seek(element.EndPosition, SeekOrigin.Begin);
            }
            
            _tracks.Add(new MatroskaTrackInfo
            {
                TrackNumber = (int)trackNumber,
                IsVideo = isVideo,
                IsAudio = isAudio,
                IsSubtitle = isSubtitle,
                Language = language,
                CodecId = codecId,
                CodecPrivate = codecPrivate,
            });
            if (isVideo)
            {
                if (defaultDuration > 0)
                    _frameRate = 1.0 / (defaultDuration / 1000000000.0);
                _videoCodecId = codecId;
            }
            else if (isSubtitle)
            {
                _subtitleList.Add(new MatroskaSubtitleInfo
                {
                    Name = name,
                    TrackNumber = trackNumber,
                    CodecId = codecId,
                    Language = language,
                    CodecPrivate = codecPrivate,
                    ContentEncodingType = contentEncodingType,
                    ContentCompressionAlgorithm = contentCompressionAlgorithm
                });
            }
        }

        private void AnalyzeMatroskaContentEncoding(Element contentEncodingElement, ref int contentCompressionAlgorithm, ref int contentEncodingType)
        {
            Element element;
            while (_stream.Position < contentEncodingElement.EndPosition && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.ContentEncodingOrder:
                        var contentEncodingOrder = ReadUInt((int)element.DataSize);
                        System.Diagnostics.Debug.WriteLine("ContentEncodingOrder: " + contentEncodingOrder);
                        break;
                    case ElementId.ContentEncodingScope:
                        var contentEncodingScope = ReadUInt((int)element.DataSize);
                        System.Diagnostics.Debug.WriteLine("ContentEncodingScope: " + contentEncodingScope);
                        break;
                    case ElementId.ContentEncodingType:
                        contentEncodingType = (int)ReadUInt((int)element.DataSize);
                        break;
                    case ElementId.ContentCompression:
                        Element compElement;
                        while (_stream.Position < element.EndPosition && (compElement = ReadElement()) != null)
                        {
                            switch (compElement.Id)
                            {
                                case ElementId.ContentCompAlgo:
                                    contentCompressionAlgorithm = (int)ReadUInt((int)compElement.DataSize);
                                    break;
                                case ElementId.ContentCompSettings:
                                    var contentCompSettings = ReadUInt((int)compElement.DataSize);
                                    System.Diagnostics.Debug.WriteLine("ContentCompSettings: " + contentCompSettings);
                                    break;
                            }
                        }
                        break;
                }
                _stream.Seek(element.EndPosition, SeekOrigin.Begin);
            }
        }

        private void AnalyzeMatroskaSegmentInformation(Element infoElement)
        {
            var duration = 0.0;

            Element element;
            while (_stream.Position < infoElement.EndPosition && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.TimecodeScale: // Timestamp scale in nanoseconds (1.000.000 means all timestamps in the segment are expressed in milliseconds)
                        _timecodeScale = (int)ReadUInt((int)element.DataSize);
                        break;
                    case ElementId.Duration: // Duration of the segment (based on TimecodeScale)
                        duration = element.DataSize == 4 ? ReadFloat32() : ReadFloat64();
                        break;
                    default:
                        _stream.Seek(element.DataSize, SeekOrigin.Current);
                        break;
                }
            }

            if (_timecodeScale > 0 && duration > 0)
                _durationInMilliseconds = duration / _timecodeScale * 1000000.0;
            else if (duration > 0)
                _durationInMilliseconds = duration;
        }

        private void AnalyzeMatroskaTracks(Element tracksElement)
        {
            _tracks = new List<MatroskaTrackInfo>();
            _subtitleList = new List<MatroskaSubtitleInfo>();

            Element element;
            while (_stream.Position < tracksElement.EndPosition && (element = ReadElement()) != null)
            {
                if (element.Id == ElementId.TrackEntry)
                {
                    AnalyzeMatroskaTrackEntry(element);
                }
                else
                {
                    _stream.Seek(element.DataSize, SeekOrigin.Current);
                }
            }
        }

        public void GetMatroskaInfo(out double frameRate, out int pixelWidth, out int pixelHeight, out double millisecondDuration, out string videoCodec)
        {
            ReadFile(true, null);

            pixelWidth = _pixelWidth;
            pixelHeight = _pixelHeight;
            frameRate = _frameRate;
            millisecondDuration = _durationInMilliseconds;
            videoCodec = _videoCodecId;
        }

        private void AnalyzeMatroskaCluster(Element clusterElement)
        {
            long clusterTimeCode = 0;
            const long duration = 0;

            Element element;
            while (_stream.Position < clusterElement.EndPosition && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.Timecode:
                        clusterTimeCode = (long)ReadUInt((int)element.DataSize);
                        break;
                    case ElementId.BlockGroup:
                        AnalyzeMatroskaBlock(clusterTimeCode);
                        break;
                    case ElementId.SimpleBlock:
                        long before = _stream.Position;
                        var trackNumber = (int)ReadVariableLengthUInt();
                        if (trackNumber == _subtitleRipTrackNumber)
                        {
                            int timeCode = ReadInt16();

                            // lacing
                            var flags = (byte)_stream.ReadByte();
                            byte numberOfFrames;
                            switch ((flags & 6)) // 6 = 00000110
                            {
                                case 0:
                                    System.Diagnostics.Debug.Print("No lacing"); // No lacing
                                    break;
                                case 2:
                                    System.Diagnostics.Debug.Print("Xiph lacing"); // 2 = 00000010 = Xiph lacing
                                    numberOfFrames = (byte)_stream.ReadByte();
                                    numberOfFrames++;
                                    break;
                                case 4:
                                    System.Diagnostics.Debug.Print("fixed-size"); // 4 = 00000100 = Fixed-size lacing
                                    numberOfFrames = (byte)_stream.ReadByte();
                                    numberOfFrames++;
                                    for (int i = 1; i <= numberOfFrames; i++)
                                        _stream.ReadByte(); // frames
                                    break;
                                case 6:
                                    System.Diagnostics.Debug.Print("EBML"); // 6 = 00000110 = EMBL
                                    numberOfFrames = (byte)_stream.ReadByte();
                                    numberOfFrames++;
                                    break;
                            }

                            var buffer = new byte[element.DataSize - (_stream.Position - before)];
                            _stream.Read(buffer, 0, buffer.Length);
                            _subtitleRip.Add(new SubtitleSequence(buffer, timeCode + clusterTimeCode, timeCode + clusterTimeCode + duration));
                        }
                        break;
                }
                _stream.Seek(element.EndPosition, SeekOrigin.Begin);
            }
        }

        private void AnalyzeMatroskaBlock(long clusterTimeCode)
        {
            var blockElement = ReadElement();
            if (blockElement == null || blockElement.Id != ElementId.Block)
            {
                return;
            }

            var trackNumber = (int)ReadVariableLengthUInt();
            var timeCode = ReadInt16();

            // lacing
            var flags = (byte)_stream.ReadByte();
            byte numberOfFrames;
            switch (flags & 6)
            {
                case 0: // 00000000 = No lacing
                    System.Diagnostics.Debug.Print("No lacing");
                    break;
                case 2: // 00000010 = Xiph lacing
                    System.Diagnostics.Debug.Print("Xiph lacing");
                    numberOfFrames = (byte)_stream.ReadByte();
                    numberOfFrames++;
                    break;
                case 4: // 00000100 = Fixed-size lacing
                    System.Diagnostics.Debug.Print("Fixed-size lacing");
                    numberOfFrames = (byte)_stream.ReadByte();
                    numberOfFrames++;
                    for (int i = 1; i <= numberOfFrames; i++)
                        _stream.ReadByte(); // frames
                    break;
                case 6: // 00000110 = EMBL lacing
                    System.Diagnostics.Debug.Print("EBML lacing");
                    numberOfFrames = (byte)_stream.ReadByte();
                    numberOfFrames++;
                    break;
            }

            // save subtitle data
            if (trackNumber == _subtitleRipTrackNumber)
            {
                long sublength = blockElement.EndPosition - _stream.Position;
                if (sublength > 0)
                {
                    var buffer = new byte[sublength];
                    _stream.Read(buffer, 0, (int)sublength);

                    //string s = GetMatroskaString(sublength);
                    //s = s.Replace("\\N", Environment.NewLine);

                    _stream.Seek(blockElement.EndPosition, SeekOrigin.Begin);
                    var durationElement = ReadElement();
                    var duration = durationElement != null && durationElement.Id == ElementId.BlockDuration
                        ? (long)ReadUInt((int)durationElement.DataSize)
                        : 0L;

                    _subtitleRip.Add(new SubtitleSequence(buffer, timeCode + clusterTimeCode, timeCode + clusterTimeCode + duration));
                }
            }
        }

        public List<MatroskaSubtitleInfo> GetMatroskaSubtitleTracks()
        {
            ReadFile(true, null);
            return _subtitleList;
        }

        public List<SubtitleSequence> GetMatroskaSubtitle(int trackNumber, LoadMatroskaCallback callback)
        {
            _subtitleRipTrackNumber = trackNumber;
            ReadFile(false, callback);
            return _subtitleRip;
        }

        public void Dispose()
        {
            if (_stream != null)
            {
                _stream.Dispose();
            }
        }

        private void ReadFile(bool tracksOnly, LoadMatroskaCallback callback)
        {
            // go to segment
            _stream.Seek(_segmentElement.DataPosition, SeekOrigin.Begin);

            Element element;
            while (_stream.Position < _segmentElement.EndPosition && (element = ReadElement()) != null)
            {
                switch (element.Id)
                {
                    case ElementId.Info:
                        AnalyzeMatroskaSegmentInformation(element);
                        break;
                    case ElementId.Tracks:
                        AnalyzeMatroskaTracks(element);
                        if (tracksOnly)
                        {
                            return;
                        }
                        break;
                    case ElementId.Cluster:
                        AnalyzeMatroskaCluster(element);
                        break;
                    default:
                        _stream.Seek(element.DataSize, SeekOrigin.Current);
                        break;
                }
                if (callback != null)
                {
                    callback.Invoke(element.EndPosition, _streamLength);
                }
            }
        }

        private Element ReadElement()
        {
            var id = ReadEbmlId();
            if (id == ElementId.None)
            {
                return null;
            }

            var size = (long)ReadVariableLengthUInt();
            return new Element(id, _stream.Position, size);
        }

        private ElementId ReadEbmlId()
        {
            // Begin loop with byte set to newly read byte
            var first = (byte)_stream.ReadByte();
            var length = 0;

            // Begin by counting the bits unset before the highest set bit
            uint mask = 0x80;
            for (var i = 0; i < 8; i++)
            {
                // Start at left, shift to right
                if ((first & mask) == mask)
                {
                    length = i + 1;
                    break;
                }
                mask >>= 1;
            }
            if (length == 0)
            {
                // Invalid identifier
                return 0;
            }

            // Setup space to store the integer
            var data = new byte[length];
            data[0] = first;
            if (length > 1)
            {
                // Read the rest of the integer
                _stream.Read(data, 1, length - 1);
            }

            return (ElementId)BigEndianToUInt64(data);
        }

        private ulong ReadVariableLengthUInt()
        {
            // Begin loop with byte set to newly read byte
            var first = (byte)_stream.ReadByte();
            var length = 0;

            // Begin by counting the bits unset before the highest set bit
            uint mask = 0x80;
            for (var i = 0; i < 8; i++)
            {
                // Start at left, shift to right
                if ((first & mask) == mask)
                {
                    length = i + 1;
                    break;
                }
                mask >>= 1;
            }
            if (length == 0)
            {
                return 0;
            }

            // Setup space to store the integer
            var data = new byte[length];
            data[0] = (byte)(first & (0xFF >> length));
            if (length > 1)
            {
                // Read the rest of the integer
                _stream.Read(data, 1, length - 1);
            }

            return BigEndianToUInt64(data);
        }

        /// <summary>
        /// Reads a fixed length unsigned integer from the current stream and advances the current
        /// position of the stream by the integer length in bytes.
        /// </summary>
        /// <param name="length">The length in bytes of the integer.</param>
        /// <returns>A 64-bit unsigned integer.</returns>
        private ulong ReadUInt(int length)
        {
            var data = new byte[length];
            _stream.Read(data, 0, length);
            return BigEndianToUInt64(data);
        }

        /// <summary>
        /// Reads a 2-byte signed integer from the current stream and advances the current position
        /// of the stream by two bytes.
        /// </summary>
        /// <returns>A 2-byte signed integer read from the current stream.</returns>
        private short ReadInt16()
        {
            var data = new byte[2];
            _stream.Read(data, 0, 2);
            return (short)(data[0] << 8 | data[1]);
        }

        /// <summary>
        /// Reads a 4-byte floating point value from the current stream and advances the current
        /// position of the stream by four bytes.
        /// </summary>
        /// <returns>A 4-byte floating point value read from the current stream.</returns>
        private unsafe float ReadFloat32()
        {
            var data = new byte[4];
            _stream.Read(data, 0, 4);
            var result = data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3];
            return *(float*)&result;
        }

        /// <summary>
        /// Reads a 8-byte floating point value from the current stream and advances the current
        /// position of the stream by eight bytes.
        /// </summary>
        /// <returns>A 8-byte floating point value read from the current stream.</returns>
        private unsafe double ReadFloat64()
        {
            var data = new byte[8];
            _stream.Read(data, 0, 8);
            var result = (long)(data[0] << 56 | data[1] << 48 | data[2] << 40 | data[3] << 32 | data[4] << 24 | data[5] << 16 | data[6] << 8 | data[7]);
            return *(double*)&result;
        }

        /// <summary>
        /// Reads a fixed length string from the current stream using the specified encoding.
        /// </summary>
        /// <param name="length">The length in bytes of the string.</param>
        /// <param name="encoding">The encoding of the string.</param>
        /// <returns>The string being read.</returns>
        private string ReadString(int length, Encoding encoding)
        {
            var buffer = new byte[length];
            _stream.Read(buffer, 0, length);
            return encoding.GetString(buffer);
        }

        /// <summary>
        /// Returns a 64-bit unsigned integer converted from a big endian byte array.
        /// </summary>
        /// <param name="value">An array of bytes.</param>
        /// <returns>A 64-bit unsigned integer.</returns>
        private static ulong BigEndianToUInt64(byte[] value)
        {
            var result = 0UL;
            var shift = 0;
            for (var i = value.Length - 1; i >= 0; i--)
            {
                result |= (ulong)value[i] << shift;
                shift += 8;
            }
            return result;
        }
    }
}
