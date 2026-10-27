using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.GameData;
using LibreLancer.Data.Schema;
using LibreLancer.Thn;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Inspector;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The animation picker of a START_MOTION event without ImGui (section 12.1 of the M8b design): which row gets the
/// picker, the groups and the filter of the list, the labels, the character of the clip preview, the idle and the
/// cache. The clip and file names are made up.
/// </summary>
public sealed class ThnAnimationPickerTests
{
    private const string BodyMale = @"DATA\characters\animations\body_m.anm";
    private const string HeadMale = @"DATA\characters\animations\head_m.anm";
    private const string BodyFemale = @"DATA\characters\animations\body_f.anm";
    private const string Shared = @"DATA\characters\animations\shared.anm";
    private const string Hands = @"DATA\characters\animations\hands.anm";
    private const string Mod = @"DATA\characters\animations\mod.anm";

    private const string FemaleIdle = "Sc_FMBODY_STND_IDLE_a";
    private const string HandClip = "Sc_hand_a";
    private const string MaleIdle = "Sc_MLBODY_STND_IDLE_a";
    private const string MaleWalk = "Sc_MLBODY_WALK_a";
    private const string MaleNod = "Sc_MLHEAD_NOD_a";
    private const string ModClip = "Sc_mod_a";
    private const string SharedClip = "Sc_shared_a";

