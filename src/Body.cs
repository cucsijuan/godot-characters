using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GodotCharacters;

/// <summary>
/// A character's body: a model made by <see cref="CharacterImport"/>, dressed as a <see cref="Look"/> says, moving
/// as its owner says. The owner tells it how fast it goes over the ground (<see cref="Speed"/>), whether it stands
/// on something (<see cref="Grounded"/>), when it jumps (<see cref="Jump"/>) and, for anything else, a clip to hold
/// (<see cref="Hold"/>: sitting, say). It picks the clip itself: standing still, the idle; moving, the locomotion
/// clip whose natural speed is nearest, played as much faster or slower as the speed asks, so the feet keep to the
/// ground. The same body serves the player, the people the game runs and the ones another machine sends; it faces
/// -Z, as Godot's nodes do. Without the model's files it shows a capsule of <see cref="Height"/>.
/// </summary>
public partial class Body : Node3D
{
    /// <summary>The model, what of it shows, what hangs on it and its paint (res:// paths).</summary>
    public sealed record Look(string Model, IReadOnlyList<string> Parts, IReadOnlyList<Mount> Mounts, string Paint);

    /// <summary>A part hung on a bone (humanoid profile names: Head, LeftHand...), moved by <paramref name="Offset"/> from where it was modelled.</summary>
    public sealed record Mount(string Scene, string Bone, Transform3D Offset);

    /// <summary>The clips by what they are for (names in the animation library); <paramref name="Locomotion"/> from slowest to fastest.</summary>
    public sealed record Moves(string Animations, string Idle, IReadOnlyList<string> Locomotion, string Jump, string Fall, string Land);

    private const string Library = "moves";
    private const float Fade = 0.2f;
    private const float AirFade = 0.12f;
    /// <summary>Under this share of the slowest clip's speed, the body stands.</summary>
    private const float StandBelow = 0.15f;
    /// <summary>How much nearer (as a ratio) another locomotion clip must be before the body changes to it.</summary>
    private const float Stickiness = 1.12f;
    private const float AirAfter = 0.12f;
    private const float LandAfter = 0.35f;

    private Moves _moves;
    private Node3D _model;
    private Skeleton3D _skeleton;
    private AnimationPlayer _player;
    private float[] _speeds = Array.Empty<float>();
    private string _playing;
    private int _gait = -1;
    private float _air;
    private bool _jumped;
    private bool _landing;

    /// <summary>Metres per second over the ground.</summary>
    public float Speed { get; set; }

    public bool Grounded { get; set; } = true;

    /// <summary>A clip played instead of moving while set (by its name in the library); null to move again.</summary>
    public string Hold { get; set; }

    /// <summary>The capsule's height when there is no model.</summary>
    public float Height { get; set; } = 1.8f;

    /// <summary>The model's skeleton, or null without one.</summary>
    public Skeleton3D Skeleton => _skeleton;

    /// <summary>The meshes that hang on bones, by bone and scene, as <see cref="Wear"/> put them.</summary>
    public IReadOnlyList<Node3D> Mounted => _mounted;
    private readonly List<Node3D> _mounted = new();

    /// <summary>Puts on a look and the moves it plays; false (and a capsule) when the model's files are missing.</summary>
    public bool Wear(Look look, Moves moves)
    {
        Undress();
        _moves = moves;
        if (!ResourceLoader.Exists(look.Model) || !ResourceLoader.Exists(moves.Animations))
        {
            GD.PushWarning($"Body: {look.Model} or {moves.Animations} is missing (run the character import); a capsule stands in");
            Capsule();
            return false;
        }

        // The models face +Z, as the humanoid profile has them.
        _model = ResourceLoader.Load<PackedScene>(look.Model).Instantiate<Node3D>();
        _model.Rotation = new Vector3(0, Mathf.Pi, 0);
        AddChild(_model);
        _skeleton = _model.FindChildren("*", nameof(Skeleton3D), true, false).OfType<Skeleton3D>().FirstOrDefault();
        foreach (AnimationPlayer imported in _model.FindChildren("*", nameof(AnimationPlayer), true, false).OfType<AnimationPlayer>())
            imported.QueueFree();

        if (look.Parts is { Count: > 0 })
            foreach (MeshInstance3D mesh in Meshes(_model))
                mesh.Visible = look.Parts.Contains(mesh.Name.ToString());
        if (_skeleton != null)
            foreach (Mount mount in look.Mounts ?? Array.Empty<Mount>())
                Hang(mount);
        if (look.Paint != null && ResourceLoader.Load<Material>(look.Paint) is { } paint)
            foreach (MeshInstance3D mesh in Meshes(_model))
                mesh.MaterialOverride = paint;

        var library = ResourceLoader.Load<AnimationLibrary>(moves.Animations);
        _player = new AnimationPlayer { Name = "Moves", RootNode = new NodePath("..") };
        _player.AddAnimationLibrary(Library, library);
        _model.AddChild(_player);

        // Natural speeds are in hip heights per second: this skeleton's motion scale makes them metres.
        var speeds = library.HasMeta(CharacterPaths.SpeedsMeta)
            ? library.GetMeta(CharacterPaths.SpeedsMeta).AsGodotDictionary<string, float>()
            : new Godot.Collections.Dictionary<string, float>();
        float scale = _skeleton?.MotionScale ?? 1;
        _speeds = moves.Locomotion.Select(clip => speeds.TryGetValue(clip, out float s) ? s * scale : 0).ToArray();
        for (int i = 0; i < _speeds.Length; i++)
            if (_speeds[i] <= 0)
                throw new InvalidOperationException($"Body: {moves.Animations} has no natural speed for '{moves.Locomotion[i]}'");
        Play(moves.Idle, 0);
        return true;
    }

