using System;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace MinimapWaypointList
{
    public class MinimapWaypointListModSystem : ModSystem
    {
        private const string ConfigFileName = "MinimapWaypointList.json";

        private ICoreClientAPI capi;
        private MinimapWaypointListConfig config;

        public override bool ShouldLoad(EnumAppSide side)
        {
            return side == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            this.capi = capi;
            config = LoadOrCreateConfig();

            MarkerListHud hud = new MarkerListHud(capi, config);
            capi.Gui.RegisterDialog(hud);

            // Vanilla registers its own client-side chat commands (e.g. "/map ...")
            // from level-finalize/hotkey callbacks rather than directly in
            // StartClientSide - registering that early seems to get lost.
            capi.Event.LevelFinalize += RegisterCommands;
        }

        private void RegisterCommands()
        {
            capi.ChatCommands.Create("mwl")
                .WithDescription("Minimap Waypoint List settings")
                .BeginSubCommand("maxwaypoints")
                    .WithDescription("Get or set the maximum number of waypoints shown in the minimap list")
                    .WithArgs(capi.ChatCommands.Parsers.OptionalInt("count", -1))
                    .HandleWith(OnCmdMaxWaypoints)
                .EndSubCommand()
                .BeginSubCommand("longnames")
                    .WithDescription("Get or set how long marker names are handled: \"expand\" (default) widens the list to fit them, \"truncate\" keeps the list at the minimap's width and shortens long names (hover one to scroll and read it)")
                    .WithArgs(capi.ChatCommands.Parsers.OptionalWordRange("mode", new[] { "expand", "truncate" }))
                    .HandleWith(OnCmdLongNames)
                .EndSubCommand();
        }

        private TextCommandResult OnCmdMaxWaypoints(TextCommandCallingArgs args)
        {
            if (args.Parsers[0].IsMissing)
            {
                return TextCommandResult.Success($"Showing up to {config.MaxMarkersShown} waypoint(s) in the minimap list.");
            }

            int count = Math.Max(0, (int)args.Parsers[0].GetValue());
            config.MaxMarkersShown = count;
            SaveConfig();
            return TextCommandResult.Success($"Now showing up to {count} waypoint(s) in the minimap list.");
        }

        private TextCommandResult OnCmdLongNames(TextCommandCallingArgs args)
        {
            if (args.Parsers[0].IsMissing)
            {
                return TextCommandResult.Success($"Long marker names currently: {config.LongNames}.");
            }

            config.LongNames = (string)args.Parsers[0].GetValue();
            SaveConfig();
            return TextCommandResult.Success($"Long marker names set to: {config.LongNames}.");
        }

        private MinimapWaypointListConfig LoadOrCreateConfig()
        {
            string path = Path.Combine(GamePaths.ModConfig, ConfigFileName);
            MinimapWaypointListConfig loaded = null;

            try
            {
                if (File.Exists(path))
                {
                    loaded = JsonConvert.DeserializeObject<MinimapWaypointListConfig>(File.ReadAllText(path));
                }
            }
            catch (Exception e)
            {
                capi.Logger.Error("[MinimapWaypointList] Failed to read {0}, falling back to defaults: {1}", path, e);
            }

            loaded ??= new MinimapWaypointListConfig();
            if (loaded.MaxMarkersShown < 0) loaded.MaxMarkersShown = 0;
            config = loaded;
            SaveConfig();

            return loaded;
        }

        private void SaveConfig()
        {
            string path = Path.Combine(GamePaths.ModConfig, ConfigFileName);
            try
            {
                GamePaths.EnsurePathExists(GamePaths.ModConfig);
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch (Exception e)
            {
                capi.Logger.Error("[MinimapWaypointList] Failed to write {0}: {1}", path, e);
            }
        }
    }
}
