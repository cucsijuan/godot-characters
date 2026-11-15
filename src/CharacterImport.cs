using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace GodotCharacters;

/// <summary>
/// Makes a game's characters from source files (Synty's POLYGON characters and animations, FBX): every skeleton is
/// retargeted to Godot's humanoid profile, so any clip plays on any model. It works in two passes inside one run:
/// the sources are copied under <see cref="ImportPlan.Output"/> with import settings written beside them, Godot
/// imports them (a second Godot process, <c>--import</c>), and then each animation set is gathered into one
/// <see cref="AnimationLibrary"/> with every moving clip's natural speed, and each palette made a material.
/// Run it from a <c>--headless --script</c>; everything it writes is generated.
/// </summary>
public sealed class CharacterImport
{
    private readonly ImportPlan _plan;
    private readonly Action<string> _log;

    public CharacterImport(ImportPlan plan, Action<string> log = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _log = log ?? (m => GD.Print($"[characters] {m}"));
    }

    public void Run()
    {
        Stage();
        SourceFiles.ImportWithGodot(_log);
        foreach (ImportPlan.AnimationSet set in _plan.AnimationSets)
            BuildLibrary(set);
        foreach (ImportPlan.Palette palette in _plan.Palettes)
            BuildMaterial(palette);
        _log("done");
    }

    // --- First pass: the sources and their import settings -------------------------------------------------------

    private void Stage()
    {
        int copied = 0;
        foreach (ImportPlan.Model m in _plan.Models)
            copied += SourceFiles.Copy(m.File, CharacterPaths.Model(_plan.Output, m.Name), SceneSettings(BoneMaps.Models));
        foreach (ImportPlan.Attachment a in _plan.Attachments)
            copied += SourceFiles.Copy(a.File, CharacterPaths.Attachment(_plan.Output, a.Name), SceneSettings(null));
        foreach (ImportPlan.AnimationSet set in _plan.AnimationSets)
            foreach (ImportPlan.Clip clip in set.Clips.Where(c => c.Take == null))
            {
                copied += SourceFiles.Copy(clip.File, CharacterPaths.Clip(_plan.Output, set.Name, clip.Name), SceneSettings(BoneMaps.Animations));
                if (clip.RootMotionFile != null)
                    copied += SourceFiles.Copy(clip.RootMotionFile, CharacterPaths.RootMotionClip(_plan.Output, set.Name, clip.Name), SceneSettings(BoneMaps.Animations));
            }
        foreach (ImportPlan.Palette p in _plan.Palettes)
        {
            copied += SourceFiles.Copy(p.Albedo, CharacterPaths.Texture(_plan.Output, p.Albedo), SourceFiles.TextureSettings(p.Size, normalMap: false));
            if (p.Normal != null)
                copied += SourceFiles.Copy(p.Normal, CharacterPaths.Texture(_plan.Output, p.Normal), SourceFiles.TextureSettings(p.Size, normalMap: true));
        }
        _log($"{copied} source files copied or changed");
    }

    private static class BoneMaps
    {
        public const string Models = CharacterPaths.Library + "/BoneMaps/synty_polygon_models.tres";
        public const string Animations = CharacterPaths.Library + "/BoneMaps/synty_polygon_animations.tres";
    }

    private static string SceneSettings(string boneMap)
    {
        var s = new StringBuilder();
        s.Append("[remap]\n\nimporter=\"scene\"\n\n[params]\n\n");
        s.Append("animation/import=true\nanimation/fps=30\nanimation/trimming=true\nanimation/remove_immutable_tracks=true\n");
        s.Append("meshes/ensure_tangents=true\nmeshes/generate_lods=true\nmeshes/create_shadow_meshes=true\nskins/use_named_skins=true\n");
        s.Append("fbx/importer=0\nfbx/embedded_image_handling=0\nfbx/naming_version=2\n");
        s.Append("_subresources={");
        if (boneMap != null)
            s.Append("\n\"nodes\": {\n\"PATH:Skeleton3D\": {\n")
                .Append($"\"retarget/bone_map\": Resource(\"{boneMap}\"),\n")
                .Append("\"retarget/rest_fixer/fix_silhouette/enable\": true,\n")
                .Append("\"retarget/remove_tracks/unmapped_bones\": true\n}\n}\n");
        s.Append("}\n");
        return s.ToString();
    }

