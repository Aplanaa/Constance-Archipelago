using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Constance;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ConstanceAP
{
    // Real map icon injection, direct request: modifies the ACTUAL in-game
    // map, not a separate overlay. Every piece of the mechanism below was
    // traced directly from the game's own compiled code (Constance.Ui.dll/
    // Constance.Core.dll), not guessed -- the same world-to-map conversion
    // and icon-creation path the game's own player position marker and
    // Boss Icon use:
    //
    //   1. CConUiMapRoot.GetLevels() -> each level's SConLevelInfo.MapBounds
    //      is a real Rect in WORLD space -- find which level a recorded
    //      check position actually falls inside.
    //   2. CConMapManager.CalcMapPos(levelInfo, worldPos) -> Vector2? --
    //      the exact function the game itself uses to place the player's
    //      own "you are here" marker. Confirmed via IL: normalizes the
    //      world position within MapBounds, then scales into MapRegion.
    //   3. CConUiMapRoot.MapPosToLocalPos(targetRect, ConMapPos, offset)
    //      -> Vector3? -- converts that into a ready-to-use local position
    //      relative to any RectTransform. This is the exact same function
    //      SetToPlayerPos uses for the player marker itself.
    //   4. A real icon is created via Instantiate(config.MapIconImagePrefab,
    //      level.ActiveVariant.IconParent, false) -- the same path the
    //      dynamic Boss Icon uses (confirmed via IL:
    //      UpdateDynamicIconState) -- then CConUiMapIcon.Init(...) sets its
    //      sprite, and its RectTransform.localPosition is set directly
    //      from step 3's result.
    //
    // What's proven: every step above is a REAL function this game already
    // calls for its own player marker and Boss Icon, not reimplemented math
    // that could be subtly wrong. What's NOT verified: whether the icons
    // actually render correctly on screen in a live session -- there is no
    // way to confirm that without an actual playtest, since nothing here
    // can be exercised outside a running Unity instance.
    //
    // Deliberately isolated in its own file and its own coroutine, with a
    // try/catch around every single icon placement individually -- one bad
    // position or one level lookup failure can never take down the whole
    // feature, and this feature can never affect anything else the mod
    // does (checks, item grants, DeathLink, etc. are all completely
    // independent of this).
    internal static class MapIconInjector
    {
        private const int RetryIntervalSeconds = 3;

        // Pin4/Pin5/Pin6 are the only generic, unthemed icon types the
        // game defines (ConMapIcon enum) -- everything else (Shrine,
        // Elevator, Merchant, Snapshot, Npc, Milkshake, Boss) is a
        // specific, real gameplay marker we shouldn't borrow. Pin4 used
        // for every check for now -- a real, if plain, choice rather than
        // guessing at a "correct" one.
        private const ConMapIcon CheckIconType = ConMapIcon.Pin4;

        private static readonly Dictionary<string, CConUiMapIcon> PlacedIcons = new Dictionary<string, CConUiMapIcon>();
        private static readonly HashSet<string> GaveUpIds = new HashSet<string>();
        private static readonly Dictionary<string, int> RetryCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, LocationState> LatestStates = new Dictionary<string, LocationState>();
        private const int MaxRetries = 10; // ~30s at the 3s retry interval

        // Icons only ever
        // appeared on the very first map load. Exiting to the main menu
        // and re-entering destroys the old map's GameObjects (including
        // every previously-placed CConUiMapIcon), but PlacedIcons/
        // GaveUpIds/RetryCounts are static and were never cleared, so the
        // loop below kept skipping every id forever ("already placed" /
        // "already gave up"), even though the actual icon it remembered
        // no longer existed. Fixed by detecting a genuine map root change
        // (a new CConUiMapRoot instance means a fresh scene/session) and
        // resetting all three trackers when that happens.
        private static CConUiMapRoot _lastKnownMapRoot;
        private static ManualLogSource Log;
        private static Sprite _checkIconSprite;
        private static bool _triedLoadingSprite = false;

        // Read by Plugin.cs's own OnGUI to draw the hover tooltip -- kept
        // as a plain static string rather than routing tooltip rendering
        // through this file, since Plugin.cs already owns the one OnGUI
        // pass this mod uses for its other on-screen UI.
        public static string HoveredTooltipText;

        // Hook up the game's own, already-existing shrine
        // map icons (ConMapIcon.Shrine) to work the same way as our own
        // AP check icons -- hover for a tooltip, press T to warp there,
        // greyed out if never visited.
        //
        //         // real testing confirmed most shrine CConCheckPoint components
        // don't even exist as loaded objects until the player has
        // physically been in that room this session, so matching against
        // "all checkpoints in the game" up front can never cover the
        // whole map. Instead: our OWN icons are pre-placed at every
        // existing game shrine icon's position (a simple, exact local-
        // position copy within the same UI parent -- no coordinate
        // conversion needed, since both live in the same space already).
        // Each one starts unlinked/greyed out. A Harmony postfix on the
        // real CConPlayerPersistence.OnPlayerMeditate (confirmed via IL
        // as the exact, only method that fires when the player actually
        // activates a shrine) then links whichever of OUR pre-placed
        // icons is closest to the player's position AT THAT MOMENT to the
        // real checkpoint id -- which is always safe, since the player
        // being physically there means that checkpoint definitely exists
        // and is loaded right now.
        private class OwnShrineIcon
        {
            public CConUiMapIcon Icon; // our own instantiated icon, not the game's
            public ConCheckPointId? LinkedCheckpoint; // null until the player has actually meditated at the matching shrine
        }

        private static readonly List<OwnShrineIcon> OwnShrineIcons = new List<OwnShrineIcon>();
        private static bool _shrinePrePlacementAttempted = false;

        // How close (in the icon parent's local UI space) the player's
        // position has to be, at the moment of meditating, to one of our
        // pre-placed icons for it to count as that same shrine. Generous
        // on purpose -- shrines are sparse, and this only ever needs to
        // distinguish "the shrine right here" from every OTHER shrine on
        // the same map, not from a precise pixel-perfect target.
        private const float ShrineLinkMaxLocalDistance = 80f;

        // Direct, polling-based hover detection -- see the comment on the
        // Update() call site for why this replaced the EventSystem-based
        // approach. RectTransformUtility.RectangleContainsScreenPoint is
        // the same check Unity's own UI raycasting ultimately relies on,
        // just invoked directly rather than through the event pipeline
        // (EventSystem, GraphicRaycaster, etc.) that wasn't delivering
        // events to these icons for whatever reason.
        private static bool _loggedFirstHoverUpdateCall = false;
        private static float _lastHoverDiagnosticLogTime = -999f;
        private static string _lastLoggedHoverText = null;

        public static void UpdateHoverState()
        {
            try
            {
                if (!_loggedFirstHoverUpdateCall)
                {
                    _loggedFirstHoverUpdateCall = true;
                    Log.LogInfo("[HoverDiag] UpdateHoverState called for the first time -- confirms Update() is reaching this method.");
                }

                // Real, root-cause fix confirmed by real testing: this
                // game has Active Input Handling set to the new Input
                // System exclusively -- the legacy UnityEngine.Input
                // class throws InvalidOperationException on every single
                // access, confirmed via the actual exception text in a
                // real log (79,140 occurrences across one session, once
                // per frame). Mouse.current.position.ReadValue() is the
                // new Input System's direct equivalent, same screen-space
                // coordinate convention (bottom-left origin, pixels) as
                // the old Input.mousePosition.
                var mouse = Mouse.current;
                if (mouse == null)
                {
                    // No mouse device recognized by the new Input System
                    // -- nothing to check against this pass, not an error.
                    return;
                }
                Vector2 mousePos = mouse.position.ReadValue();
                string newHoverText = null;

                // Throttled diagnostic snapshot -- every ~2 seconds, not
                // every frame, to avoid flooding the log while still
                // giving enough data points across a real session to see
                // exactly where this breaks: mouse position, whether any
                // icons exist to check at all, and for the closest one,
                // its actual computed screen rect vs the mouse point.
                bool logThisPass = Time.unscaledTime - _lastHoverDiagnosticLogTime >= 2f;
                if (logThisPass) _lastHoverDiagnosticLogTime = Time.unscaledTime;

                if (logThisPass)
                {
                    Log.LogInfo("[HoverDiag] mousePos=" + mousePos + " placedIconCount=" + PlacedIcons.Count + " ownShrineIconCount=" + OwnShrineIcons.Count);
                }

                CConUiMapIcon closestIcon = null;
                float closestDist = float.MaxValue;
                Rect closestScreenRect = new Rect();
                MapIconTooltipTrigger closestTrigger = null;

                // Real fix, backed by real data: a real session log showed
                // icons rendering at only ~17.5x17.5 screen pixels, with
                // the overwhelming majority of hover attempts landing
                // 30-600px away and only two genuine matches in the whole
                // session. Exact rect containment against a target that
                // small is nearly unusable. Switched to "closest icon
                // within a generous radius" instead -- tracks every
                // icon's distance from the mouse regardless of exact
                // containment, and picks the nearest one if it's within
                // HoverRadiusPixels, which is dramatically larger than
                // the icon's own visual size.
                const float HoverRadiusPixels = 25f;

                foreach (var kvp in PlacedIcons)
                {
                    var icon = kvp.Value;
                    if (icon == null) continue;

                    var rect = icon.RectTransform;
                    if (rect == null || !rect.gameObject.activeInHierarchy) continue;

                    Canvas canvas = rect.GetComponentInParent<Canvas>();
                    if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
                    Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

                    Vector3[] corners = new Vector3[4];
                    rect.GetWorldCorners(corners);
                    Vector2 minScreen = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                    Vector2 maxScreen = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                    Vector2 center = (minScreen + maxScreen) * 0.5f;
                    float dist = Vector2.Distance(mousePos, center);

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestIcon = icon;
                        closestScreenRect = new Rect(minScreen, maxScreen - minScreen);
                        closestTrigger = rect.GetComponent<MapIconTooltipTrigger>();
                    }
                }

                // This loop used to only ever check
                // PlacedIcons (our own AP-created icons). Now also checks
                // OwnShrineIcons -- our own pre-placed shrine icons, each
                // with its own MapIconTooltipTrigger kept up to date by
                // PrePlaceOwnShrineIcons/OnShrineActivated below. Same
                // exact distance-to-center logic as above, feeding into
                // the same single "closest of everything" comparison.
                foreach (var ownShrine in OwnShrineIcons)
                {
                    var icon = ownShrine.Icon;
                    if (icon == null) continue;

                    var rect = icon.RectTransform;
                    if (rect == null || !rect.gameObject.activeInHierarchy) continue;

                    Canvas canvas = rect.GetComponentInParent<Canvas>();
                    if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
                    Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

                    Vector3[] corners = new Vector3[4];
                    rect.GetWorldCorners(corners);
                    Vector2 minScreen = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                    Vector2 maxScreen = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                    Vector2 center = (minScreen + maxScreen) * 0.5f;
                    float dist = Vector2.Distance(mousePos, center);

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestIcon = icon;
                        closestScreenRect = new Rect(minScreen, maxScreen - minScreen);
                        closestTrigger = rect.GetComponent<MapIconTooltipTrigger>();
                    }
                }

                if (closestIcon != null && closestDist <= HoverRadiusPixels)
                {
                    if (closestTrigger != null && !string.IsNullOrEmpty(closestTrigger.TooltipText))
                    {
                        newHoverText = closestTrigger.TooltipText;
                        if (logThisPass)
                        {
                            Log.LogInfo("[HoverDiag] MATCH (radius) -- dist=" + closestDist.ToString("F1") + "px, tooltip text ready: " + closestTrigger.TooltipText.Replace("\n", " | "));
                        }
                    }
                    else if (logThisPass)
                    {
                        Log.LogInfo("[HoverDiag] within radius but MapIconTooltipTrigger is missing or has empty text.");
                    }
                }

                if (logThisPass && newHoverText == null && closestIcon != null)
                {
                    Log.LogInfo("[HoverDiag] no match this pass -- closest icon's screen rect=" + closestScreenRect +
                                 " (mouse-to-center dist=" + closestDist.ToString("F1") + "px), canvas renderMode=" +
                                 closestIcon.RectTransform.GetComponentInParent<Canvas>()?.renderMode);
                }

                if (newHoverText != _lastLoggedHoverText)
                {
                    // Edge-triggered -- logs exactly when the hovered
                    // tooltip text actually changes, regardless of the
                    // throttle above, so a real hover-in/hover-out is
                    // never missed even between throttled snapshots.
                    _lastLoggedHoverText = newHoverText;
                    Log.LogInfo("[HoverDiag] HoveredTooltipText changed to: " + (newHoverText == null ? "(null)" : newHoverText.Replace("\n", " | ")));
                }

                HoveredTooltipText = newHoverText;
            }
            catch (Exception e)
            {
                Log.LogWarning("MapIconInjector: error updating hover state: " + e);
            }
        }

        private struct LocationState
        {
            public bool Reachable;
            public bool Hinted;
            public bool Checked;
            public string Item;
            public string LocationName;
            public string Logic;
        }

        public static void Start(ManualLogSource log, MonoBehaviour host)
        {
            Log = log;
            host.StartCoroutine(RunLoop());
        }

        // A real, custom icon sprite (the image provided),
        // not the game's generic Pin4. Loaded from a PNG embedded directly
        // in the mod DLL as a resource -- not a loose file next to it,
        // so it can never go missing on install. Converted from the
        // original .webp to PNG ahead of time, since Unity's built-in
        // ImageConversion.LoadImage does not reliably support WebP.
        // Loaded once and cached -- every icon instance shares the same
        // Sprite object, not a fresh texture load per icon.
        private static Sprite GetCheckIconSprite()
        {
            if (_checkIconSprite != null) return _checkIconSprite;
            if (_triedLoadingSprite) return null; // already failed once this session, don't keep retrying every 3s
            _triedLoadingSprite = true;

            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string resourceName = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("ap_check_icon.png", StringComparison.Ordinal));
                if (resourceName == null)
                {
                    Log.LogWarning("MapIconInjector: embedded ap_check_icon.png resource not found -- falling back to the game's generic Pin4 icon.");
                    return null;
                }

                byte[] bytes;
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var memoryStream = new MemoryStream())
                {
                    stream.CopyTo(memoryStream);
                    bytes = memoryStream.ToArray();
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    Log.LogWarning("MapIconInjector: ImageConversion.LoadImage failed on the embedded icon -- falling back to Pin4.");
                    return null;
                }

                _checkIconSprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));
                Log.LogInfo("MapIconInjector: custom check icon sprite loaded successfully.");
                return _checkIconSprite;
            }
            catch (Exception e)
            {
                Log.LogWarning("MapIconInjector: error loading embedded icon (falling back to Pin4): " + e);
                return null;
            }
        }

        // The shrine warp icon, same embedded-resource
        // pattern as GetCheckIconSprite above -- provided as an SVG
        // (diamonds-four), converted ahead of time to a 128x128 white PNG
        // (originally black -- recolored for visibility against the
        // map's dark background, matching the white/light styling every
        // other map icon already uses) and embedded directly in the DLL.
        private static Sprite _shrineIconSprite;
        private static bool _triedLoadingShrineSprite = false;

        private static Sprite GetShrineIconSprite()
        {
            if (_shrineIconSprite != null) return _shrineIconSprite;
            if (_triedLoadingShrineSprite) return null;
            _triedLoadingShrineSprite = true;

            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string resourceName = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("shrine_icon.png", StringComparison.Ordinal));
                if (resourceName == null)
                {
                    Log.LogWarning("MapIconInjector: embedded shrine_icon.png resource not found -- falling back to the check icon sprite.");
                    return null;
                }

                byte[] bytes;
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var memoryStream = new MemoryStream())
                {
                    stream.CopyTo(memoryStream);
                    bytes = memoryStream.ToArray();
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    Log.LogWarning("MapIconInjector: ImageConversion.LoadImage failed on the embedded shrine icon -- falling back to the check icon sprite.");
                    return null;
                }

                _shrineIconSprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));
                Log.LogInfo("MapIconInjector: custom shrine icon sprite loaded successfully.");
                return _shrineIconSprite;
            }
            catch (Exception e)
            {
                Log.LogWarning("MapIconInjector: error loading embedded shrine icon (falling back to check icon sprite): " + e);
                return null;
            }
        }

        // MapFullRevealPatch
        // (forcing UpdateCompletionFlag to always return true) only takes
        // effect the next time UpdateVisible/UpdateIcons actually RUNS for
        // a given level -- and for a level the player hasn't physically
        // visited, nothing may ever trigger that at all. This forces it
        // directly for every level, not just the current one. Called from
        // two places: the periodic coroutine loop (a safety net) AND,
        // more reliably, RoomChangeMapRevealPatch's hook on the game's own
        // real "player entered a new room" event -- the exact kind of
        // trigger point suggested directly, confirmed to exist via IL as
        // IConSceneListener_ChangeLevel.OnPlayerChangedLevel.
        //
        // Real, definitive finding, direct request ("trick the game into
        // thinking I've been to every area"): confirmed via IL that
        // CheckVisibleFlags (called from UpdateVisible, right below) is
        // the actual method deciding whether a level's shape shows on the
        // map at all -- and it checks a REAL, PERSISTED per-level flag,
        // ConLevelId.VisitedPersistenceId (constructed as literally
        // "VisitedLevel-" + the level's own id string), via
        // Save.GetBoolOrDefault. This is exactly the mechanism asked
        // about -- setting this flag true for every level, before the
        // player ever visits them, is genuinely tricking the game into
        // believing every room has already been entered, using the same
        // persisted flag CConMapPersistence itself sets on a real visit.
        public static void RevealAllLevels(CConUiMapRoot mapRoot)
        {
            if (mapRoot == null) return;
            try
            {
                var persistence = CConSceneRegistry.Instance?.Persistence;
                foreach (var level in mapRoot.GetLevels())
                {
                    try
                    {
                        if (persistence != null && level.LevelInfo != null)
                        {
                            var visitedId = level.LevelInfo.Id.VisitedPersistenceId;
                            if (!(persistence.Save.GetBoolOrDefault(visitedId, false)))
                            {
                                persistence.Save.SetBool(visitedId, true, default(PersistenceEntry.Options));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("MapIconInjector: error setting VisitedPersistenceId for a level: " + e);
                    }

                    try
                    {
                        level.UpdateVisible(mapRoot);
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("MapIconInjector: UpdateVisible failed for a level during reveal: " + e);
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("MapIconInjector: error revealing map fog: " + e);
            }
        }

        private static IEnumerator RunLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(RetryIntervalSeconds);

                List<CheckPosition> positions = null;
                try
                {
                    positions = ReadPositions();
                }
                catch (Exception e)
                {
                    Log.LogWarning("MapIconInjector: error reading check_positions.jsonl (will retry): " + e);
                }

                CConMapManager mapManager = null;
                CConUiMapRoot mapRoot = null;
                try
                {
                    var registry = CConSceneRegistry.Instance;
                    if (registry != null)
                    {
                        mapManager = registry.Map as CConMapManager;
                        mapRoot = mapManager?.MapRoot;
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning("MapIconInjector: error accessing map manager/root (will retry): " + e);
                }

                // Fresh map/session detection. A
                // different CConUiMapRoot instance than last time means
                // everything from before (including every placed icon)
                // is gone -- clear all tracking so icon placement starts
                // over cleanly instead of assuming stale state still
                // applies. Uses Unity's own == (not object.ReferenceEquals)
                // deliberately: a destroyed-but-not-yet-collected
                // UnityEngine.Object compares equal to null through
                // Unity's overload, which plain reference equality
                // wouldn't catch.
                if (mapRoot != null && mapRoot != _lastKnownMapRoot)
                {
                    Log.LogInfo("MapIconInjector: new map root detected (fresh scene/session) -- resetting icon placement tracking.");
                    PlacedIcons.Clear();
                    GaveUpIds.Clear();
                    RetryCounts.Clear();
                    OwnShrineIcons.Clear();
                    _shrinePrePlacementAttempted = false;
                    _lastKnownMapRoot = mapRoot;
                }

                // Draw the whole map from the start,
                // without needing to physically visit every room first.
                // Confirmed via IL: IsDiscoveredCompletely directly gates
                // the fog shader's "_HAS_FOG" keyword (UpdateIcons in the
                // game's own CConUiMapLevel) -- setting it true disables
                // the fog-of-war render pass for that level entirely, the
                // same mechanism the game's own map-reveal item would use.
                //
                // This used to
                // sit AFTER an early "continue" that fired whenever there
                // were no recorded check positions yet -- accidentally
                // coupling two completely unrelated systems (full map
                // reveal has nothing to do with icon placement data).
                // Runs unconditionally now, every loop iteration,
                // regardless of whether any checks have been recorded.
                if (mapRoot != null)
                {
                    RevealAllLevels(mapRoot);
                }

                // Pre-place our own icons
                // at every existing game shrine icon's position (only
                // needs to happen once per fresh map session -- these
                // positions don't move). Retries like the AP icon
                // placement above until it's actually found something to
                // work with, same "one-shot flag was wrong" reasoning as
                // before -- the map UI's icon GameObjects may not be
                // instantiated yet the first time this loop runs.
                if (mapRoot != null && mapManager != null && !_shrinePrePlacementAttempted)
                {
                    try
                    {
                        int shrineIconsSeen = PrePlaceOwnShrineIcons(mapManager, mapRoot);
                        if (shrineIconsSeen > 0)
                        {
                            _shrinePrePlacementAttempted = true;
                            // Shrine link persistence.
                            // Icons are freshly placed at this exact
                            // moment, so this is the right time to
                            // restore any links saved from a previous
                            // session -- see ApplyPersistedShrineLinks'
                            // own comment for why matching by local
                            // position works reliably here without the
                            // player needing to be present.
                            try
                            {
                                ApplyPersistedShrineLinks();
                            }
                            catch (Exception e)
                            {
                                Log.LogWarning("MapIconInjector: error applying persisted shrine links: " + e);
                            }
                        }
                        else
                        {
                            Log.LogInfo("MapIconInjector: saw 0 existing shrine-type map icons this pass (map UI may not be open/instantiated yet) -- will retry.");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("MapIconInjector: error pre-placing shrine icons (will retry): " + e);
                    }
                }

                if (positions == null || positions.Count == 0 || mapManager == null || mapRoot == null) continue;

                foreach (var pos in positions)
                {
                    // "shrine:<name>" entries in check_positions.jsonl are
                    // leftover from a removed manual-recording tool --
                    // skipped here (matched case-insensitively) so any
                    // such entries still on disk from before don't get a
                    // duplicate, redundant icon placed on top of the real
                    // shrine icon already shown via OwnShrineIcons.
                    if (pos.Id != null && pos.Id.StartsWith("shrine:", StringComparison.OrdinalIgnoreCase)) continue;

                    // Defensive, on top of the mapRoot-change reset above:
                    // if an icon was destroyed for some other reason
                    // without a full map root change, its dictionary
                    // entry would still be a "fake null" Unity object --
                    // caught here directly rather than relying solely on
                    // the coarser mapRoot check.
                    if (PlacedIcons.TryGetValue(pos.Id, out CConUiMapIcon existingIcon))
                    {
                        if (existingIcon != null) continue;
                        PlacedIcons.Remove(pos.Id);
                    }
                    if (GaveUpIds.Contains(pos.Id)) continue;

                    bool succeeded = false;
                    try
                    {
                        CConUiMapIcon placed = TryPlaceIcon(mapManager, mapRoot, pos);
                        if (placed != null)
                        {
                            succeeded = true;
                            PlacedIcons[pos.Id] = placed;
                            // A location_states message may well have
                            // arrived before this icon existed to receive
                            // it -- apply whatever we already know right
                            // away instead of waiting up to 5 more seconds
                            // for the next one.
                            if (LatestStates.TryGetValue(pos.Id, out LocationState existingState))
                            {
                                ApplyVisualState(placed, existingState);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("MapIconInjector: error placing icon for " + pos.Id + ": " + e);
                    }

                    if (!succeeded)
                    {
                        // Covers both a thrown exception and a plain "not
                        // ready yet" null return the same way -- either
                        // one gets a bounded number of retries (some
                        // "not ready yet" conditions genuinely do resolve
                        // themselves, like a level not yet created), then
                        // gives up for good rather than retrying (and
                        // potentially re-logging) forever every 3 seconds.
                        RetryCounts.TryGetValue(pos.Id, out int count);
                        count++;
                        RetryCounts[pos.Id] = count;
                        if (count >= MaxRetries)
                        {
                            GaveUpIds.Add(pos.Id);
                            Log.LogInfo("MapIconInjector: giving up on " + pos.Id + " after " + MaxRetries + " attempts.");
                        }
                    }
                }
            }
        }

        private static CConUiMapIcon TryPlaceIcon(CConMapManager mapManager, CConUiMapRoot mapRoot, CheckPosition pos)
        {            var worldPos = new Vector2(pos.X, pos.Y);

            CConUiMapLevel matchedLevel = null;
            foreach (var level in mapRoot.GetLevels())
            {
                SConLevelInfo info = level.LevelInfo;
                if (info == null) continue;
                if (info.MapBounds.Contains(worldPos))
                {
                    matchedLevel = level;
                    break;
                }
            }

            if (matchedLevel == null)
            {
                Log.LogInfo("MapIconInjector: no map level found containing position for " + pos.Id + " -- will retry.");
                return null;
            }

            Vector2? mapPosVec = mapManager.CalcMapPos(matchedLevel.LevelInfo, worldPos);
            if (!mapPosVec.HasValue)
            {
                Log.LogInfo("MapIconInjector: CalcMapPos returned null for " + pos.Id + " -- will retry.");
                return null;
            }

            var mapPos = new ConMapPos
            {
                LevelId = matchedLevel.LevelInfo.MapLevelId,
                MapPos = mapPosVec,
            };

            var variant = matchedLevel.ActiveVariant;
            if (variant == null || variant.IconParent == null)
            {
                Log.LogInfo("MapIconInjector: no active variant/icon parent for " + pos.Id + " -- will retry.");
                return null;
            }

            GameObject prefab = mapRoot.config != null ? mapRoot.config.MapIconImagePrefab : null;
            if (prefab == null)
            {
                Log.LogWarning("MapIconInjector: no MapIconImagePrefab available -- cannot place any icons.");
                return null;
            }

            GameObject iconGo = UnityEngine.Object.Instantiate(prefab, variant.IconParent, false);
            var icon = iconGo.GetComponent<CConUiMapIcon>();
            if (icon == null)
            {
                UnityEngine.Object.Destroy(iconGo);
                Log.LogWarning("MapIconInjector: instantiated icon prefab has no CConUiMapIcon component for " + pos.Id + " -- will retry.");
                return null;
            }

            icon.Init(matchedLevel, CheckIconType, mapRoot.config);

            // Override with our own custom sprite -- image is a private
            // field on CConUiMapIcon, but RectTransform (public) belongs
            // to the same GameObject as the Image component, so this
            // avoids needing reflection for the private field.
            // Deliberately does NOT call SetNativeSize() again here -- our
            // source image is 128x128, which would render far larger than
            // every other map icon. Leaving whatever size Init() already
            // established (from the default Pin4 sprite) means our custom
            // sprite gets fit into the same on-screen scale as every other
            // icon on the map, not its own native pixel size.
            var image = icon.RectTransform.GetComponent<Image>();
            var sprite = GetCheckIconSprite();
            if (sprite != null && image != null)
            {
                image.sprite = sprite;
            }
            // Explicit, not just relying on the prefab's default -- pointer
            // events (for the hover tooltip) require a raycast target.
            if (image != null)
            {
                image.raycastTarget = true;
            }

            // Hover tooltip. Attached here (at creation
            // time) rather than only in ApplyVisualState, so every icon
            // has a trigger from the moment it exists, even before any
            // location_states message has arrived to give it real content.
            var trigger = iconGo.GetComponent<MapIconTooltipTrigger>();
            if (trigger == null)
            {
                trigger = iconGo.AddComponent<MapIconTooltipTrigger>();
            }
            trigger.TooltipText = pos.Id; // placeholder until a real state arrives

            Vector3? localPos = mapRoot.MapPosToLocalPos(icon.RectTransform, mapPos, Vector2.zero);
            if (localPos.HasValue)
            {
                icon.RectTransform.localPosition = localPos.Value;
            }
            else
            {
                Log.LogWarning("MapIconInjector: MapPosToLocalPos returned null for " + pos.Id + " -- icon placed but position may be wrong.");
            }

            return icon;
        }

        // Pre-places our own icon at
        // every existing game shrine icon's position -- a direct local-
        // position copy within the same UI parent the game's icon
        // already lives in, no coordinate conversion needed since both
        // are in the same space. Each one starts unlinked (greyed out,
        // no warp) until OnShrineActivated links it to a real checkpoint.
        // Returns how many existing shrine-type icons were found (used
        // by the caller to decide whether to keep retrying, same as the
        // two earlier approaches this replaced).
        private static int PrePlaceOwnShrineIcons(CConMapManager mapManager, CConUiMapRoot mapRoot)
        {
            CConUiMapIcon[] allIcons = UnityEngine.Object.FindObjectsOfType<CConUiMapIcon>(true);
            int totalShrineIconsSeen = 0;

            GameObject prefab = mapRoot.config != null ? mapRoot.config.MapIconImagePrefab : null;
            if (prefab == null)
            {
                Log.LogWarning("PrePlaceOwnShrineIcons: no MapIconImagePrefab available -- cannot place any icons.");
                return 0;
            }

            foreach (var existingIcon in allIcons)
            {
                if (existingIcon == null) continue;
                if (existingIcon.Icon != ConMapIcon.Shrine) continue;
                totalShrineIconsSeen++;

                var level = existingIcon.Level;
                if (level == null) continue;
                var variant = level.ActiveVariant;
                if (variant == null || variant.IconParent == null) continue;

                // One shrine ended up with two of our own icons
                // even though only one exists visually in the game.
                // Likely explanation: the game keeps two separate
                // CConUiMapIcon GameObjects for the same physical shrine
                // (e.g. a distinct "unvisited"/"visited" state icon),
                // toggling one active and one inactive rather than
                // reusing a single object -- and includeInactive:true
                // (needed to find them at all, see that same fix above)
                // picks up BOTH. Deduplicated here by position: if an
                // icon already placed at this exact spot on this level,
                // skip creating a second one rather than trusting every
                // existing icon to represent a genuinely distinct shrine.
                bool alreadyPlacedHere = false;
                foreach (var existing in OwnShrineIcons)
                {
                    if (existing.Icon == null || existing.Icon.Level != level) continue;
                    if (Vector2.Distance(existing.Icon.RectTransform.localPosition, existingIcon.RectTransform.localPosition) < 1f)
                    {
                        alreadyPlacedHere = true;
                        break;
                    }
                }
                if (alreadyPlacedHere)
                {
                    Log.LogInfo("PrePlaceOwnShrineIcons: skipping a duplicate existing icon at the same position as one already placed (likely an unvisited/visited state pair for the same physical shrine).");
                    continue;
                }

                GameObject iconGo = UnityEngine.Object.Instantiate(prefab, variant.IconParent, false);
                var ownIcon = iconGo.GetComponent<CConUiMapIcon>();
                if (ownIcon == null)
                {
                    UnityEngine.Object.Destroy(iconGo);
                    continue;
                }

                ownIcon.Init(level, CheckIconType, mapRoot.config);

                var sprite = GetShrineIconSprite() ?? GetCheckIconSprite();
                var image = ownIcon.RectTransform.GetComponent<Image>();
                if (sprite != null && image != null)
                {
                    image.sprite = sprite;
                }
                if (image != null)
                {
                    image.raycastTarget = true;
                    // Starts greyed out -- exactly the same dim tint
                    // already used for unreachable AP check icons, until
                    // OnShrineActivated links this to a real, visited
                    // checkpoint.
                    image.color = new Color(0.55f, 0.55f, 0.55f, 0.65f);
                }

                var trigger = iconGo.GetComponent<MapIconTooltipTrigger>();
                if (trigger == null) trigger = iconGo.AddComponent<MapIconTooltipTrigger>();
                trigger.TooltipText = "Shrine (not yet visited)\nVisit it in person first";

                // Exact position copy, same local space
                // the existing game icon already lives in -- no
                // CalcMapPos/MapPosToLocalPos conversion needed at all,
                // sidestepping the coordinate-mismatch issue real testing
                // found with that pipeline entirely.
                ownIcon.RectTransform.localPosition = existingIcon.RectTransform.localPosition;

                OwnShrineIcons.Add(new OwnShrineIcon { Icon = ownIcon, LinkedCheckpoint = null });
            }

            Log.LogInfo("PrePlaceOwnShrineIcons: saw " + totalShrineIconsSeen + " existing shrine-type icon(s), placed " +
                        OwnShrineIcons.Count + " of our own.");
            return totalShrineIconsSeen;
        }

        // Called from ShrineMeditatePatch's Harmony
        // postfix, the exact real moment the player activates a shrine.
        // The player being physically there right now means this
        // specific checkpoint is guaranteed loaded and its position
        // reliable -- no waiting/retrying needed, unlike trying to
        // enumerate every checkpoint in the game up front (confirmed by
        // real testing not to work, most don't exist as loaded
        // components until visited).
        // Unlinks every currently-linked shrine icon in
        // memory and greys them back out, without touching the icons
        // themselves (still correctly pre-placed at the game's own
        // shrine positions) -- used when a genuinely different seed is
        // detected (see Plugin.HandleSeedChange), to guarantee no
        // previous playthrough's "visited" state survives into a new one
        // even if this runs before/after the on-disk file gets cleared.
        public static void ResetAllShrineLinks()
        {
            int count = 0;
            foreach (var ownShrine in OwnShrineIcons)
            {
                if (!ownShrine.LinkedCheckpoint.HasValue) continue;
                ownShrine.LinkedCheckpoint = null;
                if (ownShrine.Icon == null) continue;

                var trigger = ownShrine.Icon.RectTransform.GetComponent<MapIconTooltipTrigger>();
                if (trigger != null) trigger.TooltipText = "Shrine (not yet visited)\nVisit it in person first";
                var image = ownShrine.Icon.RectTransform.GetComponent<Image>();
                if (image != null) image.color = new Color(0.55f, 0.55f, 0.55f, 0.65f);
                count++;
            }
            Log.LogInfo("ResetAllShrineLinks: unlinked " + count + " previously-linked shrine icon(s) for a new seed.");
        }

        public static void OnShrineActivated(ConCheckPointId meditationPointId)
        {
            try
            {
                // A blocked save file (wrong seed, or already-
                // started/never-linked) could still write real shrine
                // links to shrine_links.jsonl via this path -- it isn't
                // routed through Plugin.ReportCheck/ApplyItem at all, so
                // the existing block there never covered it. A blocked
                // file shouldn't be able to touch persistent AP-adjacent
                // state of any kind, shrine tracking included.
                if (Plugin._apIntegrationBlockedForWrongFile) return;

                // This checkpoint being meditated
                // at again does NOT mean the player did it manually a
                // second time -- the game itself appears to re-fire this
                // exact event on its own, automatically, on respawn after
                // a scene transition (confirmed directly: two separate
                // checkpoints each got "wrote link" TWICE in the same
                // session, each time immediately following a "Persistence
                // manager reference became invalid" scene transition, with
                // a genuinely DIFFERENT local position recorded each time).
                // OwnShrineIcons itself gets rebuilt from scratch on every
                // such transition too, and evidently doesn't reproduce the
                // exact same local position bit-for-bit each time it's
                // rebuilt -- so every one of these redundant re-links was
                // silently overwriting a perfectly good, already-correct
                // saved link with a slightly different position, and
                // doing that repeatedly is exactly what made specific
                // shrines drift further and further until they stopped
                // matching anything at all, appearing as "doesn't persist"
                // even though a link really was being written every time.
                // The real fix: once a checkpoint has ANY saved link at
                // all, never let a later re-trigger touch it again --
                // stable-but-possibly-imperfect beats "correct at first,
                // then silently degrades every single reload" by a wide
                // margin.
                if (Plugin.HasSavedShrineLink(meditationPointId.StringValue))
                {
                    Log.LogInfo("OnShrineActivated: checkpoint '" + meditationPointId.StringValue + "' already has a saved link -- skipping (this event firing again is almost certainly the game re-triggering it automatically on respawn, not a genuine new meditation).");
                    return;
                }

                IConSceneRegistry registry = CConSceneRegistry.Instance;
                var mapManager = registry?.Map as CConMapManager;
                CConUiMapRoot mapRoot = mapManager?.MapRoot;
                if (mapManager == null || mapRoot == null)
                {
                    Log.LogWarning("OnShrineActivated: no map manager/root available -- cannot link this shrine to an icon yet.");
                    return;
                }

                var playerComponent = registry?.PlayerOne as Component;
                if (playerComponent == null)
                {
                    Log.LogWarning("OnShrineActivated: no player component available -- cannot determine position.");
                    return;
                }
                Vector2 worldPos = playerComponent.transform.position;

                CConUiMapLevel matchedLevel = null;
                SConLevelInfo matchedInfo = null;
                foreach (var level in mapRoot.GetLevels())
                {
                    SConLevelInfo info = level.LevelInfo;
                    if (info == null) continue;
                    if (info.MapBounds.Contains(worldPos))
                    {
                        matchedLevel = level;
                        matchedInfo = info;
                        break;
                    }
                }
                if (matchedLevel == null)
                {
                    Log.LogWarning("OnShrineActivated: no map level's bounds contain the player's current position (" + worldPos + ") -- cannot link this shrine to an icon.");
                    return;
                }

                Vector2? mapPosVec = mapManager.CalcMapPos(matchedInfo, worldPos);
                if (!mapPosVec.HasValue)
                {
                    Log.LogWarning("OnShrineActivated: CalcMapPos returned null for the player's current position -- cannot link this shrine to an icon.");
                    return;
                }

                // Using
                // matchedLevel.ActiveVariant.IconParent, freshly fetched
                // right here, does NOT reliably match the actual parent
                // transform the pre-placed icons live under -- if
                // ActiveVariant changed at all between pre-placement time
                // (map load) and this moment (meditating), MapPosToLocalPos
                // would be computing relative to a completely different
                // transform than where the icons actually are, producing
                // exactly this kind of large, roughly-consistent offset
                // rather than random noise. Fixed by using the REAL,
                // actual parent of a genuine candidate icon instead of a
                // freshly re-fetched one -- guaranteed to be the same
                // transform the icons are truly parented under, no matter
                // what ActiveVariant says right now.
                var candidates = new List<OwnShrineIcon>();
                foreach (var ownShrine in OwnShrineIcons)
                {
                    if (ownShrine.LinkedCheckpoint.HasValue) continue;
                    if (ownShrine.Icon == null || ownShrine.Icon.Level != matchedLevel) continue;
                    candidates.Add(ownShrine);
                }

                if (candidates.Count == 0)
                {
                    Log.LogWarning("OnShrineActivated: no unlinked pre-placed icons at all on this level for checkpoint '" + meditationPointId.StringValue + "'.");
                    return;
                }

                // Pragmatic safety net -- if this level
                // only has one unlinked shrine icon left, there's no
                // actual ambiguity to resolve regardless of what the
                // position math says. The player just meditated at A
                // shrine on THIS level, and there's only one candidate it
                // could possibly be.
                if (candidates.Count == 1)
                {
                    var only = candidates[0];
                    only.LinkedCheckpoint = meditationPointId;
                    var onlyTrigger = only.Icon.RectTransform.GetComponent<MapIconTooltipTrigger>();
                    if (onlyTrigger != null) onlyTrigger.TooltipText = "Shrine (visited)\nPress T to warp here";
                    var onlyImage = only.Icon.RectTransform.GetComponent<Image>();
                    if (onlyImage != null) onlyImage.color = Color.white;
                    PersistShrineLink(meditationPointId.StringValue, only.Icon.RectTransform.localPosition, only.Icon.Level != null ? only.Icon.Level.name : null);
                    Log.LogInfo("OnShrineActivated: only one unlinked shrine icon on this level -- linked directly to checkpoint '" + meditationPointId.StringValue + "', no position matching needed.");
                    return;
                }

                RectTransform iconParentRect = candidates[0].Icon.RectTransform.parent as RectTransform;
                if (iconParentRect == null)
                {
                    Log.LogWarning("OnShrineActivated: candidate icon's own parent isn't a RectTransform -- cannot convert position.");
                    return;
                }

                var mapPos = new ConMapPos { LevelId = matchedInfo.MapLevelId, MapPos = mapPosVec };
                Vector3? localPos = mapRoot.MapPosToLocalPos(iconParentRect, mapPos, Vector2.zero);
                if (!localPos.HasValue)
                {
                    Log.LogWarning("OnShrineActivated: MapPosToLocalPos returned null -- cannot link this shrine to an icon.");
                    return;
                }

                Log.LogInfo("OnShrineActivated: level='" + matchedInfo.MapLevelId.StringValue + "' worldPos=" + worldPos +
                            " mapPos=" + mapPosVec.Value + " computed localPos=" + localPos.Value + " against " + candidates.Count + " candidate(s).");

                OwnShrineIcon closest = null;
                float closestDist = float.MaxValue;
                foreach (var candidate in candidates)
                {
                    float dist = Vector2.Distance(candidate.Icon.RectTransform.localPosition, localPos.Value);
                    Log.LogInfo("OnShrineActivated: candidate icon at " + candidate.Icon.RectTransform.localPosition + ", dist=" + dist.ToString("F1"));
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = candidate;
                    }
                }

                if (closest == null || closestDist > ShrineLinkMaxLocalDistance)
                {
                    Log.LogWarning("OnShrineActivated: no unlinked pre-placed icon found within range on this level for checkpoint '" +
                                   meditationPointId.StringValue + "' (closest was " +
                                   (closest != null ? closestDist.ToString("F1") + "px away" : "none found") + ") -- this shrine will stay unwarpable.");
                    return;
                }

                closest.LinkedCheckpoint = meditationPointId;

                var trigger = closest.Icon.RectTransform.GetComponent<MapIconTooltipTrigger>();
                if (trigger != null)
                {
                    trigger.TooltipText = "Shrine (visited)\nPress T to warp here";
                }
                var image = closest.Icon.RectTransform.GetComponent<Image>();
                if (image != null)
                {
                    image.color = Color.white;
                }
                PersistShrineLink(meditationPointId.StringValue, closest.Icon.RectTransform.localPosition, closest.Icon.Level != null ? closest.Icon.Level.name : null);

                Log.LogInfo("OnShrineActivated: linked checkpoint '" + meditationPointId.StringValue + "' to a pre-placed icon (" + closestDist.ToString("F1") + "px away) -- warp now enabled for it.");
            }
            catch (Exception e)
            {
                Log.LogWarning("OnShrineActivated: error linking shrine to icon: " + e);
            }
        }

        // Writes one link to disk (append-only, same
        // pattern as check_positions.jsonl) -- "last entry for this
        // checkpoint wins" on read, so a shrine linked again in a later
        // session (e.g. after the game's own save data or the mod's
        // matching logic shifted slightly) naturally supersedes the
        // older entry rather than needing an explicit update-in-place.
        private static void PersistShrineLink(string checkpointIdString, Vector3 localPosition, string levelName)
        {
            try
            {
                string escapedId = checkpointIdString.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string escapedLevel = (levelName ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                // LevelName added so restoration can use
                // the SAME "only one candidate" logic already proven
                // reliable for live linking, rather than position
                // closeness -- see ApplyPersistedShrineLinks' own
                // comment for why position alone isn't reliable enough
                // even without a distance ceiling.
                string line = "{\"checkpointId\":\"" + escapedId + "\",\"x\":" +
                              localPosition.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"y\":" +
                              localPosition.y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"level\":\"" + escapedLevel + "\"}";
                File.AppendAllText(Plugin.ShrineLinksFilePath, line + Environment.NewLine);
                Log.LogInfo("PersistShrineLink: wrote link for checkpoint '" + checkpointIdString + "' at " + localPosition + " (level: '" + levelName + "') to '" + Plugin.ShrineLinksFilePath + "'.");
            }
            catch (Exception e)
            {
                Log.LogWarning("PersistShrineLink: error writing shrine link to disk (link still applied this session, just won't survive a restart): " + e);
            }
        }

        // The other half of shrine link persistence --
        // called once per fresh map session, right after
        // PrePlaceOwnShrineIcons places this session's icons (which are
        // guaranteed to land at the exact same local positions as any
        // previous session, being a direct copy of the game's own fixed
        // shrine icon positions). Reads every saved link, keeps only the
        // last one written per checkpoint id (same last-wins convention
        // as check_positions.jsonl), and re-links each one to whichever
        // freshly-placed icon sits at that same position -- restoring
        // every previously-visited shrine's warp availability without
        // needing the player to physically revisit any of them.
        private static void ApplyPersistedShrineLinks()
        {
            // Same block as OnShrineActivated
            // -- a blocked file shouldn't have any shrine shown as
            // already-visited either, consistent with it having no other
            // AP-related state applied at all.
            if (Plugin._apIntegrationBlockedForWrongFile) return;

            if (!File.Exists(Plugin.ShrineLinksFilePath))
            {
                Log.LogInfo("ApplyPersistedShrineLinks: no shrine_links.jsonl file exists yet at '" + Plugin.ShrineLinksFilePath + "' -- nothing to restore this session.");
                return;
            }

            try
            {
                var latestByCheckpoint = new Dictionary<string, (Vector2 pos, string level)>();
                foreach (var line in File.ReadAllLines(Plugin.ShrineLinksFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string checkpointId = ExtractStringField(line, "checkpointId");
                    if (string.IsNullOrEmpty(checkpointId)) continue;
                    float x = ExtractFloatField(line, "x");
                    float y = ExtractFloatField(line, "y");
                    string level = ExtractStringField(line, "level"); // absent in pre-fix entries -- null, handled below
                    latestByCheckpoint[checkpointId] = (new Vector2(x, y), level);
                }

                // Even with no distance ceiling
                // at all, position closeness alone can still pick the
                // WRONG icon when a level has more than one candidate --
                // confirmed by a screenshot showing a genuinely-visited
                // shrine (one of the two specifically retested) still
                // showing as unvisited despite this exact fix already
                // being live. The player's own insight, applied properly
                // this time: the FIRST-time live match already has a
                // completely reliable path for this -- "only one
                // unlinked icon on this level" needs no position math at
                // all, because Icon.Level is a real, stable game
                // reference, not a derived coordinate. Grouped by level
                // first; only falls back to position-closeness for the
                // genuinely ambiguous case of multiple unresolved
                // checkpoints sharing one level, which position data is
                // at least SOME signal for.
                var byLevel = new Dictionary<CConUiMapLevel, List<OwnShrineIcon>>();
                foreach (var ownShrine in OwnShrineIcons)
                {
                    if (ownShrine.LinkedCheckpoint.HasValue) continue;
                    if (ownShrine.Icon == null || ownShrine.Icon.Level == null) continue;
                    if (!byLevel.TryGetValue(ownShrine.Icon.Level, out var list))
                    {
                        list = new List<OwnShrineIcon>();
                        byLevel[ownShrine.Icon.Level] = list;
                    }
                    list.Add(ownShrine);
                }

                var pendingByLevelName = new Dictionary<string, List<string>>();
                foreach (var kvp in latestByCheckpoint)
                {
                    string levelName = kvp.Value.level ?? "";
                    if (!pendingByLevelName.TryGetValue(levelName, out var list))
                    {
                        list = new List<string>();
                        pendingByLevelName[levelName] = list;
                    }
                    list.Add(kvp.Key);
                }

                int relinked = 0;
                var stillPending = new List<string>(latestByCheckpoint.Keys);

                foreach (var levelEntry in byLevel)
                {
                    CConUiMapLevel level = levelEntry.Key;
                    List<OwnShrineIcon> iconsOnLevel = levelEntry.Value;
                    string levelName = level.name ?? "";

                    if (pendingByLevelName.TryGetValue(levelName, out var pendingHere) && pendingHere.Count == 1 && iconsOnLevel.Count == 1)
                    {
                        string checkpointId = pendingHere[0];
                        OwnShrineIcon only = iconsOnLevel[0];
                        only.LinkedCheckpoint = new ConCheckPointId(checkpointId);
                        var trigger = only.Icon.RectTransform.GetComponent<MapIconTooltipTrigger>();
                        if (trigger != null) trigger.TooltipText = "Shrine (visited)\nPress T to warp here";
                        var image = only.Icon.RectTransform.GetComponent<Image>();
                        if (image != null) image.color = Color.white;
                        relinked++;
                        stillPending.Remove(checkpointId);
                        Log.LogInfo("ApplyPersistedShrineLinks: checkpoint '" + checkpointId + "' -- only one candidate on level '" + levelName + "', linked directly (no position matching needed).");
                    }
                    else if (pendingByLevelName.TryGetValue(levelName, out var pendingMulti) && pendingMulti.Count > 1 && pendingMulti.Count == iconsOnLevel.Count)
                    {
                        // A level with more than one shrine (e.g. the
                        // circus/Chaotic Carnival biome) can't use the
                        // "only one candidate" shortcut above, since
                        // there's more than one on both sides -- but
                        // absolute position still isn't reliable for
                        // picking between them (that's the exact failure
                        // this whole level-grouping approach exists to
                        // avoid). When the count matches exactly on both
                        // sides, though, RELATIVE order is a much
                        // stronger signal than absolute distance: which
                        // shrine sits further left/up than the other is
                        // fixed by the level's own geometry and far less
                        // prone to drifting between rebuilds than any one
                        // shrine's own absolute coordinate. Sorted by x
                        // then y on both sides and matched pairwise in
                        // that order, rather than nearest-neighbor, which
                        // is exactly what let two shrines on the same
                        // level get swapped or left unmatched before.
                        var sortedPending = new List<string>(pendingMulti);
                        sortedPending.Sort((a, b) =>
                        {
                            Vector2 pa = latestByCheckpoint[a].pos;
                            Vector2 pb = latestByCheckpoint[b].pos;
                            int cmp = pa.x.CompareTo(pb.x);
                            return cmp != 0 ? cmp : pa.y.CompareTo(pb.y);
                        });
                        var sortedIcons = new List<OwnShrineIcon>(iconsOnLevel);
                        sortedIcons.Sort((a, b) =>
                        {
                            Vector2 pa = a.Icon.RectTransform.localPosition;
                            Vector2 pb = b.Icon.RectTransform.localPosition;
                            int cmp = pa.x.CompareTo(pb.x);
                            return cmp != 0 ? cmp : pa.y.CompareTo(pb.y);
                        });

                        for (int i = 0; i < sortedPending.Count; i++)
                        {
                            string checkpointId = sortedPending[i];
                            OwnShrineIcon matched = sortedIcons[i];
                            matched.LinkedCheckpoint = new ConCheckPointId(checkpointId);
                            var trigger = matched.Icon.RectTransform.GetComponent<MapIconTooltipTrigger>();
                            if (trigger != null) trigger.TooltipText = "Shrine (visited)\nPress T to warp here";
                            var image = matched.Icon.RectTransform.GetComponent<Image>();
                            if (image != null) image.color = Color.white;
                            relinked++;
                            stillPending.Remove(checkpointId);
                            Log.LogInfo("ApplyPersistedShrineLinks: checkpoint '" + checkpointId + "' -- matched by relative position order on level '" + levelName + "' (" + sortedPending.Count + " candidates, exact count match).");
                        }
                    }
                }

                // Fallback: anything left (either no level was recorded
                // for it -- a pre-fix save -- or its level genuinely has
                // more than one unresolved candidate) still gets the
                // closest-position attempt, same as before.
                foreach (string checkpointId in stillPending)
                {
                    Vector2 savedPos = latestByCheckpoint[checkpointId].pos;

                    OwnShrineIcon closest = null;
                    float closestDist = float.MaxValue;
                    foreach (var ownShrine in OwnShrineIcons)
                    {
                        if (ownShrine.LinkedCheckpoint.HasValue) continue;
                        if (ownShrine.Icon == null) continue;
                        float dist = Vector2.Distance(ownShrine.Icon.RectTransform.localPosition, savedPos);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closest = ownShrine;
                        }
                    }

                    Log.LogInfo("ApplyPersistedShrineLinks: checkpoint '" + checkpointId + "' saved at " + savedPos +
                                " -- fallback to closest unlinked icon " + (closest != null ? closestDist.ToString("F1") + "px away" : "none found"));

                    if (closest == null) continue;

                    closest.LinkedCheckpoint = new ConCheckPointId(checkpointId);
                    var trigger = closest.Icon.RectTransform.GetComponent<MapIconTooltipTrigger>();
                    if (trigger != null) trigger.TooltipText = "Shrine (visited)\nPress T to warp here";
                    var image = closest.Icon.RectTransform.GetComponent<Image>();
                    if (image != null) image.color = Color.white;
                    relinked++;
                }

                Log.LogInfo("ApplyPersistedShrineLinks: restored " + relinked + " of " + latestByCheckpoint.Count + " saved shrine link(s) this session.");
            }
            catch (Exception e)
            {
                Log.LogWarning("ApplyPersistedShrineLinks: error restoring saved shrine links (shrines will need re-visiting this session): " + e);
            }
        }

        // T-key warp trigger. Called from Plugin.cs's own
        // Update(), alongside its existing F10 handling -- returns
        // silently (no-op) if no linked, warpable shrine icon is
        // currently hovered.
        public static void CheckShrineWarpKeyPress(Vector2 mousePos, Action<string> warpByCheckpointId)
        {
            const float HoverRadiusPixels = 25f; // same radius as the tooltip hover match above

            OwnShrineIcon closest = null;
            float closestDist = float.MaxValue;

            foreach (var ownShrine in OwnShrineIcons)
            {
                if (ownShrine.Icon == null) continue;
                var rect = ownShrine.Icon.RectTransform;
                if (rect == null || !rect.gameObject.activeInHierarchy) continue;

                Canvas canvas = rect.GetComponentInParent<Canvas>();
                if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
                Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

                Vector3[] corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                Vector2 minScreen = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 maxScreen = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                Vector2 center = (minScreen + maxScreen) * 0.5f;
                float dist = Vector2.Distance(mousePos, center);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = ownShrine;
                }
            }

            if (closest == null || closestDist > HoverRadiusPixels) return;
            if (!closest.LinkedCheckpoint.HasValue)
            {
                Log.LogInfo("CheckShrineWarpKeyPress: T pressed over an unlinked/unvisited shrine -- ignored.");
                return;
            }

            warpByCheckpointId(closest.LinkedCheckpoint.Value.StringValue);
        }


        // Color/outline based on live logic state
        // (reachable = normal, unreachable = greyed out, hinted =
        // outlined), plus the hover tooltip's actual content.
        private static void ApplyVisualState(CConUiMapIcon icon, LocationState state)
        {
            try
            {
                var image = icon.RectTransform.GetComponent<Image>();
                if (image == null)
                {
                    Log.LogWarning("MapIconInjector: ApplyVisualState -- no Image component found on icon's RectTransform.");
                    return;
                }

                // Direct clarification: "checked" (already found) is its
                // own, third visual state -- takes priority over
                // reachable/unreachable entirely, since a found check is
                // neither "needs attention" nor really "unreachable" in
                // any meaningful sense, it's just done. Heavily dimmed
                // and semi-transparent, distinct from BOTH the normal
                // (white) and unreachable (light grey) states.
                if (state.Checked)
                {
                    image.color = new Color(0.35f, 0.35f, 0.35f, 0.35f);
                }
                else
                {
                    image.color = state.Reachable ? Color.white : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                }

                var outline = icon.RectTransform.GetComponent<Outline>();
                if (state.Hinted && !state.Checked)
                {
                    if (outline == null)
                    {
                        outline = icon.RectTransform.gameObject.AddComponent<Outline>();
                    }
                    outline.effectColor = new Color(1f, 0.9f, 0.2f, 1f);
                    outline.effectDistance = new Vector2(2.5f, -2.5f);
                    outline.enabled = true;
                }
                else if (outline != null)
                {
                    outline.enabled = false;
                }

                var trigger = icon.RectTransform.GetComponent<MapIconTooltipTrigger>();
                if (trigger == null)
                {
                    Log.LogWarning("MapIconInjector: ApplyVisualState -- no MapIconTooltipTrigger component found on icon.");
                }
                else
                {
                    // Only show an item line when we
                    // actually know something (hinted) -- "Unknown item"
                    // as a placeholder for every un-hinted location added
                    // noise without information. "(already found)" still
                    // shows on its own for checked-but-unhinted locations,
                    // since that's real status info, just not paired with
                    // a fabricated item name.
                    string nameLine = string.IsNullOrEmpty(state.LocationName) ? "" : state.LocationName + "\n";
                    string statusLine;
                    if (state.Item != null)
                    {
                        string hintedPart = state.Hinted ? " (hinted)" : "";
                        string checkedPart = state.Checked ? " (already found)" : "";
                        statusLine = state.Item + hintedPart + checkedPart + "\n";
                    }
                    else if (state.Checked)
                    {
                        statusLine = "(already found)\n";
                    }
                    else
                    {
                        statusLine = "";
                    }
                    trigger.TooltipText = nameLine + statusLine + state.Logic;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("MapIconInjector: error applying visual state: " + e);
            }
        }

        // When a category isn't randomized this seed (so
        // its locations don't exist in Archipelago at all), the player
        // should still see the map icon go "found" once they've actually
        // opened that chest / beaten that boss. There's no server-side
        // location to ask about, so the state is set locally instead --
        // same LatestStates dictionary the real location_states message
        // writes into, so icons placed later pick it up identically.
        //
        // Deliberately idempotent and safe to call every session: the
        // mod re-derives these from the game's own persistence flags on
        // startup (ReportAlreadyTrueLocations), which is what makes the
        // icons come back after a restart without needing a file of
        // their own.
        //
        // Never touches anything player-visible -- no notification, no
        // UI log line, nothing sent to the client. The icon is the only
        // output, which is exactly the point: internally tracked, never
        // announced.
        public static void MarkLocallyChecked(string persistenceId)
        {
            try
            {
                if (string.IsNullOrEmpty(persistenceId)) return;

                var state = new LocationState
                {
                    Reachable = true,
                    Hinted = false,
                    Checked = true,
                    Item = null,
                    LocationName = null,
                    // Stands in for the logic line a real AP location
                    // would carry, so the tooltip explains why this icon
                    // has no item/hint information rather than looking
                    // like a location whose data failed to arrive.
                    Logic = "Not randomized this seed -- vanilla contents",
                };
                LatestStates[persistenceId] = state;

                if (PlacedIcons.TryGetValue(persistenceId, out CConUiMapIcon icon) && icon != null)
                {
                    ApplyVisualState(icon, state);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("MapIconInjector: error marking '" + persistenceId + "' as locally checked: " + e);
            }
        }

        // Parses the batched location_states message from the live client
        // and applies it to every currently-placed icon, storing the data
        // regardless so any icon placed LATER can pick up its state
        // immediately too (see the RunLoop check above).
        public static void ApplyLocationStates(string json)
        {
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var statesArray = obj["states"] as Newtonsoft.Json.Linq.JArray;
                if (statesArray == null)
                {
                    Log.LogWarning("MapIconInjector: location_states message parsed but had no 'states' array.");
                    return;
                }

                int updatedIcons = 0;
                foreach (var entry in statesArray)
                {
                    string id = entry["id"]?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;

                    var state = new LocationState
                    {
                        Reachable = entry["reachable"]?.ToObject<bool>() ?? true,
                        Hinted = entry["hinted"]?.ToObject<bool>() ?? false,
                        Checked = entry["checked"]?.ToObject<bool>() ?? false,
                        Item = entry["item"]?.ToObject<string>(),
                        LocationName = entry["location_name"]?.ToObject<string>(),
                        Logic = entry["logic"]?.ToObject<string>(),
                    };
                    LatestStates[id] = state;

                    if (PlacedIcons.TryGetValue(id, out CConUiMapIcon icon) && icon != null)
                    {
                        ApplyVisualState(icon, state);
                        updatedIcons++;
                    }
                }
                Log.LogInfo("MapIconInjector: location_states applied -- " + statesArray.Count + " states received, " +
                            updatedIcons + " already-placed icon(s) updated, " + PlacedIcons.Count + " total icon(s) placed so far.");
            }
            catch (Exception e)
            {
                Log.LogWarning("MapIconInjector: error parsing location_states message: " + e);
            }
        }

        private struct CheckPosition
        {
            public string Id;
            public string Scene;
            public float X;
            public float Y;
        }

        private static List<CheckPosition> ReadPositions()
        {
            string path = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "check_positions.jsonl");
            if (!File.Exists(path)) return null;

            var results = new List<CheckPosition>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    string id = ExtractStringField(line, "id");
                    string scene = ExtractStringField(line, "scene");
                    float x = ExtractFloatField(line, "x");
                    float y = ExtractFloatField(line, "y");
                    if (id == null) continue;
                    // Defensive cleanup for a real,
                    // observed mistake -- typing an Archipelago client
                    // command like "!warp X" into the F10 position-record
                    // box (rather than the actual client console) used to
                    // silently record the current position under that
                    // literal command text as its id, creating a
                    // confusingly-named stray map icon. F10 itself now
                    // rejects new entries like this outright, but this
                    // filters out any that were already written to the
                    // file before that fix existed, so old, already-
                    // confusing entries stop being drawn too.
                    if (id.StartsWith("!")) continue;
                    // "ps_item_Potion"
                    // is a suppression-only id, never a real AP location
                    // (see ReportCheck's own NeverCheckIds exclusion,
                    // which stops any NEW recording of it) -- but an
                    // already-written entry from before that fix would
                    // still be sitting in an existing file and keep
                    // showing a spurious icon. Same defensive-cleanup
                    // pattern as the "!"-prefixed filter just above.
                    if (id == "ps_item_Potion") continue;
                    results.Add(new CheckPosition { Id = id, Scene = scene, X = x, Y = y });
                }
                catch
                {
                    // One malformed line must never break reading the rest.
                    continue;
                }
            }
            return results;
        }

        private static string ExtractStringField(string line, string fieldName)
        {
            int keyIndex = line.IndexOf("\"" + fieldName + "\"", StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colonIndex = line.IndexOf(':', keyIndex);
            if (colonIndex < 0) return null;
            int firstQuote = line.IndexOf('"', colonIndex + 1);
            if (firstQuote < 0) return null;
            int secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0) return null;
            return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        private static float ExtractFloatField(string line, string fieldName)
        {
            int keyIndex = line.IndexOf("\"" + fieldName + "\"", StringComparison.Ordinal);
            if (keyIndex < 0) return 0f;
            int colonIndex = line.IndexOf(':', keyIndex);
            if (colonIndex < 0) return 0f;
            int start = colonIndex + 1;
            int end = start;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '-' || line[end] == '.'))
                end++;
            string numStr = line.Substring(start, end - start);
            return float.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float result) ? result : 0f;
        }
    }

    // Hovering an icon should show the item name and
    // logic. A plain, minimal IPointerEnterHandler/IPointerExitHandler
    // implementation -- relies on the game's own existing UI EventSystem
    // (confirmed to exist implicitly: the game already has interactive UI
    // elements like buttons elsewhere, so this doesn't need to create one
    // itself). Only ever writes a static string; MapIconInjector.
    // HoveredTooltipText is read and actually drawn by Plugin.cs's own
    // OnGUI, which already owns the one OnGUI pass this mod uses.
    internal class MapIconTooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string TooltipText;
        private static bool _loggedFirstHover = false;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_loggedFirstHover)
            {
                _loggedFirstHover = true;
                Debug.Log("[ConstanceAP] MapIconTooltipTrigger: first hover event received -- pointer events are reaching map icons correctly.");
            }
            MapIconInjector.HoveredTooltipText = TooltipText;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            // Only clear if we're still the one showing -- avoids a stale
            // exit event from one icon clearing a DIFFERENT icon's tooltip
            // that started showing in between.
            if (MapIconInjector.HoveredTooltipText == TooltipText)
            {
                MapIconInjector.HoveredTooltipText = null;
            }
        }

        private void OnDisable()
        {
            if (MapIconInjector.HoveredTooltipText == TooltipText)
            {
                MapIconInjector.HoveredTooltipText = null;
            }
        }
    }
}
