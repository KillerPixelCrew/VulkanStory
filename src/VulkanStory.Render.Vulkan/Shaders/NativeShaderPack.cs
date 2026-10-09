using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VulkanStory.Render.Vulkan.Shaders;

/// <summary>
/// <c>shaders-vk.pak</c>: the shipped native shader corpus (the manifest and every SPIR-V file it names) in one file.
///
/// Little-endian. Header: the 8 ASCII bytes <c>VSSHPAK1</c>, u32 format version (1), u32 entry count. Index, one
/// record per entry: u16 name length, the UTF-8 name, u64 offset from the start of the file, u64 length, the
/// 32-byte SHA-256 of the blob. Blobs follow the index, each at a 4-byte aligned offset, zero padding between.
/// Entry names are the file names of the <c>shaders-vk</c> directory; the builder writes the manifest first, then
/// the SPIR-V names in ordinal order.
///
/// The reader checks the index when it opens the file and each blob's SHA-256 when it is read.
/// </summary>
internal sealed class NativeShaderPack
{
    public const int FormatVersion = 1;

    private const int HeaderSize = 16;

    /// <summary>Index record size without the name: u16 length, u64 offset, u64 length, SHA-256.</summary>
    private const int FixedEntrySize = 2 + 8 + 8 + 32;

    private static ReadOnlySpan<byte> Magic => "VSSHPAK1"u8;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly record struct Entry(long Offset, long Length, byte[] Sha256);

    private readonly Dictionary<string, Entry> _entries;

    /// <summary>The pack file, for messages.</summary>
    public string FilePath { get; }

    private NativeShaderPack(string filePath, Dictionary<string, Entry> entries)
    {
        FilePath = filePath;
        _entries = entries;
    }

    /// <summary>Whether the index has an entry of that name.</summary>
    public bool Contains(string name) => _entries.ContainsKey(name);

    // ------------------------------------------------------------------ reader

    /// <summary>
    /// Reads and checks the header and index of <paramref name="path" />. False, with the reason, when the file is
    /// missing or unreadable, of another format or version, truncated, or has empty, duplicate, unaligned or
    /// overlapping entries.
    /// </summary>
    public static bool TryOpen(string path, [NotNullWhen(true)] out NativeShaderPack? pack, out string reason)
    {
        pack = null;
        if (!File.Exists(path))
        {
            reason = "not found";
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            pack = new NativeShaderPack(path, ReadIndex(stream));
            reason = "";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            reason = error.Message;
            return false;
        }
    }

