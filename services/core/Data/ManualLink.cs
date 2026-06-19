using GameHub.Shared;

namespace GameHub.Core.Data;

/// <summary>
/// Stores a manual override that forces a specific platform game entry
/// to be linked to a specific UnifiedGame, bypassing the automatic slug-based matching.
///
/// Use cases:
///   - Two games with different titles are actually the same game (merge)
///     e.g. "The Witcher 3: Wild Hunt - GOTY" (GOG) and "The Witcher 3: Wild Hunt" (Steam)
///   - Two games with the same title are actually different games (split)
///     e.g. "DOOM" (1993) and "DOOM" (2016) would generate the same slug
///
/// During sync, the UnificationService checks ManualLinks FIRST before
/// falling back to slug-based matching. Once a ManualLink exists, automatic
/// matching will never override it.
/// </summary>
public class ManualLink : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Which platform this override applies to (Steam, PSN, GOG, Epic).</summary>
    public Platform Platform { get; set; }

    /// <summary>The platform-specific game ID (e.g. Steam AppId "730", PSN TitleId "CUSA00001_00").</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>The UnifiedGame this item should be linked to.</summary>
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
}
