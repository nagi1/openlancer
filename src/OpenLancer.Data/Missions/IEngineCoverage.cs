namespace OpenLancer.Data.Missions;

/// <summary>What the OpenLancer engine does with a condition or an action name (section 14.8 of the M6a design).</summary>
public enum EngineMark
{
    /// <summary>The engine runs the line.</summary>
    Runs,

    /// <summary>The engine reads the line but does nothing with it.</summary>
    NoEffect,

    /// <summary>The engine stops when it loads the line, so the mission does not start.</summary>
    CannotLoad,
}

/// <summary>
/// The marks of the OpenLancer engine for the mission names (decision D526). The editor reads them from the engine at run
/// time and gives them to the index, so the data layer has no reference to the engine runtime. When the own runtime
/// replaces the fork, only the class behind this interface changes.
/// </summary>
public interface IEngineCoverage
{
    /// <summary>The mark of a condition or action name, without letter case. An unknown name runs, because the engine
    /// skips an unknown key.</summary>
    EngineMark Of(string name);
}

/// <summary>Coverage that runs each name, for a run without the engine, for example a test of the checks.</summary>
public sealed class FullCoverage : IEngineCoverage
{
    public static readonly FullCoverage Instance = new();

    public EngineMark Of(string name) => EngineMark.Runs;
}
