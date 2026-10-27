using System;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.Thn;
using LibreLancer.World;
using OpenLancer.Editor.Inspector;
using Xunit;

namespace OpenLancer.DataTests;

public sealed class AnimationPreviewRetailTests
{
    [Fact]
    public void PreviewAdvancesTheBonesOfARealCharacter()
    {
        GameDataPaths.RequireBase();
        var game = RetailGame.Instance;
        var costume = game.Data.Items.Costumes.First(c => c.Body != null);
        var skeleton = new DfmSkeletonManager(costume.Body.LoadModel(game.Resources)!,
            costume.Head?.LoadModel(game.Resources), costume.LeftHand?.LoadModel(game.Resources),
            costume.RightHand?.LoadModel(game.Resources));
        var candidate = game.Data.GetCharacterAnimations().Scripts.Values.SelectMany(script =>
            script.JointMaps.Select(joint => (script, joint))).First(x =>
        {
            if (!x.joint.Channel.HasOrientation || x.joint.Channel.FrameCount < 2 ||
                x.joint.Channel.Duration < 0.1f || !skeleton.BodySkinning.Bones.ContainsKey(x.joint.ChildName))
                return false;
            var channel = x.joint.Channel;
            var a = 0;
            var b = 0;
            var start = channel.QuaternionAtTime(0, ref a);
            var middle = channel.QuaternionAtTime(channel.Duration * 0.5f, ref b);
            return MathF.Abs(Quaternion.Dot(start, middle)) < 0.9999f;
        });
        var bone = skeleton.BodySkinning.Bones[candidate.joint.ChildName];
        var actor = new GameObject { RenderComponent = new CharacterRenderer(skeleton) };
        var character = new ThnSceneObject { Name = "preview", Object = actor, Rotate = Quaternion.Identity };
        skeleton.StartScript(candidate.script, 0, 1, ThnAnimationPreview.ClipDuration(candidate.script));
        ThnAnimationPreview.Advance(skeleton, character, 0);
        var before = bone.Rotation;
        ThnAnimationPreview.Advance(skeleton, character, candidate.joint.Channel.Duration * 0.5);
        Assert.True(MathF.Abs(Quaternion.Dot(before, bone.Rotation)) < 0.9999f);
    }
    [Fact]
    public void PreviewAppliesRootMovement()
    {
        GameDataPaths.RequireBase();
        var game = RetailGame.Instance;
        var costume = game.Data.Items.Costumes.First(c => c.Body != null);
        var skeleton = new DfmSkeletonManager(costume.Body.LoadModel(game.Resources)!,
            costume.Head?.LoadModel(game.Resources), costume.LeftHand?.LoadModel(game.Resources),
            costume.RightHand?.LoadModel(game.Resources));
        var candidate = game.Data.GetCharacterAnimations().Scripts.Values.SelectMany(script =>
            script.ObjectMaps.Select(map => (script, map))).First(x =>
        {
            if (!string.Equals(x.map.ParentName, "Root", StringComparison.OrdinalIgnoreCase) ||
                !x.map.Channel.HasPosition || x.map.Channel.FrameCount < 2 || x.map.Channel.Duration < 0.1f)
                return false;
            var channel = x.map.Channel;
            var a = 0;
            var b = 0;
            var start = channel.PositionAtTime(0, ref a);
            var middle = channel.PositionAtTime(channel.Duration * 0.5f, ref b);
            return Vector3.Distance(start, middle) > 0.01f;
        });
        var actor = new GameObject { RenderComponent = new CharacterRenderer(skeleton) };
        var character = new ThnSceneObject { Name = "preview", Object = actor, Rotate = Quaternion.Identity };
        skeleton.StartScript(candidate.script, 0, 1, ThnAnimationPreview.ClipDuration(candidate.script));
        ThnAnimationPreview.Advance(skeleton, character, 0);
        var before = character.Translate;
        ThnAnimationPreview.Advance(skeleton, character, candidate.map.Channel.Duration * 0.5);
        Assert.True(Vector3.Distance(before, character.Translate) > 0.01f);
    }

}
