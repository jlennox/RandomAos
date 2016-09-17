using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace RandomCastle
{
    class Program
    {
        static void Main(string[] args)
        {
            const string input = @"D:\Files\emu\0999 - Castlevania - Aria of Sorrow (U)(GBATemp).gba";
            const string output = @"D:\Files\emu\0999 - Castlevania - Aria of Sorrow (U)(GBATemp)-foo.gba";
            const int firstRoom = 0x50EF9C;

            File.Copy(input, output, true);

            var rng = new Random(0x133);

            var fs = new MemoryStream(File.ReadAllBytes(input));
            fs.Seek(firstRoom, SeekOrigin.Begin);

            var br = new BinaryStreamReader(fs);
            var rooms = new List<GameStruct<Room>>();

            for (var i = 0; i < fs.Length; i += 4)
            {
                fs.Seek(i, SeekOrigin.Begin);

                Room room;

                try
                {
                    room = new Room(br);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                if (!room.IsValid())
                {
                    continue;
                }

                rooms.Add(new GameStruct<Room>
                {
                    Offset = (uint)i,
                    Value = room
                });
            }

            var swapCount = 0;
            var roomCount = rooms.Count;

            while (rooms.Count > 1)
            {
                var roomIndex = rng.Next(0, rooms.Count - 1);
                var room = rooms[roomIndex];
                rooms.RemoveAt(roomIndex);

                var searchStart = rng.Next(0, rooms.Count - 1);

                GameStruct<Room> newRoom = null;
                for (var i = searchStart; i < rooms.Count; ++i)
                {
                    if (rooms[i].Value.CanSwitch(room.Value))
                    {
                        newRoom = rooms[i];
                        break;
                    }
                }

                if (newRoom == null)
                {
                    for (var i = 0; i < searchStart; ++i)
                    {
                        if (rooms[i].Value.CanSwitch(room.Value))
                        {
                            newRoom = rooms[i];
                            break;
                        }
                    }
                }

                if (newRoom == null)
                {
                    continue;
                }

                swapCount++;

                fs.Seek(newRoom.Offset, SeekOrigin.Begin);
                room.Value.Write(fs);

                fs.Seek(room.Offset, SeekOrigin.Begin);
                newRoom.Value.Write(fs);
            }

            File.WriteAllBytes(output, fs.ToArray());

            Console.WriteLine($"Swapped {swapCount} of {roomCount} rooms.");

            Console.ReadLine();
        }
    }

    class GameStruct<T>
    {
        public uint Offset { get; set; }
        public T Value { get; set; }
    }

    static class BinaryStreamReaderEx
    {
        public static IDisposable SeekGba(
            this BinaryStreamReader reader, uint ptr)
        {
            var curPtr = (int)reader.Stream.Position;

            reader.Stream.Seek(ptr & 0x00FFFFFF, SeekOrigin.Begin);

            return new Unseek(reader.Stream, curPtr);
        }

        private struct Unseek : IDisposable
        {
            private readonly Stream _stream;
            private readonly int _ptr;

            public Unseek(Stream stream, int ptr)
            {
                _stream = stream;
                _ptr = ptr;
            }

            public void Dispose()
            {
                _stream.Seek(_ptr, SeekOrigin.Begin);
            }
        }
    }

    class Room
    {
        public const int Size = 4 * 9;

        public uint Magic { get; set; }
        public uint Magic00 { get; set; }
        public uint BackgroundDataPtr { get; set; }
        public BackgroundData BackgroundData { get; set; }
        public uint TileSets { get; set; }
        public uint PaletteSets { get; set; }
        public uint Objects { get; set; }
        public uint Exits { get; set; }
        public uint ExitCount { get; set; }
        public uint MapLocation { get; set; }

        public Room(BinaryStreamReader reader)
        {
            Magic = reader.ReadUInt32();
            Magic00 = reader.ReadUInt32();

            if (Magic != 0xFFFF1F00 || Magic00 != 0)
            {
                return;
            }

            BackgroundDataPtr = reader.ReadUInt32();
            TileSets = reader.ReadUInt32();
            PaletteSets = reader.ReadUInt32();
            Objects = reader.ReadUInt32();
            Exits = reader.ReadUInt32();
            ExitCount = reader.ReadUInt32();
            MapLocation = reader.ReadUInt32();

            using (reader.SeekGba(BackgroundDataPtr))
            {
                BackgroundData = new BackgroundData(reader);
            }
        }

        public void Write(Stream stream)
        {
            var buf = new byte[Size];
            BinaryBufferWriter.Write(Magic, buf, 4 * 0);
            BinaryBufferWriter.Write(Magic00, buf, 4 * 1);
            BinaryBufferWriter.Write(BackgroundDataPtr, buf, 4 * 2);
            BinaryBufferWriter.Write(TileSets, buf, 4 * 3);
            BinaryBufferWriter.Write(PaletteSets, buf, 4 * 4);
            BinaryBufferWriter.Write(Objects, buf, 4 * 5);
            BinaryBufferWriter.Write(Exits, buf, 4 * 6);
            BinaryBufferWriter.Write(ExitCount, buf, 4 * 7);
            BinaryBufferWriter.Write(MapLocation, buf, 4 * 8);

            stream.Write(buf, 0, buf.Length);
        }

        public bool CanSwitch(Room otherRoom)
        {
            return ExitCount == otherRoom.ExitCount &&
                BackgroundData.Height == otherRoom.BackgroundData.Height &&
                BackgroundData.Width == otherRoom.BackgroundData.Width;
        }

        public bool IsValid()
        {
            return Magic == 0xFFFF1F00 && Magic00 == 0;
        }
    }

    class BackgroundData
    {
        public ushort Height { get; set; }
        public ushort Width { get; set; }

        public BackgroundData(BinaryStreamReader reader)
        {
            Height = reader.ReadUInt16();
            Width = reader.ReadUInt16();
        }
    }

    class BinaryStructReader
    {
        private static readonly Dictionary<Type, int> _typeSizes =
            new Dictionary<Type, int>
            {
                { typeof(byte), 1 },
                { typeof(short), 2 }, { typeof(ushort), 2 },
                { typeof(int), 4 }, { typeof(uint), 4 },
                { typeof(long), 8 }, { typeof(ulong), 8 }
            };

        public int Size { get; set; }

        public static T Read<T>(Stream stream, byte[] buffer)
            where T : new()
        {
            var type = typeof(T);

            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToArray();

            var size = properties.Select(t =>
            {
                int propSize;
                return _typeSizes.TryGetValue(t.PropertyType, out propSize)
                     ? propSize : 1;
            }).Sum();

            /*var method = new DynamicMethod(
                "Setter", typeof(void),
                new[] { typeof(Stream), typeof(byte[]) },
                true);
            
            var ilgen = method.GetILGenerator();*/

            if (buffer.Length < size)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(buffer), buffer.Length,
                    $"Buffer length of {size} required.");
            }

            var readLeft = size;
            var readOffset = 0;

            while (readLeft > 0)
            {
                var nread = stream.Read(buffer, readOffset, readLeft);

                if (nread == 0)
                {
                    throw new EndOfStreamException();
                }

                readLeft -= nread;
                readOffset += nread;
            }

            var dest = new T();
            var offset = 0;

            foreach (var property in properties)
            {
                if (property.PropertyType == typeof(byte))
                {
                    property.SetValue(dest,
                        BinaryBufferReader.ReadByte(buffer, offset));
                    offset++;
                }
                else if (property.PropertyType == typeof(short))
                {
                    property.SetValue(dest,
                        BinaryBufferReader.ReadInt16(buffer, offset));
                    offset += 2;
                }
                else if (property.PropertyType == typeof(ushort))
                {
                    property.SetValue(dest,
                        BinaryBufferReader.ReadUInt16(buffer, offset));
                    offset += 2;
                }
                else if (property.PropertyType == typeof(int))
                {
                    property.SetValue(dest,
                        BinaryBufferReader.ReadInt32(buffer, offset));
                    offset += 4;
                }
                else if (property.PropertyType == typeof(uint))
                {
                    property.SetValue(dest,
                        BinaryBufferReader.ReadUInt32(buffer, offset));
                    offset += 4;
                }
                else if (property.PropertyType == typeof(long))
                {
                    property.SetValue(dest,
                        BinaryBufferReader.ReadInt64(buffer, offset));
                    offset += 8;
                }
                else if (property.PropertyType == typeof(ulong))
                {
                    property.SetValue(dest,
                        BinaryBufferReader.ReadUInt64(buffer, offset));
                    offset += 8;
                }
                else
                {
                    throw new NotSupportedException();
                }
                /*ilgen.Emit(OpCodes.Ldarg_0);
                ilgen.Emit(OpCodes.Ldc_I4_0);
                ilgen.Emit(OpCodes.Ldc_I4, size);

                //public abstract int Read(byte[] buffer, int offset, int count);
                ilgen.Emit(OpCodes.Call, typeof(Stream)
                    .GetMethod("Read",
                    new [] { typeof(byte[]), typeof(int), typeof(int) }));

                ilgen.Emit(OpCodes.Ldarg_0);
                ilgen.Emit(OpCodes.Ldarg_1);
                ilgen.Emit(OpCodes.Callvirt, property.GetSetMethod());
                ilgen.Emit(OpCodes.Ret);*/
            }

            return dest;
        }
    }

    struct BinaryStreamReader
    {
        private const bool _littleEndianDefault = true;
        private const int _minBufferSize = 8;

        public Stream Stream { get; }

        private readonly byte[] _buffer;
        private readonly bool _littleEndian;
        private readonly int _offset;

        public BinaryStreamReader(
            Stream stream,
            bool littleEndian = _littleEndianDefault)
            : this()
        {
            Stream = stream;
            _buffer = new byte[_minBufferSize];
            _offset = 0;
            _littleEndian = littleEndian;
        }

        public BinaryStreamReader(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
            : this()
        {
            Stream = stream;
            _buffer = buffer;
            _offset = offset;
            _littleEndian = littleEndian;
        }

        private static void Read(
            Stream stream, byte[] buffer,
            int offset, int amount)
        {
            var amountLeft = amount;
            var readOffset = offset;

            while (amountLeft > 0)
            {
                var nread = stream.Read(buffer, readOffset, amountLeft);

                if (nread == 0)
                {
                    break;
                }

                amountLeft -= nread;
                readOffset += nread;
            }

            if (amountLeft > 0)
            {
                throw new EndOfStreamException();
            }
        }

        private static async Task ReadAsync(
            Stream stream, byte[] buffer,
            int offset, int amount)
        {
            var amountLeft = amount;
            var readOffset = offset;

            while (amountLeft > 0)
            {
                var nread = await stream
                    .ReadAsync(buffer, readOffset, amountLeft);

                if (nread == amount)
                {
                    break;
                }

                amountLeft -= nread;
                readOffset += nread;
            }
        }

        public long ReadInt64()
        {
            Read(Stream, _buffer, _offset, 8);
            return BinaryBufferReader.ReadInt64(_buffer, _offset, _littleEndian);
        }

        public ulong ReadUInt64()
        {
            Read(Stream, _buffer, _offset, 8);
            return BinaryBufferReader.ReadUInt64(_buffer, _offset, _littleEndian);
        }

        public int ReadInt32()
        {
            Read(Stream, _buffer, _offset, 4);
            return BinaryBufferReader.ReadInt32(_buffer, _offset, _littleEndian);
        }

        public uint ReadUInt32()
        {
            Read(Stream, _buffer, _offset, 4);
            return BinaryBufferReader.ReadUInt32(_buffer, _offset, _littleEndian);
        }

        public short ReadInt16()
        {
            Read(Stream, _buffer, _offset, 2);
            return BinaryBufferReader.ReadInt16(_buffer, _offset, _littleEndian);
        }

        public ushort ReadUInt16()
        {
            Read(Stream, _buffer, _offset, 2);
            return BinaryBufferReader.ReadUInt16(_buffer, _offset, _littleEndian);
        }

        public byte ReadByte()
        {
            Read(Stream, _buffer, _offset, 1);
            return BinaryBufferReader.ReadByte(_buffer, _offset);
        }

        public async Task<long> ReadInt64Async()
        {
            await ReadAsync(Stream, _buffer, _offset, 8);
            return BinaryBufferReader.ReadInt64(_buffer, _offset, _littleEndian);
        }

        public async Task<ulong> ReadUInt64Async()
        {
            await ReadAsync(Stream, _buffer, _offset, 8);
            return BinaryBufferReader.ReadUInt64(_buffer, _offset, _littleEndian);
        }

        public async Task<int> ReadInt32Async()
        {
            await ReadAsync(Stream, _buffer, _offset, 4);
            return BinaryBufferReader.ReadInt32(_buffer, _offset, _littleEndian);
        }

        public async Task<uint> ReadUInt32Async()
        {
            await ReadAsync(Stream, _buffer, _offset, 4);
            return BinaryBufferReader.ReadUInt32(_buffer, _offset, _littleEndian);
        }

        public async Task<short> ReadInt16Async()
        {
            await ReadAsync(Stream, _buffer, _offset, 2);
            return BinaryBufferReader.ReadInt16(_buffer, _offset, _littleEndian);
        }

        public async Task<ushort> ReadUInt16Async()
        {
            await ReadAsync(Stream, _buffer, _offset, 2);
            return BinaryBufferReader.ReadUInt16(_buffer, _offset, _littleEndian);
        }

        public async Task<byte> ReadByteAsync()
        {
            await ReadAsync(Stream, _buffer, _offset, 1);
            return BinaryBufferReader.ReadByte(_buffer, _offset);
        }

        public static long ReadInt64(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Read(stream, buffer, offset, 8);
            return BinaryBufferReader.ReadInt64(buffer, offset, littleEndian);
        }

        public static ulong ReadUInt64(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Read(stream, buffer, offset, 8);
            return BinaryBufferReader.ReadUInt64(buffer, offset, littleEndian);
        }

        public static int ReadInt32(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Read(stream, buffer, offset, 4);
            return BinaryBufferReader.ReadInt32(buffer, offset, littleEndian);
        }

        public static uint ReadUInt32(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Read(stream, buffer, offset, 4);
            return BinaryBufferReader.ReadUInt32(buffer, offset, littleEndian);
        }

        public static short ReadInt16(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Read(stream, buffer, offset, 2);
            return BinaryBufferReader.ReadInt16(buffer, offset, littleEndian);
        }

        public static ushort ReadUInt16(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Read(stream, buffer, offset, 2);
            return BinaryBufferReader.ReadUInt16(buffer, offset, littleEndian);
        }

        public static byte ReadByte(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Read(stream, buffer, offset, 1);
            return BinaryBufferReader.ReadByte(buffer, offset);
        }

        public static async Task<long> ReadInt64Async(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            await ReadAsync(stream, buffer, offset, 8);
            return BinaryBufferReader.ReadInt64(buffer, offset, littleEndian);
        }

        public static async Task<ulong> ReadUInt64Async(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            await ReadAsync(stream, buffer, offset, 8);
            return BinaryBufferReader.ReadUInt64(buffer, offset, littleEndian);
        }

        public static async Task<int> ReadInt32Async(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            await ReadAsync(stream, buffer, offset, 4);
            return BinaryBufferReader.ReadInt32(buffer, offset, littleEndian);
        }

        public static async Task<uint> ReadUInt32Async(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            await ReadAsync(stream, buffer, offset, 4);
            return BinaryBufferReader.ReadUInt32(buffer, offset, littleEndian);
        }

        public static async Task<short> ReadInt16Async(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            await ReadAsync(stream, buffer, offset, 2);
            return BinaryBufferReader.ReadInt16(buffer, offset, littleEndian);
        }

        public static async Task<ushort> ReadUInt16Async(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            await ReadAsync(stream, buffer, offset, 2);
            return BinaryBufferReader.ReadUInt16(buffer, offset, littleEndian);
        }

        public static async Task<byte> ReadByteAsync(
            Stream stream, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            await ReadAsync(stream, buffer, offset, 1);
            return BinaryBufferReader.ReadByte(buffer, offset);
        }
    }

    struct BinaryBufferReader
    {
        private const bool _littleEndianDefault = true;

        private readonly byte[] _buffer;
        private readonly bool _littleEndian;

        public BinaryBufferReader(
            byte[] buffer,
            bool littleEndian = _littleEndianDefault)
            : this()
        {
            _buffer = buffer;
            _littleEndian = littleEndian;
        }

        public long ReadInt64(int offset)
        {
            return ReadInt64(_buffer, offset, _littleEndian);
        }

        public ulong ReadUInt64(int offset)
        {
            return ReadUInt64(_buffer, offset, _littleEndian);
        }

        public int ReadInt32(int offset)
        {
            return ReadInt32(_buffer, offset, _littleEndian);
        }

        public uint ReadUInt32(int offset)
        {
            return ReadUInt32(_buffer, offset, _littleEndian);
        }

        public short ReadInt16(int offset)
        {
            return ReadInt16(_buffer, offset, _littleEndian);
        }

        public ushort ReadUInt16(int offset)
        {
            return ReadUInt16(_buffer, offset, _littleEndian);
        }

        public byte ReadByte(int offset)
        {
            return ReadByte(_buffer, offset);
        }

        public static long ReadInt64(
            byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            if (littleEndian)
            {
                return (buffer[offset + 0] << 0) |
                    (buffer[offset + 1] << 8) |
                    (buffer[offset + 2] << 16) |
                    (buffer[offset + 3] << 24) |
                    (buffer[offset + 4] << 32) |
                    (buffer[offset + 5] << 40) |
                    (buffer[offset + 6] << 48) |
                    (buffer[offset + 7] << 56);
            }

            return (buffer[offset + 7] << 0) |
                (buffer[offset + 6] << 8) |
                (buffer[offset + 5] << 16) |
                (buffer[offset + 4] << 24) |
                (buffer[offset + 3] << 32) |
                (buffer[offset + 2] << 40) |
                (buffer[offset + 1] << 48) |
                (buffer[offset + 0] << 56);
        }

        public static ulong ReadUInt64(
            byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            return (ulong)ReadInt64(buffer, offset, littleEndian);
        }

        public static int ReadInt32(
            byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            if (littleEndian)
            {
                return (buffer[offset + 0] << 0) |
                    (buffer[offset + 1] << 8) |
                    (buffer[offset + 2] << 16) |
                    (buffer[offset + 3] << 24);
            }

            return (buffer[offset + 3] << 0) |
                (buffer[offset + 2] << 8) |
                (buffer[offset + 1] << 16) |
                (buffer[offset + 0] << 24);
        }

        public static uint ReadUInt32(
            byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            return (uint)ReadInt32(buffer, offset, littleEndian);
        }

        public static short ReadInt16(
            byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            if (littleEndian)
            {
                return (short)((buffer[offset + 0] << 0) |
                    (buffer[offset + 1] << 8));
            }

            return (short)((buffer[offset + 1] << 0) |
                (buffer[offset + 0] << 8));
        }

        public static ushort ReadUInt16(
            byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            return (ushort)ReadInt16(buffer, offset, littleEndian);
        }

        public static byte ReadByte(
            byte[] buffer, int offset)
        {
            return buffer[offset];
        }
    }

    struct BinaryBufferWriter
    {
        private const bool _littleEndianDefault = true;

        private readonly byte[] _buffer;
        private readonly bool _littleEndian;

        public BinaryBufferWriter(
            byte[] buffer,
            bool littleEndian = _littleEndianDefault)
            : this()
        {
            _buffer = buffer;
            _littleEndian = littleEndian;
        }

        public void Write(long input, int offset)
        {
            Write(input, _buffer, offset, _littleEndian);
        }

        public void Write(ulong input, int offset)
        {
            Write(input, _buffer, offset, _littleEndian);
        }

        public void Write(int input, int offset)
        {
            Write(input, _buffer, offset, _littleEndian);
        }

        public void Write(uint input, int offset)
        {
            Write(input, _buffer, offset, _littleEndian);
        }

        public void Write(short input, int offset)
        {
            Write(input, _buffer, offset, _littleEndian);
        }

        public void Write(ushort input, int offset)
        {
            Write(input, _buffer, offset, _littleEndian);
        }

        public void Write(byte input, int offset)
        {
            Write(input, _buffer, offset);
        }

        public static void Write(
            long input, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            if (littleEndian)
            {
                buffer[offset + 0] = (byte)((input >> 0) & 0xFF);
                buffer[offset + 1] = (byte)((input >> 8) & 0xFF);
                buffer[offset + 2] = (byte)((input >> 16) & 0xFF);
                buffer[offset + 3] = (byte)((input >> 24) & 0xFF);
                buffer[offset + 4] = (byte)((input >> 32) & 0xFF);
                buffer[offset + 5] = (byte)((input >> 40) & 0xFF);
                buffer[offset + 6] = (byte)((input >> 48) & 0xFF);
                buffer[offset + 7] = (byte)((input >> 56) & 0xFF);
            }

            buffer[offset + 7] = (byte)((input >> 0) & 0xFF);
            buffer[offset + 6] = (byte)((input >> 8) & 0xFF);
            buffer[offset + 5] = (byte)((input >> 16) & 0xFF);
            buffer[offset + 4] = (byte)((input >> 24) & 0xFF);
            buffer[offset + 3] = (byte)((input >> 32) & 0xFF);
            buffer[offset + 2] = (byte)((input >> 40) & 0xFF);
            buffer[offset + 1] = (byte)((input >> 48) & 0xFF);
            buffer[offset + 0] = (byte)((input >> 56) & 0xFF);
        }

        public static void Write(
            ulong input, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Write((long)input, buffer, offset, littleEndian);
        }

        public static void Write(
            int input, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            if (littleEndian)
            {
                buffer[offset + 0] = (byte)((input >> 0) & 0xFF);
                buffer[offset + 1] = (byte)((input >> 8) & 0xFF);
                buffer[offset + 2] = (byte)((input >> 16) & 0xFF);
                buffer[offset + 3] = (byte)((input >> 24) & 0xFF);
            }

            buffer[offset + 3] = (byte)((input >> 0) & 0xFF);
            buffer[offset + 2] = (byte)((input >> 8) & 0xFF);
            buffer[offset + 1] = (byte)((input >> 16) & 0xFF);
            buffer[offset + 0] = (byte)((input >> 24) & 0xFF);
        }

        public static void Write(
            uint input, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Write((int)input, buffer, offset, littleEndian);
        }

        public static void Write(
            short input, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            if (littleEndian)
            {
                buffer[offset + 0] = (byte)((input >> 0) & 0xFF);
                buffer[offset + 1] = (byte)((input >> 8) & 0xFF);
            }

            buffer[offset + 1] = (byte)((input >> 0) & 0xFF);
            buffer[offset + 0] = (byte)((input >> 8) & 0xFF);
        }

        public static void Write(
            ushort input, byte[] buffer, int offset,
            bool littleEndian = _littleEndianDefault)
        {
            Write((short)input, buffer, offset, littleEndian);
        }

        public static void Write(
            byte input, byte[] buffer, int offset)
        {
            buffer[offset] = input;
        }
    }
}
