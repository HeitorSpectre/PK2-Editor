using System.IO;
using System.Text;

namespace PK2Editor.PK2;

// Reader for the CSI: 3 Dimensions of Murder (PS2) gameData.pk2 archive.
//
// The file is a Telltale "BMS3" meta stream that holds a single TTArchive
// instance (legacy class registered in CSI3.lua / TTArchive.lua). Layout
// derived from Pack2.cpp + Meta.cpp + the Lua serialiser:
//
//   [4]   "BMS3" magic
//   [4]   flags  (always 0x02010000 in CSI3 PS2)
//   [4]   numClasses
//   [N*8] per-class: legacyTypeHash u32 + versionCRC u32
//
//   TTArchive (root instance, no enclosing block):
//     -- member 1: Baseclass_TTArchiveFolder, blocked --
//     [4]   block size (includes the 4 size bytes)
//     TTArchiveFolder content:
//       Default-serialised members:
//         -- mName : String, blocked --
//         [4] block size
//         [4] string length
//         [N] ASCII chars
//         -- mFolders : HashMap<String,TTArchiveFolder>, blocked --
//         [4] block size
//         [4] entry count (collection size)
//         per entry:
//           [4] key string length        <-- collection elements are NOT blocked
//           [N] key chars
//           TTArchiveFolder value (recurses; its members are blocked again)
//       Custom (post-default) appended by SerialiseCSI3ArchiveFolder:
//         [2] numFiles (U16)
//         [12*numFiles] file table { U32 nameCRC32, U32 offset, U32 size }
//
//     -- member 2: mDataSize, intrinsic, NOT blocked --
//     [4]   mDataSize  (always 0 in this game; comment in source confirms)
//
//     -- member 3: _mCachedPayload, serialise-disabled in default --
//     [rest of file] cached payload. File offsets in the file table are
//                    measured from the start of this region.
public sealed class PK2Reader : IDisposable
{
    public string FilePath { get; }
    public PK2Folder Root { get; private set; } = null!;
    public List<PK2VersionInfo> VersionInfo { get; } = new();
    public List<PK2File> AllFiles { get; } = new();
    public uint Flags { get; private set; }
    public uint DataSize { get; private set; }
    public long PayloadStart { get; private set; }

    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly HashDatabase _hashDb;

