namespace MinimapWaypointList
{
    /// <summary>
    /// Persisted in ModConfig/MinimapWaypointList.json. Editing the file requires a
    /// restart to take effect; the in-game ".mwl maxwaypoints &lt;count&gt;" command
    /// (client-side commands use a "." prefix, not "/") updates and saves it
    /// immediately instead.
    /// </summary>
    public class MinimapWaypointListConfig
    {
        /// <summary>
        /// Maximum number of markers to list below the minimap, nearest first.
        /// </summary>
        public int MaxMarkersShown { get; set; } = 10;

        /// <summary>
        /// "expand" (default): the list widens past the minimap to fit the longest
        /// marker name in full. "truncate": the list never gets wider than the
        /// minimap, and long names are shortened with an ellipsis (hover one to
        /// scroll and read it in full).
        /// </summary>
        public string LongNames { get; set; } = "expand";
    }
}
