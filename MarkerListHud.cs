using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace MinimapWaypointList
{
    /// <summary>
    /// A small HUD panel that attaches itself directly under (or above, depending on
    /// the minimap's screen corner) the vanilla minimap and lists the nearest waypoints
    /// as a dynamically column-sized table (icon, direction arrow, name, distance),
    /// plus a small toggle icon docked onto the minimap's own edge to show/hide it.
    /// Follows the minimap's open/closed state and screen corner automatically, and
    /// pushes the vanilla coordinate HUD (when present, top-right corner only - that
    /// HUD is not corner-configurable) to sit below the list instead of above it.
    /// </summary>
    public class MarkerListHud : HudElement
    {
        private const double RowHeight = 20;
        private const double RowGap = 6;
        private const double RowsPadding = 6;
        private const double ColumnGap = 6;
        private const double SidePadding = 6;
        private const double IconColumnWidth = 18;
        private const double ArrowColumnWidth = 20;
        private const double MinNameColumnWidth = 30;
        private const double ToggleIconSize = 20;
        private const double ToggleClearance = 4;

        private readonly MinimapWaypointListConfig config;

        private bool listExpanded = true;
        private bool? lastToggleBottomAnchored = null;
        private List<string> lastComposedFingerprint = null;
        private float lastComposedGuiScale = -1f;
        private string lastComposedLongNamesMode = null;
        private double lastDistRightEdge;

        // "truncate" mode only: which row's name is currently under the mouse (-1 if
        // none), how far it's scrolled to reveal text past the truncated width, and a
        // shared text-drawing helper for the custom-drawn name cells.
        private const double NameScrollPixelsPerSecond = 60.0;
        private int hoveredNameRow = -1;
        private double nameScrollOffset;
        private readonly TextDrawUtil textDrawUtil = new TextDrawUtil();

        /// <summary>
        /// Guid alone isn't enough to know the composed rows are still up to date -
        /// the name is baked into a static element at compose time (no cheap
        /// SetNewText path like distance/arrow have), so renaming, re-iconing or
        /// recoloring a marker needs to be caught here too, or it'd only show up
        /// whenever something else (add/remove/reorder) happened to force a recompose.
        /// </summary>
        private static List<string> Fingerprint(List<Waypoint> markers)
        {
            return markers.Select(wp => $"{wp.Guid}|{wp.Title}|{wp.Icon}|{wp.Color}").ToList();
        }

        public override double DrawOrder => 0.09;
        public override bool PrefersUngrabbedMouse => true;

        public MarkerListHud(ICoreClientAPI capi, MinimapWaypointListConfig config) : base(capi)
        {
            this.config = config;
        }

        public override bool ShouldReceiveKeyboardEvents()
        {
            return false;
        }

        public override void OnOwnPlayerDataReceived()
        {
            base.OnOwnPlayerDataReceived();
            capi.Event.RegisterGameTickListener(OnTick, 200);
        }

        /// <summary>
        /// Game tick listeners can be suspended while the game is paused (e.g. the
        /// options/settings screen in singleplayer), but rendering keeps going - so
        /// both whether we're shown at all, and where, have to be decided here every
        /// frame rather than only in OnTick. Otherwise disabling the minimap (or a
        /// GUI scale edit, which recomputes everyone's bounds) while paused would
        /// leave us open/mispositioned until a tick actually got to run again, i.e.
        /// until the menu closed.
        /// </summary>
        public override void OnRenderGUI(float deltaTime)
        {
            UpdateVisibility();
            RepositionEverything();
            UpdateNameHoverScroll(deltaTime);
            base.OnRenderGUI(deltaTime);
        }

        private bool IsMinimapVisible()
        {
            GuiDialogWorldMap minimapDlg = GetMinimapDialog();
            return minimapDlg != null && minimapDlg.IsOpened() && minimapDlg.DialogType == EnumDialogType.HUD;
        }

        private void UpdateVisibility()
        {
            if (IsMinimapVisible())
            {
                if (!IsOpened()) TryOpen();
            }
            else if (IsOpened())
            {
                TryClose();
            }
        }

        private void RepositionEverything()
        {
            GuiDialogWorldMap minimapDlg = GetMinimapDialog();
            if (minimapDlg == null || !minimapDlg.IsOpened() || minimapDlg.DialogType != EnumDialogType.HUD) return;

            ElementBounds minimapBounds = minimapDlg.SingleComposer.Bounds;
            EnumDialogArea area = minimapBounds.Alignment;

            RepositionIcon(minimapBounds, area);
            if (SingleComposer != null)
            {
                RepositionList(minimapBounds, area);
            }

            // Must run even when our own list isn't showing (collapsed, or no markers
            // visible right now) - otherwise, once the coordinate HUD had been pushed
            // down to make room for the list, it would stay pushed down after the list
            // disappeared instead of moving back up against the minimap.
            RepositionCoordinateHud(SingleComposer?.Bounds ?? minimapBounds, SingleComposer == null, area);
        }

        private GuiDialogWorldMap GetMinimapDialog()
        {
            return capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
        }

        private WaypointMapLayer GetWaypointLayer()
        {
            return capi.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers
                ?.FirstOrDefault(layer => layer is WaypointMapLayer) as WaypointMapLayer;
        }

        private GuiElementMap GetMapElement(GuiDialogWorldMap minimapDlg)
        {
            return minimapDlg?.SingleComposer?.GetElement("mapElem") as GuiElementMap;
        }

        /// <summary>
        /// Markers currently drawn on the minimap: on-screen within its viewport (or
        /// pinned, which the vanilla renderer always clamps to the edge instead of
        /// culling), nearest first. Mirrors the exact on/off-screen test
        /// WaypointMapComponent.Render uses, so this list tracks what's actually visible.
        /// </summary>
        private List<Waypoint> GetVisibleMarkers(GuiElementMap mapElem)
        {
            WaypointMapLayer layer = GetWaypointLayer();
            if (layer == null || !layer.Active || layer.ownWaypoints == null || mapElem == null)
            {
                return new List<Waypoint>();
            }

            // Note: layer.Waypoints is the server's authoritative list and is never
            // populated on the client. The client-visible set (synced down from the
            // server, already filtered to what this player is allowed to see) lands
            // in ownWaypoints instead - that's the one actually rendered on the map.
            Vec3d playerPos = capi.World.Player.Entity.Pos.XYZ;
            Vec2f viewPos = new Vec2f();

            bool OnScreen(Waypoint wp)
            {
                if (wp.Pinned) return true;
                mapElem.TranslateWorldPosToViewPos(wp.Position, ref viewPos);
                return viewPos.X >= -10f && viewPos.Y >= -10f
                    && viewPos.X <= mapElem.Bounds.OuterWidth + 10.0
                    && viewPos.Y <= mapElem.Bounds.OuterHeight + 10.0;
            }

            return layer.ownWaypoints
                .Where(wp => wp != null && OnScreen(wp))
                .OrderBy(wp => wp.Position.DistanceTo(playerPos))
                .Take(Math.Max(0, config.MaxMarkersShown))
                .ToList();
        }

        /// <summary>
        /// The game only calls OnRenderGUI on a dialog once it's already opened, so
        /// UpdateVisibility (which performs that very first TryOpen) still needs to
        /// run from here too - otherwise the panel could never open in the first
        /// place. OnRenderGUI's copy remains for the pause-immune close/reposition
        /// once we're already open.
        /// </summary>
        private void OnTick(float dt)
        {
            UpdateVisibility();

            GuiDialogWorldMap minimapDlg = GetMinimapDialog();
            if (!IsMinimapVisible()) return;

            List<Waypoint> markers = GetVisibleMarkers(GetMapElement(minimapDlg));

            // Vanilla lets the player cycle the minimap between all four screen
            // corners, and the list flips to sit above it instead of below whenever
            // it's docked to a bottom corner (see RepositionList) - so the toggle
            // icon's arrow direction needs to flip along with it, rather than always
            // pointing down.
            EnumDialogArea area = minimapDlg.SingleComposer.Bounds.Alignment;
            bool bottomAnchored = area == EnumDialogArea.LeftBottom || area == EnumDialogArea.RightBottom;
            if (Composers["toggle"] == null || lastToggleBottomAnchored != bottomAnchored)
            {
                ComposeToggleIcon(bottomAnchored);
            }

            UpdateListComposer(markers, minimapDlg.SingleComposer.Bounds.OuterWidth);
        }

        private void UpdateListComposer(List<Waypoint> markers, double minWidth)
        {
            bool wantList = listExpanded && markers.Count > 0;

            if (!wantList)
            {
                if (SingleComposer != null)
                {
                    SingleComposer.Dispose();
                    Composers.Remove("single");
                }
                lastComposedFingerprint = null;
                lastComposedGuiScale = -1f;
                lastComposedLongNamesMode = null;
                hoveredNameRow = -1;
                nameScrollOffset = 0.0;
                return;
            }

            List<string> fingerprint = Fingerprint(markers);
            bool guiScaleChanged = lastComposedGuiScale >= 0f && lastComposedGuiScale != RuntimeEnv.GUIScale;
            bool longNamesModeChanged = lastComposedLongNamesMode != null && lastComposedLongNamesMode != config.LongNames;
            if (SingleComposer == null || lastComposedFingerprint == null || guiScaleChanged || longNamesModeChanged || !fingerprint.SequenceEqual(lastComposedFingerprint))
            {
                ComposeListGui(markers, minWidth);
            }
            else
            {
                RefreshDynamicRowContent(markers);
            }
        }

        private void ComposeListGui(List<Waypoint> markers, double minWidth)
        {
            lastComposedFingerprint = Fingerprint(markers);
            lastComposedGuiScale = RuntimeEnv.GUIScale;
            lastComposedLongNamesMode = config.LongNames;
            // Row indices are about to be rebuilt from scratch, so a hover/scroll state
            // pointing at an old row index wouldn't necessarily even refer to the same
            // marker anymore.
            hoveredNameRow = -1;
            nameScrollOffset = 0.0;

            bool truncateNames = config.LongNames == "truncate";
            CairoFont nameFont = CairoFont.WhiteDetailText();
            CairoFont distFont = DistanceFont();

            // In "expand" mode, columns size to whatever the longest name/distance in
            // the current list actually needs (scaled automatically since GetTextExtents
            // measures with the same, already-GUI-scaled font used to render), not a
            // fixed guess. Distance and arrow are fixed-width so they can anchor to the
            // panel's right edge; the name column flexes with its own content.
            // In "truncate" mode the name column instead gets whatever's left over
            // after everything else is reserved, and individual names get shortened
            // (see DrawNameCell) to fit rather than growing the panel.
            string[] titles = new string[markers.Count];
            string[] distTexts = new string[markers.Count];
            double nameColWidth = MinNameColumnWidth;

            for (int i = 0; i < markers.Count; i++)
            {
                Waypoint wp = markers[i];
                titles[i] = string.IsNullOrEmpty(wp.Title) ? "?" : wp.Title;
                distTexts[i] = DistanceText(wp);
                if (!truncateNames)
                {
                    // GetTextExtents measures in already-scaled screen pixels, but
                    // ElementBounds.Fixed expects the unscaled reference units it scales
                    // itself - dividing back out is the same correction CairoFont's own
                    // AutoBoxSize applies. Skipping it under-sizes the column (and only
                    // becomes visible enough to wrap text at GUI scales other than 100%).
                    nameColWidth = Math.Max(nameColWidth, nameFont.GetTextExtents(titles[i]).Width / RuntimeEnv.GUIScale + 4.0);
                }
            }

            // "Fixed" here means it doesn't change based on which distances happen to
            // be in the current list (per the earlier request), but it still has to be
            // measured against the current font/scale - a bare guess (the previous
            // constant 46) isn't guaranteed to fit "9999m" at every GUI scale/font
            // combination, which is what clipped the text on the right.
            double distColWidth = distFont.GetTextExtents("9999m").Width / RuntimeEnv.GUIScale + 4.0;

            if (truncateNames)
            {
                double reserved = SidePadding * 2.0 + IconColumnWidth + ColumnGap * 3.0 + distColWidth + ArrowColumnWidth;
                double available = minWidth / RuntimeEnv.GUIScale - reserved;
                nameColWidth = Math.Max(MinNameColumnWidth, available);
            }

            double contentWidth = SidePadding * 2 + IconColumnWidth + ColumnGap + nameColWidth + ColumnGap + distColWidth + ColumnGap + ArrowColumnWidth;
            // minWidth (the minimap's live OuterWidth) is an already-scaled absolute
            // pixel measurement, same as the GetTextExtents values above - needs the
            // same unscaling before comparing against/feeding into unscaled bounds.
            double rowWidth = Math.Max(contentWidth, minWidth / RuntimeEnv.GUIScale);
            double totalHeight = markers.Count * RowHeight + Math.Max(0, markers.Count - 1) * RowGap + RowsPadding * 2.0;

            // Not corner-aligned: this dialog is positioned entirely by RepositionList
            // every tick. Giving it a real screen-corner Alignment would make vanilla's
            // per-corner HUD stacking (e.g. the coordinate HUD) try to stack around it.
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.None)
                .WithFixedAlignmentOffset(0.0, 0.0);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(0.0);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds listBounds = ElementBounds.Fixed(0.0, 0.0, rowWidth, totalHeight);
            bgBounds.WithChildren(listBounds);

            GuiComposer composer = capi.Gui.CreateCompo("minimapmarkerlist", dialogBounds)
                .AddGameOverlay(bgBounds)
                .BeginChildElements(bgBounds);

            double iconX = SidePadding;
            double nameX = iconX + IconColumnWidth + ColumnGap;
            double arrowX = rowWidth - SidePadding - ArrowColumnWidth;

            // Rather than trust a fixed-width box plus right text-orientation to render
            // flush against the arrow (which kept clipping the text on the right for
            // reasons that didn't show up in the math), each row's distance box is
            // sized tightly to its own text and positioned so its right edge lands on
            // this same boundary - a plain left-aligned box that's exactly as wide as
            // its content can't clip, regardless of what was wrong with the other approach.
            lastDistRightEdge = arrowX - ColumnGap;

            // GuiElementStaticText/DynamicText always draw from the top of their own
            // bounds - there's no built-in vertical centering - so we nudge each text
            // column's Y down by half the leftover space in the row ourselves.
            double nameYOffset = (RowHeight - nameFont.GetFontExtents().Height / RuntimeEnv.GUIScale) / 2.0;
            double distYOffset = (RowHeight - distFont.GetFontExtents().Height / RuntimeEnv.GUIScale) / 2.0;

            for (int i = 0; i < markers.Count; i++)
            {
                int rowIndex = i;
                Waypoint wp = markers[i];
                string title = titles[i];
                double rowY = RowsPadding + i * (RowHeight + RowGap);
                double[] tint = ColorUtil.Hex2Doubles(ColorUtil.Int2Hex(wp.Color));
                string iconKey = "wp" + StringUtil.UcFirst(string.IsNullOrEmpty(wp.Icon) ? "circle" : wp.Icon);
                double rowDistWidth = distFont.GetTextExtents(distTexts[i]).Width / RuntimeEnv.GUIScale + 4.0;
                double distX = lastDistRightEdge - rowDistWidth;

                // In "truncate" mode, nameColWidth was sized against the worst-case
                // distance width ("9999m") so every row lines up on the same grid, but
                // most distances are shorter than that - reserving the full worst case
                // left a dead strip of background between the truncated/faded name and
                // wherever this row's actual (usually narrower) distance text landed.
                // Letting the name cell claim that reclaimed space per row instead means
                // the visible/fading text runs right up to just before this row's real
                // distance text, rather than fading out early into empty space.
                double nameCellWidth = truncateNames ? Math.Max(MinNameColumnWidth, distX - ColumnGap - nameX) : nameColWidth;

                ElementBounds iconBounds = ElementBounds.Fixed(iconX, rowY, IconColumnWidth, RowHeight);
                // DrawNameCell centers its own text vertically, so its bounds don't need
                // the nameYOffset nudge the plain static-text path (below) relies on.
                ElementBounds nameBounds = ElementBounds.Fixed(nameX, truncateNames ? rowY : rowY + nameYOffset, nameCellWidth, RowHeight);
                ElementBounds distBounds = ElementBounds.Fixed(distX, rowY + distYOffset, rowDistWidth, RowHeight);
                ElementBounds arrowBounds = ElementBounds.Fixed(arrowX, rowY, ArrowColumnWidth, RowHeight);

                composer.AddStaticCustomDraw(iconBounds, (ctx, surface, b) =>
                {
                    // Static custom-draw elements share one surface across the whole
                    // composer, so the draw position must be this element's own offset
                    // within it (drawX/drawY) - not a hardcoded local (x,y), which is
                    // what caused every row's icon to land on top of row 0's.
                    capi.Gui.Icons.DrawIcon(ctx, iconKey, b.drawX + 1.0, b.drawY + 1.0, b.InnerWidth - 2.0, b.InnerHeight - 2.0, tint);
                });
                composer.AddDynamicCustomDraw(arrowBounds, (ctx, surface, b) => DrawDirectionArrow(ctx, b, wp), "arrow" + i);

                if (truncateNames)
                {
                    composer.AddDynamicCustomDraw(nameBounds, (ctx, surface, b) => DrawNameCell(ctx, b, title, rowIndex), "name" + i);
                }
                else
                {
                    composer.AddStaticText(title, nameFont, nameBounds, "name" + i);
                }

                composer.AddDynamicText(distTexts[i], distFont, distBounds, "dist" + i);
            }

            // The engine can silently recompose a composer on its own (e.g. a GUI
            // scale change recalculates everyone's bounds) as part of GuiComposer's
            // own Render() call, immediately before it draws that frame - i.e. after
            // OnRenderGUI's own reposition pass already ran. Recomposing resets our
            // manually-applied absOffsetX/Y back to the fixed (0,0) we declared them
            // with, so without also correcting position here, in direct response to
            // that recompose, the panel would render at (0,0) for that one frame.
            composer.OnComposed += RepositionEverything;

            SingleComposer?.Dispose();
            SingleComposer = composer.EndChildElements().Compose();
        }

        /// <summary>
        /// A small standalone toggle button positioned to straddle the minimap's edge
        /// (the edge facing the list), so it reads as docked onto the minimap itself
        /// rather than as part of the list panel below/above it. Its arrow points
        /// towards the list (down when the list is below the minimap, up when the
        /// minimap is docked to a bottom corner and the list sits above it instead) -
        /// recomposed only when that direction actually changes, not every tick.
        /// The arrow itself is drawn as a small Cairo triangle rather than a text
        /// glyph (the button label is left empty) - a "▾"/"▴" character depends on
        /// the active font actually containing that glyph, which isn't guaranteed
        /// for custom/resource-pack fonts and left the button blank for some players.
        /// </summary>
        private void ComposeToggleIcon(bool bottomAnchored)
        {
            lastToggleBottomAnchored = bottomAnchored;

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.None)
                .WithFixedAlignmentOffset(0.0, 0.0);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(0.0);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds iconBounds = ElementBounds.Fixed(0.0, 0.0, ToggleIconSize, ToggleIconSize);
            ElementBounds arrowBounds = ElementBounds.Fixed(0.0, 0.0, ToggleIconSize, ToggleIconSize);
            bgBounds.WithChildren(iconBounds, arrowBounds);

            GuiComposer composer = capi.Gui.CreateCompo("minimapmarkerlist-toggle", dialogBounds)
                .BeginChildElements(bgBounds)
                    .AddToggleButton("", CairoFont.SmallButtonText(), OnToggleClicked, iconBounds, "toggleicon")
                    // Must be dynamic, not static - static custom-draw content gets
                    // baked into one shared background bitmap that's drawn *before*
                    // interactive elements like this button, so it rendered completely
                    // hidden underneath the button's own surface.
                    .AddDynamicCustomDraw(arrowBounds, (ctx, surface, b) => DrawToggleArrow(ctx, b, bottomAnchored), "togglearrow")
                .EndChildElements();

            // See the matching comment in ComposeListGui: this catches the engine
            // recomposing this composer on its own (e.g. on a GUI scale change).
            composer.OnComposed += RepositionEverything;
            composer.Compose();

            Composers["toggle"] = composer;
            composer.GetToggleButton("toggleicon")?.SetValue(listExpanded);
        }

        /// <summary>
        /// Draws the toggle icon's direction arrow directly (see ComposeToggleIcon for
        /// why this isn't just a text glyph): a small filled triangle pointing down
        /// (towards a list below) or up (towards a list above), centered in the button.
        /// </summary>
        private static void DrawToggleArrow(Context ctx, ElementBounds bounds, bool pointUp)
        {
            double cx = bounds.InnerWidth / 2.0;
            double cy = bounds.InnerHeight / 2.0;
            double halfWidth = Math.Min(bounds.InnerWidth, bounds.InnerHeight) * 0.28;
            double tipOffset = halfWidth * 1.2;
            double direction = pointUp ? -1.0 : 1.0;

            ctx.Save();
            ctx.MoveTo(cx - halfWidth, cy - direction * tipOffset * 0.5);
            ctx.LineTo(cx + halfWidth, cy - direction * tipOffset * 0.5);
            ctx.LineTo(cx, cy + direction * tipOffset * 0.5);
            ctx.ClosePath();
            ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.9);
            ctx.Fill();
            ctx.Restore();
        }

        private void OnToggleClicked(bool nowExpanded)
        {
            listExpanded = nowExpanded;

            GuiDialogWorldMap minimapDlg = GetMinimapDialog();
            if (minimapDlg == null) return;

            UpdateListComposer(GetVisibleMarkers(GetMapElement(minimapDlg)), minimapDlg.SingleComposer.Bounds.OuterWidth);

            // ComposeListGui always starts a freshly (re)built panel at a fixed (0,0)
            // offset - reposition immediately so it never renders there even for one
            // frame (OnRenderGUI would also catch this before the next frame, but not
            // before this method returns and something else might render in between).
            RepositionEverything();
        }

        private static CairoFont DistanceFont()
        {
            return CairoFont.WhiteDetailText()
                .WithColor(ColorUtil.Hex2Doubles("#a0a0a0"));
        }

        private string DistanceText(Waypoint wp)
        {
            Vec3d playerPos = capi.World.Player.Entity.Pos.XYZ;
            return (int)Math.Round(wp.Position.DistanceTo(playerPos)) + "m";
        }

        private void RefreshDynamicRowContent(List<Waypoint> markers)
        {
            CairoFont distFont = DistanceFont();

            for (int i = 0; i < markers.Count; i++)
            {
                GuiElementDynamicText distEl = SingleComposer.GetDynamicText("dist" + i);
                if (distEl != null)
                {
                    string text = DistanceText(markers[i]);
                    double width = distFont.GetTextExtents(text).Width / RuntimeEnv.GUIScale + 4.0;

                    // Resize/reposition this row's box to the new text before asking it
                    // to redraw - forceRedraw because the string itself may be unchanged
                    // (same rounded distance) while we still want the box refreshed.
                    distEl.Bounds.fixedWidth = width;
                    distEl.Bounds.fixedX = lastDistRightEdge - width;
                    distEl.SetNewText(text, false, true);
                }

                SingleComposer.GetCustomDraw("arrow" + i)?.Redraw();
            }
        }

        /// <summary>
        /// "truncate" mode only: tracks whether the mouse is currently over a name cell
        /// and, if so, scrolls that row's custom-drawn text left until its tail end has
        /// been revealed. Runs every frame (not the slower content tick) so the mouse
        /// check and the scroll animation are both responsive.
        /// </summary>
        private void UpdateNameHoverScroll(float deltaTime)
        {
            // Must check what the panel was actually last composed with, not the live
            // config value - the setting can change (e.g. via the chat command) up to
            // 200ms before UpdateListComposer's tick notices and rebuilds the panel to
            // match, and this runs every frame. Checking the live value let this run
            // against the still-existing "expand" mode's plain text elements and crash
            // trying to treat them as the custom-draw ones only "truncate" mode creates.
            if (SingleComposer == null || lastComposedLongNamesMode != "truncate" || lastComposedFingerprint == null)
            {
                if (hoveredNameRow != -1)
                {
                    hoveredNameRow = -1;
                    nameScrollOffset = 0.0;
                }
                return;
            }

            int mouseX = capi.Input.MouseX;
            int mouseY = capi.Input.MouseY;
            int newHover = -1;
            for (int i = 0; i < lastComposedFingerprint.Count; i++)
            {
                if (SingleComposer.GetCustomDraw("name" + i)?.Bounds.PointInside(mouseX, mouseY) == true)
                {
                    newHover = i;
                    break;
                }
            }

            if (newHover != hoveredNameRow)
            {
                int previousHover = hoveredNameRow;
                hoveredNameRow = newHover;
                nameScrollOffset = 0.0;
                if (previousHover >= 0) SingleComposer.GetCustomDraw("name" + previousHover)?.Redraw();
            }

            if (hoveredNameRow >= 0)
            {
                nameScrollOffset += NameScrollPixelsPerSecond * deltaTime;
                SingleComposer.GetCustomDraw("name" + hoveredNameRow)?.Redraw();
            }
        }

        /// <summary>
        /// Shortens text with a trailing "..." until it fits within maxWidth, dropping
        /// one character at a time (cheap enough here - only called for the handful of
        /// rows that don't fit, not every frame).
        /// </summary>
        private static string TruncateToFit(CairoFont font, string text, double maxWidth)
        {
            const string ellipsis = "...";
            for (int len = text.Length - 1; len > 0; len--)
            {
                string candidate = text.Substring(0, len) + ellipsis;
                if (font.GetTextExtents(candidate).Width <= maxWidth) return candidate;
            }
            return ellipsis;
        }

        /// <summary>
        /// Draws a marker name left-aligned in its cell. If it fits, that's all there is
        /// to it. If it doesn't and the row isn't hovered, it's shortened with a trailing
        /// "...". If it doesn't and the row *is* hovered, the full name is drawn clipped
        /// to the cell and scrolled left (see UpdateNameHoverScroll) until the tail end
        /// has scrolled into view, so hovering a truncated name reveals it.
        /// </summary>
        private void DrawNameCell(Context ctx, ElementBounds bounds, string fullName, int rowIndex)
        {
            CairoFont font = CairoFont.WhiteDetailText();
            font.SetupContext(ctx);

            double w = bounds.InnerWidth;
            double h = bounds.InnerHeight;
            double y = (h - font.GetFontExtents().Height) / 2.0;
            double fullWidth = font.GetTextExtents(fullName).Width;

            ctx.Save();
            ctx.Rectangle(0.0, 0.0, w, h);
            ctx.Clip();

            if (fullWidth <= w)
            {
                textDrawUtil.DrawTextLine(ctx, font, fullName, 0.0, y);
            }
            else if (rowIndex == hoveredNameRow)
            {
                double offset = Math.Min(nameScrollOffset, fullWidth - w);
                textDrawUtil.DrawTextLine(ctx, font, fullName, -offset, y);
            }
            else
            {
                textDrawUtil.DrawTextLine(ctx, font, TruncateToFit(font, fullName, w), 0.0, y);
            }

            ctx.Restore();
        }

        private const double FacingThresholdRadians = 5.0 * Math.PI / 180.0;

        /// <summary>
        /// Wraps an angle in radians to (-pi, pi].
        /// </summary>
        private static double NormalizeAngle(double a)
        {
            a %= 2.0 * Math.PI;
            if (a > Math.PI) a -= 2.0 * Math.PI;
            if (a <= -Math.PI) a += 2.0 * Math.PI;
            return a;
        }

        /// <summary>
        /// Draws a small triangle pointing from the player towards the marker, relative
        /// to the direction the player is currently facing (0 = straight ahead, positive
        /// = to the right). Turns yellow once the player is facing within ~5 degrees of it.
        /// </summary>
        private void DrawDirectionArrow(Context ctx, ElementBounds bounds, Waypoint wp)
        {
            Vec3d playerPos = capi.World.Player.Entity.Pos.XYZ;
            double dx = wp.Position.X - playerPos.X;
            double dz = wp.Position.Z - playerPos.Z;
            double bearingToMarker = Math.Atan2(dx, -dz);

            // BlockFacing.HorizontalFromYaw resolves yaw=0 to South, yaw=Pi/2 to East,
            // and so on around the compass, i.e. compassBearing = Pi - yaw.
            double playerFacingBearing = NormalizeAngle(Math.PI - capi.World.Player.Entity.Pos.Yaw);
            double angle = NormalizeAngle(bearingToMarker - playerFacingBearing);
            bool facingIt = Math.Abs(angle) <= FacingThresholdRadians;

            double w = bounds.InnerWidth;
            double h = bounds.InnerHeight;
            double r = Math.Min(w, h) / 2.0 - 1.0;

            ctx.Save();
            ctx.Translate(w / 2.0, h / 2.0);
            ctx.Rotate(angle);
            ctx.MoveTo(0.0, -r);
            ctx.LineTo(r * 0.6, r * 0.5);
            ctx.LineTo(0.0, r * 0.15);
            ctx.LineTo(-r * 0.6, r * 0.5);
            ctx.ClosePath();
            ctx.SetSourceRGBA(facingIt ? new double[] { 1.0, 0.9, 0.1, 0.95 } : new double[] { 1.0, 1.0, 1.0, 0.85 });
            ctx.Fill();
            ctx.Restore();
        }

        /// <summary>
        /// The toggle icon straddles the minimap's edge, centered on it (see
        /// RepositionIcon), so it pokes out past that edge by half its own height -
        /// anything docked past that same edge (the list, or the coordinate HUD when
        /// our list isn't showing) needs to clear that protrusion, plus a little
        /// visual breathing room, rather than just the vanilla screen-edge padding.
        /// </summary>
        private static double EdgeClearance()
        {
            return GuiElement.scaled(ToggleIconSize) / 2.0 + GuiElement.scaled(ToggleClearance);
        }

        /// <summary>
        /// Stacks the row list directly against the minimap's outer edge, on whichever
        /// side faces away from the screen corner the minimap is currently anchored to.
        /// Horizontally, the edge of the list nearer the screen's edge is kept flush
        /// with the minimap's corresponding edge, so when the list needs to be wider
        /// than the minimap (a long marker name), it grows toward the screen's center
        /// instead of drifting past the screen edge.
        /// </summary>
        private void RepositionList(ElementBounds minimapBounds, EnumDialogArea area)
        {
            ElementBounds ownBounds = SingleComposer.Bounds;
            bool bottomAnchored = area == EnumDialogArea.LeftBottom || area == EnumDialogArea.RightBottom;
            bool rightAnchored = area == EnumDialogArea.RightTop || area == EnumDialogArea.RightBottom;
            double gap = EdgeClearance();

            double targetAbsY = bottomAnchored
                ? minimapBounds.absY - gap - ownBounds.OuterHeight
                : minimapBounds.absY + minimapBounds.OuterHeight + gap;
            double targetAbsX = rightAnchored
                ? minimapBounds.absX + minimapBounds.OuterWidth - ownBounds.OuterWidth
                : minimapBounds.absX;

            ownBounds.absOffsetY += targetAbsY - ownBounds.absY;
            ownBounds.absOffsetX += targetAbsX - ownBounds.absX;
        }

        /// <summary>
        /// Centers the toggle icon horizontally on the minimap and straddles it
        /// vertically over whichever edge faces the list, so it looks docked onto
        /// the minimap rather than floating separately.
        /// </summary>
        private void RepositionIcon(ElementBounds minimapBounds, EnumDialogArea area)
        {
            GuiComposer toggle = Composers["toggle"];
            if (toggle == null) return;

            ElementBounds ownBounds = toggle.Bounds;
            bool bottomAnchored = area == EnumDialogArea.LeftBottom || area == EnumDialogArea.RightBottom;

            double edgeY = bottomAnchored ? minimapBounds.absY : minimapBounds.absY + minimapBounds.OuterHeight;
            double targetAbsY = edgeY - ownBounds.OuterHeight / 2.0;
            double targetAbsX = minimapBounds.absX + (minimapBounds.OuterWidth - ownBounds.OuterWidth) / 2.0;

            ownBounds.absOffsetY += targetAbsY - ownBounds.absY;
            ownBounds.absOffsetX += targetAbsX - ownBounds.absX;
        }

        /// <summary>
        /// Vanilla's coordinate HUD is hardcoded to the top-right corner and stacks
        /// itself directly under whatever else it finds there (normally the minimap).
        /// To put it below our list instead, we push it down ourselves whenever our
        /// list is showing and the minimap is in that same corner. When our list isn't
        /// showing, referenceBounds is the minimap's own bounds instead - in that case
        /// the coordinate HUD is docked directly below the minimap, same as the list
        /// would be, so it needs the same toggle-icon clearance the list uses rather
        /// than just the vanilla screen-edge padding.
        /// </summary>
        private void RepositionCoordinateHud(ElementBounds referenceBounds, bool referenceIsMinimap, EnumDialogArea area)
        {
            if (area != EnumDialogArea.RightTop) return;

            ElementBounds coordBounds = capi.OpenedGuis.OfType<HudElementCoordinates>().FirstOrDefault()?.SingleComposer?.Bounds;
            if (coordBounds == null) return;

            double gap = referenceIsMinimap ? EdgeClearance() : GuiElement.scaled(GuiStyle.DialogToScreenPadding);
            double targetAbsY = referenceBounds.absY + referenceBounds.OuterHeight + gap;

            coordBounds.absOffsetY += targetAbsY - coordBounds.absY;
        }
    }
}
