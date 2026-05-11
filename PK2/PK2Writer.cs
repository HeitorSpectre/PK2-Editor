using System.IO;
using System.Text;

namespace PK2Editor.PK2;

public static class PK2Writer
{
    private const int PayloadAlignment = 0x800;

    public static PK2RepackResult RebuildFromFolder(
        PK2Reader source,
        string replacementRoot,
        string outputPath,
        IProgress<PK2RepackProgress>? progress = null,
        Func<bool>? isCancelled = null)
    {
        if (!Directory.Exists(replacementRoot))
            throw new DirectoryNotFoundException(replacementRoot);

        var replacements = BuildReplacementMap(source.AllFiles, replacementRoot);
        return RebuildWithReplacements(source, replacements, outputPath, progress, isCancelled);
    }

    public static PK2RepackResult RebuildWithReplacements(
        PK2Reader source,
        IReadOnlyDictionary<PK2File, string> replacements,
        string outputPath,
        IProgress<PK2RepackProgress>? progress = null,
        Func<bool>? isCancelled = null)
    {
        if (replacements.Count == 0)
            throw new InvalidOperationException("No replacement files were selected.");

        string sourcePath = Path.GetFullPath(source.FilePath);
        string finalPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, finalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new output file. Rebuilding directly over the source PK2 is not allowed.");

        foreach (string path in replacements.Values)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Replacement file was not found.", path);
        }

        var plans = BuildPlans(source, replacements);
        int replacementCount = plans.Values.Count(p => p.ReplacementPath is not null);
        if (replacementCount == 0)
            throw new InvalidOperationException("No replacement files match files in the PK2.");

        AssignOffsets(source, plans);

