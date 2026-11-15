using System;
using System.IO;
using Godot;

namespace GodotCharacters;

/// <summary>
/// The first pass of an import from source files (FBX, PNG): each is copied into the project with its import settings
/// written beside it, then a second Godot process imports what changed. <see cref="CharacterImport"/> uses it, and so
/// can a game for its other sources (a building kit, say), so that every import stages its files the same way.
/// </summary>
public static class SourceFiles
{
    /// <summary>
    /// Copies <paramref name="from"/> (an absolute path) to <paramref name="to"/> (res://) when it changed, and writes
    /// its import settings when they differ; 1 if anything changed. Godot adds its own lines (uid, dest_files) to an
    /// import file, so the settings are written again only when the file no longer holds them, and Godot then
    /// imports the file again.
    /// </summary>
    public static int Copy(string from, string to, string settings)
    {
        if (!File.Exists(from))
            throw new FileNotFoundException($"source file not found: {from}");
        string target = ProjectSettings.GlobalizePath(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        int changed = 0;
        var source = new FileInfo(from);
        var copy = new FileInfo(target);
        if (!copy.Exists || copy.Length != source.Length || copy.LastWriteTimeUtc != source.LastWriteTimeUtc)
        {
            File.Copy(from, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
            changed = 1;
        }
        string import = target + ".import";
        if (!File.Exists(import) || !File.ReadAllText(import).Contains(settings))
        {
            File.WriteAllText(import, settings);
            changed = 1;
        }
        return changed;
    }

    /// <summary>A texture's import settings: compressed, with mipmaps, at most <paramref name="size"/> on a side.</summary>
    public static string TextureSettings(int size, bool normalMap) =>
        "[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\n\n[params]\n\n" +
        "compress/mode=2\ncompress/high_quality=false\n" +
        $"compress/normal_map={(normalMap ? 1 : 2)}\n" +
        "mipmaps/generate=true\n" +
        $"process/size_limit={size}\n" +
        "detect_3d/compress_to=0\n";

    /// <summary>Has a second Godot process import the project's new and changed files (<c>--import</c>).</summary>
    public static void ImportWithGodot(Action<string> log)
    {
        string project = ProjectSettings.GlobalizePath("res://");
        var output = new Godot.Collections.Array();
        log("Godot imports the sources");
        int code = OS.Execute(OS.GetExecutablePath(), new[] { "--headless", "--path", project, "--import" }, output, readStderr: true);
        if (code != 0)
        {
            foreach (Variant line in output)
                GD.PrintErr(line.AsString());
            throw new InvalidOperationException($"Godot's import failed ({code})");
        }
    }
}
