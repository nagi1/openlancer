using System.Collections.Generic;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private AssetCatalog? assets;
    private PropGroupStore? propGroups;

    /// <summary>The assets of the edit view for the Assets panel (milestone M23).</summary>
    public AssetCatalog Assets => assets ??= new AssetCatalog(this);

    /// <summary>The prop groups of the active mod and of the mods that load before it (milestone M23).</summary>
    internal PropGroupStore PropGroupStore => propGroups ??= new PropGroupStore(this);

    /// <summary>The mods that load before the active mod, in load order.</summary>
    internal IReadOnlyList<Mod> LowerMods => lower;

    /// <summary>
    /// How the active mod changes a section of W, or null when W has no such section: <c>IsNew</c> when the lower layer
    /// does not have the section, and <c>IsChanged</c> when W differs from the lower layer (decision D245 of the M3
    /// design). It copies no line, so the Assets panel can ask for each row.
    /// </summary>
    internal (bool IsNew, bool IsChanged)? ChangeState(SectionRef target)
    {
        if (!TryFind(target, out var document, out var index))
            return null;
        var lower = document.BaseSection(document.IdentityAt(index));
        return (lower == null, lower == null || !ReferenceEquals(lower, document.Working[index]));
    }
}
