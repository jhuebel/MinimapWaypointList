namespace MinimapWaypointList
{
    /// <summary>
    /// Persisted in ModConfig/MinimapWaypointList.json. Editing the file requires a
    /// restart to take effect; the in-game "/mwl maxwaypoints &lt;count&gt;" command
    /// updates and saves it immediately instead.
    /// </summary>
    public class MinimapWaypointListConfig
    {
        /// <summary>
        /// Maximum number of markers to list below the minimap, nearest first.
        /// </summary>
        public int MaxMarkersShown { get; set; } = 10;
    }
}