    // --- Second pass: libraries and materials --------------------------------------------------------------------

    private void BuildLibrary(ImportPlan.AnimationSet set)
    {
        var library = new AnimationLibrary();
        var speeds = new Godot.Collections.Dictionary<string, float>();
        foreach (ImportPlan.Clip clip in set.Clips)
        {
            Animation animation = clip.Take != null
                ? TakeAnimation(clip.File, clip.Take)
                : TakeAnimation(CharacterPaths.Clip(_plan.Output, set.Name, clip.Name));
            animation.LoopMode = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            library.AddAnimation(clip.Name, animation);
            if (clip.RootMotionFile != null)
            {
                float speed = NaturalSpeed(TakeAnimation(CharacterPaths.RootMotionClip(_plan.Output, set.Name, clip.Name)));
                speeds[clip.Name] = speed;
                _log($"{set.Name}/{clip.Name}: {animation.Length:0.00} s, {speed:0.000} hip heights per second");
            }
        }
        library.SetMeta(CharacterPaths.SpeedsMeta, speeds);
        Save(library, CharacterPaths.Animations(_plan.Output, set.Name));
    }

    /// <summary>A clip by name from what the game imports itself: an animation library, or a scene with an AnimationPlayer.</summary>
    private static Animation TakeAnimation(string path, string name)
    {
        Resource source = ResourceLoader.Load(path, null, ResourceLoader.CacheMode.Replace)
            ?? throw new InvalidOperationException($"{path} was not imported");
        if (source is AnimationLibrary library)
            return library.HasAnimation(name)
                ? (Animation)library.GetAnimation(name).Duplicate()
                : throw new InvalidOperationException($"{path} has no animation '{name}'");
        if (source is not PackedScene scene)
            throw new InvalidOperationException($"{path} is neither an animation library nor a scene");
        Node root = scene.Instantiate();
        try
        {
            foreach (AnimationPlayer player in root.FindChildren("*", nameof(AnimationPlayer), true, false).OfType<AnimationPlayer>())
                if (player.HasAnimation(name))
                    return (Animation)player.GetAnimation(name).Duplicate();
            throw new InvalidOperationException($"{path} has no animation '{name}'");
        }
        finally
        {
            root.Free();
        }
    }

    private static Animation TakeAnimation(string scenePath)
    {
        var scene = ResourceLoader.Load<PackedScene>(scenePath, null, ResourceLoader.CacheMode.Replace)
            ?? throw new InvalidOperationException($"{scenePath} was not imported");
        Node root = scene.Instantiate();
        try
        {
            AnimationPlayer player = root.FindChildren("*", nameof(AnimationPlayer), true, false) is { Count: > 0 } found
                ? (AnimationPlayer)found[0]
                : throw new InvalidOperationException($"{scenePath} has no animation");
            string[] names = player.GetAnimationList();
            if (names.Length != 1)
                throw new InvalidOperationException($"{scenePath} has {names.Length} animations, one is expected");
            return (Animation)player.GetAnimation(names[0]).Duplicate();
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// How fast a root-motion clip travels: the root's way over the ground from its first key to its last, over the
    /// time between them. Retargeted positions are in hip heights (the skeleton's motion scale), so the speed is
    /// too: a body multiplies it by its own skeleton's motion scale to have metres.
    /// </summary>
    private static float NaturalSpeed(Animation animation)
    {
        for (int t = 0; t < animation.GetTrackCount(); t++)
        {
            if (animation.TrackGetType(t) != Animation.TrackType.Position3D || animation.TrackGetPath(t).GetConcatenatedSubNames() != "Root")
                continue;
            int last = animation.TrackGetKeyCount(t) - 1;
            double time = animation.TrackGetKeyTime(t, last) - animation.TrackGetKeyTime(t, 0);
            Vector3 way = animation.TrackGetKeyValue(t, last).AsVector3() - animation.TrackGetKeyValue(t, 0).AsVector3();
            if (time <= 0)
                break;
            return (float)(new Vector2(way.X, way.Z).Length() / time);
        }
        throw new InvalidOperationException($"the root-motion clip {animation.ResourceName} has no root track");
    }

    private void BuildMaterial(ImportPlan.Palette palette)
    {
        var material = new StandardMaterial3D
        {
            AlbedoTexture = ResourceLoader.Load<Texture2D>(CharacterPaths.Texture(_plan.Output, palette.Albedo), null, ResourceLoader.CacheMode.Replace),
            Roughness = palette.Roughness,
        };
        if (palette.Normal != null)
        {
            material.NormalEnabled = true;
            material.NormalTexture = ResourceLoader.Load<Texture2D>(CharacterPaths.Texture(_plan.Output, palette.Normal), null, ResourceLoader.CacheMode.Replace);
        }
        Save(material, CharacterPaths.Material(_plan.Output, palette.Name));
    }

    private void Save(Resource resource, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!);
        Error e = ResourceSaver.Save(resource, path);
        if (e != Error.Ok)
            throw new IOException($"saving {path}: {e}");
        _log($"saved {path}");
    }
}

/// <summary>What a game asks <see cref="CharacterImport"/> to make. Source files are absolute paths.</summary>
public sealed class ImportPlan
{
    /// <summary>The res:// folder everything goes in (a game keeps it out of its repository when the sources' licence asks).</summary>
    public required string Output { get; init; }

