# GodotCharacters

People's bodies for Godot .NET games: Synty's POLYGON characters and Synty's Animation Base Locomotion made into
models and animation libraries on Godot's humanoid profile, and a body that wears a model and moves it by its speed.
The art is not here (Synty's licence forbids redistribution); a game points the import at its own copy of the packs
and keeps what the import makes out of its repository.

- `CharacterImport` (`src/CharacterImport.cs`): from an `ImportPlan` (models, attachments, animation sets, palettes;
  source files by absolute path) it copies the sources under the plan's output folder with import settings beside
  them, has Godot import them (a second Godot process, `--import`), then gathers each animation set into one
  `AnimationLibrary` and makes each palette a material. Every skeleton is retargeted to the humanoid profile
  (`BoneMaps/`: the packs' models end the right hand's finger bones in `_2`, the animation files in `_1`, so each has
  its map), with the silhouette fix on and tracks of unmapped bones removed. A clip that travels names its root-motion
  version; its natural speed (the root's way over the ground over its time, in hip heights per second, as retargeted
  positions are) is kept on the library as metadata (`CharacterPaths.SpeedsMeta`). A clip the packs lack can come
  from something the game already imports onto the humanoid profile with its own bone map (an animation library or a
  scene, by the clip's name in it: `Take`). `CharacterPaths` says where everything goes.
- `Body` (`src/Body.cs`): a node that wears a `Body.Look` (the imported model, which of its meshes show, parts hung on
  bones, a material) and plays `Body.Moves` (idle, the locomotion clips slowest first, jump, fall, land) from a
  library. Its owner sets `Speed` (metres per second over the ground), `Grounded`, calls `Jump()`, and may `Hold` any
  clip of the library (sitting, say). Moving, it plays the locomotion clip nearest the speed, as much faster or slower
  as the speed asks: the natural speed times the skeleton's motion scale is how fast that model's feet go. Parts hang
  on a bone by the skin's bind pose, as the artist's rig had the bone. It faces -Z. Without the imported files it
  shows a capsule.

## Using it

The library is a submodule at `external/godot-characters` (its scripts are found there: `res://external/godot-characters`).
Its sources are compiled into the game's own assembly, so Godot finds `Body` as a script: a project that removes
`external/**` from its sources adds them back:

```xml
<Compile Include="external/godot-characters/src/**/*.cs" />
```

The import runs from a SceneTree script that builds the game's plan from its own data:

```csharp
new CharacterImport(new ImportPlan
{
    Output = "res://Assets/Synty/Characters",
    Models = { new("worker", "/path/to/SM_Chr_Worker_Male_01.fbx") },
    Attachments = { new("hair", "/path/to/SM_Chr_Hair_Male_06.fbx") },
    AnimationSets = { new("masculine", new ImportPlan.Clip[]
    {
        new("idle", ".../A_Idle_Standing_Masc.fbx", Loop: true),
        new("walk", ".../A_Walk_F_Masc.fbx", Loop: true, ".../A_Walk_F_RootMotion_Masc.fbx"),
    }) },
    Palettes = { new("palette_0", ".../Texture_01_A.png", ".../Normal_01.png", 2048) },
}).Run();
```

and a body wears what it made:

```csharp
var body = new Body();
AddChild(body);
string output = "res://Assets/Synty/Characters";
body.Wear(
    new Body.Look(CharacterPaths.Model(output, "worker"), null,
        new[] { new Body.Mount(CharacterPaths.Attachment(output, "hair"), "Head", Transform3D.Identity) },
        CharacterPaths.Material(output, "palette_0")),
    new Body.Moves(CharacterPaths.Animations(output, "masculine"), "idle", new[] { "walk", "run", "sprint" }, "jump", "fall", "land"));
// each frame
body.Speed = groundSpeed;
body.Grounded = IsOnFloor();
```
