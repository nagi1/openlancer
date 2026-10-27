using OpenLancer.Data.Editing.Lists;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.IO;
using Xunit;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Inspector;
using LibreLancer.Utf.Anm;

namespace OpenLancer.Tests;

public sealed class AnimationCatalogTests
{
    [Fact]
    public void PreviewUsesTheFirstEventTarget()
    {
        var targets = ThnTable.FromList(new ThnText("Juni"), new ThnText("Trent"));
        var ev = ThnTable.FromList(new ThnNumber(0), new ThnNumber(0), targets);
        Assert.Equal("Juni", ThnAnimationPicker.TargetName(ev));
        Assert.StartsWith("FMBODY", ThnAnimationPreview.IdlePrefix("female_body.dfm", null));
        Assert.StartsWith("FMBODY", ThnAnimationPreview.IdlePrefix("body.dfm", "juni_head.dfm"));
        Assert.StartsWith("MLBODY", ThnAnimationPreview.IdlePrefix("male_body.dfm", null));
        Assert.Equal("", ThnAnimationPicker.TargetName(ThnTable.Empty));
        Assert.Equal("", ThnAnimationPicker.TargetName(ThnTable.FromList(new ThnNumber(0), new ThnNumber(0), ThnTable.Empty)));
    }

    [Fact]
    public void PreviewZoomMovesCloserAndHasLimits()
    {
        Assert.True(ThnAnimationPreview.Zoom(1, 1) < 1);
        Assert.True(ThnAnimationPreview.Zoom(1, -1) > 1);
        Assert.Equal(0.35f, ThnAnimationPreview.Zoom(1, 100));
        Assert.Equal(4f, ThnAnimationPreview.Zoom(1, -100));
    }

    [Fact]
    public void CharacterFilterUsesTheTargetAndAppearance()
    {
        Assert.Equal(1, ThnAnimationPicker.CharacterIndex("actor_juni", null, "female_body"));
        Assert.Equal(2, ThnAnimationPicker.CharacterIndex("actor", "TRENT", "male_body"));
        Assert.Equal(3, ThnAnimationPicker.CharacterIndex("Orilion", "male_body"));
        Assert.Equal(5, ThnAnimationPicker.CharacterIndex("female_body"));
        Assert.Equal(6, ThnAnimationPicker.CharacterIndex("male_body"));
        Assert.Equal(0, ThnAnimationPicker.CharacterIndex("unknown"));
        Assert.Equal(0, ThnAnimationPicker.CharacterIndex("walking"));
        Assert.Equal(4, ThnAnimationPicker.CharacterIndex("actor_king"));
    }

    [Fact]
    public void PreviewPanMovesInCameraSpaceAndScalesWithDistance()
    {
        var horizontal = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0), Quaternion.Identity, 2, 400);
        var vertical = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(0, 20), Quaternion.Identity, 2, 400);
        Assert.True(horizontal.X > 0);
        Assert.True(vertical.Y > 0);
        var farther = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0), Quaternion.Identity, 4, 400);
        Assert.Equal(horizontal.X * 2, farther.X, 5);
    }

    [Fact]
    public void PreviewDurationCoversTheFullMotion()
    {
        var script = new Script("motion");
        script.JointMaps.Add(new JointMap { Channel = new Channel { FrameCount = 5, Interval = 0.5f } });
        script.JointMaps.Add(new JointMap { Channel = new Channel { FrameCount = 3, Interval = 0.25f } });
        Assert.Equal(2f, ThnAnimationPreview.ClipDuration(script));
        Assert.Equal(0.1f, ThnAnimationPreview.ClipDuration(new Script("empty")));
    }

    [Fact]
    public void ReadsClipNamesFromTheActiveDataFiles()
    {
        using var ws = new TestWorkspace();
        var folder = Path.Combine(ws.Base, "DATA", "CHARACTERS", "ANIMATIONS");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "bodygenericfemale.anm"), AnimationNamesTests.Build().Bytes);
        var files = FileSystem.FromPath(ws.Base);
        var clips = AnimationCatalog.Read(files, ["DATA/CHARACTERS/ANIMATIONS/bodygenericfemale.anm"]);
        Assert.Equal(new[] { "Sc_Run", "Sc_Walk" }, clips.Select(c => c.Name));
        Assert.All(clips, c => Assert.True(AnimationCatalog.Matches(c, "Juni", "Body", "")));
    }

    [Fact]
    public void LaterRegisteredArchivesReplaceDuplicateNames()
    {
        using var ws = new TestWorkspace();
        var folder = Path.Combine(ws.Base, "DATA", "CHARACTERS", "ANIMATIONS");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "first.anm"), AnimationNamesTests.Build().Bytes);
        File.WriteAllBytes(Path.Combine(folder, "second.anm"), AnimationNamesTests.Build().Bytes);
        var files = FileSystem.FromPath(ws.Base);
        var clips = AnimationCatalog.Read(files, ["DATA/CHARACTERS/ANIMATIONS/first.anm",
            "DATA/CHARACTERS/ANIMATIONS/second.anm"]);
        Assert.Equal(2, clips.Count);
        Assert.All(clips, c => Assert.EndsWith("second.anm", c.Archive));
        Assert.Empty(AnimationCatalog.Read(files, []));
    }

    [Fact]
    public void FiltersCharacterPartAndSearchWithoutCase()
    {
        var female = new AnimationClip("Clip_Walk", "DATA/CHARACTERS/ANIMATIONS/bodygenericfemale.anm");
        var juni = new AnimationClip("Clip_Talk", "DATA/CHARACTERS/ANIMATIONS/facialjuni.anm");
        var male = new AnimationClip("Clip_Wave", "DATA/CHARACTERS/ANIMATIONS/handmale.anm");
        var trent = new AnimationClip("Clip_Face", "DATA/CHARACTERS/ANIMATIONS/facialtrent.anm");
        Assert.Equal("Body", AnimationCatalog.Part(female));
        Assert.Equal("Head", AnimationCatalog.Part(juni));
        Assert.Equal("Hands", AnimationCatalog.Part(male));
        Assert.True(AnimationCatalog.Matches(female, "Juni", "Body", "WALK"));
        Assert.True(AnimationCatalog.Matches(juni, "Juni", "Head", "talk"));
        Assert.False(AnimationCatalog.Matches(juni, "Trent", "Head", ""));
        Assert.False(AnimationCatalog.Matches(female, "Juni", "Hands", ""));
        Assert.False(AnimationCatalog.Matches(male, "Juni", "All", ""));
        Assert.True(AnimationCatalog.Matches(male, "Trent", "Hands", "wave"));
        Assert.False(AnimationCatalog.Matches(trent, "Orillion", "Head", ""));
        Assert.True(AnimationCatalog.Matches(male, "All", "All", ""));
        Assert.True(AnimationCatalog.Matches(female, "Juni", "Body", "walk"));
        Assert.Equal("Walk", AnimationCatalog.Label("Sc_FMBODY_WLKG_TEST"));
    }
}
