using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using LibreLancer;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.Thn;
using LibreLancer.Thn.Events;
using LibreLancer.World;
using LibreLancer.World.Components;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.DataTests;

/// <summary>
/// A cutscene without a renderer for the clock tests (section 8.4 of the M8 design), in the pattern of
/// <c>RetailThnReplay</c> of the engine tests. It makes the scene objects as <c>ConstructEntities</c> makes them
/// without spawned objects (ThnScriptInstance.cs:63-313). A step runs in the order of <c>Cutscene._Update</c>: the
/// events and the processors, then the scene objects, then the characters (Cutscene.cs:418-431).
/// </summary>
/// <remarks>
/// <para>The player leaves out the events that need the cutscene, a renderer or a sound manager. SET_CAMERA and
/// START_FOG_PROP_ANIM call the cutscene (SetCameraEvent.cs:30, StartFogPropAnimEvent.cs:61). START_PSYS and
/// START_PSYS_PROP_ANIM need a particle renderer. START_SOUND and START_AUDIO_PROP_ANIM need a sound.</para>
/// <para>A compound has no model, as in a cutscene that does not spawn objects. So a hardpoint connection to a compound
/// logs an error and does nothing. A character always gets its costume, because no retail <c>userprops.Actor</c> names
/// an entity of its own script.</para>
/// <para>The player stops before the end of the script, because the end calls the cutscene too
/// (ThnScriptInstance.cs:348-356).</para>
/// </remarks>
internal sealed class HeadlessCutscenePlayer : ICutscenePlayer
{
    private readonly ThnScriptInstance instance;
    private readonly Dictionary<string, ThnSceneObject> objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, DfmSkeletonManager Skeleton, AnimationComponent Animation)> characters = [];
    private bool disposed;

    public HeadlessCutscenePlayer(ThnScript script, GameDataManager data, ResourceManager resources)
    {
        var kept = new ThnScript { Duration = script.Duration };
        kept.Events.AddRange(script.Events.Where(e => e is not (SetCameraEvent or StartFogPropAnimEvent or
            StartPSysEvent or StartPSysPropAnimEvent or StartSoundEvent or StartAudioPropAnimEvent)));
        LeftOut = script.Events.Count - kept.Events.Count;
        instance = new ThnScriptInstance(null!, kept);

        foreach (var (name, entity) in script.Entities)
        {
            if ((entity.ObjectFlags & ThnObjectFlags.Reference) == ThnObjectFlags.Reference)
                continue;
            var obj = new ThnSceneObject
            {
                Name = name, Translate = entity.Position ?? Vector3.Zero, Rotate = entity.Rotation, Entity = entity
            };
            switch (entity.Type)
            {
                case EntityTypes.Camera:
                    obj.Camera = new ThnCameraProps();
                    obj.Camera.FovH = entity.FovH ?? obj.Camera.FovH;
                    obj.Camera.AspectRatio = entity.HVAspect ?? obj.Camera.AspectRatio;
                    obj.Camera.Znear = entity.NearPlane ?? obj.Camera.Znear;
                    obj.Camera.Zfar = entity.FarPlane ?? obj.Camera.Zfar;
                    break;
                case EntityTypes.Light:
                    obj.Light = new DynamicLight
                    {
                        LightGroup = entity.LightGroup, Active = entity.LightProps!.On, Light = entity.LightProps.Render
                    };
                    obj.LightDir = obj.Light.Light.Direction;
                    obj.Light.Light.Direction = Vector3.Transform(obj.Light.Light.Direction, obj.Rotate);
                    break;
                case EntityTypes.Marker:
                    obj.Object = new GameObject { Name = new ObjectName("Marker"), Nickname = "" };
                    break;
                case EntityTypes.Deformable:
                    obj.Actor = entity.Actor;
                    obj.Object = Character(name, entity, data, resources);
                    break;
            }
            obj.Object?.SetLocalTransform(new Transform3D(entity.Position ?? Vector3.Zero, entity.Rotation));
            objects[name] = obj;
        }
        instance.Objects = objects;

        // Cutscene.SceneSetup runs the events at 0 s before the first step (Cutscene.cs:351-354).
        instance.Update(0);
    }

    /// <summary>The events that the player leaves out.</summary>
    public int LeftOut { get; }

    public int Objects => objects.Count;

    public int Characters => characters.Count;

    public double Time => instance.CurrentTime;

    /// <summary>The scene objects that are not at the pose of their entity.</summary>
    public int Moved => objects.Values.Count(o =>
        o.Translate != (o.Entity.Position ?? Vector3.Zero) || o.Rotate != o.Entity.Rotation);

    public void Step(double seconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (instance.CurrentTime + seconds > instance.Duration)
            throw new InvalidOperationException(
                $"The headless player stops before the end of the script at {instance.Duration} s.");
        instance.Update(seconds);
        foreach (var obj in objects.Values)
            obj.Update();
        foreach (var character in characters)
            character.Animation.Update(seconds, null!);
    }

    public void Dispose() => disposed = true;

    /// <summary>
    /// The state of the scene as raw bytes, with a name for each value: the script time, the pose of each scene object,
    /// the camera and light values, and the pose of each bone of each character.
    /// </summary>
    public SortedDictionary<string, byte[]> Pose()
    {
        var pose = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        Add(pose, "time", instance.CurrentTime);
        foreach (var (name, obj) in objects)
        {
            Add(pose, $"{name}.translate", obj.Translate);
            Add(pose, $"{name}.rotate", obj.Rotate);
            if (obj.Object != null)
                Add(pose, $"{name}.object", obj.Object.LocalTransform);
            if (obj.Camera is { } camera)
                Add(pose, $"{name}.camera", new Vector4(camera.FovH, camera.AspectRatio, camera.Znear, camera.Zfar));
            if (obj.Light is { } light)
            {
                Add(pose, $"{name}.light", light.Light);
                Add(pose, $"{name}.light.active", light.Active);
            }
        }
        foreach (var (name, skeleton, _) in characters)
        {
            Add(pose, $"{name}.floor", skeleton.FloorHeight);
            AddBones(pose, $"{name}.body", skeleton.BodySkinning);
            AddBones(pose, $"{name}.head", skeleton.HeadSkinning);
            AddBones(pose, $"{name}.left", skeleton.LeftHandSkinning);
            AddBones(pose, $"{name}.right", skeleton.RightHandSkinning);
        }
        return pose;
    }

    /// <summary>A character as <c>ConstructEntities</c> makes it (ThnScriptInstance.cs:253-272).</summary>
    private GameObject Character(string name, ThnEntity entity, GameDataManager data, ResourceManager resources)
    {
        var costume = data.Items.Costumes.Get(entity.Template) ??
                      throw new InvalidOperationException($"The costume {entity.Template} of {name} does not exist.");
        var skeleton = new DfmSkeletonManager(costume.Body.LoadModel(resources)!, costume.Head?.LoadModel(resources),
            costume.LeftHand?.LoadModel(resources), costume.RightHand?.LoadModel(resources))
        {
            FloorHeight = entity.FloorHeight
        };
        var character = new GameObject { RenderComponent = new CharacterRenderer(skeleton) };
        var animation = new AnimationComponent(character, data.GetCharacterAnimations());
        character.AnimationComponent = animation;
        character.AddComponent(animation);
        characters.Add((name, skeleton, animation));
        return character;
    }

    private static void AddBones(SortedDictionary<string, byte[]> pose, string part, DfmSkinning? skinning)
    {
        foreach (var (bone, instance) in skinning?.Bones ?? [])
        {
            var name = $"{part}.{bone}";
            Add(pose, $"{name}.local", instance.LocalTransform);
            Add(pose, $"{name}.rotation", instance.Rotation);
            Add(pose, $"{name}.translation", instance.Translation);
            Add(pose, $"{name}.ik", instance.IKRotation);
        }
    }

    private static void Add<T>(SortedDictionary<string, byte[]> pose, string name, T value) where T : unmanaged =>
        pose.Add(name, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref value)).ToArray());
}