    public List<Model> Models { get; init; } = new();
    public List<Attachment> Attachments { get; init; } = new();
    public List<AnimationSet> AnimationSets { get; init; } = new();
    public List<Palette> Palettes { get; init; } = new();

    /// <summary>A character model: a skinned body on the POLYGON rig.</summary>
    public sealed record Model(string Name, string File);

    /// <summary>A mesh without a skeleton that hangs on a bone (hair, a helmet), modelled in that bone's frame.</summary>
    public sealed record Attachment(string Name, string File);

    public sealed record AnimationSet(string Name, IReadOnlyList<Clip> Clips);

    /// <summary>
    /// A clip, in place; <paramref name="RootMotionFile"/> is the same move travelling, to measure its speed. With
    /// <paramref name="Take"/>, the clip is not a Synty file to import but an animation of that name in something
    /// the game already imports onto the humanoid profile with its own bone map (<paramref name="File"/>, a res://
    /// path to an animation library or a scene): a clip from another set, say.
    /// </summary>
    public sealed record Clip(string Name, string File, bool Loop, string RootMotionFile = null, string Take = null);

    /// <summary>A texture set every model can wear: its colours, its normal map (or null), the side it is made at.</summary>
    public sealed record Palette(string Name, string Albedo, string Normal, int Size, float Roughness = 0.85f);
}

/// <summary>Where <see cref="CharacterImport"/> puts what it makes, so the game finds it.</summary>
public static class CharacterPaths
{
    /// <summary>This library, as games hold it (a submodule at external/godot-characters).</summary>
    public const string Library = "res://external/godot-characters";

    /// <summary>Metadata of an animation library: clip name to natural speed in hip heights per second.</summary>
    public const string SpeedsMeta = "speeds";

    public static string Model(string output, string name) => $"{output}/Models/{name}.fbx";
    public static string Attachment(string output, string name) => $"{output}/Attachments/{name}.fbx";
    public static string Clip(string output, string set, string clip) => $"{output}/Clips/{set}/{clip}.fbx";
    public static string RootMotionClip(string output, string set, string clip) => $"{output}/Clips/{set}/{clip}_root_motion.fbx";
    public static string Animations(string output, string set) => $"{output}/Animations/{set}.res";
    /// <summary>A texture, by its source file's name: palettes that share a file (a normal map) share the texture.</summary>
    public static string Texture(string output, string sourceFile) => $"{output}/Textures/{System.IO.Path.GetFileName(sourceFile)}";
    public static string Material(string output, string palette) => $"{output}/Materials/{palette}.tres";
}