    /// <summary>Risk R5 of the M8b design: a COMPOUND plays the scripts of its model, so only a DEFORMABLE target gets
    /// the picker. A target that names no entity, another key and another event keep the text row.</summary>
    [Fact]
    public void OnlyTheAnimationOfACharacterGetsThePicker()
    {
        Assert.True(ThnAnimationPicker.Applies(EventTypes.StartMotion, "props.animation", "DEFORMABLE"));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, "props.animation", "COMPOUND"));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, "props.animation", null));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, "props.start_time", "DEFORMABLE"));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartSound, "props.animation", "DEFORMABLE"));
        Assert.False(ThnAnimationPicker.Applies(null, "props.animation", "DEFORMABLE"));
    }

    /// <summary>A word fits only at the start of a part in capitals. So a word inside a part and the name of a character
    /// give no label, and a name without a word gets no label.</summary>
    [Fact]
    public void ALabelComesFromAWordAtTheStartOfAPartInCapitals()
    {
        Assert.Equal("Walk", AnimationClips.Label("Sc_MLBODY_WALK_000LV_TEST"));
        Assert.Equal("Walk", AnimationClips.Label("Sc_FMBODY_WLKG_TEST"));
        Assert.Equal("Gesture", AnimationClips.Label("Sc_FMBODY_CHRB_GESTL_TEST"));
        Assert.Equal("Sit", AnimationClips.Label("Sc_MLBODY_SITCL_TEST"));
        Assert.Equal("Idle", AnimationClips.Label("Sc_MLBODY_STND_IDLE_test"));
        // TURN inside RETURNL and the character name Walker give no label.
        Assert.Equal("", AnimationClips.Label("Sc_MLBODY_STND_RETURNL_TEST"));
        Assert.Equal("", AnimationClips.Label("Sc_dx_s001_Walker"));
        Assert.Equal("", AnimationClips.Label("Sc_MLHEAD_MOTION_TEST"));
        // A word before another word in the list wins.
        Assert.Equal("Walk", AnimationClips.Label("Sc_TALK_WALK"));
    }

    /// <summary>
    /// Decision D119: the clips of the skeletons of the costume of the target come first, then the other clips. Each group
    /// is sorted by name. A target without a costume gets one group without a header, and a value outside the list gets
    /// the "Missing" row first.
    /// </summary>
    [Fact]
    public void TheSkeletonsOfTheCostumeOfTheTargetComeFirst()
    {
        var clips = Clips();

        var model = clips.Model("Sc_typo", Set("BODY_M1", "head_m1"), "Trent");

        Assert.Equal(ListRowKind.Missing, model.Rows[0].Kind);
        Assert.Equal(0, model.Current);
        var entries = model.Rows.Skip(1).ToList();
        Assert.Equal([MaleIdle, MaleWalk, MaleNod, SharedClip, FemaleIdle, HandClip, ModClip], entries.Select(r => r.Value));
        Assert.Equal(Enumerable.Repeat("Skeletons of Trent", 4).Concat(Enumerable.Repeat(AnimationClips.OtherSkeletons, 3)),
            entries.Select(r => r.Group));

        var plain = clips.Model("sc_mlhead_NOD_a", null, "Trent");
        Assert.Equal(clips.Clips.Select(c => c.Name), plain.Rows.Select(r => r.Value));
        Assert.All(plain.Rows, r => Assert.Null(r.Group));
        Assert.Equal(MaleNod, plain.Rows[plain.Current].Value);
        Assert.Equal(["Idle", "", "Idle", "Walk", "Nod", "", ""], plain.Rows.Select(r => r.Note));
    }

    /// <summary>The body part filter shows the clips of the files of the skeletons with that part. Other shows the clips
    /// of the files that no skeleton lists.</summary>
    [Fact]
    public void TheBodyPartFilterUsesTheSkeletonsOfTheFile()
    {
        var clips = Clips();
        var model = clips.Model("", null, "Trent");

        string[] Shown(ClipPart part) =>
            model.Visible("", r => clips.Shows(r, part)).Select(i => model.Rows[i].Value).ToArray();

        Assert.Equal(model.Rows.Count, Shown(ClipPart.All).Length);
        Assert.Equal([FemaleIdle, MaleIdle, MaleWalk, SharedClip], Shown(ClipPart.Body));
        Assert.Equal([MaleNod, SharedClip], Shown(ClipPart.Head));
        Assert.Equal([HandClip], Shown(ClipPart.Hands));
        Assert.Equal([ModClip], Shown(ClipPart.Other));
        Assert.Equal(SkeletonParts.Body | SkeletonParts.Head, clips.PartsOf(clips.Find(SharedClip.ToUpperInvariant())!));
    }

    [Fact]
    public void ThePaneNamesTheSkeletonsOfTheFile()
    {
        var clips = Clips();

        Assert.Equal("Skeletons: male body, male head", clips.SkeletonText(clips.Find(SharedClip)!));
        Assert.Equal("Skeletons: hands", clips.SkeletonText(clips.Find(HandClip)!));
        Assert.Equal("No skeleton of bodyparts.ini lists this file.", clips.SkeletonText(clips.Find(ModClip)!));
        Assert.True(clips.Contains(ModClip.ToUpperInvariant()));
        Assert.False(clips.Contains("Sc_typo"));
    }

    /// <summary>The clip preview plays on the character that the user chose, else on the target of the event, else on the
    /// first character of the script.</summary>
    [Fact]
    public void TheClipPreviewPlaysOnTheChosenCharacterOrTheTarget()
    {
        string[] characters = ["Trent", "Juni", "Hatcher"];

        Assert.Equal("Juni", AnimationClips.PreviewOn(characters, null, "juni"));
        Assert.Equal("Hatcher", AnimationClips.PreviewOn(characters, "HATCHER", "Juni"));
        Assert.Equal("Juni", AnimationClips.PreviewOn(characters, "gone", "Juni"));
        Assert.Equal("Trent", AnimationClips.PreviewOn(characters, null, "Not_a_character"));
        Assert.Null(AnimationClips.PreviewOn([], null, "Juni"));
    }

    /// <summary>A head or a hand clip plays over the standing idle of a body skeleton of the sex of the body. A body clip
    /// gets no idle. Without a sex, the idle has the sex of the skeletons of the clip.</summary>
    [Fact]
    public void AHeadClipPlaysOverTheIdleOfTheSexOfTheBody()
    {
        var clips = Clips();
        var nod = clips.Find(MaleNod)!;

        Assert.Equal(MaleIdle, clips.Idle(FLGender.male));
        Assert.Equal(FemaleIdle, clips.Idle(FLGender.female));
        Assert.Null(clips.Idle(FLGender.unset));
        Assert.Equal(FemaleIdle, clips.IdleFor(nod, FLGender.female));
        Assert.Equal(MaleIdle, clips.IdleFor(nod, FLGender.unset));
        Assert.Equal(MaleIdle, clips.IdleFor(clips.Find(HandClip)!, FLGender.male));
        Assert.Null(clips.IdleFor(clips.Find(MaleWalk)!, FLGender.female));
        Assert.Null(clips.IdleFor(clips.Find(SharedClip)!, FLGender.male));
    }

    [Fact]
    public void ACostumeGivesTheNicknamesOfItsBodyParts()
    {
        var costume = new Costume
        {
            Body = new Bodypart { Nickname = "body_m1" },
            Head = new Bodypart { Nickname = "head_m1" },
            RightHand = new Bodypart { Nickname = "hand_r" },
        };

        var names = AnimationClips.Bodyparts(costume)!;

        Assert.True(names.SetEquals(["body_m1", "head_m1", "hand_r"]));
        Assert.Contains("BODY_M1", names);
        Assert.Null(AnimationClips.Bodyparts(null));
    }

    /// <summary>The cache reads the clips once for each game data. Reset forgets them, so the next profile reads its own
    /// data. A read error and a list without clips give the reason, and the row keeps its text field.</summary>
    [Fact]
    public void TheCacheReadsOnceForEachGameDataAndResetForgetsIt()
    {
        var reads = 0;
        var cache = new AnimationClipsCache<object>(_ =>
        {
            reads++;
            return Clips();
        });
        var data = new object();

        Assert.NotNull(cache.Get(data, out var reason));
        Assert.Null(reason);
        Assert.Same(cache.Get(data, out _), cache.Get(data, out _));
        Assert.Equal(1, reads);
        cache.Reset();
        Assert.NotNull(cache.Get(data, out _));
        Assert.Equal(2, reads);
        Assert.NotNull(cache.Get(new object(), out _));
        Assert.Equal(3, reads);
        Assert.Null(cache.Get(null, out reason));
        Assert.Equal("The game data is not loaded.", reason);

        var empty = new AnimationClipsCache<object>(_ => new AnimationClips([], []));
        Assert.Null(empty.Get(data, out reason));
        Assert.Equal("The editor found no animation clips in the game data.", reason);
        var broken = new AnimationClipsCache<object>(_ => throw new InvalidDataException("The file is not a UTF file."));
        Assert.Null(broken.Get(data, out reason));
        Assert.Equal("The editor cannot read the animation files: The file is not a UTF file.", reason);
    }

    /// <summary>
    /// Clips of a male body, a male head, a female body, a file of both male skeletons, hands, and a mod file that no
    /// skeleton lists. The body files have a standing idle each.
    /// </summary>
    private static AnimationClips Clips()
    {
        AnimationClip[] clips =
        [
            new(FemaleIdle, BodyFemale), new(HandClip, Hands), new(MaleIdle, BodyMale), new(MaleWalk, BodyMale),
            new(MaleNod, HeadMale), new(ModClip, Mod), new(SharedClip, Shared),
        ];
        AnimationSkeleton[] skeletons =
        [
            new([BodyMale, Shared], FLGender.male, SkeletonParts.Body, Set("body_m1")),
            new([HeadMale, Shared], FLGender.male, SkeletonParts.Head, Set("head_m1")),
            new([BodyFemale], FLGender.female, SkeletonParts.Body, Set("body_f1")),
            new([Hands], FLGender.unset, SkeletonParts.Hands, Set("hand_l", "hand_r")),
        ];
        return new AnimationClips(clips, skeletons);
    }

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
