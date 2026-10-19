using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The key that a text field edits: a key of a section and one of its lines or all of them, or a path of an element of
/// a cutscene.
/// </summary>
/// <param name="Owner">The section (<see cref="SectionRef"/>) or the element of a cutscene (<see cref="ThnRef"/>).</param>
/// <param name="Name">The INI key, or the path in the element.</param>
/// <param name="Line">The index of the line that the field edits, or <see cref="AllLines"/>.</param>
/// <remarks>Two keys are equal when they have the same owner, line and name. The name of an INI key ignores letter
/// case, as INI keys do. A path of a cutscene does not, because the record keys of a cutscene are case-sensitive.</remarks>
internal readonly record struct FieldKey(object Owner, string Name, int Line)
{
    /// <summary>The line value of a field that edits all lines of its key.</summary>
    public const int AllLines = -1;

    private StringComparison NameComparison =>
        Owner is SectionRef ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool Equals(FieldKey other) =>
        Owner.Equals(other.Owner) && Line == other.Line && Name.Equals(other.Name, NameComparison);

    public override int GetHashCode() =>
        HashCode.Combine(Owner, Line, Owner is SectionRef ? StringComparer.OrdinalIgnoreCase.GetHashCode(Name) : Name.GetHashCode());
}
