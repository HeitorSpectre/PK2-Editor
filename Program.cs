using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PK2Editor.PK2;

namespace PK2Editor;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--smoke")
            return RunSmoke(args[1]);
        if (args.Length >= 3 && args[0] == "--extract-all")
            return RunExtractAll(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "--check-replacements")
            return RunCheckReplacements(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "--rebuild")
            return RunRebuild(args[1], args[2], args[3]);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static int RunCheckReplacements(string pk2Path, string replacementRoot)
    {
        var db = new HashDatabase();
        string symmap = Path.Combine(AppContext.BaseDirectory, "Resources", "Files_CSI3.symmap");
        if (File.Exists(symmap)) db.LoadSymmap(symmap);

        using var reader = new PK2Reader(pk2Path, db);
        reader.Parse();
        var replacements = PK2Writer.BuildReplacementMap(reader.AllFiles, replacementRoot);
        Console.WriteLine($"matches={replacements.Count} total={reader.AllFiles.Count}");

        int mismatch = 0;
        foreach (var (file, path) in replacements)
        {
            long size = new FileInfo(path).Length;
            if (size != file.Size)
            {
                mismatch++;
                if (mismatch <= 80)
                    Console.WriteLine($"{file.NameHash:X8},{file.Size},{size},{file.FullPath},{path}");
            }
        }
        Console.WriteLine($"size_mismatches={mismatch}");
        return mismatch == 0 && replacements.Count == reader.AllFiles.Count ? 0 : 1;
    }

    private static int RunExtractAll(string pk2Path, string outputRoot)
    {
        var db = new HashDatabase();
        string symmap = Path.Combine(AppContext.BaseDirectory, "Resources", "Files_CSI3.symmap");
        if (File.Exists(symmap)) db.LoadSymmap(symmap);

        using var reader = new PK2Reader(pk2Path, db);
        try
        {
            reader.Parse();
            Directory.CreateDirectory(outputRoot);
            int completed = 0;
            foreach (var file in reader.AllFiles)
            {
                string targetDir = Path.Combine(outputRoot, SanitizeFolderPath(file.FolderPath));
                Directory.CreateDirectory(targetDir);
                string targetFile = Path.Combine(targetDir, SanitizeFileName(file.DisplayName));
                File.WriteAllBytes(targetFile, reader.ExtractFile(file));
                completed++;
                if (completed % 500 == 0 || completed == reader.AllFiles.Count)
                    Console.WriteLine($"{completed}/{reader.AllFiles.Count}");
            }
            Console.WriteLine($"Extracted: {reader.AllFiles.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EXTRACT ERROR: {ex.Message}");
            return 2;
        }
    }

    private static int RunRebuild(string pk2Path, string replacementRoot, string outputPath)
    {
        var db = new HashDatabase();
        string symmap = Path.Combine(AppContext.BaseDirectory, "Resources", "Files_CSI3.symmap");
        if (File.Exists(symmap)) db.LoadSymmap(symmap);

        using var reader = new PK2Reader(pk2Path, db);
        try
        {
            reader.Parse();
            var progress = new Progress<PK2RepackProgress>(p =>
            {
                if (p.Completed % 500 == 0 || p.Completed == p.Total)
                    Console.WriteLine($"{p.Completed}/{p.Total}");
            });
            var result = PK2Writer.RebuildFromFolder(reader, replacementRoot, outputPath, progress);
            Console.WriteLine($"Rebuilt: {outputPath}");
            Console.WriteLine($"Replaced files: {result.ReplacedFiles}");
            Console.WriteLine($"Total files: {result.TotalFiles}");
            Console.WriteLine($"Output size: {result.OutputSize}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"REBUILD ERROR: {ex.Message}");
            return 2;
        }
    }

    private static int RunSmoke(string path)
    {
        var db = new HashDatabase();
        string symmap = Path.Combine(AppContext.BaseDirectory, "Resources", "Files_CSI3.symmap");
        if (File.Exists(symmap)) db.LoadSymmap(symmap);
        Console.WriteLine($"HashDB loaded: {db.Count} entries");

        using var reader = new PK2Reader(path, db);
        try
        {
            reader.Parse();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PARSE ERROR: {ex.Message}");
            return 2;
        }
        Console.WriteLine($"Flags=0x{reader.Flags:X8}");
        Console.WriteLine($"Classes={reader.VersionInfo.Count}");
        foreach (var v in reader.VersionInfo)
            Console.WriteLine($"  legacy=0x{v.LegacyHash:X8}  versionCRC=0x{v.VersionCRC:X8}");
        Console.WriteLine($"PayloadStart=0x{reader.PayloadStart:X}");
        Console.WriteLine($"DataSize={reader.DataSize}");
        Console.WriteLine($"Total files: {reader.AllFiles.Count}");
        Console.WriteLine($"Resolved   : {reader.AllFiles.Count(f => f.IsResolved)}");
        Console.WriteLine();
        Console.WriteLine("Folders (recursive, capped):");
        WalkFolder(reader.Root, 0, 25);
        Console.WriteLine();
        Console.WriteLine("First 10 resolved files:");
        foreach (var f in reader.AllFiles.Where(f => f.IsResolved).Take(10))
            Console.WriteLine($"  {f.NameHash:X8}  +0x{f.Offset:X8}  {f.Size,10}  {f.FullPath}");

        // Extraction sanity check: pull the smallest resolved file and dump
        // the first bytes so we can eyeball it.
        var sample = reader.AllFiles.Where(f => f.IsResolved && f.Size > 4)
            .OrderBy(f => f.Size).First();
        byte[] bytes = reader.ExtractFile(sample);
        Console.WriteLine();
        Console.WriteLine($"Extracted sample: {sample.FullPath}  ({bytes.Length} bytes)");
        Console.Write("  first 32 bytes hex: ");
        for (int i = 0; i < Math.Min(32, bytes.Length); i++) Console.Write($"{bytes[i]:X2} ");
        Console.WriteLine();
        Console.Write("  first 32 bytes asc: ");
        for (int i = 0; i < Math.Min(32, bytes.Length); i++)
        {
            byte b = bytes[i];
            Console.Write(b >= 32 && b < 127 ? (char)b : '.');
        }
        Console.WriteLine();
        return 0;
    }

    private static int WalkFolder(PK2Folder f, int depth, int budget)
    {
        if (budget <= 0) return budget;
        string indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}'{f.Name}' (files={f.Files.Count}, sub={f.Subfolders.Count})");
        budget--;
        foreach (var sub in f.Subfolders)
        {
            budget = WalkFolder(sub, depth + 1, budget);
            if (budget <= 0) break;
        }
        return budget;
    }

    private static string SanitizeFolderPath(string folderPath)
    {
        var parts = folderPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? string.Empty
            : Path.Combine(parts.Select(SanitizeFileName).ToArray());
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.TrimEnd('\\', '/');
    }
}