    /// <summary>The natural speed of each locomotion clip on this body, metres per second (empty without a model).</summary>
    public IReadOnlyList<float> NaturalSpeeds => _speeds;

    /// <summary>The clip playing (its name in the library), or null.</summary>
    public string Playing => _playing;

    public void Jump() => _jumped = true;

    public override void _Process(double delta)
    {
        if (_player == null)
            return;
        float dt = (float)delta;
        _air = Grounded ? 0 : _air + dt;

        if (Hold != null)
        {
            Play(Hold, Fade);
            _player.SpeedScale = 1;
            _gait = -1;
            return;
        }

        if (_air > AirAfter || (_jumped && !Grounded))
        {
            bool rising = _jumped && _playing == _moves.Jump && _player.IsPlaying();
            if (_jumped && _playing != _moves.Jump && _air < AirAfter * 2)
                rising = Play(_moves.Jump, AirFade);
            if (!rising)
                Play(_moves.Fall, AirFade);
            _player.SpeedScale = 1;
            _landing = _air > LandAfter;
            _gait = -1;
            return;
        }
        _jumped = false;

        if (_landing)
        {
            // A long fall ends on its feet, unless the body runs on at once.
            if (Speed < _speeds[0] * StandBelow * 2 && (_playing != _moves.Land || _player.IsPlaying()))
            {
                Play(_moves.Land, AirFade);
                _player.SpeedScale = 1;
                return;
            }
            _landing = false;
        }

        if (Speed < _speeds[0] * StandBelow)
        {
            Play(_moves.Idle, Fade);
            _player.SpeedScale = 1;
            _gait = -1;
            return;
        }
        _gait = Gait(Speed);
        Play(_moves.Locomotion[_gait], Fade);
        _player.SpeedScale = Speed / _speeds[_gait];
    }

    /// <summary>The locomotion clip for a speed: the nearest by ratio, keeping the one playing unless another is clearly nearer.</summary>
    private int Gait(float speed)
    {
        int best = 0;
        float bestRatio = float.MaxValue;
        for (int i = 0; i < _speeds.Length; i++)
        {
            float ratio = Ratio(speed, _speeds[i]);
            if (ratio < bestRatio)
            {
                bestRatio = ratio;
                best = i;
            }
        }
        if (_gait >= 0 && best != _gait && Ratio(speed, _speeds[_gait]) < bestRatio * Stickiness)
            return _gait;
        return best;
    }

    private static float Ratio(float a, float b) => Mathf.Max(a, b) / Mathf.Max(Mathf.Min(a, b), 0.001f);

    /// <summary>Starts a clip unless it is the one playing; false if the library has no such clip.</summary>
    private bool Play(string clip, float fade)
    {
        if (clip == null)
            return false;
        if (clip == _playing && (_player.IsPlaying() || clip == _moves.Land || clip == _moves.Jump))
            return true;
        string name = $"{Library}/{clip}";
        if (!_player.HasAnimation(name))
            return false;
        _player.Play(name, fade);
        _playing = clip;
        return true;
    }

    /// <summary>
    /// Hangs a part on a bone. Synty's attachments are modelled in the bone's frame as the artist's rig had it;
    /// retargeting changes the bone's rest (the silhouette fix tilts bones and their axes are reset), but a skinned
    /// mesh's bind pose still takes the model's space to that frame, so its turn is the part's. With no skinned mesh
    /// on the bone, the rest is all there is.
    /// </summary>
    private void Hang(Mount mount)
    {
        int bone = _skeleton.FindBone(mount.Bone);
        if (bone < 0 || !ResourceLoader.Exists(mount.Scene))
        {
            GD.PushWarning($"Body: can't hang {mount.Scene} on '{mount.Bone}'");
            return;
        }
        var attachment = new BoneAttachment3D { Name = $"On{mount.Bone}", BoneName = mount.Bone };
        _skeleton.AddChild(attachment);
        var part = ResourceLoader.Load<PackedScene>(mount.Scene).Instantiate<Node3D>();
        part.Transform = new Transform3D(ModelledIn(bone), Vector3.Zero) * mount.Offset;
        attachment.AddChild(part);
        _mounted.Add(part);
    }

    private Basis ModelledIn(int bone)
    {
        foreach (MeshInstance3D mesh in Meshes(_skeleton))
        {
            Skin skin = mesh.Skin;
            for (int i = 0; skin != null && i < skin.GetBindCount(); i++)
                if (skin.GetBindBone(i) == bone || skin.GetBindName(i) == _skeleton.GetBoneName(bone))
                    return skin.GetBindPose(i).Basis;
        }
        return _skeleton.GetBoneGlobalRest(bone).Basis.Inverse();
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node under) =>
        under.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>();

    private void Capsule()
    {
        var look = new StandardMaterial3D { AlbedoColor = new Color("c2a173"), Roughness = 0.8f };
        _model = new Node3D { Name = "Capsule" };
        _model.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Height = Height, Radius = Height / 6, Material = look },
            Position = new Vector3(0, Height / 2, 0),
        });
        // A nose, so the way it faces shows.
        _model.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.12f, 0.2f), Material = look },
            Position = new Vector3(0, Height * 0.85f, -Height / 6),
        });
        AddChild(_model);
    }

    private void Undress()
    {
        _model?.QueueFree();
        _model = null;
        _skeleton = null;
        _player = null;
        _playing = null;
        _gait = -1;
        _speeds = Array.Empty<float>();
        _mounted.Clear();
    }
}
