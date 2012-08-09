﻿/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis
{
    class OggContainerReader : IDisposable
    {
        class OggPacket : DataPacket
        {
            Stream _stream;

            List<long> _offsets;
            List<int> _lengths;
            int _curIdx;
            int _curOfs;

            internal OggPacket(Stream stream, long startPos, int length)
                : base(length)
            {
                _stream = stream;

                _offsets = new List<long>();
                _lengths = new List<int>();
                _curIdx = 0;
                _curOfs = 0;

                _offsets.Add(startPos);
                _lengths.Add(length);
            }

            protected override void DoMergeWith(NVorbis.DataPacket continuation)
            {
                var op = continuation as OggPacket;

                if (op == null) throw new ArgumentException("Incorrect packet type!");

                _offsets.AddRange(op._offsets);
                _lengths.AddRange(op._lengths);

                Length += continuation.Length;
            }

            protected override bool CanReset
            {
                get { return true; }
            }

            protected override void DoReset()
            {
                _curIdx = 0;
                _curOfs = 0;
                _stream.Position = _offsets[0];
            }

            protected override int ReadNextByte()
            {
                if (_curIdx == _offsets.Count) return -1;

                var pos = _offsets[_curIdx] + _curOfs;
                if (_stream.Position != pos) _stream.Seek(pos, SeekOrigin.Begin);
                var b = _stream.ReadByte();
                ++_curOfs;
                if (_curOfs >= _lengths[_curIdx])
                {
                    ++_curIdx;
                    _curOfs = 0;
                }
                return b;
            }
        }

        const uint CRC32_POLY = 0x04c11db7;
        static uint[] crcTable = new uint[256];

        static OggContainerReader()
        {
            for (uint i = 0; i < 256; i++)
            {
                uint s = i << 24;
                for (int j = 0; j < 8; ++j)
                {
                    s = (s << 1) ^ (s >= (1U << 31) ? CRC32_POLY : 0);
                }
                crcTable[i] = s;
            }
        }

        Stream _stream;
        Dictionary<int, OggPacketReader> _packetReaders;
        Dictionary<int, bool> _eosFlags;
        List<int> _streamSerials;
        long _nextPageOffset;
        int _pageCount;

        internal long _containerBits;

        internal Stream BaseStream
        {
            get { return _stream; }
        }

        internal int[] StreamSerials
        {
            get { return _streamSerials.ToArray(); }
        }

        internal OggContainerReader(string fileName)
            : this(File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {

        }

        internal OggContainerReader(Stream stream)
        {
            if (!stream.CanSeek) throw new ArgumentException("stream must be seekable!");

            _stream = stream;

            _packetReaders = new Dictionary<int, OggPacketReader>();
            _eosFlags = new Dictionary<int, bool>();
            _streamSerials = new List<int>();

            InitContainer();
        }

        public void Dispose()
        {
            _packetReaders.Clear();
            _nextPageOffset = 0L;
            _containerBits = 0L;

            _stream.Dispose();
        }

        void InitContainer()
        {
            int streamSerial, seqNo;
            long granulePosition, dataOffset;
            PageFlags pageFlags;
            int[] packetSizes;
            bool lastPacketContinues;

            if (!ReadPageHeader(out streamSerial, out pageFlags, out granulePosition, out seqNo, out dataOffset, out packetSizes, out lastPacketContinues)) throw new InvalidDataException("Not an OGG container!");

            // go ahead and process this first page
            _packetReaders.Add(streamSerial, new OggPacketReader(this, streamSerial));
            _eosFlags.Add(streamSerial, false);
            _streamSerials.Add(streamSerial);

            for (int i = 0; i < packetSizes.Length - 1; i++)
            {
                _packetReaders[streamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[i]) { PageGranulePosition = granulePosition, IsContinued = false, IsContinuation = false, IsResync = false, PageSequenceNumber = seqNo });
                dataOffset += packetSizes[i];
            }
            _packetReaders[streamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[packetSizes.Length - 1]) { PageGranulePosition = granulePosition, IsContinued = lastPacketContinues, IsContinuation = false, IsResync = false, PageSequenceNumber = seqNo });
        }

        bool ReadPageHeader(out int streamSerial, out PageFlags flags, out long granulePosition, out int seqNo, out long dataOffset, out int[] packetSizes, out bool lastPacketContinues)
        {
            streamSerial = -1;
            flags = PageFlags.None;
            granulePosition = -1L;
            seqNo = -1;
            dataOffset = -1L;
            packetSizes = null;
            lastPacketContinues = false;

            // header
            var hdrBuf = new byte[27];
            if (_stream.Read(hdrBuf, 0, hdrBuf.Length) != hdrBuf.Length) return false;

            // capture signature
            if (hdrBuf[0] != 0x4f || hdrBuf[1] != 0x67 || hdrBuf[2] != 0x67 || hdrBuf[3] != 0x53) return false;

            // check the stream version
            if (hdrBuf[4] != 0) return false;

            // bit flags
            flags = (PageFlags)hdrBuf[5];

            // granulePosition
            granulePosition = BitConverter.ToInt64(hdrBuf, 6);

            // stream serial
            streamSerial = BitConverter.ToInt32(hdrBuf, 14);

            // sequence number
            seqNo = BitConverter.ToInt32(hdrBuf, 18);

            // save off the CRC
            var crc = BitConverter.ToUInt32(hdrBuf, 22);

            // start calculating the CRC value for this page
            var testCRC = 0U;
            for (int i = 0; i < 22; i++)
            {
                UpdateCRC(hdrBuf[i], ref testCRC);
            }
            UpdateCRC(0, ref testCRC);
            UpdateCRC(0, ref testCRC);
            UpdateCRC(0, ref testCRC);
            UpdateCRC(0, ref testCRC);
            UpdateCRC(hdrBuf[26], ref testCRC);

            // figure out the length of the page
            var segCnt = (int)hdrBuf[26];
            packetSizes = new int[segCnt];
            int size = 0, idx = 0;
            for (int i = 0; i < segCnt; i++)
            {
                var temp = _stream.ReadByte();
                UpdateCRC(temp, ref testCRC);

                packetSizes[idx] += temp;
                if (temp < 255)
                {
                    ++idx;
                    lastPacketContinues = false;
                }
                else
                {
                    lastPacketContinues = true;
                }

                size += temp;
            }
            if (lastPacketContinues) ++idx;
            if (idx < packetSizes.Length)
            {
                var temp = new int[idx];
                for (int i = 0; i < idx; i++)
                {
                    temp[i] = packetSizes[i];
                }
                packetSizes = temp;
            }

            dataOffset = _stream.Position;

            // now we have to go through every byte in the page 
            while (--size >= 0)
            {
                UpdateCRC(_stream.ReadByte(), ref testCRC);
            }

            _nextPageOffset = _stream.Position;
            
            _containerBits += 8 * (27 + segCnt);
            if (testCRC == crc)
            {
                ++_pageCount;
                return true;
            }
            _containerBits -= 8 * (27 + segCnt);    // we're going to look for the bits separately...
            return false;
        }

        void UpdateCRC(int nextVal, ref uint crc)
        {
            crc = (crc << 8) ^ crcTable[nextVal ^ (crc >> 24)];
        }

        /// <summary>
        /// Gathers pages until finding a page for the stream indicated
        /// </summary>
        internal void GatherNextPage(int streamSerial)
        {
            int pageStreamSerial, seqNo;
            long granulePosition, dataOffset;
            PageFlags pageFlags;
            int[] packetSizes;
            bool lastPacketContinues;

            do
            {
                if (_eosFlags[streamSerial]) throw new EndOfStreamException();

                _stream.Position = _nextPageOffset;
                var startPos = _nextPageOffset;

                var isResync = false;
                while (!ReadPageHeader(out pageStreamSerial, out pageFlags, out granulePosition, out seqNo, out dataOffset, out packetSizes, out lastPacketContinues))
                {
                    isResync = true;

                    // gotta find the next sync header...
                    // start on the next byte...
                    _containerBits += 8;
                    _stream.Position++;

                    var cnt = 0;
                    while (++cnt < 65536)
                    {
                        if (_stream.ReadByte() == 0x4f)
                        {
                            var checkPos = _stream.Position;
                            if (_stream.ReadByte() == 'g' && _stream.ReadByte() == 'g' && _stream.ReadByte() == 'S')
                            {
                                // found it!
                                _stream.Position -= 4;
                                startPos = _stream.Position;
                            }
                            else
                            {
                                _stream.Position = checkPos;
                                _containerBits += 8;
                            }
                        }
                    }
                    if (cnt == 65536) throw new InvalidDataException("Sync lost and could not find next page.");
                }

                // we now have a parsed header...  generate packets...
                if (!_packetReaders.ContainsKey(pageStreamSerial))
                {
                    _packetReaders.Add(pageStreamSerial, new OggPacketReader(this, pageStreamSerial));
                    _eosFlags.Add(pageStreamSerial, false);
                    _streamSerials.Add(pageStreamSerial);
                }

                _packetReaders[pageStreamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[0]) { PageGranulePosition = granulePosition, IsContinued = false, IsContinuation = (pageFlags & PageFlags.ContinuesPacket) == PageFlags.ContinuesPacket, IsResync = isResync, PageSequenceNumber = seqNo });
                dataOffset += packetSizes[0];
                for (int i = 1; i < packetSizes.Length - 1; i++)
                {
                    _packetReaders[pageStreamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[i]) { PageGranulePosition = granulePosition, IsContinued = false, IsContinuation = false, IsResync = false, PageSequenceNumber = seqNo });
                    dataOffset += packetSizes[i];
                }
                if (packetSizes.Length > 1)
                {
                    _packetReaders[pageStreamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[packetSizes.Length - 1]) { PageGranulePosition = granulePosition, IsContinued = lastPacketContinues, IsContinuation = false, IsResync = false, IsEndOfStream = (pageFlags & PageFlags.EndOfStream) == PageFlags.EndOfStream, PageSequenceNumber = seqNo });
                }

                if ((pageFlags & PageFlags.EndOfStream) == PageFlags.EndOfStream)
                {
                    _eosFlags[pageStreamSerial] = true;
                }
            } while (pageStreamSerial != streamSerial);
        }

        internal DataPacket GetNextPacket(int streamSerial)
        {
            return _packetReaders[streamSerial].GetNextPacket();
        }

        internal void SetDataStart(int streamSerial)
        {
            _packetReaders[streamSerial].SetDataStart();
        }

        internal long GetLastGranulePos(int streamSerial)
        {
            return _packetReaders[streamSerial].GetLastPacket().PageGranulePosition;
        }

        internal void SeekToSample(int streamSerial, long sampleNum)
        {
            _packetReaders[streamSerial].SeekToGranule(sampleNum);
        }

        internal bool FindNextStream(int currentStreamSerial)
        {
            // goes through all the pages until the serial count increases
            
            // if the index is less than the highest, go ahead and return true
            var idx = Array.IndexOf(StreamSerials, currentStreamSerial);
            var cnt = this._packetReaders.Count;
            if (idx < cnt - 1) return true;

            // read pages until we're done...
            while (cnt == this._packetReaders.Count)
            {
                GatherNextPage(currentStreamSerial);
            }

            return cnt > this._packetReaders.Count;
        }

        internal int GetReadPageCount()
        {
            return _pageCount;
        }

        internal int GetTotalPageCount()
        {
            _eosFlags.Add(-1, false);

            // there cannot possibly be another page less than 28 bytes from the end of the file
            while (_stream.Position < _stream.Length - 28)
            {
                GatherNextPage(-1);
            }

            _eosFlags.Remove(-1);

            return _pageCount;
        }

        internal bool CanSeek
        {
            get { return true; }
        }
    }
}
