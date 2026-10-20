using System.Collections.Generic;
using System.Globalization;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The detail codes of the mission commands with their error kinds and their messages (section 4.7 of the M6b design).
/// Each message uses the strict form of Simplified Technical English: what is wrong, why the editor or the game cannot
/// use it, and the fix. The studio and the agent tools show the same text.
/// </summary>
/// <remarks>The codes after <see cref="UnknownCommand"/> are not in the table of the design. The commands need them
/// for refusals that the table of section 4.9 names without a code.</remarks>
public static class MissionErrors
{
    public const string NotEditable = "NotEditable";
    public const string Busy = "Busy";
    public const string ScriptChanged = "ScriptChanged";
    public const string ScriptNotFound = "ScriptNotFound";
    public const string TriggerNotFound = "TriggerNotFound";
    public const string ItemNotFound = "ItemNotFound";
    public const string RowNotFound = "RowNotFound";
    public const string RowChanged = "RowChanged";
    public const string ReadOnlySection = "ReadOnlySection";
    public const string RepeatedNickname = "RepeatedNickname";
    public const string NameInvalid = "NameInvalid";
    public const string NameTaken = "NameTaken";
    public const string NameIdTaken = "NameIdTaken";
    public const string UnknownKey = "UnknownKey";
    public const string WrongPlace = "WrongPlace";
    public const string NotAKey = "NotAKey";
    public const string ValueKind = "ValueKind";
    public const string ValueMissing = "ValueMissing";
    public const string WordNotInSet = "WordNotInSet";
    public const string WholeNumber = "WholeNumber";
    public const string PatchText = "PatchText";
    public const string RequiredBy = "RequiredBy";
    public const string NoShipBuildFile = "NoShipBuildFile";
    public const string NewErrors = "NewErrors";
    public const string SectionChanged = "SectionChanged";
    public const string KeyLine = "KeyLine";
    public const string SharedBuildRename = "SharedBuildRename";
    public const string SharedBuildChange = "SharedBuildChange";
    public const string UnknownCommand = "UnknownCommand";

    // Codes that the table of the design does not have.
    public const string NoSuchValue = "NoSuchValue";
    public const string NoCondition = "NoCondition";
    public const string BadPlace = "BadPlace";
    public const string NotAnAction = "NotAnAction";
    public const string LinkNotFound = "LinkNotFound";
    public const string NotALink = "NotALink";
    public const string NotInBase = "NotInBase";
    public const string NoBaseDifference = "NoBaseDifference";
    public const string NotWholeFile = "NotWholeFile";
    public const string PathInUse = "PathInUse";
    public const string NotTextId = "NotTextId";
    public const string MissingLines = "MissingLines";
    public const string NotMovable = "NotMovable";
    public const string NotAnItem = "NotAnItem";
    public const string NotADirective = "NotADirective";
    public const string BadJson = "BadJson";
    public const string UnknownField = "UnknownField";

    internal static EditError NotEditableError(string reason) =>
        new(EditErrorKind.ReadOnly, NotEditable, $"The editor cannot change missions now. {reason}");

    internal static EditError BusyError() =>
        new(EditErrorKind.Busy, Busy, "A drag or a text edit is not complete. Complete it, then try again.");

    internal static EditError ScriptChangedError(string script) =>
        new(EditErrorKind.StaleRevision, ScriptChanged,
            $"Script {script} changed after you read it. Read the script again, then try again.") { Target = Script(script) };

    internal static EditError ScriptNotFoundError(string script) =>
        new(EditErrorKind.NotFound, ScriptNotFound,
            $"The game data and the mods have no mission script {script}. Pick a script from the Missions tab.")
        {
            Target = Script(script),
        };

    internal static EditError TriggerNotFoundError(string script, string name, string hint = "") =>
        new(EditErrorKind.NotFound, TriggerNotFound,
            $"Script {script} has no trigger '{name}'. Pick a trigger of this script.{hint}");

    internal static EditError ItemNotFoundError(string script, string kind, string name, string hint = "") =>
        new(EditErrorKind.NotFound, ItemNotFound,
            $"Script {script} has no {kind} '{name}'. Pick {MissionChecks.WithArticle(kind)} of this script.{hint}");

    internal static EditError RowNotFoundError(string owner, int row) =>
        new(EditErrorKind.NotFound, RowNotFound,
            $"{owner} has no line {Number(row)}. Read the item again, then try again.");