    public PK2Reader(string path, HashDatabase hashDb)
    {
        FilePath = path;
        _hashDb = hashDb;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: false);
    }

    public void Parse()
    {
        _stream.Position = 0;
        ReadHeader();
        Root = ReadRootArchive();
        ResolveAllNames();
    }

    private void ReadHeader()
    {
        byte[] magicBytes = _reader.ReadBytes(4);
        string magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != "BMS3")
            throw new InvalidDataException($"This is not a BMS3 file (CSI3 PS2 .pk2). Read magic: '{magic}'");

        Flags = _reader.ReadUInt32();
        if (Flags != 0x02010000)
            throw new InvalidDataException($"Unexpected BMS3 flags: 0x{Flags:X8} (expected 0x02010000).");

        uint numClasses = _reader.ReadUInt32();
        if (numClasses > 256)
            throw new InvalidDataException($"Invalid header: numClasses = {numClasses}");

        for (uint i = 0; i < numClasses; i++)
        {
            VersionInfo.Add(new PK2VersionInfo
            {
                LegacyHash = _reader.ReadUInt32(),
                VersionCRC = _reader.ReadUInt32(),
            });
        }
    }

    private PK2Folder ReadRootArchive()
    {
        // member 1: Baseclass_TTArchiveFolder is BLOCKED
        long blockEnd = ReadBlockStart();
        var root = ReadFolder(parentPath: "");
        ConsumeBlockEnd(blockEnd, "Baseclass_TTArchiveFolder");

        // member 2: mDataSize (intrinsic U32, not blocked)
        DataSize = _reader.ReadUInt32();

        // remaining bytes are the cached payload
        PayloadStart = _stream.Position;
        return root;
    }

    private PK2Folder ReadFolder(string parentPath)
    {
        // mName String (blocked). The folder's own mName already contains
        // its trailing path separator (e.g., "GameData-LasVegas\"), so the
        // full path is just parent + mName.
        long mNameEnd = ReadBlockStart();
        string name = ReadString();
        ConsumeBlockEnd(mNameEnd, "mName");

        string fullPath = parentPath + name;
        var folder = new PK2Folder { Name = name, FullPath = fullPath };

        // mFolders HashMap (blocked)
        long mFoldersEnd = ReadBlockStart();
        uint childCount = _reader.ReadUInt32();
        if (childCount > 0x10000)
            throw new InvalidDataException($"Suspicious subfolder count: {childCount}");

        for (uint i = 0; i < childCount; i++)
        {
            // collection key (String) is NOT blocked, just length+chars.
            // The key is the lowercase hash-friendly variant; the folder's
            // own mName (read inside ReadFolder) is the case-preserved
            // display name.
            string key = ReadString();
            var sub = ReadFolder(fullPath);
            sub.CollectionKey = key;
            folder.Subfolders.Add(sub);
        }
        ConsumeBlockEnd(mFoldersEnd, "mFolders");

        // Custom append: U16 numFiles, then 12 bytes per file
        ushort numFiles = _reader.ReadUInt16();
        for (ushort i = 0; i < numFiles; i++)
        {
            uint nameHash = _reader.ReadUInt32();
            uint offset = _reader.ReadUInt32();
            uint size = _reader.ReadUInt32();
            folder.Files.Add(new PK2File
            {
                NameHash = nameHash,
                Offset = offset,
                Size = size,
                FolderPath = fullPath,
            });
        }

        return folder;
    }

    private long ReadBlockStart()
    {
        long blockStart = _stream.Position;
        uint blockSize = _reader.ReadUInt32();
        if (blockSize < 4)
            throw new InvalidDataException($"Corrupt block at 0x{blockStart:X}: size {blockSize}");
        return blockStart + blockSize;
    }

    private void ConsumeBlockEnd(long expectedEnd, string what)
    {
        if (_stream.Position != expectedEnd)
        {
            throw new InvalidDataException(
                $"Block '{what}' was not fully consumed. Pos=0x{_stream.Position:X}, expected=0x{expectedEnd:X}");
        }
    }

    private string ReadString()
    {
        uint len = _reader.ReadUInt32();
        if (len > 10000)
            throw new InvalidDataException($"Suspicious string length: {len}");
        if (len == 0) return string.Empty;
        byte[] bytes = _reader.ReadBytes((int)len);
        return Encoding.ASCII.GetString(bytes);
    }

    private void ResolveAllNames()
    {
        AllFiles.Clear();
        Walk(Root);

        void Walk(PK2Folder folder)
        {
            foreach (var f in folder.Files)
            {
                if (_hashDb.TryGetName(f.NameHash, out var n))
                    f.ResolvedName = n;
                AllFiles.Add(f);
            }
            foreach (var sub in folder.Subfolders) Walk(sub);
        }
    }

    public byte[] ExtractFile(PK2File file)
    {
        long pos = PayloadStart + file.Offset;
        if (pos + file.Size > _stream.Length)
            throw new InvalidDataException($"File is outside the PK2 bounds: offset 0x{pos:X} + {file.Size}");

        _stream.Position = pos;
        byte[] data = new byte[file.Size];
        int read = _stream.Read(data, 0, data.Length);
        if (read != data.Length)
            throw new EndOfStreamException($"Incomplete read for {file.DisplayName}");
        return data;
    }

    public byte[] ReadPreview(PK2File file, int maxBytes = 4096)
    {
        int n = (int)Math.Min((long)maxBytes, file.Size);
        long pos = PayloadStart + file.Offset;
        _stream.Position = pos;
        byte[] data = new byte[n];
        _stream.Read(data, 0, n);
        return data;
    }

    public void CopyFileTo(PK2File file, Stream output)
    {
        long pos = PayloadStart + file.Offset;
        if (pos + file.Size > _stream.Length)
            throw new InvalidDataException($"File is outside the PK2 bounds: offset 0x{pos:X} + {file.Size}");

        _stream.Position = pos;
        byte[] buffer = new byte[1024 * 128];
        long remaining = file.Size;
        while (remaining > 0)
        {
            int read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
                throw new EndOfStreamException($"Incomplete read for {file.DisplayName}");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