        string tempPath = finalPath + ".tmp";
        try
        {
            File.Copy(source.FilePath, tempPath, overwrite: true);
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true))
            {
                fs.Position = 0;
                WriteHeader(writer, source);
                WriteBlock(writer, () => WriteFolder(writer, source.Root, plans));
                writer.Write(source.DataSize);
                if (fs.Position != source.PayloadStart)
                    throw new InvalidOperationException(
                        $"Rebuilt BMS3 table size changed unexpectedly. Pos=0x{fs.Position:X}, expected=0x{source.PayloadStart:X}.");

                int completed = 0;
                foreach (var file in source.AllFiles.Where(f => plans[f].ReplacementPath is not null).OrderBy(f => plans[f].Offset))
                {
                    if (isCancelled?.Invoke() == true)
                        throw new OperationCanceledException();

                    var plan = plans[file];
                    SeekPreservingExistingBytes(fs, source.PayloadStart + plan.Offset);
                    using var input = File.OpenRead(plan.ReplacementPath!);
                    input.CopyTo(fs);

                    completed++;
                    progress?.Report(new PK2RepackProgress
                    {
                        Completed = completed,
                        Total = replacementCount,
                        Message = $"{completed}/{replacementCount}: {file.FullPath}",
                    });
                }
            }

            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(tempPath, finalPath);

            return new PK2RepackResult
            {
                TotalFiles = source.AllFiles.Count,
                ReplacedFiles = replacementCount,
                OutputSize = new FileInfo(finalPath).Length,
            };
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best effort cleanup only.
            }
            throw;
        }
    }

    public static Dictionary<PK2File, string> BuildReplacementMap(
        IEnumerable<PK2File> candidates,
        string replacementRoot,
        string? baseFolderPath = null)
    {
        if (!Directory.Exists(replacementRoot))
            throw new DirectoryNotFoundException(replacementRoot);

        var replacements = new Dictionary<PK2File, string>();
        foreach (var file in candidates)
        {
            string? replacement = FindReplacementPath(file, replacementRoot, baseFolderPath);
            if (replacement is not null)
                replacements[file] = replacement;
        }
        return replacements;
    }

    private static Dictionary<PK2File, RepackPlan> BuildPlans(
        PK2Reader source,
        IReadOnlyDictionary<PK2File, string> replacements)
    {
        var plans = new Dictionary<PK2File, RepackPlan>();
        foreach (var file in source.AllFiles)
        {
            replacements.TryGetValue(file, out string? replacement);
            long size = replacement is not null ? new FileInfo(replacement).Length : file.Size;
            if (size > uint.MaxValue)
                throw new InvalidOperationException($"File is too large for this PK2 table: {replacement ?? file.FullPath}");

            plans[file] = new RepackPlan
            {
                ReplacementPath = replacement,
                Size = (uint)size,
            };
        }
        return plans;
    }

    private static string? FindReplacementPath(PK2File file, string replacementRoot, string? baseFolderPath)
    {
        foreach (string relative in EnumeratePossibleRelativePaths(file, baseFolderPath))
        {
            string candidate = Path.Combine(replacementRoot, relative);
            if (File.Exists(candidate))
                return candidate;

            string withoutTopFolder = RemoveTopFolder(relative);
            if (!string.Equals(withoutTopFolder, relative, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(replacementRoot, withoutTopFolder);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumeratePossibleRelativePaths(PK2File file, string? baseFolderPath)
    {
        string folder = NormalizeFolderPath(file.FolderPath);
        string name = SanitizeFileName(file.DisplayName);

        if (!string.IsNullOrEmpty(baseFolderPath) &&
            file.FolderPath.StartsWith(baseFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return name;
            string relativeFolder = NormalizeFolderPath(file.FolderPath[baseFolderPath.Length..]);
            if (!string.IsNullOrEmpty(relativeFolder))
                yield return Path.Combine(relativeFolder, name);
        }
        else
        {
            yield return Path.Combine(folder, name);
            yield return name;
        }

        if (!file.IsResolved)
        {
            string inferredName = $"{file.NameHash:X8}{file.InferredExtension}";
            if (!string.IsNullOrEmpty(file.InferredExtension))
            {
                yield return Path.Combine(folder, inferredName);
                yield return inferredName;
            }

            yield return Path.Combine(folder, $"_{file.NameHash:X8}_");
            yield return Path.Combine(folder, $"{file.NameHash:X8}");
            yield return $"_{file.NameHash:X8}_";
            yield return $"{file.NameHash:X8}";
        }
    }

    private static void AssignOffsets(PK2Reader source, Dictionary<PK2File, RepackPlan> plans)
    {
        var byOffset = source.AllFiles.OrderBy(f => f.Offset).ToList();
        long payloadLength = new FileInfo(source.FilePath).Length - source.PayloadStart;
        long appendOffset = Align(payloadLength, PayloadAlignment);

        for (int i = 0; i < byOffset.Count; i++)
        {
            var file = byOffset[i];
            var plan = plans[file];
            long slotEnd = i + 1 < byOffset.Count ? byOffset[i + 1].Offset : payloadLength;
            long slotSize = Math.Max(file.Size, slotEnd - file.Offset);

            if (plan.ReplacementPath is null || plan.Size <= slotSize)
            {
                plan.Offset = file.Offset;
                continue;
            }

            appendOffset = Align(appendOffset, PayloadAlignment);
            if (appendOffset > uint.MaxValue)
                throw new InvalidOperationException("The rebuilt PK2 payload is too large for 32-bit offsets.");

            plan.Offset = (uint)appendOffset;
            appendOffset += plan.Size;
        }
    }

    private static void WriteHeader(BinaryWriter writer, PK2Reader source)
    {
        writer.Write(Encoding.ASCII.GetBytes("BMS3"));
        writer.Write(source.Flags);
        writer.Write((uint)source.VersionInfo.Count);
        foreach (var version in source.VersionInfo)
        {
            writer.Write(version.LegacyHash);
            writer.Write(version.VersionCRC);
        }
    }

    private static void WriteFolder(BinaryWriter writer, PK2Folder folder, Dictionary<PK2File, RepackPlan> plans)
    {
        WriteBlock(writer, () => WriteString(writer, folder.Name));
        WriteBlock(writer, () =>
        {
            writer.Write((uint)folder.Subfolders.Count);
            foreach (var subfolder in folder.Subfolders)
            {
                string key = string.IsNullOrEmpty(subfolder.CollectionKey)
                    ? subfolder.Name.ToLowerInvariant()
                    : subfolder.CollectionKey;
                WriteString(writer, key);
                WriteFolder(writer, subfolder, plans);
            }
        });

        if (folder.Files.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Folder has too many files for a CSI3 PK2 table: {folder.FullPath}");

        writer.Write((ushort)folder.Files.Count);
        foreach (var file in folder.Files)
        {
            var plan = plans[file];
            writer.Write(file.NameHash);
            writer.Write(plan.Offset);
            writer.Write(plan.Size);
        }
    }

    private static void WriteBlock(BinaryWriter writer, Action writeContent)
    {
        long start = writer.BaseStream.Position;
        writer.Write(0u);
        writeContent();
        long end = writer.BaseStream.Position;
        long size = end - start;
        if (size > uint.MaxValue)
            throw new InvalidOperationException("BMS3 block is too large.");

        writer.BaseStream.Position = start;
        writer.Write((uint)size);
        writer.BaseStream.Position = end;
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void PadTo(Stream stream, long targetPosition)
    {
        if (stream.Position > targetPosition)
            throw new InvalidOperationException($"Writer passed expected payload offset 0x{targetPosition:X}.");

        Span<byte> zeros = stackalloc byte[256];
        while (stream.Position < targetPosition)
        {
            int count = (int)Math.Min(zeros.Length, targetPosition - stream.Position);
            stream.Write(zeros[..count]);
        }
    }

    private static void SeekPreservingExistingBytes(Stream stream, long targetPosition)
    {
        if (targetPosition <= stream.Length)
        {
            stream.Position = targetPosition;
            return;
        }

        PadTo(stream, targetPosition);
    }

    private static long Align(long value, int alignment)
    {
        long mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        var parts = folderPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;
        return Path.Combine(parts.Select(SanitizeFileName).ToArray());
    }

    private static string RemoveTopFolder(string relativePath)
    {
        int firstSlash = relativePath.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return firstSlash < 0 ? relativePath : relativePath[(firstSlash + 1)..];
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.TrimEnd('\\', '/');
    }

    private sealed class RepackPlan
    {
        public string? ReplacementPath { get; init; }
        public uint Offset { get; set; }
        public required uint Size { get; init; }
    }
}
