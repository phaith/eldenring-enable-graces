namespace EldenRingEnableGraces;

/// <summary>
/// One row of Elden Ring's BonfireWarpParam — i.e. one entry in the
/// "Site of Grace" warp list.
/// </summary>
public class GraceRow
{
    /// <summary>
    /// Event-flag value written when a grace is "force-enabled" by checking its
    /// box. Configurable at runtime via the "Enable flag" box in the UI (persisted
    /// to settings.json); defaults to 76800.
    /// </summary>
    public static uint EnableEventFlagId { get; set; } = 76800;

    /// <summary>Param row ID (e.g. 100000).</summary>
    public int Id { get; init; }

    /// <summary>Human-readable name from Smithbox's English row-name metadata.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The grace's event-flag value as captured the first time the regulation
    /// was opened (from the sidecar). Unchecking a row restores this.
    /// </summary>
    public uint OriginalEventFlagId { get; set; }

    /// <summary>
    /// The event-flag value that will be written on save. Toggled by the
    /// checkbox: checked → <see cref="EnableEventFlagId"/>; unchecked →
    /// <see cref="OriginalEventFlagId"/>.
    /// </summary>
    public uint CurrentEventFlagId { get; set; }

    /// <summary>True when this row is currently force-enabled (Current == EnableEventFlagId).</summary>
    public bool IsEnabled => CurrentEventFlagId == EnableEventFlagId;
}