    internal static EditError RowChangedError(string owner, int row, string expected) =>
        new(EditErrorKind.StaleRevision, RowChanged,
            $"Line {Number(row)} of {owner} is not '{expected}' now. The script changed after you read it. Read the item " +
            "again, then try again.");

    internal static EditError ReadOnlySectionError(string section, string reason) =>
        new(EditErrorKind.Refused, ReadOnlySection, $"The editor cannot change {section}. {reason}");

    internal static EditError RepeatedNicknameError(string type, string name) =>
        new(EditErrorKind.Refused, RepeatedNickname,
            $"Two [{type}] sections have the nickname '{name}'. A patch cannot find one of them. Change the script in a " +
            "whole file of the mod.");

    internal static EditError NameInvalidError(string name, int max) =>
        new(EditErrorKind.InvalidValue, NameInvalid,
            $"'{name}' is not a valid name. Use letters, digits, '_' and '-', with at most {Number(max)} characters. A " +
            "name cannot be a number.");

    internal static EditError NameTakenError(string script, string kind, string name) =>
        new(EditErrorKind.NameTaken, NameTaken, $"Script {script} already has {MissionChecks.WithArticle(kind)} '{name}'. " +
                                                "Pick another name.");

    internal static EditError NameIdTakenError(string name, string other) =>
        new(EditErrorKind.NameTaken, NameIdTaken,
            $"The game gives '{name}' the same id as trigger '{other}'. The game then keeps only the first of the two " +
            "triggers. Pick another name.");

    internal static EditError UnknownKeyError(string key) =>
        new(EditErrorKind.InvalidValue, UnknownKey,
            $"'{key}' is not an action or a condition of the game. Pick one from the list.");

    internal static EditError WrongPlaceError(string key, bool isCondition) =>
        new(EditErrorKind.InvalidValue, WrongPlace, isCondition
            ? $"{key} is a condition. A trigger has one condition. Use \"Set condition\"."
            : $"{key} is an action. Use \"Add action\".");

    internal static EditError NotAKeyError(string key, string type) =>
        new(EditErrorKind.InvalidValue, NotAKey, $"The game does not read '{key}' in a {type} section. Pick a field of the page.");

    internal static EditError ValueKindError(string value, string kind, string label, string what) =>
        new(EditErrorKind.InvalidValue, ValueKind, $"'{value}' is not {kind}. {label} needs {what}.");

    internal static EditError ValueMissingError(string label) =>
        new(EditErrorKind.InvalidValue, ValueMissing,
            $"{label} needs a value, because a later value is set. Give {label} a value.");

    internal static EditError WordNotInSetError(string value, string label, IEnumerable<string> words) =>
        new(EditErrorKind.InvalidValue, WordNotInSet,
            $"'{value}' is not a word of {label}. Use one of these words: {string.Join(", ", words)}.");

    internal static EditError WholeNumberError(string label) =>
        new(EditErrorKind.InvalidValue, WholeNumber, $"The game reads only the whole part of {label}. Use a whole number.");

    internal static EditError PatchTextError() =>
        new(EditErrorKind.InvalidValue, PatchText,
            "A value cannot hold a comma or a semicolon. The game splits or cuts the line there. Remove the character.");

    internal static EditError RequiredByError(string kind, string name, int count, string users) =>
        new(EditErrorKind.Refused, RequiredBy,
            $"{Capital(kind)} '{name}' is in use by {Number(count)} {users}. Remove them or change them first.");

    internal static EditError NoShipBuildFileError() =>
        new(EditErrorKind.Refused, NoShipBuildFile,
            "The mission has no ship build file. Set \"Ship build file\" on the Mission page first.");

    internal static EditError NewErrorsError(int count, string first) =>
        new(EditErrorKind.NewErrors, NewErrors, count == 1
            ? $"The edit adds 1 error, so the editor did not apply it. {first}"
            : $"The edit adds {Number(count)} errors, so the editor did not apply it. {first}");

    internal static EditError SectionChangedError(string section) =>
        new(EditErrorKind.StaleRevision, SectionChanged,
            $"{section} changed after the edit that this inverse reverts. Read the item again, then change it back with " +
            "the mission commands.");

    internal static EditError KeyLineError(string key) =>
        new(EditErrorKind.InvalidArgument, KeyLine, $"{key} is the name of the item. Use Rename to change it.");

