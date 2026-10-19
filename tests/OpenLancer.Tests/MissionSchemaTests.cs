using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreLancer.Data.Schema.Missions;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rule data of the mission studio (sections 14.4, 14.6 and 18.1 of the M6a design): each name of the engine has a
/// row, each help text passes the STE lint, each sentence names only values of its row, and the story slot table matches
/// the storyline of the engine.
/// </summary>
public sealed partial class MissionSchemaTests
{
    private static readonly MissionSchema Schema = MissionSchema.Retail;

    [Fact]
    public void EachNameOfTheForkEnumsHasARowAPlainNameAndAGroup()
    {
        foreach (var action in Enum.GetValues<TriggerActions>())
        {
            var name = Schema.Name(action.ToString());
            Assert.True(name is { Kind: MissionNameKind.Action }, $"{action} has no action row.");
            Assert.False(string.IsNullOrWhiteSpace(name.Plain), $"{action} has no plain name.");
            Assert.True(Schema.Group(name.Group) != null, $"{action} has no group.");
        }
        foreach (var condition in Enum.GetValues<TriggerConditions>())
        {
            var name = Schema.Name(condition.ToString());
            Assert.True(name is { Kind: MissionNameKind.Condition }, $"{condition} has no condition row.");
            Assert.False(string.IsNullOrWhiteSpace(name.Plain), $"{condition} has no plain name.");
            Assert.True(Schema.Event(name.Group) != null, $"{condition} has no event.");
        }
        foreach (var command in Enum.GetValues<ObjListCommands>())
            Assert.True(Schema.Directive(command.ToString()) != null, $"{command} has no order row.");

        Assert.Equal(Enum.GetValues<TriggerActions>().Length, Schema.Actions.Count);
        Assert.Equal(Enum.GetValues<TriggerConditions>().Length, Schema.Conditions.Count);
        Assert.Equal(129, Schema.Actions.Count + Schema.Conditions.Count);
    }

