namespace OpenLancer.Data.Editing;

/// <summary>How a line of a color holds its channels. The color picker of the Inspector writes the channels in the form
/// of the key.</summary>
public enum ColorForm
{
    /// <summary>Numbers from 0 to 255. The engine divides each value by 255. The picker writes whole numbers.</summary>
    Byte,

    /// <summary>Numbers from 0 to 1, for example a <c>FloatColor</c> key or the light of a cutscene. The engine reads
    /// each value as it is. The picker writes numbers with 3 decimals.</summary>
    Unit,
}