    internal static EditError SharedBuildRenameError(string name) =>
        new(EditErrorKind.Refused, SharedBuildRename,
            $"Ship build '{name}' is in a shared file that random encounters can use. The editor cannot rename it in a " +
            "mission.");

    internal static EditError SharedBuildChangeError(string name) =>
        new(EditErrorKind.NeedsConfirmation, SharedBuildChange,
            $"Ship build '{name}' is in a shared file. Random encounters can use it, so this change can change them too. " +
            "Use \"This ship only\", or confirm the change.");

    internal static EditError UnknownCommandError(string kind) =>
        new(EditErrorKind.InvalidArgument, UnknownCommand, $"'{kind}' is not a mission command.");

    internal static EditError BadJsonError(string reason) =>
        new(EditErrorKind.InvalidArgument, BadJson, $"The request is not a valid mission batch: {reason} Correct the JSON.");

    internal static EditError UnknownFieldError(string kind, string field) =>
        new(EditErrorKind.InvalidArgument, UnknownField,
            $"Command '{kind}' has no field '{field}'. Remove the field, or correct its name.");

    internal static EditError NoSuchValueError(string key, string name, IEnumerable<string> names) =>
        new(EditErrorKind.InvalidArgument, NoSuchValue,
            $"{key} has no value '{name}'. Use one of these values: {string.Join(", ", names)}.");

    internal static EditError NoConditionError(string trigger) =>
        new(EditErrorKind.NotFound, NoCondition,
            $"Trigger '{trigger}' has no condition. Use \"Set condition\" first.");

    internal static EditError BadPlaceError() =>
        new(EditErrorKind.InvalidArgument, BadPlace,
            "An action cannot go before the nickname line or before the condition, because the game ignores it there. " +
            "Pick a place after the condition.");

    internal static EditError NotAnActionError(string owner, int row) =>
        new(EditErrorKind.InvalidArgument, NotAnAction,
            $"Line {Number(row)} of {owner} is not an action. Pick an action line.");

    internal static EditError LinkNotFoundError(string source, string target, string kind) =>
        new(EditErrorKind.NotFound, LinkNotFound,
            $"Trigger '{source}' has no {kind} link to '{target}'. Read the trigger again, then try again.");

    internal static EditError NotALinkError(string owner, int row) =>
        new(EditErrorKind.InvalidArgument, NotALink,
            $"Line {Number(row)} of {owner} names no trigger. Pick a line that starts, stops or watches a trigger.");

    internal static EditError NotInBaseError(string section) =>
        new(EditErrorKind.Refused, NotInBase,
            $"The active mod adds {section}, so the game data has no lines to go back to. Remove the section instead.");

    internal static EditError NoBaseDifferenceError(string section) =>
        new(EditErrorKind.Refused, NoBaseDifference,
            $"The base of {section} did not change after the edit. Nothing needs a decision.");

    internal static EditError NotWholeFileError(string script) =>
        new(EditErrorKind.Refused, NotWholeFile,
            $"Script {script} is not a whole file of the active mod. The editor cannot delete a script of the game data " +
            "or of another mod.");

    internal static EditError PathInUseError(string path, string reason) =>
        new(EditErrorKind.Refused, PathInUse, $"The editor cannot make a script at {path}. {reason}");

    internal static EditError NotTextIdError(string label) =>
        new(EditErrorKind.InvalidArgument, NotTextId, $"{label} is not a text id. Only a text id can take own text.");

    internal static EditError MissingLinesError(int count) =>
        new(EditErrorKind.NeedsConfirmation, MissingLines, count == 1
            ? "After this edit, 1 line names an item that is gone. The game ignores such a line. Confirm the edit, or " +
              "keep the item."
            : $"After this edit, {Number(count)} lines name an item that is gone. The game ignores such lines. Confirm " +
              "the edit, or keep the item.");

    internal static EditError NotMovableError(string type) =>
        new(EditErrorKind.Refused, NotMovable,
            $"The place of a [{type}] section in the file has no effect in the game. Only a ship or a formation section " +
            "can move.");

    internal static EditError NotAnItemError(string reference, string what) =>
        new(EditErrorKind.InvalidArgument, NotAnItem, $"'{reference}' is not {what}. Pick {what}.");

    internal static EditError NotADirectiveError(string key) =>
        new(EditErrorKind.InvalidValue, NotADirective,
            $"'{key}' is not an order of the game. Pick an order from the list.");

    private static string Script(string script) => "msn:" + script;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
