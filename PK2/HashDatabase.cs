using System.IO;

namespace PK2Editor.PK2;

// Resolves CRC32 filename hashes back to ASCII names by hashing every entry
// of a Telltale .symmap and indexing by hash. Source file: the project's
// Resources/Files_CSI3.symmap (16k+ known names extracted from CSI3 PS2).
public sealed class HashDatabase
{
    private readonly Dictionary<uint, string> _byHash = new();

    public int Count => _byHash.Count;

    public bool TryGetName(uint hash, out string name) => _byHash.TryGetValue(hash, out name!);

    public string Resolve(uint hash) => _byHash.TryGetValue(hash, out var n) ? n : $"<{hash:X8}>";

    public void LoadSymmap(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string entry = raw.StartsWith("CRC32:") ? raw.Substring(6) : raw;
            if (entry.Length == 0) continue;

            RegisterName(entry);

            // The CSI3 PS2 PK2 uses case-sensitive CRC32 names, and many
            // resources in the available symmap are capitalized differently
            // from the lowercase names packed by the console build.
            string lower = entry.ToLowerInvariant();
            if (lower != entry)
                RegisterName(lower);
        }
    }

    public void RegisterName(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _byHash[Crc32.Compute(name)] = name;
    }
}