    private static Dictionary<string, Entry> ReadIndex(Stream stream)
    {
        long fileLength = stream.Length;
        Span<byte> header = stackalloc byte[HeaderSize];
        if (fileLength < HeaderSize || stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
        {
            throw new InvalidDataException("truncated header (" + fileLength + " bytes)");
        }
        if (!header[..8].SequenceEqual(Magic)) throw new InvalidDataException("not a VSSHPAK1 file");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (version != FormatVersion)
        {
            throw new InvalidDataException("format version " + version + ", this build reads " + FormatVersion);
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (count > (fileLength - HeaderSize) / (FixedEntrySize + 1))
        {
            throw new InvalidDataException("index of " + count + " entries does not fit in " + fileLength + " bytes");
        }

        var entries = new Dictionary<string, Entry>((int)count, StringComparer.Ordinal);
        var names = new List<string>((int)count);
        Span<byte> fixedPart = stackalloc byte[FixedEntrySize - 2];
        Span<byte> nameLength = stackalloc byte[2];
        for (uint i = 0; i < count; i++)
        {
            ReadExactly(stream, nameLength, "entry " + i + " name length");
            int length = BinaryPrimitives.ReadUInt16LittleEndian(nameLength);
            if (length == 0) throw new InvalidDataException("entry " + i + " has an empty name");
            byte[] nameBytes = new byte[length];
            ReadExactly(stream, nameBytes, "entry " + i + " name");
            string name;
            try
            {
                name = StrictUtf8.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("entry " + i + " name is not UTF-8");
            }

            ReadExactly(stream, fixedPart, "entry '" + name + "'");
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(fixedPart);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(fixedPart[8..]);
            if (offset > (ulong)fileLength || size > (ulong)fileLength - offset)
            {
                throw new InvalidDataException("entry '" + name + "' (" + size + " bytes at " + offset + ") runs past the end of the " +
                                               fileLength + "-byte file; the pack is truncated");
            }
            if (size > (ulong)Array.MaxLength) throw new InvalidDataException("entry '" + name + "' is too large");
            if (offset % 4 != 0) throw new InvalidDataException("entry '" + name + "' offset " + offset + " is not 4-byte aligned");
            if (entries.ContainsKey(name)) throw new InvalidDataException("duplicate entry '" + name + "'");

            entries[name] = new Entry((long)offset, (long)size, fixedPart[16..].ToArray());
            names.Add(name);
        }

        // Blobs lie after the index and do not overlap one another.
        long indexEnd = stream.Position;
        names.Sort((a, b) => entries[a].Offset.CompareTo(entries[b].Offset));
        long previousEnd = indexEnd;
        string previous = "the index";
        foreach (string name in names)
        {
            Entry entry = entries[name];
            if (entry.Offset < previousEnd)
            {
                throw new InvalidDataException("entry '" + name + "' at " + entry.Offset + " overlaps " + previous + " (ends at " + previousEnd + ")");
            }
            previousEnd = entry.Offset + entry.Length;
            previous = "entry '" + name + "'";
        }
        return entries;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string what)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
        {
            throw new InvalidDataException("truncated index at " + what);
        }
    }

    /// <summary>
    /// The bytes of entry <paramref name="name" />, read from the file and checked against the index SHA-256.
    /// Throws <see cref="InvalidDataException" /> for an absent entry, a short read or a hash mismatch, and
    /// <see cref="IOException" /> or <see cref="UnauthorizedAccessException" /> when the file cannot be read.
    /// </summary>
    public byte[] ReadBytes(string name)
    {
        if (!_entries.TryGetValue(name, out Entry entry))
        {
            throw new InvalidDataException("shader pack " + FilePath + " has no entry '" + name + "'");
        }

        byte[] bytes = new byte[entry.Length];
        using (SafeFileHandle handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = 0;
            while (read < bytes.Length)
            {
                int chunk = RandomAccess.Read(handle, bytes.AsSpan(read), entry.Offset + read);
                if (chunk == 0)
                {
                    throw new InvalidDataException("shader pack " + FilePath + " entry '" + name + "' is truncated: " + read +
                                                   " of " + entry.Length + " bytes");
                }
                read += chunk;
            }
        }

        byte[] actual = SHA256.HashData(bytes);
        if (!actual.AsSpan().SequenceEqual(entry.Sha256))
        {
            throw new InvalidDataException("shader pack " + FilePath + " entry '" + name + "' sha256 " + Convert.ToHexStringLower(actual) +
                                           ", index says " + Convert.ToHexStringLower(entry.Sha256));
        }
        return bytes;
    }

    /// <summary>Entry <paramref name="name" /> as UTF-8 text, a leading byte order mark dropped; throws as <see cref="ReadBytes" />.</summary>
    public string ReadText(string name)
    {
        byte[] bytes = ReadBytes(name);
        ReadOnlySpan<byte> text = bytes;
        if (text.StartsWith(Encoding.UTF8.Preamble)) text = text[Encoding.UTF8.Preamble.Length..];
        return Encoding.UTF8.GetString(text);
    }

    // ------------------------------------------------------------------ writer

    /// <summary>
    /// The pack of <paramref name="entries" />, in the order given. Throws <see cref="InvalidDataException" /> for an
    /// empty, overlong or duplicate name, or a pack too large for one array.
    /// </summary>
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Bytes)> entries)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var encoded = new byte[entries.Count][];
        long indexEnd = HeaderSize;
        for (int i = 0; i < entries.Count; i++)
        {
            string name = entries[i].Name;
            if (string.IsNullOrEmpty(name)) throw new InvalidDataException("shader pack entry " + i + " has an empty name");
            if (!names.Add(name)) throw new InvalidDataException("duplicate shader pack entry '" + name + "'");
            encoded[i] = StrictUtf8.GetBytes(name);
            if (encoded[i].Length > ushort.MaxValue) throw new InvalidDataException("shader pack entry name '" + name + "' is too long");
            indexEnd += FixedEntrySize + encoded[i].Length;
        }

        var offsets = new long[entries.Count];
        long end = indexEnd;
        for (int i = 0; i < entries.Count; i++)
        {
            offsets[i] = Align4(end);
            end = offsets[i] + entries[i].Bytes.Length;
        }
        if (end > Array.MaxLength) throw new InvalidDataException("shader pack of " + end + " bytes is too large");

        byte[] pack = new byte[end];
        Span<byte> span = pack;
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)entries.Count);
        int position = HeaderSize;
        for (int i = 0; i < entries.Count; i++)
        {
            byte[] bytes = entries[i].Bytes;
            BinaryPrimitives.WriteUInt16LittleEndian(span[position..], (ushort)encoded[i].Length);
            position += 2;
            encoded[i].CopyTo(span[position..]);
            position += encoded[i].Length;
            BinaryPrimitives.WriteUInt64LittleEndian(span[position..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(position + 8)..], (ulong)bytes.Length);
            SHA256.HashData(bytes, span.Slice(position + 16, 32));
            position += FixedEntrySize - 2;
            bytes.CopyTo(span[(int)offsets[i]..]);
        }
        return pack;
    }

    private static long Align4(long value) => (value + 3) & ~3L;
}
