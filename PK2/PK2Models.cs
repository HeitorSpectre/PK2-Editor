namespace PK2Editor.PK2;

public sealed class PK2File
{
    public required uint NameHash { get; init; }
    public required uint Offset { get; init; }
    public required uint Size { get; init; }
    public required string FolderPath { get; init; }
    public string? ResolvedName { get; set; }

    public string DisplayName => ResolvedName ?? $"{NameHash:X8}{InferredExtension}";
    public string FullPath => FolderPath + DisplayName;
    public bool IsResolved => ResolvedName != null;

    public string InferredExtension
    {
        get
        {
            if (string.Equals(FolderPath, @"GameData-LasVegas\Textures\", StringComparison.OrdinalIgnoreCase))
                return ".d3dtx";
            if (string.Equals(FolderPath, @"GameData-LasVegas\ZippedScenes\", StringComparison.OrdinalIgnoreCase))
                return NameHash == 0x01B6BD36 ? ".txt" : ".gz";
            if (string.Equals(FolderPath, @"GameData-LasVegas\Properties\", StringComparison.OrdinalIgnoreCase))
                return ".prop";
            return string.Empty;
        }
    }
}

public sealed class PK2Folder
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string CollectionKey { get; set; } = string.Empty;
    public List<PK2Folder> Subfolders { get; } = new();
    public List<PK2File> Files { get; } = new();
}

public sealed class PK2RepackProgress
{
    public required int Completed { get; init; }
    public required int Total { get; init; }
    public required string Message { get; init; }
}

public sealed class PK2RepackResult
{
    public required int TotalFiles { get; init; }
    public required int ReplacedFiles { get; init; }
    public required long OutputSize { get; init; }
}

public sealed class PK2VersionInfo
{
    public required uint LegacyHash { get; init; }
    public required uint VersionCRC { get; init; }
}