    [Fact]
    public void EachHelpTextPassesTheSteLint()
    {
        var problems = Texts().SelectMany(t => Lint(t.Text).Select(p => $"{t.Where}: {p}: {t.Text}")).ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void TheLintFindsTheBrokenRules()
    {
        Assert.NotEmpty(Lint("Do this; do that."));
        Assert.NotEmpty(Lint("The game doesn't read it."));
        Assert.NotEmpty(Lint("Make sure that it's done."));
        Assert.NotEmpty(Lint("Use the robust value."));
        Assert.NotEmpty(Lint(string.Join(' ', Enumerable.Repeat("word", 26)) + "."));
        Assert.Empty(Lint("The game reads the attitude of the player. It sets it to -0.2."));
    }

    [Fact]
    public void EachSentenceNamesOnlyValuesOfItsRow()
    {
        foreach (var name in Schema.Conditions.Concat(Schema.Actions).Concat(Schema.Directives))
        {
            foreach (var sentence in name.Sentences.Concat(name.Suffixes))
            {
                foreach (var value in sentence.When.Keys.Concat(MissionSchema.Placeholders(sentence.Text)))
                    Assert.True(name.Value(value) != null, $"A sentence of {name.Name} names '{value}'.");
            }
        }
    }

    [Fact]
    public void ASentenceThatNamesAnotherValueDoesNotLoad()
    {
        var root = JsonNode.Parse(ResourceText())!;
        var condition = root["conditions"]!.AsArray().First(c => c!["sentences"] is JsonArray { Count: > 0 })!;
        condition["sentences"]![0]!["text"] = "{no_such_value} happens.";

        var error = Assert.Throws<InvalidDataException>(() => MissionSchema.Parse(root.ToJsonString()));
        Assert.Contains("no_such_value", error.Message);
    }

    /// <summary>
    /// The slot table against the storyline of the engine (section 14.6 of the M6a design). The path of each slot is the
    /// file of its story mission. The start stage is the stage <c>{mission}_loaded</c>, or the stage after it when the
    /// game skips that stage. The accept stage is the stage <c>{mission}_accepted</c>. "At once" means that no free time
    /// stage without a skip comes before the next mission.
    /// </summary>
    [Fact]
    public void TheSlotTableMatchesTheForkStoryline()
    {
        var storyline = StoryStages.Default;
        var items = storyline.Items;
        // The engine storyline gives these free time stages no skip, although the game code starts the next mission at
        // once. MF1 item 15.9 corrects the storyline (decision D549).
        string[] knownGaps = ["freetime_06_07", "freetime_09_10", "freetime_10_11"];

        Assert.Equal(storyline.Missions.Count, Schema.StorySlots.Count);
        for (var i = 0; i < Schema.StorySlots.Count; i++)
        {
            var slot = Schema.StorySlots[i];
            var mission = storyline.Missions[i];
            Assert.Equal(mission.File, slot.Path, ignoreCase: true);
            var loaded = items.FindIndex(s => s.Nickname.Equals($"{mission.Nickname}_loaded", StringComparison.OrdinalIgnoreCase));
            var accepted = items.FindIndex(s => s.Nickname.Equals($"{mission.Nickname}_accepted", StringComparison.OrdinalIgnoreCase));
            Assert.True(loaded >= 0 && accepted > loaded, slot.Slot);
            Assert.Equal(items[loaded].Skip ? loaded + 1 : loaded, slot.StartStage);
            Assert.Equal(accepted, slot.AcceptStage);
            Assert.Equal(slot.Slot.Equals("M13", StringComparison.Ordinal), slot.Next.Kind == NextMissionKind.End);

            if (slot.Next.Kind == NextMissionKind.End)
                continue;
            var next = items.FindIndex(accepted, s => s.Mission != null && !s.Mission.Equals(mission.Nickname, StringComparison.OrdinalIgnoreCase));
            var freeTime = items.Skip(accepted + 1).Take(next - accepted - 1)
                .Where(s => !s.Skip && s.Mission == null && !knownGaps.Contains(s.Nickname)).ToList();
            Assert.True(slot.Next.Kind == NextMissionKind.AtOnce == (freeTime.Count == 0), slot.Slot);
        }
    }

    [Fact]
    public void EachSlotNoteHasAText()
    {
        foreach (var note in Schema.StorySlots.SelectMany(s => s.Notes))
            Assert.True(Schema.SlotNotes.ContainsKey(note), note);
    }

    private static string ResourceText()
    {
        using var stream = typeof(MissionSchema).Assembly.GetManifestResourceStream("OpenLancer.Data.Missions.mission-schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Each text of the rule data that the editor shows: the help texts, the plain names, the labels, the
    /// sentences, the slot notes and the texts of the prefabs.</summary>
    private static IEnumerable<(string Where, string Text)> Texts()
    {
        IEnumerable<(string, string)> Help(string where, HelpText help) =>
            new[] { help.Title, help.WhatItIs, help.WhatItWrites, help.Example }.OfType<string>().Select(t => (where, t));

        foreach (var (id, help) in Schema.Help)
        {
            foreach (var text in Help($"help {id}", help))
                yield return text;
        }
        foreach (var group in Schema.Groups)
        {
            yield return ($"group {group.Id}", group.Chip);
            foreach (var text in Help($"group {group.Id}", group.Help))
                yield return text;
        }
        foreach (var group in Schema.Events)
            yield return ($"event {group.Id}", group.Label);
        foreach (var (id, note) in Schema.SlotNotes)
            yield return ($"slot note {id}", note);
        foreach (var buffer in Schema.Buffers.Values)
            yield return ($"buffer {buffer.Id}", buffer.What);
        foreach (var name in Schema.Conditions.Concat(Schema.Actions).Concat(Schema.Directives))
        {
            yield return (name.Name, name.Plain);
            foreach (var value in name.Values)
            {
                yield return ($"{name.Name} {value.Name}", value.Label);
                if (value.Help != null)
                    yield return ($"{name.Name} {value.Name}", value.Help);
            }
            foreach (var sentence in name.Sentences.Concat(name.Suffixes))
                yield return ($"{name.Name} sentence", sentence.Text);
        }
        foreach (var section in Schema.Sections)
        {
            foreach (var key in section.Keys)
            {
                yield return ($"[{section.Type}] {key.Key}", key.Label);
                if (key.Help != null)
                    yield return ($"[{section.Type}] {key.Key}", key.Help);
                foreach (var value in key.Values)
                {
                    yield return ($"[{section.Type}] {key.Key} {value.Name}", value.Label);
                    if (value.Help != null)
                        yield return ($"[{section.Type}] {key.Key} {value.Name}", value.Help);
                }
            }
        }
        foreach (var prefab in MissionPrefabs.Retail.Definitions)
        {
            yield return ($"prefab {prefab.Id}", prefab.Name);
            foreach (var text in Help($"prefab {prefab.Id}", prefab.Help))
                yield return text;
            foreach (var member in prefab.Members)
                yield return ($"prefab {prefab.Id} {member.Role}", member.Label);
            foreach (var field in prefab.Fields)
                yield return ($"prefab {prefab.Id} {field.Id}", field.Label);
            foreach (var step in prefab.Steps)
            {
                yield return ($"prefab {prefab.Id} {step.Id}", step.Name);
                yield return ($"prefab {prefab.Id} {step.Id}", step.Reason);
            }
        }
        foreach (var e in MissionPrefabs.Retail.Events)
            yield return ($"event {e.Id}", e.Label);
    }

    /// <summary>The words that STE replaces with a short common word, and the marketing words (AGENTS.md STE rules 6
    /// and 8).</summary>
    private static readonly string[] Banned =
    [
        "utilize", "leverage", "ensure", "prior to", "regarding", "obtain", "demonstrate", "additionally", "initiate",
        "seamless", "robust", "powerful", "effortless",
    ];

    /// <summary>The STE problems of a text: semicolons, contractions, banned words and sentences of more than 25
    /// words.</summary>
    private static List<string> Lint(string text)
    {
        var problems = new List<string>();
        if (text.Contains(';'))
            problems.Add("a semicolon");
        if (Contraction().Match(text) is { Success: true } contraction)
            problems.Add($"the contraction '{contraction.Value}'");
        problems.AddRange(Banned.Where(w => Regex.IsMatch(text, $@"\b{w}\b", RegexOptions.IgnoreCase))
            .Select(w => $"the word '{w}'"));
        foreach (var sentence in SentenceEnd().Split(text))
        {
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 25)
                problems.Add($"a sentence of {words} words");
        }
        return problems;
    }

    [GeneratedRegex(@"\b\w+n't\b|\w'(re|ve|ll|m|d)\b|\b(it|that|there|what|here|who|let)'s\b", RegexOptions.IgnoreCase)]
    private static partial Regex Contraction();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceEnd();
}
