// Constance Archipelago Mod
// ===========================
//
// Live, in-process alternative to the file-watching ConstanceClient.py.
// Instead of polling the save file, this hooks directly into the game's
// own real, public persistence API (found via IL disassembly of
// Constance.Core.dll, not guessed):
//
//   Constance.CConSceneRegistry.Instance   (static singleton)
//     .Persistence                          -> CConPersistenceManager
//       .RegisterListener(ConPersistenceId, Action)   real-time callback
//       .GetInt / .GetBool / .Contains(...)            read current state
//       .SetInt / .SetBool(...)                        write, takes effect
//                                                       immediately, no
//                                                       save/reload needed
//
// This plugin does NOT speak the Archipelago network protocol itself --
// that stays in Python (ConstanceBridgeClient.py), which already has a
// fully tested CommonClient-based implementation. This plugin only runs a
// tiny local TCP bridge (newline-delimited JSON) on 127.0.0.1:24242 for
// the Python side to talk to. This split keeps the C# side small and the
// AP protocol logic in the one place it's already been validated.
//
// HONEST STATUS: this has NOT been loaded into a running Constance
// process. It compiles cleanly against the real game assemblies and the
// real BepInEx/Harmony assemblies (see README), which is the strongest
// verification possible without a live Windows machine + the actual game.
// The API calls themselves (RegisterListener, SetInt, GetInt, Contains,
// CConSceneRegistry.Instance) are all real, public, non-obfuscated
// methods found directly in the decompiled IL -- not guessed names.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using Constance;

namespace ConstanceAP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.archipelago.constance";
        public const string PluginName = "Constance Archipelago Client";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        // The same 47 curated location IDs as the Python client / apworld.
        // Keep these in sync with locations.py / ConstanceClient.py.
        // Per-category randomize toggles. Restructured
        // from one flat list into categories so ApplicableItemIds can be
        // dynamically rebuilt when settings arrive from the live client
        // (see ApplySettings below) -- removing a category's ids from
        // this set is enough on its own, since all four suppression
        // patches (AbilityUnlockPatch, AbilityAltarStayInteractivePatch,
        // DropCollectPatch, ChestLootPatch) already check membership in
        // this exact same shared set.
        // Abilities/tears/Frida Mask no
        // longer need their own toggle-conditional inclusion here at all.
        // With the "keep the check, guarantee the vanilla item" approach
        // (place_locked_guaranteed_abilities on the apworld side), the
        // check/suppression mechanism for these is now IDENTICAL whether
        // the corresponding randomize_* toggle is on or off -- only which
        // specific item comes back from the server differs (locked vs
        // genuinely shuffled), which is entirely transparent to the mod.
        // So all of them are unconditionally always-tracked now, same as
        // Paintbrush/Paint Dive always were.
        private static readonly string[] AlwaysTrackedIds = new[]
        {
            "ps_unlock_Ability_Brush", "ps_unlock_Ability_Dash",
            "ps_unlock_Ability_Stab", "ps_unlock_Ability_Slice", "ps_unlock_Ability_Pogo",
            "ps_unlock_Ability_WallDive", "ps_unlock_Ability_DoubleJump", "ps_unlock_Ability_BombClone",
            "ps_unlock_Tear_Foundry", "ps_unlock_Tear_Academy", "ps_unlock_Tear_Carnival", "ps_unlock_Tear_Vaults",
            "ps_unlock_FridaMask",
            // "ps_unlock_Map" removed -- real testing found it softlocks
            // the game into a stuck dialogue when randomized.
            "ps_unlock_MapIcons",
            // "Boss: Constance" is
            // exempt from randomize_bosses, always a real AP location --
            // it's not just a regular check, it's tied directly to the
            // real, live "you finished the game" signal (FINAL_BOSS_ID
            // in the live client). Kept always-tracked here to match.
            "ps_Boss_PuppetMaster_Beaten",
        };

        // The Camera is no longer an Archipelago item at
        // all (removed from the apworld's item pool), but "Camera Pickup"
        // is still a real check. That combination needs an id that is
        // LISTENED to (so the check fires) but never SUPPRESSED (so the
        // real game grants the real camera on the spot, since nothing is
        // ever going to send one back). Ids here go into
        // KnownPersistenceIds but are deliberately kept out of
        // ApplicableItemIds under every setting.
        private static readonly string[] UnsuppressedTrackedIds = new[]
        {
            "ps_unlock_Camera",
        };

        private static readonly string[] InspirationIds = new[]
        {
            "ps_inspDrawing_Copycat",
            "ps_inspDrawing_DarkLightning", "ps_inspDrawing_DarkRevival", "ps_inspDrawing_MilkshakeRemix",
            "ps_inspDrawing_NaturalTwenty", "ps_inspDrawing_PaintOverload", "ps_inspDrawing_PogoWave",
            "ps_inspDrawing_PotentPotion", "ps_inspDrawing_PotionSynthesizer", "ps_inspDrawing_ScrewJump",
            "ps_inspDrawing_SliceConductor", "ps_inspDrawing_StabLifesteal", "ps_inspDrawing_StabProjectile",
            "ps_inspDrawing_SturdyKnight", "ps_inspDrawing_TaxRefund", "ps_inspDrawing_TeleportClone",
        };

        // New toggle. Pulled out of AlwaysTrackedIds
        // above into its own array, same treatment as InspirationIds.
        // ps_item_Potion included too -- see the comment that used to sit
        // with these in AlwaysTrackedIds: the real purchase flow calls
        // Collect() TWICE (the shop item's own flag AND, separately, this
        // underlying reward id), so both need to move together or the
        // second one would stay suppressed even with the toggle off.
        private static readonly string[] HealthVialIds = new[]
        {
            "ps_shopItem_Potion1", "ps_shopItem_Potion2", "ps_item_Potion",
        };

        // 2 of these 24 are actually a Paint Flask in
        // disguise (their real vanilla content), 1 is actually a Heart
        // Piece -- pulled out into their own arrays below rather than
        // included here, since their inclusion depends on BOTH
        // randomizeChests AND the matching randomizeFlasks/Hearts
        // setting, not just chests alone (see ApplySettings).
        private static readonly string[] ChestGuidIds = new[]
        {
            "ps_Prod_V18_d336557d-21de-494b-9252-3249efd8a329",
            "ps_Prod_V25_85f4c4a9-2607-4e0b-a8cf-880b870b5547",
            "ps_Prod_V04_1323ea94-0f95-dc12-c4ce-691e45cfd61c",
            "ps_Prod_C90_3a191403-d505-9a9d-10ce-6be0167f13d2",
            "ps_Prod_C96_49252065-b059-02ba-e69e-edaa30f89d14",
            "ps_Prod_C93_0d28e8d6-a001-436e-9ec1-9c525217f192",
            "ps_Prod_C95_e7442277-a767-7428-c17d-05c9b94ec518",
            "ps_Prod_C94_1feb337b-de88-b0db-92e5-c948661aa51d",
            "ps_Prod_F21_6a1b3f09-be2b-4645-a74f-75174bace78d",
            "ps_Prod_F13_c5e9a808-8a12-5dbe-943b-9e50cadf6f18",
            "ps_Prod_F16_a046b9c0-14bd-4053-b0ad-cef476f54daa",
            "ps_Prod_F20_6713e14b-6a29-4871-8ac6-7f04b594bd57",
            "ps_Prod_F03_9c79f624-ecb2-4be2-a979-dae5fd90f875",
            "ps_Prod_F25_2a26ffc7-0a98-1ced-4f63-ac3ee2224f5c",
            "ps_Prod_P03_73cee3e6-0f17-4b15-817f-476c28071c80",
            "ps_Prod_J07_a115468a-2a96-4b67-9ebd-37e0be846c63",
            "ps_Prod_J08_05ab593c-d503-41cf-b3eb-0eb89726594f",
            "ps_Prod_J13_2176d0ba-6fff-4407-417a-ecbe13c97576",
            "ps_Prod_A16_a8a6a17b-df53-495e-9c7a-8b9b828ccdc7",
            "ps_Prod_A20_d51fea7c-0552-d01f-4c9c-439cafe52c8a",
            "ps_Prod_A27_2d28c2b8-aeea-4fd1-b695-0faae8b91057",
        };

        // "Vanishing Vaults - Chest 3" and "Janky Junction - Chest 2" --
        // both actually a Paint Flask. Direct request/correction: this
        // used to say "Vanishing Vaults - Chest 2" and "Floral Foundry -
        // Chest 7" -- confirmed wrong by real testing.
        private static readonly string[] FlaskDisguisedChestGuidIds = new[]
        {
            "ps_Prod_V08_47add804-cdd9-43b4-8d80-eabcfc5787b8",
            "ps_Prod_J19_dacbb823-357f-b229-30ec-3a5f372e265a",
        };

        // "Floral Foundry - Chest 3" -- actually a Heart Piece.
        private static readonly string[] HeartDisguisedChestGuidIds = new[]
        {
            "ps_Prod_F02_cc06e08a-23b9-4cc9-ab1e-e4795e51419e",
        };

        // The 16 REGULAR bosses -- Boss: Constance is deliberately
        // excluded here, already covered by AlwaysTrackedIds above.
        private static readonly string[] RegularBossIds = new[]
        {
            "ps_Boss_AweKing_Beaten", "ps_Boss_BossJoker_Beaten", "ps_Boss_BrainStoker_Beaten",
            "ps_Boss_ChaseNemesis_Beaten", "ps_Boss_CornelisBoss_Beaten", "ps_Boss_JokerInvisible_Beaten",
            "ps_Boss_JugglerBalloons_Beaten", "ps_Boss_JugglerBalls_Beaten", "ps_Boss_MothQueen_Beaten",
            "ps_Boss_Palettus_Beaten", "ps_Boss_PukeyBoy_Beaten", "ps_Boss_PuppetHandCorruption_Beaten",
            "ps_Boss_PuppetHandKungfu_Beaten", "ps_Boss_PuppetHandStrings_Beaten",
            "ps_Boss_SlimeNemesis_Beaten", "ps_Boss_Smasher_Beaten",
        };

        // Every id that any RegisterListener/reconcile loop needs to know
        // about at all -- independent of whether a given id is currently
        // SUPPRESSED (that's ApplicableItemIds, a subset of this).
        private static readonly string[] KnownPersistenceIds =
            AlwaysTrackedIds.Concat(UnsuppressedTrackedIds).Concat(ChestGuidIds).Concat(FlaskDisguisedChestGuidIds)
                .Concat(HeartDisguisedChestGuidIds).Concat(RegularBossIds).Concat(InspirationIds).ToArray();

        // Real, genuine in-game pickup animations, per direct request:
        // the game already has a UI notification/animation system for
        // exactly this (CConUiCollectNotification_Item, found in
        // Constance.Ui.dll) -- but since our grants bypass the vanilla
        // Collect() call entirely (that's what suppression means), the
        // listener that normally triggers this notification
        // (IConSceneListener_Inventory.OnPlayerCollect, called from
        // inside Collect()) never fires for AP-granted items either.
        // This finds the live scene instance bound to a specific
        // collectable (matched via the same PersistenceId comparison
        // pattern used everywhere else in this mod) and calls its
        // OnCurrencyChanged(old, new) directly -- the exact same public
        // method the real vanilla pickup flow itself calls, just invoked
        // manually instead of via the suppressed listener broadcast.
        // Covers Currency/Lightstone/Heart Piece/Paint Flask/Eraser,
        // which all share this same accumulating-counter-style
        // notification component. Inspirations use a different, more
        // complex async cutscene system (PlayInspirationCollectedCutscene)
        // not yet wired up -- a real, harder follow-up, not attempted
        // here to avoid guessing at an unverified implementation.
        private void TriggerCollectAnimation(string persistenceId, int oldValue, int newValue)
        {
            try
            {
                var instances = UnityEngine.Object.FindObjectsOfType<CConUiCollectNotification_Item>();
                foreach (var instance in instances)
                {
                    var collectable = instance.Collectable;
                    if (collectable != null && collectable.PersistenceId.StringValue == persistenceId)
                    {
                        instance.OnCurrencyChanged(oldValue, newValue);
                        return;
                    }
                }
                Log.LogWarning("No live CConUiCollectNotification_Item found for " + persistenceId + " -- animation not played (value was still applied correctly).");
            }
            catch (Exception e)
            {
                Log.LogWarning("Error triggering collect animation for " + persistenceId + ": " + e);
            }
        }

        // Paint Flasks have no per-instance persistence records (confirmed
        // by exhaustive recheck -- see the matching note in the apworld's
        // locations.py). Only the real aggregate counter exists.
        //
        // Mechanism (mirrors the ability-suppression pattern, applied to a
        // counter instead of a boolean): every real vanilla pickup still
        // increments the real counter and is detected as a check -- but
        // we immediately claw the counter back down to whatever the
        // player has actually been GRANTED via received AP items, using
        // the same suppression technique as ApplyItem. Two bookkeeping
        // values (persisted using the same real save system, so they
        // survive naturally): how many real pickups have become checks so
        // far, and how many "Paint Flask" items have actually been
        // received and banked. A first-run baseline grandfathers in
        // whatever the player already had before ever connecting, so
        // existing progress is never taken away -- only pickups from this
        // point onward get the suppress-until-granted treatment.
        //
        // Real, honest tradeoff: if anything in-game gates on this
        // counter's value (a shop purchase, say), the player's visible
        // total will lag behind their actual exploration until AP sends
        // the corresponding items back -- the same category of thing as
        // an ability not working until received, just on a number.
        private const string PaintFlaskCounterPersistenceId = "ps_item_PaintPiece";
        private const string PaintFlaskCheckCountPersistenceId = "ps_ap_PaintFlaskChecksReported";
        private const string PaintFlaskBankedPersistenceId = "ps_ap_PaintFlaskBanked";
        // A real pickup happening after AP grants had already
        // pushed the real counter (and thus its claw-back target,
        // "banked") ahead would sometimes report a burst of checks, and
        // sometimes report none at all for a genuine pickup. Root cause:
        // detecting a new real pickup by comparing the real counter
        // against checksReported broke down once claw-back kept resetting
        // the real counter back to banked -- every real pickup after that
        // point lands on the exact same value (banked + 1) as the LAST
        // one did, since the counter never remembers it was ever higher.
        // The first such pickup after an AP grant burst correctly fires
        // (checksReported catches up to banked+1), but every pickup after
        // THAT lands on that same already-reported value and fires
        // nothing -- exactly the "some real ones get skipped" symptom.
        // Fixed with a separate baseline that ONLY moves when we
        // ourselves write to the real counter (claw-back or AP grant),
        // never reset by anything else -- so a real pickup is always
        // measured against what we actually expect, not against how many
        // instances have been assigned so far (a fully separate, no
        // longer conflated concept).
        private const string PaintFlaskLastKnownRealPersistenceId = "ps_ap_PaintFlaskLastKnownReal";
        private const string PaintFlaskGrantSentinel = "ps_ap_PaintFlaskGrant";
        // This used to be used
        // for BOTH the location count (10, since 2 real flasks are inside
        // chests and have no distinct AP location) AND the banked-grant
        // cap / "received" display -- but the actual item pool still has
        // the full 12 Paint Flask items (they stay obtainable via AP even
        // without their own location, per direct request). Conflating the
        // two meant the 11th and 12th real AP grants would silently hit
        // "banked >= 10" and get dropped entirely, never applied to the
        // real game counter. Split into two separate constants: this one
        // is the location count (still used for the "found via real
        // pickup" progress display and the tracker's own bookkeeping);
        // PaintFlaskItemPoolSize below is the true total, used for the
        // grant cap and the "received via AP" display.
        private const int PaintFlaskMaxInstances = 10;  // reduced from 12: 2 real flasks are inside chests, whose own checks take priority (chest suppression prevents the counter from ever incrementing for those two)
        private const int PaintFlaskItemPoolSize = 12;
        // Each instance now has real, distinct identity
        // (its own AP location name, region, and logic), possible because
        // the position-based tracker can now tell them apart. Real,
        // important detail: these are the SURVIVING instance numbers after
        // the 2 chest-absorbed ones were excluded -- NOT necessarily
        // 1..10. Whichever specific numbers those were came from
        // whichever order things got recorded in across earlier testing,
        // so the valid set has to be listed explicitly here rather than
        // assumed as a clean range.
        private static readonly int[] PaintFlaskValidInstances = { 1, 2, 4, 5, 6, 7, 8, 9, 10, 11 };
        private bool _paintFlaskInitialized;

        private void InitPaintFlaskTracking()
        {
            var checkCountId = new ConPersistenceId(PaintFlaskCheckCountPersistenceId);
            var bankedId = new ConPersistenceId(PaintFlaskBankedPersistenceId);
            var realId = new ConPersistenceId(PaintFlaskCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(PaintFlaskLastKnownRealPersistenceId);

            if (!_persistence.Save.Contains(bankedId))
            {
                // First run: grandfather in whatever the player already has,
                // so existing progress is never retroactively taken away.
                int currentReal = _persistence.Save.Contains(realId) ? (_persistence.Save.GetInt(realId) ?? 0) : 0;
                _suppressNextChangeFor.Add(PaintFlaskBankedPersistenceId);
                _persistence.Save.SetInt(bankedId, currentReal, default(PersistenceEntry.Options));
                _suppressNextChangeFor.Add(PaintFlaskCheckCountPersistenceId);
                _persistence.Save.SetInt(checkCountId, currentReal, default(PersistenceEntry.Options));
            }

            // Direct fix, per explicit request to not need a fresh save:
            // grandfathers in the new tracking field gracefully even on an
            // existing, in-progress save that predates this fix -- if it's
            // missing, initialize it to the current banked value (the most
            // sensible assumption: "as far as we know, the real counter
            // currently matches banked," which is true immediately after
            // any prior claw-back).
            if (!_persistence.Save.Contains(lastKnownRealId))
            {
                int banked = _persistence.Save.GetInt(bankedId) ?? 0;
                _suppressNextChangeFor.Add(PaintFlaskLastKnownRealPersistenceId);
                _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));
            }

            if (_paintFlaskTracker == null)
            {
                _paintFlaskTracker = new PositionBasedItemTracker(
                    TrackerFilePath("paintflask_positions.json"), CheckPositionsFilePath,
                    PaintFlaskCounterPersistenceId, PaintFlaskValidInstances, Log);
            }

            _paintFlaskInitialized = true;
            ReconcilePaintFlaskCounter();
        }

        private void ReconcilePaintFlaskCounter()
        {
            // Same block as ApplyItem/ApplyFiller/
            // ReportCheck -- this one specifically because it writes
            // directly to persistence (the claw-back) before ever
            // reaching ReportCheck internally, so gating ReportCheck
            // alone wouldn't have stopped that write.
            if (_apIntegrationBlockedForWrongFile) return;
            if (!_paintFlaskInitialized) return;

            var realId = new ConPersistenceId(PaintFlaskCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(PaintFlaskLastKnownRealPersistenceId);
            int currentReal = _persistence.Save.Contains(realId) ? (_persistence.Save.GetInt(realId) ?? 0) : 0;

            // No more separate
            // "category disabled" branch at all. The apworld now
            // guarantees a Paint Flask location's item is ALWAYS a real
            // Paint Flask, even when randomize_flasks is off (locked in
            // via place_locked_guaranteed_items, not shuffled) -- so the
            // mod's own behavior needs to be identical either way:
            // suppress the real counter, report the check, and trust AP
            // to send the guaranteed-matching item straight back. The old
            // disabled branch let the real counter run unsuppressed
            // instead, which -- now that ReportCheck always reports a
            // real check for these regardless of the toggle (see
            // ApplySettings) -- would double-count: once from the
            // unsuppressed vanilla pickup, again when AP's grant arrived.
            var bankedId = new ConPersistenceId(PaintFlaskBankedPersistenceId);

            int banked = _persistence.Save.GetInt(bankedId) ?? 0;
            int lastKnownReal = _persistence.Save.Contains(lastKnownRealId) ? (_persistence.Save.GetInt(lastKnownRealId) ?? banked) : banked;

            // Position-based identity, replacing the old
            // "next sequential number" assignment entirely. checksReported
            // (a plain persisted count) is gone -- _paintFlaskTracker.
            // ReportedCount is now the source of truth, since "how many
            // found" and "which specific ones" are the same bookkeeping
            // now, not two separate things that could drift apart.
            int delta = currentReal - lastKnownReal;
            // Real correctness fix, needed now that this category is
            // suppressed unconditionally: a pickup the tracker cannot
            // attribute to any real AP location (the copies permanently
            // absorbed by chests are the known case -- there is one for
            // each of these three types, and with randomize_chests off
            // they are handed out as plain vanilla loot) must NOT be
            // clawed back. There is no AP location behind it, so no item
            // will ever arrive to give it back, and silently deleting a
            // legitimately-earned pickup is far worse than not reporting
            // a check for it. Banking it instead keeps the player's real
            // total intact while leaving the AP side untouched.
            int unattributed = 0;
            if (delta > 0)
            {
                Vector3? posOpt = GetCurrentPlayerPosition();
                for (int i = 0; i < delta; i++)
                {
                    int? instance = posOpt.HasValue
                        ? _paintFlaskTracker.ResolvePickup(new Vector2(posOpt.Value.x, posOpt.Value.y))
                        : _paintFlaskTracker.ResolvePickupWithoutPosition();
                    if (instance.HasValue)
                    {
                        ReportCheck(PaintFlaskCounterPersistenceId + "#instance" + instance.Value);
                    }
                    else
                    {
                        unattributed++;
                        Log.LogInfo("ReconcilePaintFlaskCounter: this pickup matches no remaining AP location (all " + PaintFlaskMaxInstances + " already accounted for) -- keeping it as a plain vanilla pickup, no check reported.");
                    }
                }
                // "X found (Y/Z)" removed -- it always
                // showed a stale count (banked hasn't incremented yet at
                // this exact point, only once AP's grant round-trips
                // back), which was confusing/misleading on its own.
                // "Received: X (Y/Z)" (a separate message, elsewhere)
                // shows the same data but AFTER it's actually accurate,
                // and is kept exactly as-is.
            }

            if (unattributed > 0)
            {
                banked += unattributed;
                _suppressNextChangeFor.Add(PaintFlaskBankedPersistenceId);
                _persistence.Save.SetInt(bankedId, banked, default(PersistenceEntry.Options));
            }

            if (currentReal != banked)
            {
                // Claw back to what's actually been granted -- undoes the
                // vanilla increment until the matching AP item arrives.
                _suppressNextChangeFor.Add(PaintFlaskCounterPersistenceId);
                _persistence.Save.SetInt(realId, banked, default(PersistenceEntry.Options));
            }

            if (lastKnownReal != banked)
            {
                _suppressNextChangeFor.Add(PaintFlaskLastKnownRealPersistenceId);
                _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));
            }
        }

        private void GrantPaintFlask(string instanceKey = null)
        {
            if (WasInstanceProcessed(instanceKey))
            {
                Log.LogInfo("Paint Flask instance '" + instanceKey + "' already processed -- skipping (safe resend).");
                return;
            }

            var bankedId = new ConPersistenceId(PaintFlaskBankedPersistenceId);
            var realId = new ConPersistenceId(PaintFlaskCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(PaintFlaskLastKnownRealPersistenceId);

            int banked = _persistence.Save.GetInt(bankedId) ?? 0;
            if (banked >= PaintFlaskItemPoolSize)
            {
                MarkInstanceProcessed(instanceKey);
                return;
            }

            banked++;
            _suppressNextChangeFor.Add(PaintFlaskBankedPersistenceId);
            _persistence.Save.SetInt(bankedId, banked, default(PersistenceEntry.Options));

            _suppressNextChangeFor.Add(PaintFlaskCounterPersistenceId);
            _persistence.Save.SetInt(realId, banked, default(PersistenceEntry.Options));
            TriggerCollectAnimation(PaintFlaskCounterPersistenceId, banked - 1, banked);

            // This write is suppressed (the listener
            // never fires for it), so ReconcilePaintFlaskCounter never
            // runs here to update lastKnownReal on its own -- has to be
            // done explicitly, right here, or the NEXT real pickup would
            // measure its delta against a stale baseline and either burst
            // or silently skip, exactly the bug this whole fix addresses.
            _suppressNextChangeFor.Add(PaintFlaskLastKnownRealPersistenceId);
            _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));

            MarkInstanceProcessed(instanceKey);
            UiLog("Received: Paint Flask (" + banked + "/" + PaintFlaskItemPoolSize + ")");
            AddNotification("Received: Paint Flask (" + banked + "/" + PaintFlaskItemPoolSize + ")");
            ClientLog("Received: Paint Flask (" + banked + "/" + PaintFlaskItemPoolSize + ")");
            RefreshPaintCapacity();
        }

        // Heart Piece: check-detection is handled cleanly by
        // Heart Piece: converted to the full Paint-Flask-style pure
        // counter system, same reasoning and mechanism as Eraser below --
        // only 6 of the originally hoped-for 9 real per-instance
        // locations could ever be confirmed, so this switches entirely to
        // watching the real aggregate counter (ps_item_HeartPiece)
        // crossing each of 9 thresholds, unlocking all 9 instead of
        // settling for 6.
        private const string HeartPieceCounterPersistenceId = "ps_item_HeartPiece";
        private const string HeartPieceCheckCountPersistenceId = "ps_ap_HeartPieceChecksReported";
        private const string HeartPieceBankedPersistenceId = "ps_ap_HeartPieceBanked";
        // Same real fix as PaintFlaskLastKnownRealPersistenceId -- see
        // that field's comment for the full real-world bug this closes.
        private const string HeartPieceLastKnownRealPersistenceId = "ps_ap_HeartPieceLastKnownReal";
        private const string HeartPieceGrantSentinel = "ps_ap_HeartPieceGrant";
        private const int HeartPieceMaxInstances = 8;  // reduced from 9: same reasoning as Paint Flask, 1 real heart piece is inside a chest
        private const int HeartPieceItemPoolSize = 9;  // real, true item pool size -- see PaintFlaskItemPoolSize's comment for why this needs to be separate
        private static readonly int[] HeartPieceValidInstances = { 1, 3, 4, 5, 6, 7, 8, 9 };
        private bool _heartPieceInitialized;

        // Eraser: switched to the full Paint-Flask-style pure counter
        // system (no per-instance location at all), per direct request,
        // after only 1 of an expected 5 real per-instance locations could
        // ever be confirmed. Keeping a single real per-instance check
        // alongside counter-watching would have risked double-counting
        // that one pickup (once via the per-instance patch, again via the
        // counter noticing the same increment) -- switching entirely to
        // the counter-based approach, matching Paint Flask exactly,
        // avoids that and gives all 5 expected checks instead of just 1.
        private const string EraserCounterPersistenceId = "ps_item_Eraser";
        private const string EraserCheckCountPersistenceId = "ps_ap_EraserChecksReported";
        private const string EraserBankedPersistenceId = "ps_ap_EraserBanked";
        // Same real fix as PaintFlaskLastKnownRealPersistenceId.
        private const string EraserLastKnownRealPersistenceId = "ps_ap_EraserLastKnownReal";
        private const string EraserGrantSentinel = "ps_ap_EraserGrant";
        // The "Received: Eraser" message was showing 2/5
        // for the very first eraser received, because it printed how many
        // erasers the player HAS -- and the game hands out one for free at
        // the start of a run, which InitEraserTracking correctly banks.
        //
        // Corrected per direct clarification: nothing about the player's
        // actual eraser count should change (an earlier attempt subtracted
        // a baseline from it, which was the wrong fix). This is purely a
        // message problem, so it gets a message-only answer -- a plain
        // count of erasers RECEIVED FROM ARCHIPELAGO, incremented once per
        // grant and never touching the real counter, the banked value or
        // anything else. First eraser received now reads 1/5.
        //
        // It also replaces `banked` as the grant cap, which fixes the same
        // off-by-one seen from the other end: capping at "banked >= 5"
        // while banked already started at 1 meant the 5th eraser in the
        // pool was silently dropped.
        private const string EraserReceivedCountPersistenceId = "ps_ap_EraserReceivedCount";
        // Same exact fix, same exact reasoning,
        // for the OTHER "how many erasers" message -- "Eraser found"
        // (shown for a real, vanilla pickup resolved to a specific AP
        // location, not an AP grant). This one was still printing raw
        // `banked` directly, which inherits the same free-starting-eraser
        // inflation EraserReceivedCountPersistenceId was introduced to
        // fix on the grant side. A separate, dedicated counter here too,
        // incremented once per real pickup this reconcile pass actually
        // processes (whether resolved to a specific instance or not --
        // the player found a real eraser either way), never touching
        // banked/the real counter/anything else.
        private const string EraserFoundCountPersistenceId = "ps_ap_EraserFoundCount";
        private const int EraserMaxInstances = 4;  // reduced from 5: same reasoning as Paint Flask, 1 real eraser is inside a chest
        private const int EraserItemPoolSize = 5;  // real, true item pool size -- see PaintFlaskItemPoolSize's comment for why this needs to be separate
        private static readonly int[] EraserValidInstances = { 2, 3, 4, 5 };
        private bool _eraserInitialized;

        private void InitHeartPieceTracking()
        {
            var checkCountId = new ConPersistenceId(HeartPieceCheckCountPersistenceId);
            var bankedId = new ConPersistenceId(HeartPieceBankedPersistenceId);
            var realId = new ConPersistenceId(HeartPieceCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(HeartPieceLastKnownRealPersistenceId);

            if (!_persistence.Save.Contains(bankedId))
            {
                int currentReal = _persistence.Save.Contains(realId) ? (_persistence.Save.GetInt(realId) ?? 0) : 0;
                _suppressNextChangeFor.Add(HeartPieceBankedPersistenceId);
                _persistence.Save.SetInt(bankedId, currentReal, default(PersistenceEntry.Options));
                _suppressNextChangeFor.Add(HeartPieceCheckCountPersistenceId);
                _persistence.Save.SetInt(checkCountId, currentReal, default(PersistenceEntry.Options));
            }

            if (!_persistence.Save.Contains(lastKnownRealId))
            {
                int banked = _persistence.Save.GetInt(bankedId) ?? 0;
                _suppressNextChangeFor.Add(HeartPieceLastKnownRealPersistenceId);
                _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));
            }

            if (_heartPieceTracker == null)
            {
                _heartPieceTracker = new PositionBasedItemTracker(
                    TrackerFilePath("heartpiece_positions.json"), CheckPositionsFilePath,
                    HeartPieceCounterPersistenceId, HeartPieceValidInstances, Log);
            }

            try
            {
                _persistence.RegisterListener(realId, OnHeartPieceCounterChanged);
                Log.LogInfo("Registered Heart Piece counter listener.");
            }
            catch (Exception e)
            {
                Log.LogWarning("Failed to register Heart Piece counter listener: " + e);
            }

            _heartPieceInitialized = true;
            ReconcileHeartPieceCounter();
        }

        private void OnHeartPieceCounterChanged()
        {
            try
            {
                if (_suppressNextChangeFor.Remove(HeartPieceCounterPersistenceId)) return;
                ReconcileHeartPieceCounter();
            }
            catch (Exception e)
            {
                Log.LogWarning("Error handling Heart Piece counter change: " + e);
            }
        }

        private void ReconcileHeartPieceCounter()
        {
            // Same block, same reasoning as
            // ReconcilePaintFlaskCounter above.
            if (_apIntegrationBlockedForWrongFile) return;
            if (!_heartPieceInitialized) return;

            var realId = new ConPersistenceId(HeartPieceCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(HeartPieceLastKnownRealPersistenceId);
            int currentReal = _persistence.Save.Contains(realId) ? (_persistence.Save.GetInt(realId) ?? 0) : 0;

            // Same fix as
            // Paint Flask -- no more separate "category disabled" branch.
            // This is exactly what fixes the disguised-chest Heart Piece
            // dropping back to 0: the apworld now always guarantees a
            // matching Heart Piece item here (locked when
            // randomize_hearts is off, shuffled when on), so the mod
            // always suppresses+waits for AP's grant either way, with no
            // special unsuppressed path left to conflict with it.
            var bankedId = new ConPersistenceId(HeartPieceBankedPersistenceId);

            int banked = _persistence.Save.GetInt(bankedId) ?? 0;
            int lastKnownReal = _persistence.Save.Contains(lastKnownRealId) ? (_persistence.Save.GetInt(lastKnownRealId) ?? banked) : banked;

            int delta = currentReal - lastKnownReal;
            // Real correctness fix, needed now that this category is
            // suppressed unconditionally: a pickup the tracker cannot
            // attribute to any real AP location (the copies permanently
            // absorbed by chests are the known case -- there is one for
            // each of these three types, and with randomize_chests off
            // they are handed out as plain vanilla loot) must NOT be
            // clawed back. There is no AP location behind it, so no item
            // will ever arrive to give it back, and silently deleting a
            // legitimately-earned pickup is far worse than not reporting
            // a check for it. Banking it instead keeps the player's real
            // total intact while leaving the AP side untouched.
            int unattributed = 0;
            if (delta > 0)
            {
                Vector3? posOpt = GetCurrentPlayerPosition();
                for (int i = 0; i < delta; i++)
                {
                    int? instance = posOpt.HasValue
                        ? _heartPieceTracker.ResolvePickup(new Vector2(posOpt.Value.x, posOpt.Value.y))
                        : _heartPieceTracker.ResolvePickupWithoutPosition();
                    if (instance.HasValue)
                    {
                        ReportCheck(HeartPieceCounterPersistenceId + "#instance" + instance.Value);
                    }
                    else
                    {
                        unattributed++;
                        Log.LogInfo("ReconcileHeartPieceCounter: this pickup matches no remaining AP location (all " + HeartPieceMaxInstances + " already accounted for) -- keeping it as a plain vanilla pickup, no check reported.");
                    }
                }
                // Same removal, same reasoning as
                // ReconcilePaintFlaskCounter above.
            }

            if (unattributed > 0)
            {
                banked += unattributed;
                _suppressNextChangeFor.Add(HeartPieceBankedPersistenceId);
                _persistence.Save.SetInt(bankedId, banked, default(PersistenceEntry.Options));
            }

            if (currentReal != banked)
            {
                _suppressNextChangeFor.Add(HeartPieceCounterPersistenceId);
                _persistence.Save.SetInt(realId, banked, default(PersistenceEntry.Options));
            }

            if (lastKnownReal != banked)
            {
                _suppressNextChangeFor.Add(HeartPieceLastKnownRealPersistenceId);
                _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));
            }
        }

        private void GrantHeartPiece(string instanceKey = null)
        {
            if (WasInstanceProcessed(instanceKey))
            {
                Log.LogInfo("Heart Piece instance '" + instanceKey + "' already processed -- skipping (safe resend).");
                return;
            }

            var bankedId = new ConPersistenceId(HeartPieceBankedPersistenceId);
            var realId = new ConPersistenceId(HeartPieceCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(HeartPieceLastKnownRealPersistenceId);

            int banked = _persistence.Save.GetInt(bankedId) ?? 0;
            if (banked >= HeartPieceItemPoolSize)
            {
                MarkInstanceProcessed(instanceKey);
                return;
            }

            banked++;
            _suppressNextChangeFor.Add(HeartPieceBankedPersistenceId);
            _persistence.Save.SetInt(bankedId, banked, default(PersistenceEntry.Options));

            _suppressNextChangeFor.Add(HeartPieceCounterPersistenceId);
            _persistence.Save.SetInt(realId, banked, default(PersistenceEntry.Options));
            TriggerCollectAnimation(HeartPieceCounterPersistenceId, banked - 1, banked);

            _suppressNextChangeFor.Add(HeartPieceLastKnownRealPersistenceId);
            _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));

            MarkInstanceProcessed(instanceKey);
            UiLog("Received: Heart Piece (" + banked + "/" + HeartPieceItemPoolSize + ")");
            AddNotification("Received: Heart Piece (" + banked + "/" + HeartPieceItemPoolSize + ")");
            ClientLog("Received: Heart Piece (" + banked + "/" + HeartPieceItemPoolSize + ")");
            RefreshHealthCapacity();
        }

        // Eraser: full Paint-Flask-style system -- see that implementation
        // for the detailed reasoning, this mirrors it exactly.
        private void InitEraserTracking()
        {
            var checkCountId = new ConPersistenceId(EraserCheckCountPersistenceId);
            var bankedId = new ConPersistenceId(EraserBankedPersistenceId);
            var realId = new ConPersistenceId(EraserCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(EraserLastKnownRealPersistenceId);

            if (!_persistence.Save.Contains(bankedId))
            {
                int currentReal = _persistence.Save.Contains(realId) ? (_persistence.Save.GetInt(realId) ?? 0) : 0;
                _suppressNextChangeFor.Add(EraserBankedPersistenceId);
                _persistence.Save.SetInt(bankedId, currentReal, default(PersistenceEntry.Options));
                _suppressNextChangeFor.Add(EraserCheckCountPersistenceId);
                _persistence.Save.SetInt(checkCountId, currentReal, default(PersistenceEntry.Options));
            }

            if (!_persistence.Save.Contains(lastKnownRealId))
            {
                int banked = _persistence.Save.GetInt(bankedId) ?? 0;
                _suppressNextChangeFor.Add(EraserLastKnownRealPersistenceId);
                _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));
            }

            if (_eraserTracker == null)
            {
                _eraserTracker = new PositionBasedItemTracker(
                    TrackerFilePath("eraser_positions.json"), CheckPositionsFilePath,
                    EraserCounterPersistenceId, EraserValidInstances, Log);
            }

            try
            {
                _persistence.RegisterListener(realId, OnEraserCounterChanged);
                Log.LogInfo("Registered Eraser counter listener.");
            }
            catch (Exception e)
            {
                Log.LogWarning("Failed to register Eraser counter listener: " + e);
            }

            _eraserInitialized = true;
            ReconcileEraserCounter();
        }

        private void OnEraserCounterChanged()
        {
            try
            {
                if (_suppressNextChangeFor.Remove(EraserCounterPersistenceId)) return;
                ReconcileEraserCounter();
            }
            catch (Exception e)
            {
                Log.LogWarning("Error handling Eraser counter change: " + e);
            }
        }

        private void ReconcileEraserCounter()
        {
            // Same block, same reasoning as
            // ReconcilePaintFlaskCounter/ReconcileHeartPieceCounter above.
            if (_apIntegrationBlockedForWrongFile) return;
            if (!_eraserInitialized) return;

            var realId = new ConPersistenceId(EraserCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(EraserLastKnownRealPersistenceId);
            int currentReal = _persistence.Save.Contains(realId) ? (_persistence.Save.GetInt(realId) ?? 0) : 0;

            // Same fix as
            // Paint Flask/Heart Piece -- no more separate "category
            // disabled" branch, always suppress+wait for AP's guaranteed-
            // matching grant either way.
            var bankedId = new ConPersistenceId(EraserBankedPersistenceId);

            int banked = _persistence.Save.GetInt(bankedId) ?? 0;
            int lastKnownReal = _persistence.Save.Contains(lastKnownRealId) ? (_persistence.Save.GetInt(lastKnownRealId) ?? banked) : banked;

            int delta = currentReal - lastKnownReal;
            // Real correctness fix, needed now that this category is
            // suppressed unconditionally: a pickup the tracker cannot
            // attribute to any real AP location (the copies permanently
            // absorbed by chests are the known case -- there is one for
            // each of these three types, and with randomize_chests off
            // they are handed out as plain vanilla loot) must NOT be
            // clawed back. There is no AP location behind it, so no item
            // will ever arrive to give it back, and silently deleting a
            // legitimately-earned pickup is far worse than not reporting
            // a check for it. Banking it instead keeps the player's real
            // total intact while leaving the AP side untouched.
            int unattributed = 0;
            if (delta > 0)
            {
                Vector3? posOpt = GetCurrentPlayerPosition();
                for (int i = 0; i < delta; i++)
                {
                    int? instance = posOpt.HasValue
                        ? _eraserTracker.ResolvePickup(new Vector2(posOpt.Value.x, posOpt.Value.y))
                        : _eraserTracker.ResolvePickupWithoutPosition();
                    if (instance.HasValue)
                    {
                        ReportCheck(EraserCounterPersistenceId + "#instance" + instance.Value);
                    }
                    else
                    {
                        unattributed++;
                        Log.LogInfo("ReconcileEraserCounter: this pickup matches no remaining AP location (all " + EraserMaxInstances + " already accounted for) -- keeping it as a plain vanilla pickup, no check reported.");
                    }
                }
                // Same removal, same reasoning as
                // ReconcilePaintFlaskCounter above.
            }

            if (unattributed > 0)
            {
                banked += unattributed;
                _suppressNextChangeFor.Add(EraserBankedPersistenceId);
                _persistence.Save.SetInt(bankedId, banked, default(PersistenceEntry.Options));
            }

            if (currentReal != banked)
            {
                _suppressNextChangeFor.Add(EraserCounterPersistenceId);
                _persistence.Save.SetInt(realId, banked, default(PersistenceEntry.Options));
            }

            if (lastKnownReal != banked)
            {
                _suppressNextChangeFor.Add(EraserLastKnownRealPersistenceId);
                _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));
            }
        }

        private int EraserReceivedCount()
        {
            var receivedId = new ConPersistenceId(EraserReceivedCountPersistenceId);
            return _persistence != null && _persistence.Save.Contains(receivedId)
                ? (_persistence.Save.GetInt(receivedId) ?? 0)
                : 0;
        }

        private int EraserFoundCount()
        {
            var foundId = new ConPersistenceId(EraserFoundCountPersistenceId);
            return _persistence != null && _persistence.Save.Contains(foundId)
                ? (_persistence.Save.GetInt(foundId) ?? 0)
                : 0;
        }

        private void GrantEraser(string instanceKey = null)
        {
            if (WasInstanceProcessed(instanceKey))
            {
                Log.LogInfo("Eraser instance '" + instanceKey + "' already processed -- skipping (safe resend).");
                return;
            }

            var bankedId = new ConPersistenceId(EraserBankedPersistenceId);
            var realId = new ConPersistenceId(EraserCounterPersistenceId);
            var lastKnownRealId = new ConPersistenceId(EraserLastKnownRealPersistenceId);

            int banked = _persistence.Save.GetInt(bankedId) ?? 0;

            // No more separate
            // dedicated "received"/"found" counters that could drift out
            // of sync with banked or carry a stale value across test
            // sessions on the same save -- banked is already the single,
            // authoritative "total received" count, unified across both
            // real pickups and AP grants. The one free Eraser the game
            // hands out automatically at save start is grandfathered
            // straight into banked (InitEraserTracking), so it's always
            // exactly 1 higher than what the player actually received
            // through a location -- both the cap check and the displayed
            // count below just subtract that 1 directly, consistently,
            // in one place.
            if (banked - 1 >= EraserItemPoolSize)
            {
                MarkInstanceProcessed(instanceKey);
                return;
            }

            banked++;
            _suppressNextChangeFor.Add(EraserBankedPersistenceId);
            _persistence.Save.SetInt(bankedId, banked, default(PersistenceEntry.Options));

            _suppressNextChangeFor.Add(EraserCounterPersistenceId);
            _persistence.Save.SetInt(realId, banked, default(PersistenceEntry.Options));
            TriggerCollectAnimation(EraserCounterPersistenceId, banked - 1, banked);

            _suppressNextChangeFor.Add(EraserLastKnownRealPersistenceId);
            _persistence.Save.SetInt(lastKnownRealId, banked, default(PersistenceEntry.Options));

            MarkInstanceProcessed(instanceKey);
            int displayedReceived = Math.Max(0, banked - 1);
            UiLog("Received: Eraser (" + displayedReceived + "/" + EraserItemPoolSize + ")");
            AddNotification("Received: Eraser (" + displayedReceived + "/" + EraserItemPoolSize + ")");
            ClientLog("Received: Eraser (" + displayedReceived + "/" + EraserItemPoolSize + ")");
        }

        // Real (int) items only -- these are the ones the client can
        // actually apply live via SetInt. Filler has no persistence id.
        // No longer readonly -- ApplySettings below
        // rebuilds this whenever category settings arrive, adding/
        // removing ChestGuidIds/RegularBossIds based on the corresponding
        // toggle. AlwaysTrackedIds are never removed.
        private static HashSet<string> ApplicableItemIds = new HashSet<string>(AlwaysTrackedIds);

        // Per-category randomize toggles. Flask/Heart/
        // Eraser don't go through Collect()-based suppression at all
        // (confirmed: their persistence ids were never in
        // KnownPersistenceIds to begin with) -- they're handled entirely
        // by the separate counter-reconcile/claw-back mechanism, so their
        // toggle works differently: these flags gate the reconcile logic
        // directly (see ReconcilePaintFlaskCounter etc.), rather than
        // going through ApplicableItemIds like chests/bosses do.
        // Default true (matches existing behavior) until real settings
        // arrive from the live client, since the mod can start running
        // before that connection exists.
        // Only chests and bosses still have a
        // real mod-side "off" behaviour. Flasks/hearts/erasers (and
        // inspirations/health vials, which go through ApplicableItemIds)
        // are now ALWAYS suppressed, always position-resolved and always
        // reported, exactly as if their toggle were on -- the apworld
        // handles a disabled toggle by locking the vanilla item into the
        // same location instead of deleting it, so the round trip ends
        // with the player holding exactly the vanilla item either way.
        // This is the fix for non-randomized pickups going completely
        // untracked (no check, no map icon, no message).

        // Ids that are real, findable things in-game this
        // seed but are NOT Archipelago locations (because their category
        // isn't randomized). These still get tracked internally -- position
        // recorded, map icon marked found -- but are never reported to the
        // server and never produce any player-visible output. Rebuilt in
        // ApplySettings alongside ApplicableItemIds; empty by default,
        // since "everything is randomized" is the assumption until real
        // settings arrive.
        private static HashSet<string> NonApLocationIds = new HashSet<string>();

        // Paint Flask/Heart Piece/Eraser now
        // get the exact same treatment as chests/bosses/inspirations
        // when their category isn't randomized -- routed through
        // ReportCheck's existing NonApLocationIds mechanism instead of a
        // separate, custom "disabled branch" that bypassed
        // MarkLocallyChecked/the unified reported-check bookkeeping
        // entirely. Needs the full set of synthetic instance ids up
        // front (there's no fixed GUID list to union in like the other
        // categories have), built once here rather than recomputed
        // inline at every ApplySettings call.
        private static IEnumerable<string> BuildInstanceIds(string counterPersistenceId, int[] validInstances)
        {
            foreach (int instance in validInstances)
            {
                yield return counterPersistenceId + "#instance" + instance;
            }
        }

        // No longer takes randomizeAbilities/
        // Tears/FridaMask -- these 3 no longer need any mod-side
        // awareness at all (see AlwaysTrackedIds' comment above). The
        // apworld side still exposes all 9 toggles to the player; these
        // 3 are just purely a generation-time decision now, invisible to
        // the mod's own runtime behavior.
        // "not randomized" no
        // longer means "no AP location at all" for ANY category except
        // bosses -- the apworld now keeps every one of these as a real,
        // trackable AP location even when disabled, just with its item
        // LOCKED to the exact matching vanilla one (see locations.py's
        // place_locked_guaranteed_items). That guarantee only holds if
        // the mod ALWAYS reports these as real checks and ALWAYS
        // suppresses+waits for the AP grant -- so unlike before,
        // ApplicableItemIds/NonApLocationIds no longer branch on
        // randomizeChests/Flasks/Hearts/Erasers/Inspirations/HealthVials
        // at all, only on randomizeBosses (the one category still
        // genuinely excluded when off, since a boss defeat has no
        // natural "vanilla item" to lock in). randomizeChests itself is
        // gone entirely -- chests are unconditionally always real AP
        // locations now, no toggle exists for them anymore.
        internal static void ApplySettings(bool randomizeFlasks,
            bool randomizeHearts, bool randomizeErasers, bool randomizeBosses, bool randomizeInspirations,
            bool randomizeHealthVials)
        {
            var rebuilt = new HashSet<string>(AlwaysTrackedIds);
            rebuilt.UnionWith(ChestGuidIds);
            rebuilt.UnionWith(FlaskDisguisedChestGuidIds);
            rebuilt.UnionWith(HeartDisguisedChestGuidIds);
            rebuilt.UnionWith(InspirationIds);
            rebuilt.UnionWith(HealthVialIds);
            if (randomizeBosses) rebuilt.UnionWith(RegularBossIds);
            ApplicableItemIds = rebuilt;

            // Only bosses can still end up here now -- every other
            // category is always a real AP location regardless of its
            // toggle, so nothing else is ever "local only" anymore.
            var nonApLocations = new HashSet<string>();
            if (!randomizeBosses) nonApLocations.UnionWith(RegularBossIds);
            NonApLocationIds = nonApLocations;

            Log.LogInfo("Applied category settings -- flasks:" + randomizeFlasks +
                        " hearts:" + randomizeHearts + " erasers:" + randomizeErasers + " bosses:" + randomizeBosses +
                        " inspirations:" + randomizeInspirations + " healthVials:" + randomizeHealthVials +
                        " (ApplicableItemIds now has " + rebuilt.Count + " entries, NonApLocationIds " + nonApLocations.Count + ")");
        }

        // Parses the raw {"type":"settings",...} bridge
        // message and applies it. Separate from ApplySettings itself so
        // that method stays simple/directly testable with plain bools,
        // while all the JSON-specific handling (missing fields, parse
        // failure) lives here instead. randomize_abilities/tears are
        // intentionally read and then ignored -- present in the message
        // (the apworld still sends all 9, one fewer now that
        // randomize_frida_mask has been removed entirely -- Frida Mask
        // is always randomized), but no longer acted on here, see
        // ApplySettings' own comment for why.
        internal static void ApplySettingsFromJson(string json)
        {
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                // Randomize_chests no longer
                // read here -- the apworld stopped sending it entirely
                // (see world.py's fill_slot_data), chests are always
                // real, randomized AP locations now, no toggle exists.
                bool randomizeFlasks = obj["randomize_flasks"]?.ToObject<bool>() ?? true;
                bool randomizeHearts = obj["randomize_hearts"]?.ToObject<bool>() ?? true;
                bool randomizeErasers = obj["randomize_erasers"]?.ToObject<bool>() ?? true;
                bool randomizeBosses = obj["randomize_bosses"]?.ToObject<bool>() ?? true;
                bool randomizeInspirations = obj["randomize_inspirations"]?.ToObject<bool>() ?? true;
                bool randomizeHealthVials = obj["randomize_health_vials"]?.ToObject<bool>() ?? true;
                ApplySettings(randomizeFlasks, randomizeHearts, randomizeErasers, randomizeBosses,
                    randomizeInspirations, randomizeHealthVials);

                string seedId = obj["seed_id"]?.ToObject<string>() ?? "";
                HandleSeedChange(seedId);
            }
            catch (Exception e)
            {
                Log.LogWarning("Plugin: error parsing settings message (keeping previous/default settings): " + e);
            }
        }

        // Shrine
        // warp links were persisting even across a brand new save file and
        // a different Archipelago seed entirely -- wrong, since "has this
        // shrine been visited" is playthrough-specific progress, unlike
        // check_positions.jsonl's recorded positions (genuinely the same
        // regardless of seed, deliberately left alone). Compares the
        // incoming seed_id against a small marker file next to
        // shrine_links.jsonl; a mismatch (including "never seen a marker
        // before" -- an existing shrine_links.jsonl from before this fix
        // existed) means a genuinely different playthrough, and
        // shrine_links.jsonl gets cleared so it starts fresh.
        private static readonly string ShrineLinksSeedMarkerPath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "shrine_links_seed.txt");

        // Shrine links were
        // still persisting into a BRAND NEW SAVE FILE. The old check only
        // ever compared the AP seed, so starting a fresh file on the same
        // seed left shrine_links.jsonl completely untouched -- and since
        // the only shrines ever written to that file are the ones the
        // player actually meditated at, the symptom was exactly "the two
        // shrines we tested are still warpable, everything else is
        // correctly locked". Nothing was broken about the other shrines;
        // they had simply never been in the file to begin with.
        //
        // The seed alone can't detect this, because the seed genuinely
        // didn't change. So ownership of shrine_links.jsonl is now keyed
        // on TWO things: the AP seed AND a per-save-file token stored
        // inside the game's own persistence (a fresh file starts with no
        // token and gets a new random one). Either half changing means a
        // different playthrough, and the file is cleared.
        //
        // No longer stores its own custom
        // token in the save at all -- see RegisterShrineLinksSaveToken's
        // own comment for why (the custom value wasn't reliably
        // surviving the game's own save/reload cycle). Uses
        // CConPersistenceManager.SaveId, the game's own native save
        // identifier, instead.

        // Whichever of the two arrives first just records itself and
        // waits; the actual comparison runs as soon as both are known.
        // The settings message (seed) and persistence manager becoming
        // ready (save token) genuinely race with each other, in both
        // directions, so neither can be treated as "the" trigger.
        private static string _knownSeedId;
        private static string _knownSaveToken;

        private static void HandleSeedChange(string seedId)
        {
            if (string.IsNullOrEmpty(seedId))
            {
                Log.LogWarning("HandleSeedChange: no seed_id in settings message -- cannot detect a seed change, leaving shrine_links.jsonl as-is.");
                return;
            }
            _knownSeedId = seedId;
            //             // ReconcileShrineLinksOwnership used to run BEFORE
            // ValidateSaveSeedLink, meaning a save file that turns out
            // to be BLOCKED (wrong seed, or already-started/never-linked)
            // still got to write its own token into the shrine seed
            // marker first. The next time the player returned to their
            // actual, correct, valid save, that marker no longer matched
            // -- shrine_links.jsonl got wiped as a "different playthrough"
            // even though it was the exact same correct save the whole
            // time, just because a blocked file was briefly visited in
            // between. ValidateSaveSeedLink now runs FIRST, and
            // ReconcileShrineLinksOwnership only runs at all if this file
            // isn't blocked -- a blocked file can no longer touch shrine
            // tracking, exactly like it already can't apply items or
            // report checks.
            ValidateSaveSeedLink();
            if (!_apIntegrationBlockedForWrongFile) ReconcileShrineLinksOwnership();
        }

        /// Reads (or creates) this save file's own shrine-link token.
        /// Called once persistence manager is available; a new file has no
        /// token yet and gets a fresh random one written into it, which is
        /// what makes it distinguishable from every previous save.
        private void RegisterShrineLinksSaveToken()
        {
            try
            {
                if (_persistence == null) return;

                // Direct request/real, confirmed bug found by real
                // testing, and the actual root cause behind two specific
                // shrines never persisting no matter what was tried on
                // the matching/restoration side: this used to generate
                // and store its OWN random token inside a custom
                // persistence value (ps_ap_ShrineLinksSaveToken). Direct,
                // real evidence from a real log: the exact same save
                // produced two DIFFERENT tokens across two sessions
                // (same seed, confirmed by ReconcileShrineLinksOwnership
                // itself logging both together) -- meaning that custom
                // value was not reliably surviving the game's own save-
                // to-disk/reload cycle, silently forcing a fresh, random
                // token -- and therefore a full, incorrect
                // "different playthrough" wipe of shrine_links.jsonl --
                // on faithfully unchanged saves. Every previous fix in
                // this area (widening match tolerance, removing it
                // entirely, refusing to re-touch an already-linked
                // checkpoint) was papering over symptoms of THIS, the
                // actual disease.
                //
                // Fixed by using CConPersistenceManager's own, native
                // SaveId property instead -- confirmed to exist via
                // direct reflection on the game's own compiled assembly.
                // This is the game's own, vanilla-defined identifier for
                // which save this is; it doesn't depend on a custom,
                // mod-added value surviving whatever serialization path
                // the game's own save system uses, because it's already
                // part of that same system by definition.
                string saveId = _persistence.SaveId;
                if (string.IsNullOrEmpty(saveId))
                {
                    Log.LogWarning("RegisterShrineLinksSaveToken: CConPersistenceManager.SaveId is empty -- cannot reliably identify this save this session, leaving shrine link ownership as-is.");
                    return;
                }

                _knownSaveToken = saveId;
                // Same fix, same reasoning as
                // HandleSeedChange above -- validate first, only touch
                // shrine tracking if this file isn't blocked.
                ValidateSaveSeedLink();
                if (!_apIntegrationBlockedForWrongFile) ReconcileShrineLinksOwnership();
            }
            catch (Exception e)
            {
                Log.LogWarning("RegisterShrineLinksSaveToken: error reading this save's SaveId: " + e);
            }
        }

        /// Compares "seed + save file" against whatever produced the
        /// current shrine_links.jsonl, and clears it on any mismatch.
        /// Safe to call repeatedly and from either side of the race; it
        /// no-ops until both halves are known, and again once they match.
        private static void ReconcileShrineLinksOwnership()
        {
            try
            {
                if (string.IsNullOrEmpty(_knownSeedId) || string.IsNullOrEmpty(_knownSaveToken)) return;

                string current = _knownSeedId + "|" + _knownSaveToken;
                string previous = File.Exists(ShrineLinksSeedMarkerPath)
                    ? File.ReadAllText(ShrineLinksSeedMarkerPath).Trim()
                    : null;

                if (previous == current)
                {
                    Log.LogInfo("ReconcileShrineLinksOwnership: same seed and same save file as last time -- keeping existing shrine_links.jsonl.");
                    return;
                }

                // Reset in-memory links unconditionally, regardless of
                // whether the file existed: ApplyPersistedShrineLinks (run
                // from RunLoop) isn't guaranteed to happen after this, so
                // OwnShrineIcons can already be holding stale links even
                // when the file itself is about to be deleted.
                MapIconInjector.ResetAllShrineLinks();

                if (File.Exists(ShrineLinksFilePath))
                {
                    File.Delete(ShrineLinksFilePath);
                    Log.LogInfo("ReconcileShrineLinksOwnership: different playthrough (previous: " + (previous ?? "none") +
                                ", now: " + current + ") -- cleared shrine_links.jsonl.");
                }
                else
                {
                    Log.LogInfo("ReconcileShrineLinksOwnership: different playthrough (previous: " + (previous ?? "none") +
                                ", now: " + current + ") -- no existing shrine_links.jsonl to clear.");
                }

                // The position-based item trackers for Paint
                // Flask/Heart Piece/Eraser persist their own "known
                // position" and "already reported" state in these three
                // files -- but that file has never been scoped to any
                // particular seed at all, it just accumulates across
                // EVERY seed/session ever tested. After enough testing,
                // every single valid instance slot ends up with SOME
                // known position on file, even ones genuinely unreported
                // in the CURRENT seed -- which broke the fallback
                // assignment logic (fixed separately, in
                // PositionBasedItemTracker.cs itself) and, worse, could
                // leave instances still marked "reported" from a
                // completely unrelated earlier seed, undercounting real
                // progress in this one. A genuinely different playthrough
                // needs a clean slate here for exactly the same reason
                // shrine_links.jsonl does -- cleared together, in the
                // same reconciliation pass, rather than treating this as
                // a separate concern.
                foreach (string trackerFile in new[] { "paintflask_positions.json", "heartpiece_positions.json", "eraser_positions.json" })
                {
                    string path = TrackerFilePath(trackerFile);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        Log.LogInfo("ReconcileShrineLinksOwnership: also cleared '" + trackerFile + "' for this new playthrough (position-based item tracker state, same reasoning as shrine_links.jsonl).");
                    }
                }

                File.WriteAllText(ShrineLinksSeedMarkerPath, current);
            }
            catch (Exception e)
            {
                Log.LogWarning("ReconcileShrineLinksOwnership: error checking/clearing shrine links for a new playthrough: " + e);
            }
        }

        // The player's own suggestion --
        // once a save file is linked to a seed, that link is permanent
        // and the ONLY way to break it is deleting the file. Broader
        // than shrine_links.jsonl cleanup (ReconcileShrineLinksOwnership
        // above, which stays exactly as-is and unrelated to this): this
        // one gates EVERY piece of AP integration, not just shrine data.
        //
        // Considered, and rejected, actually blocking file SELECTION at
        // the main menu itself (closer to the literal suggestion) --
        // that needs a Harmony patch on whatever UI component handles
        // save-slot selection, which isn't something safe to guess at
        // blind without live testing to verify the exact hook point.
        // This achieves the same real protection a different way: catch
        // it the moment the save becomes active (the earliest point this
        // mod can reliably detect anything at all) and refuse EVERY
        // AP-facing operation for the rest of the session if the file
        // doesn't belong to the connected seed -- no checks sent, no
        // items applied, nothing that could corrupt either the wrong
        // seed's tracking or this save's own state. The player can still
        // technically load the wrong file, but nothing damaging can
        // happen once they do; the fix is "impossible to corrupt", if
        // not literally "impossible to click".
        //
        // The link itself lives in a small, append-only, NEVER-cleared
        // file (distinct from the shrine seed marker, which intentionally
        // gets overwritten on every change) -- one entry per save token,
        // written once and never touched again except by reading it back.
        // A save file that's never been seen before gets linked to
        // whatever seed is currently connected, permanently, the first
        // time persistence hooks with a known seed already present.
        private static readonly string SaveSeedLinksFilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "save_seed_links.jsonl");

        // While true, ReportCheck/ApplyItem/ApplyFiller/
        // OnPersistenceChanged/every Reconcile*Counter all refuse to do
        // anything at all -- the actual enforcement mechanism. Starts
        // false (never blocks a session where the check hasn't run yet,
        // e.g. very early before persistence/seed are both known) and
        // can only ever become true, never reset back to false within a
        // session -- there's no legitimate way to "fix" a mismatch
        // without restarting the game against the correct file anyway.
        internal static bool _apIntegrationBlockedForWrongFile = false;
        private static string _apIntegrationBlockedReason = null;

        /// Permanent record of which seed a save file belongs to, checked
        /// (and created, for a genuinely new file) every time both the
        /// save token and the connected seed are known. Runs from the
        /// same two race-prone trigger points as
        /// ReconcileShrineLinksOwnership, for the same reason: either the
        /// settings message or persistence hooking can legitimately win
        /// the race, in either direction.
        private static void ValidateSaveSeedLink()
        {
            try
            {
                if (string.IsNullOrEmpty(_knownSeedId) || string.IsNullOrEmpty(_knownSaveToken)) return;
                if (_apIntegrationBlockedForWrongFile) return; // already decided, permanently, for this session

                string linkedSeed = ReadLinkedSeedForToken(_knownSaveToken);
                if (linkedSeed == null)
                {
                    // No longer auto-links
                    // unconditionally just because this save token has
                    // never been seen before -- that covered a genuinely
                    // brand new file correctly, but ALSO silently
                    // auto-linked an already-started save (played before
                    // ever connecting to AP, or brought in from somewhere
                    // else) to whatever seed just happened to be
                    // connected the first time this mod saw it. That's
                    // exactly the mistaken-file scenario this whole
                    // system exists to prevent, just occurring at the
                    // link-creation step instead of at a later mismatch.
                    //
                    // Paintbrush is the one thing every single player
                    // must have to do anything at all in the game -- a
                    // save that already has it unlocked the very first
                    // moment this mod ever sees that save token is
                    // unambiguous proof of real, prior progress, fresh
                    // link or not. Refuses to link (and thus blocks
                    // exactly like a genuine mismatch would) rather than
                    // ever guessing that an already-progressed file was
                    // meant to start this seed.
                    bool alreadyStarted = _instance != null && _instance._persistence != null && IsTruthyStatic("ps_unlock_Ability_Brush");
                    if (alreadyStarted)
                    {
                        _apIntegrationBlockedForWrongFile = true;
                        // Two different messages for two
                        // different real situations. If this exact seed
                        // already has some OTHER save linked to it
                        // somewhere, the player has a specific, correct
                        // file to go find -- "pick a new file" would be
                        // actively wrong advice there, since starting
                        // ANOTHER new file wouldn't help at all (this
                        // seed already has its one file). If it doesn't,
                        // there's genuinely nothing existing to point
                        // them back to yet, so "pick a new file" (start
                        // fresh) is the correct instruction.
                        _apIntegrationBlockedReason = SeedHasAnyLinkedSave(_knownSeedId)
                            ? "Please pick your Archipelago file! Not connected to this save."
                            : "Please pick a new file! Not connected to this save.";
                        Log.LogWarning("ValidateSaveSeedLink: save token '" + _knownSaveToken + "' has never been linked to a seed, but this save already has real progress (Paintbrush unlocked) -- refusing to auto-link it, BLOCKING all AP integration for this session. Start a genuinely new file to use seed '" + _knownSeedId + "'.");
                        return;
                    }

                    // Genuinely fresh -- link it to the currently
                    // connected seed, permanently.
                    File.AppendAllText(SaveSeedLinksFilePath,
                        "{\"saveToken\":\"" + _knownSaveToken + "\",\"seedId\":\"" + _knownSeedId.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}" + Environment.NewLine);
                    Log.LogInfo("ValidateSaveSeedLink: save token '" + _knownSaveToken + "' had no linked seed yet, and no prior progress found -- linked it to '" + _knownSeedId + "' permanently.");
                    return;
                }

                if (linkedSeed == _knownSeedId)
                {
                    Log.LogInfo("ValidateSaveSeedLink: save token '" + _knownSaveToken + "' is correctly linked to the connected seed -- proceeding normally.");
                    return;
                }

                // Mismatch: this save file belongs to a DIFFERENT seed
                // than the one currently connected. Block everything,
                // permanently, for this session -- see the comment on
                // _apIntegrationBlockedForWrongFile above for why this
                // (rather than a menu-level block) is the actual
                // enforcement.
                _apIntegrationBlockedForWrongFile = true;
                // Same distinction as the already-started
                // branch above -- if the CONNECTED seed already has its
                // own correct save somewhere, point the player at it
                // rather than telling them to start yet another new file.
                _apIntegrationBlockedReason = SeedHasAnyLinkedSave(_knownSeedId)
                    ? "Please pick your Archipelago file! Not connected to this save."
                    : "Please pick a new file! Not connected to this save.";
                Log.LogWarning("ValidateSaveSeedLink: save token '" + _knownSaveToken + "' is linked to seed '" + linkedSeed +
                               "', but the CONNECTED seed is '" + _knownSeedId + "' -- BLOCKING all AP integration for this session.");
            }
            catch (Exception e)
            {
                Log.LogWarning("ValidateSaveSeedLink: error validating save/seed link: " + e);
            }
        }

        // Static wrapper around the same IsTruthy logic already used
        // elsewhere, since ValidateSaveSeedLink is static (called from
        // both instance and static contexts) and can't reach the
        // instance-only IsTruthy directly -- goes through the singleton
        // _instance reference instead.
        private static bool IsTruthyStatic(string idString)
        {
            if (_instance == null || _instance._persistence == null) return false;
            var id = new ConPersistenceId(idString);
            var save = _instance._persistence.Save;
            if (!save.Contains(id)) return false;

            int? asInt = save.GetInt(id);
            if (asInt.HasValue) return asInt.Value != 0;

            bool? asBool = save.GetBool(id);
            if (asBool.HasValue) return asBool.Value;

            return false;
        }

        // Distinguishes the two real reasons a save can
        // end up blocked, for a clearer message in each case. If this
        // exact seed already has SOME save linked to it somewhere, the
        // player has a real, specific correct file to go find -- if it
        // doesn't, "pick a new file" is the more accurate instruction
        // since there's nothing existing to point them back to yet.
        private static bool SeedHasAnyLinkedSave(string seedId)
        {
            if (string.IsNullOrEmpty(seedId) || !File.Exists(SaveSeedLinksFilePath)) return false;
            foreach (string line in File.ReadLines(SaveSeedLinksFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (ExtractStringFieldStatic(line, "seedId") == seedId) return true;
            }
            return false;
        }

        private static string ReadLinkedSeedForToken(string saveToken)
        {
            if (!File.Exists(SaveSeedLinksFilePath)) return null;
            string latest = null;
            foreach (string line in File.ReadLines(SaveSeedLinksFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string token = ExtractStringFieldStatic(line, "saveToken");
                if (token != saveToken) continue;
                string seed = ExtractStringFieldStatic(line, "seedId");
                if (seed != null) latest = seed; // last entry for this token wins, same convention as everywhere else
            }
            return latest;
        }

        // Local copy of the same manual field-extraction pattern used
        // throughout this project (ConBridgeServer.ExtractField,
        // MapIconInjector.ExtractStringField) -- those are private to
        // their own classes, so this is its own small copy rather than
        // reaching across class boundaries for something this simple.
        private static string ExtractStringFieldStatic(string line, string fieldName)
        {
            string key = "\"" + fieldName + "\"";
            int keyIndex = line.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colonIndex = line.IndexOf(':', keyIndex + key.Length);
            if (colonIndex < 0) return null;
            int firstQuote = line.IndexOf('"', colonIndex + 1);
            if (firstQuote < 0) return null;
            int i = firstQuote + 1;
            var result = new System.Text.StringBuilder();
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    result.Append(line[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '"') return result.ToString();
                result.Append(c);
                i++;
            }
            return null;
        }

        private static Plugin _instance;
        private CConPersistenceManager _persistence;
        private readonly HashSet<string> _reportedChecks = new HashSet<string>();
        private readonly HashSet<string> _appliedItems = new HashSet<string>();

        /// Persistence ids whose next RegisterListener callback should be
        /// ignored, because we ourselves are about to write them (via
        /// ApplyItem), not the player. Without this, applying a received
        /// item would fire our own listener and get misreported as a live
        /// check for whatever location shares that same persistence id --
        /// a real feedback loop caught by real testing: applying Paint
        /// Clone spuriously re-triggered a check for its own altar
        /// location, which then received a second (filler) item back.
        private readonly HashSet<string> _suppressNextChangeFor = new HashSet<string>();

        private readonly List<string> _uiLog = new List<string>();
        private const int MaxUiLogLines = 14;
        private string _lastLoggedOnGuiTooltipText = null;

        // Separate, second overlay per direct request: bottom-left,
        // mirroring the same "Aplana found their X (Y)" style messages
        // shown in the AP Text Client, in the same colors, rendered
        // in-game -- no background box (transparent), distinct from the
        // top-right mod status/check log above.
        private readonly List<string> _notificationLog = new List<string>();
        private const int MaxNotificationLines = 8;
        private GUIStyle _richTextLabelStyle;

        internal void AddNotification(string richText)
        {
            _notificationLog.Add(richText);
            while (_notificationLog.Count > MaxNotificationLines)
                _notificationLog.RemoveAt(0);
        }

        private ConBridgeServer _bridge;
        private Harmony _harmony;

        private void Awake()
        {
            // Minimal, additive static reference to this
            // singleton instance -- BepInEx only ever creates one Plugin
            // per game process, so this is safe. Needed so static methods
            // (ValidateSaveSeedLink, called from both instance and static
            // contexts) can read instance-only state like _persistence
            // without the much larger, riskier change of converting
            // _persistence itself to static and re-verifying every one of
            // its many existing call sites.
            _instance = this;

            Log = Logger;
            Log.LogInfo("Constance Archipelago mod loading...");

            // Real, plausible bug being investigated: a log where
            // WaitForPersistenceManager's every-1-second retry loop
            // simply stopped forever, with no further attempts ever
            // logged, even though the game kept running. A coroutine
            // going permanently silent like that matches exactly what
            // happens if its host GameObject gets destroyed during a
            // scene transition (menu -> gameplay, for instance).
            // BepInEx generally protects its own plugin objects across
            // scene loads by default, but that's not something to lean
            // on without verifying it -- explicitly marking this object
            // persistent here is a cheap, safe, direct fix for exactly
            // this failure mode, regardless of whether it was the actual
            // cause or BepInEx was already handling it.
            UnityEngine.Object.DontDestroyOnLoad(gameObject);

            // Reveal the whole map from the start.
            // Confirmed via IL this is a REAL, developer-intended debug
            // flag (Constance.ConDebugFlags.DebugRevealMap), checked
            // directly at the top of the exact method (CheckVisibleFlags,
            // called from UpdateVisible) that decides whether a level's
            // shape is shown on the map at all -- when true, that check
            // short-circuits and returns "visible" unconditionally,
            // before even looking at VisitedPersistenceId. Far more
            // direct and reliable than the earlier attempts at
            // reverse-engineering this behavior through the fog shader
            // material.
            //
            // Deliberately does NOT touch ConDebugFlags.UnlockEverything,
            // a similarly real flag sitting right next to this one --
            // that one would bypass all randomized progression gating
            // entirely, which would break the point of the randomizer.
            // This one is purely a map/fog visual, unrelated to item or
            // ability progression.
            Constance.ConDebugFlags.DebugRevealMap = true;
            Log.LogInfo("Set ConDebugFlags.DebugRevealMap = true (full map reveal).");

            // Confirmed root cause of a real, reported crash: this used
            // to sit at the very end of Awake(), after all the Harmony
            // patching -- when that patching got deferred to a later
            // frame (the fix below, for a DIFFERENT crash), this got
            // dragged along with it purely because of where it happened
            // to sit in the file, not because it needed deferring at
            // all. It only sets MapIconInjector's own Log field and
            // starts a coroutine -- trivial, instant work, nothing like
            // the actual expensive Harmony IL rewriting. But the bridge
            // server below starts accepting and queuing messages
            // immediately, and Plugin.Update() processes that queue every
            // frame regardless of whether the deferred coroutine has run
            // yet -- so if the Python client connected and sent a
            // location_states message fast enough, MapIconInjector.Log
            // was still null when a handler tried to log a warning
            // through it, throwing a NullReferenceException from inside
            // what looked like a fully caught try/catch. Moved back
            // ahead of the bridge starting, closing that window
            // completely.
            MapIconInjector.Start(Log, this);

            _bridge = new ConBridgeServer(this);
            _bridge.Start();

            // Intermittent crashes during the game's own
            // very first scene load (its own "SceneRegistry" service
            // failing to register in time -- a race condition, confirmed
            // by a Player.log capturing the exact failure, and confirmed
            // to be mod-related by the crash disappearing entirely across
            // repeated launches with the mod removed). Harmony patching
            // -- 13 separate patches below, each involving reflection,
            // real IL generation, and JIT recompilation of the target
            // method -- is genuinely CPU-intensive, synchronous work,
            // and it was ALL happening directly in Awake(), which
            // BepInEx's Chainloader calls (and blocks on) before the
            // game's own boot sequence even starts running its own code.
            // That's squarely inside the same narrow timing window the
            // race was observed in. Deferred to run one frame later
            // instead -- letting Awake() itself return immediately means
            // Chainloader finishes loading plugins and the game's own
            // Boot sequence can start on schedule, without this mod's own
            // patching work competing with it for CPU time during
            // exactly the window that race condition depends on.
            StartCoroutine(DeferredHarmonyPatching());
        }

        private IEnumerator DeferredHarmonyPatching()
        {
            // Direct request/real, confirmed contributing factor found by
            // real testing, a second time: one frame turned out to still
            // be a fairly tight margin -- strengthened to a real, fixed
            // time delay instead, giving the game's own boot sequence
            // meaningfully more breathing room before this mod's own
            // patching work starts competing with it for CPU time.
            // WaitForSecondsRealtime specifically (not WaitForSeconds) --
            // doesn't depend on Time.timeScale, which isn't necessarily
            // even meaningfully initialized this early in the game's own
            // startup.
            yield return new WaitForSecondsRealtime(1.5f);

            _harmony = new Harmony(PluginGuid);
            AbilityUnlockPatch.IsTrackedPersistenceId = id => ApplicableItemIds.Contains(id);
            // Design update, per direct correction: no general live safety
            // net. Abilities are fully, genuinely randomized -- the altar
            // is just another disconnected check, not tied to the ability
            // it happens to give in vanilla. Reaching a location that
            // needs an ability nobody has yet is now purely a logic
            // problem (see rules.py), not something patched around live.
            //
            // TWO deliberate, narrow exceptions: Paintbrush and Paint
            // Dive. Both stay permanently locked to their own altars
            // (see the apworld's locations.py, per direct request) --
            // meaning we know with total certainty, regardless of any AP
            // round-trip timing, exactly what belongs at each. Real
            // testing found genuine, repeated cases where a slow AP-server
            // handshake (a separate connection from the mod bridge, can
            // legitimately still be authenticating tens of seconds into a
            // session) meant Paintbrush's real grant never arrived in a
            // reasonable window. Granting these two directly the moment
            // their altar is touched removes any dependency on that round
            // trip for these always-safe cases. Idempotent by construction
            // (reuses the same IsTruthy/MarkGrantedByUs plumbing ApplyItem
            // already uses) -- the "official" AP grant arriving later just
            // correctly no-ops.
            var lockedAbilitySafetyNetIds = new Dictionary<string, string>
            {
                { "ps_unlock_Ability_Brush", "Paintbrush" },
                { "ps_unlock_Ability_Dash", "Paint Dive" },
            };
            AbilityUnlockPatch.OnVanillaGrantSuppressed = id =>
            {
                ReportCheck(id);
                if (lockedAbilitySafetyNetIds.TryGetValue(id, out string displayName))
                {
                    GrantLockedAbilitySafetyNet(id, displayName);
                }
            };
            AbilityUnlockPatch.ApplyPatch(_harmony);

            // Spreads the 8 independent patch groups
            // below across multiple frames instead of applying all of
            // them in one single burst -- each involves its own
            // reflection lookup, IL generation, and JIT recompilation,
            // the same category of work the earlier real-time delay
            // exists to keep away from the game's own critical boot
            // window. A fixed delay before starting reduces WHEN this
            // work competes for CPU time; spreading it out reduces HOW
            // MUCH lands in any single frame once it does start. Safe to
            // split this way specifically because every patch group
            // targets a completely different, independent game method --
            // no patch here depends on another one already being active,
            // and the game is still sitting at (or just past) its own
            // main menu for this entire window, well before any real
            // gameplay a still-unpatched method could matter for is even
            // reachable. Each delegate assignment stays paired with its
            // own ApplyPatch call on the same frame; only the gap BETWEEN
            // pairs spreads out.
            yield return null;

            // Still needed even without the safety net: the altar's own
            // underlying flag can still become true from an AP grant
            // that came from a completely different location, and the
            // altar must not disable itself as a result -- it's still a
            // real, independent check that needs to stay reachable.
            AbilityAltarStayInteractivePatch.IsTrackedPersistenceId = id => ApplicableItemIds.Contains(id);
            // Per direct request, spread even further than before -- this
            // one class alone used to apply 5 separate Harmony patches
            // (the single most concentrated group of them) in one
            // uninterrupted burst. Split into its own 6 individually-
            // callable pieces (see AbilityAltarStayInteractivePatch's own
            // comment), each getting its own frame here instead.
            AbilityAltarStayInteractivePatch.ApplyPatchSetupFields(_harmony);
            yield return null;
            AbilityAltarStayInteractivePatch.ApplyPatchOnTouchStart(_harmony);
            yield return null;
            AbilityAltarStayInteractivePatch.ApplyPatchCanvasStart(_harmony);
            yield return null;
            AbilityAltarStayInteractivePatch.ApplyPatchOnTouchHandleIncomingAttack(_harmony);
            yield return null;
            AbilityAltarStayInteractivePatch.ApplyPatchCanvasHandleIncomingAttackAndHas(_harmony);
            yield return null;
            AbilityAltarStayInteractivePatch.ApplyPatchInspirationHandleIncomingAttack(_harmony);
            yield return null;

            // Full map reveal from the start.
            MapFullRevealPatch.ApplyPatch(_harmony);
            yield return null;
            RoomChangeMapRevealPatch.ApplyPatch(_harmony);
            yield return null;
            ShrineMeditatePatch.ApplyPatch(_harmony);
            yield return null;

            DeathDetectionPatch.OnLocalPlayerDeath = OnLocalPlayerDeath;
            DeathDetectionPatch.ApplyPatch(_harmony);
            yield return null;

            DropCollectPatch.IsTrackedPersistenceId = id => ApplicableItemIds.Contains(id);
            DropCollectPatch.OnVanillaGrantSuppressed = id => ReportCheck(id);
            DropCollectPatch.ApplyPatch(_harmony);
            yield return null;

            ChestLootPatch.IsTrackedPersistenceId = id => ApplicableItemIds.Contains(id);
            ChestLootPatch.OnVanillaGrantSuppressed = id => ReportCheck(id);
            ChestLootPatch.ApplyPatch(_harmony);

            StartCoroutine(WaitForPersistenceManager());
        }

        private void OnDestroy()
        {
            _bridge?.Stop();
            _harmony?.UnpatchSelf();
        }

        private IEnumerator WaitForPersistenceManager()
        {
            // This used to be a one-shot loop that
            // permanently exited the moment _persistence was first found
            // non-null. But CConPersistenceManager is a real Unity object
            // (extends ConMonoBehaviour, even implements
            // IConSceneListener_ChangeLevel -- confirmed via IL, strongly
            // suggesting it's genuinely scene-scoped). Unity overrides ==
            // for its own objects so a DESTROYED object compares as ==
            // null even though the C# reference itself was never
            // explicitly cleared -- meaning _persistence could silently
            // "become null" again later in a session (e.g. after a scene
            // transition), well after this coroutine had already
            // succeeded once and stopped checking forever. Real symptom
            // this explains: checks kept firing correctly (suppression
            // doesn't need persistence manager), but nothing further ever
            // actually got granted for the rest of the session, since
            // nothing was left running to notice persistence had become
            // invalid and re-acquire it. This loop now runs indefinitely,
            // continuously re-validating and re-hooking whenever that
            // happens, instead of exiting for good after the first
            // success.
            UiLog("Waiting for game to finish loading...");
            int waitingAttempts = 0;
            bool wasValid = false;

            while (true)
            {
                yield return new WaitForSeconds(1f);

                bool isCurrentlyNull = (_persistence == null);

                if (isCurrentlyNull)
                {
                    if (wasValid)
                    {
                        Log.LogWarning("Persistence manager reference became invalid (likely a scene transition destroyed it) -- re-acquiring.");
                        waitingAttempts = 0;
                        wasValid = false;

                        // The save-seed block was static and
                        // never reset, so leaving a wrongly-blocked file
                        // for the main menu and starting a genuinely NEW
                        // file kept the block (and its banner) active for
                        // that new file too -- AP integration stayed
                        // disabled even though the new file had every
                        // right to be linked fresh. The mod itself never
                        // restarts when the player returns to the main
                        // menu (BepInEx plugins persist across scene
                        // loads by design), so nothing was ever going to
                        // reset these on its own. This exact transition
                        // -- persistence manager going from valid to
                        // invalid -- is the earliest, most reliable
                        // signal that the player has genuinely left the
                        // previous save, so the save-specific state gets
                        // cleared right here. _knownSeedId is
                        // deliberately left alone -- the AP connection
                        // itself doesn't change just because the game's
                        // own save state did.
                        _persistence = null;
                        _knownSaveToken = null;
                        _apIntegrationBlockedForWrongFile = false;
                        _apIntegrationBlockedReason = null;
                        // _appliedItems (an in-memory, per-mod-
                        // instance set, never itself persisted) is what
                        // ApplyItem uses to avoid re-applying the same id
                        // twice -- but the mod instance survives scene
                        // transitions by design, so it never reset on its
                        // own either, for exactly the same underlying
                        // reason the seed-link state above didn't. An id
                        // already applied to the PREVIOUS save (like
                        // ps_unlock_MapIcons) would then be silently
                        // skipped for a genuinely different save that
                        // hasn't had it set yet at all. Cleared here too.
                        _appliedItems.Clear();
                        // Same reasoning, same fix -- ReportCheck's own
                        // in-memory dedup cache, layered on top of a
                        // properly per-save persisted marker
                        // (ps_ap_reported_*), but itself just as
                        // vulnerable to surviving across a save switch it
                        // was never scoped to.
                        _reportedChecks.Clear();
                        Log.LogInfo("Cleared save-specific seed-link state (save token, integration block, applied-items cache, reported-checks cache) for the new save/session about to load.");
                    }

                    waitingAttempts++;
                    TryHookPersistence();
                    if (_persistence == null && waitingAttempts % 15 == 0)
                    {
                        // Periodic status so a long/stuck wait is visible in the
                        // log instead of going silent -- real feedback described
                        // items queuing "but never releasing them" for an entire
                        // session, and there was no way to tell from the log
                        // whether this coroutine was still trying, or how long
                        // it had been stuck, or which specific piece was null.
                        bool registryNull = CConSceneRegistry.Instance == null;
                        Log.LogInfo("Still waiting for persistence manager after " + waitingAttempts +
                                    "s (registry null: " + registryNull + "). " +
                                    _pendingApplyRequests.Count + " apply request(s) queued so far.");
                    }
                }
                else
                {
                    wasValid = true;
                }
            }
        }

        // Real bug, found by real testing: if a player receives an item
        // very early in a session (persistence manager can take well over
        // a minute to initialize -- confirmed in real testing), ApplyItem/
        // ApplyFiller used to just log a warning and silently drop the
        // request. The Python client has no confirmation protocol -- it
        // marks an item "applied" the instant it SENDS the request, not
        // when the mod confirms success -- so a dropped request was
        // permanently lost, with only a check ever visible, never the
        // matching "Received: ..." line. Queuing early requests and
        // flushing them once the persistence manager is actually found
        // fixes this at the real source, rather than needing a retry
        // protocol on the Python side.
        // Item4 carries the client-supplied filler amount (as text, null
        // when there isn't one) so a grant queued before persistence
        // manager was ready still applies the exact quantity that was
        // already announced, rather than quietly re-rolling on flush.
        private readonly List<Tuple<string, string, string, string>> _pendingApplyRequests = new List<Tuple<string, string, string, string>>();

        // Real, confirmed bug found by real testing --
        // Map Icons only ever actually applied to the FIRST save file it
        // was ever sent to. The Python client marks a precollected grant
        // "already applied" the moment it's sent, tracked globally for
        // the whole session/seed -- but ps_unlock_MapIcons is a PER-SAVE
        // persistence value, not a global one. A second, later save file
        // linked to the exact same seed never gets its own apply_filler/
        // apply resent at all, since the client correctly (from its own
        // point of view) believes this item was already delivered once
        // already.
        //
        // Rather than try to teach the
        // Python client "this specific grant needs to be tracked
        // per-save instead of per-seed" (a much larger, riskier change
        // touching the general apply-tracking system every other item
        // relies on), this is a simple, targeted safety net instead --
        // called every single time persistence hooks (i.e. every time
        // ANY save file, new or returning, becomes the active one),
        // unconditionally ensuring this specific, always-guaranteed item
        // is set correctly for THIS save, regardless of whatever the
        // Python client's own tracking believes happened before.
        // Skipped entirely if this file is currently blocked (wrong
        // seed/already-started) -- forcing this on for a file that
        // shouldn't have any AP state at all would defeat that whole
        // protection.
        //
        // Camera removed from this list
        // entirely -- an earlier round mistook it for the same
        // precollected pattern as Map Icons and force-granted it here
        // too, but Camera was never meant to be free from the start at
        // all. It's a genuinely ordinary check: ps_unlock_Camera is
        // deliberately left unsuppressed (see UnsuppressedTrackedIds'
        // own comment) specifically so the real, vanilla game grants it
        // naturally when the player actually reaches that spot -- forcing
        // it here defeated that entirely, handing it out for free
        // regardless of whether the player had actually been there yet.
        private void EnsureAlwaysGrantedItems()
        {
            try
            {
                if (_apIntegrationBlockedForWrongFile) return;

                // This used to duplicate the suppress+SetInt
                // logic itself, but skipped MarkGrantedByUs -- the one
                // piece of bookkeeping that tells ReportAlreadyTrueLocations
                // "this flag being true is a GRANT, not a discovery."
                // ps_unlock_MapIcons is both the item's own flag AND
                // "Map Icons Pickup"'s own location flag (same shared id,
                // same pattern as Camera) -- without that marker, the
                // very next time persistence hooked (e.g. a later scene
                // transition, or returning to this same save), 
                // ReportAlreadyTrueLocations saw the flag sitting true
                // with no "granted by us" marker on it and reported it as
                // a genuine location discovery -- sending a real, spurious
                // "Map Icons Pickup" check to the server for a location
                // the player never actually visited. Fixed the same way
                // GrantLockedAbilitySafetyNet already does it for
                // Paintbrush/Paint Dive: call ApplyItem directly instead
                // of reimplementing any part of its logic -- it already
                // marks the grant correctly, queues itself if persistence
                // isn't ready, and safely no-ops if this exact id was
                // somehow already applied.
                ApplyItem("ps_unlock_MapIcons");
            }
            catch (Exception e)
            {
                Log.LogWarning("EnsureAlwaysGrantedItems: error ensuring always-granted items: " + e);
            }
        }

        private void TryHookPersistence()
        {
            try
            {
                IConSceneRegistry registry = CConSceneRegistry.Instance;
                if (registry == null) return;

                CConPersistenceManager manager = registry.Persistence;
                if (manager == null) return;

                _persistence = manager;
                UiLog("Connected to game persistence manager.");
                HookListeners();
                RegisterShrineLinksSaveToken();
                ReportAlreadyTrueLocations();
                EnsureAlwaysGrantedItems();

                if (_pendingApplyRequests.Count > 0)
                {
                    Log.LogInfo("Flushing " + _pendingApplyRequests.Count + " apply request(s) that arrived before persistence manager was ready.");
                    var pending = new List<Tuple<string, string, string, string>>(_pendingApplyRequests);
                    _pendingApplyRequests.Clear();
                    foreach (var request in pending)
                    {
                        if (request.Item1 == "apply")
                            ApplyItem(request.Item2, request.Item3);
                        else if (request.Item1 == "apply_filler")
                        {
                            int pendingAmount;
                            ApplyFiller(request.Item2, request.Item3,
                                (!string.IsNullOrEmpty(request.Item4) && int.TryParse(request.Item4, out pendingAmount) && pendingAmount > 0)
                                    ? (int?)pendingAmount : null);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Error while trying to hook persistence manager: " + e);
            }
        }

        private void HookListeners()
        {
            foreach (string idString in KnownPersistenceIds)
            {
                string captured = idString; // capture for closure
                try
                {
                    var id = new ConPersistenceId(captured);
                    _persistence.RegisterListener(id, () => OnPersistenceChanged(captured));
                }
                catch (Exception e)
                {
                    Log.LogWarning("Failed to register listener for " + captured + ": " + e);
                }
            }
            Log.LogInfo("Registered " + KnownPersistenceIds.Length + " persistence listeners.");

            try
            {
                var counterId = new ConPersistenceId(PaintFlaskCounterPersistenceId);
                _persistence.RegisterListener(counterId, OnPaintFlaskCounterChanged);
                Log.LogInfo("Registered Paint Flask counter listener.");
            }
            catch (Exception e)
            {
                Log.LogWarning("Failed to register Paint Flask counter listener: " + e);
            }
        }

        private void OnPaintFlaskCounterChanged()
        {
            try
            {
                // Real bug, found by real testing: GrantPaintFlask() already
                // added this id to _suppressNextChangeFor before writing, but
                // nothing ever checked/consumed it here -- so receiving a
                // Paint Flask fired this reconciliation unsuppressed, which
                // could misread the grant itself as a brand-new real pickup
                // (since ReconcilePaintFlaskCounter only compares the real
                // counter's value to how many pickups we've already turned
                // into checks, with no way to tell "genuine pickup" from
                // "our own grant" without this check). Same bug class as the
                // earlier ability feedback loop, just missed in this second
                // counter-based spot.
                if (_suppressNextChangeFor.Remove(PaintFlaskCounterPersistenceId))
                {
                    return;
                }
                ReconcilePaintFlaskCounter();
            }
            catch (Exception e)
            {
                Log.LogWarning("Error handling Paint Flask counter change: " + e);
            }
        }

        // Health Vial claw-back tried and reverted: real testing showed a
        // worse problem than the one it was solving. The shop UI reads
        // the same flag we were clawing back to false, so it saw "not
        // purchased" again and let the player buy it repeatedly, each
        // time spending more currency for nothing (the effect never
        // actually sticks). An infinitely-repeatable currency drain is
        // worse than the original single double-grant, so this reverts
        // to plain unsuppressed tracking: the check still reports
        // correctly, but the real purchase is left alone rather than
        // clawed back. Health Vial suppression remains a genuinely open
        // problem -- needs the real purchase-confirmation method found,
        // not a workaround here.
        // Brian's own persistence id, kept as its own named constant since
        // both the boss list above and the Dark Embrace safety net below
        // both need to refer to it specifically (not just as one of many
        // regular bosses).
        private const string BrianBeatenId = "ps_Boss_BrainStoker_Beaten";
        private const string DarkEmbraceId = "ps_inspDrawing_DarkLightning";

        private void OnPersistenceChanged(string idString)
        {
            try
            {
                if (_suppressNextChangeFor.Remove(idString))
                {
                    // This change was caused by our own ApplyItem() write,
                    // not a real player action -- don't report it as a check.
                    return;
                }
                if (!IsTruthy(idString)) return;
                ReportCheck(idString);

                // Direct request: "Inspiration: Dark Embrace" doesn't
                // live at one fixed, always-present spot like every other
                // inspiration -- in vanilla, it appears in the very next
                // boss-defeat reward after Brian specifically is beaten,
                // whichever boss that happens to be. Its own check can
                // apparently go missing under our suppression sometimes
                // (the underlying vanilla trigger for a dynamically-
                // appearing reward doesn't seem to reliably survive us
                // intercepting the normal grant). This is a safety net,
                // not a replacement for the real trigger: whenever ANY
                // boss OTHER than Brian himself is defeated, if Brian is
                // already beaten and Dark Embrace's own check hasn't
                // fired yet, it's reported directly here too. ReportCheck
                // already dedupes safely against its own persisted
                // "already reported" marker, so this is a no-op on every
                // call after the real trigger (or this safety net itself)
                // successfully gets it through once -- only ever fires
                // the check itself, never attempts to force the actual
                // vanilla item/visual, which stays exactly however it
                // already behaves.
                if (idString != BrianBeatenId
                    && (RegularBossIds.Contains(idString) || idString == "ps_Boss_PuppetMaster_Beaten")
                    && IsTruthy(BrianBeatenId)
                    && !IsTruthy(DarkEmbraceId))
                {
                    Log.LogInfo("Dark Embrace safety net: Brian is beaten and '" + idString + "' was just defeated -- reporting Dark Embrace's own check directly, in case its normal dynamic trigger didn't fire.");
                    ReportCheck(DarkEmbraceId);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Error handling persistence change for " + idString + ": " + e);
            }
        }

        /// Called both when a persistence flag legitimately becomes true
        /// (non-suppressed locations, e.g. chests/hearts we don't patch)
        /// and directly from the Harmony patch when a vanilla grant is
        /// suppressed -- suppression itself proves the location was
        /// reached, even though the flag is deliberately never set.
        // "ps_item_Potion" is
        // purely a suppression-tracking id (see HealthVialIds' own
        // comment) -- the real purchase flow calls Collect() TWICE, once
        // for the shop item's own flag (a real, reportable check) and
        // separately for this underlying reward id, which was never
        // meant to be treated as a check at all. It still flowed through
        // here like any other id though, getting sent to the client
        // (which silently ignores it, not a real location name) AND
        // recorded via RecordCheckPosition -- which map icon placement
        // doesn't filter by "is this a real AP location" at all, so a
        // spurious, meaningless icon appeared on the map for it. Excluded
        // here, at the single, unified entry point every check already
        // goes through, rather than needing this same exclusion repeated
        // at every individual caller.
        private static readonly HashSet<string> NeverCheckIds = new HashSet<string> { "ps_item_Potion" };

        internal void ReportCheck(string idString)
        {
            // This save file is linked to a different
            // seed than the one connected right now -- refuse to send
            // anything to the server. See ValidateSaveSeedLink's own
            // comment for the full reasoning.
            if (_apIntegrationBlockedForWrongFile) return;

            if (NeverCheckIds.Contains(idString)) return;

            // A location whose category isn't randomized
            // this seed still gets its map icon marked as found, so the
            // map stays a complete picture of what's been done -- it just
            // never reaches the server (there's no location there to
            // check) and never says anything to the player.
            //
            // Deliberately BEFORE the dedupe checks below: those exist to
            // stop the server being told the same thing twice, but the
            // icon state lives only in memory and has to be re-applied
            // every session. Re-marking is free and idempotent, so it runs
            // on every pass, including the ReportAlreadyTrueLocations
            // sweep at startup -- which is precisely what restores these
            // icons after a restart.
            bool localOnly = NonApLocationIds.Contains(idString);
            if (localOnly) MapIconInjector.MarkLocallyChecked(idString);

            if (_reportedChecks.Contains(idString)) return;

            // Real bug, found by real testing: _reportedChecks only ever
            // lived in memory, so every game restart lost all record of
            // what had already been reported -- meaning EVERY already-
            // true flag got re-detected as brand new on reload, including
            // anything only true because we ourselves granted it via
            // ApplyItem in an earlier session, never actually found by
            // the player at all. The result: items received via AP
            // appeared to fire their check again "as if just obtained"
            // on every single reload. A small persisted marker (written
            // the first time a check is genuinely reported) lets a fresh
            // session correctly recognize "already told AP about this"
            // instead of re-reporting it -- this single fix point covers
            // every caller uniformly (abilities, chests, bosses,
            // inspirations, and even the synthetic Paint Flask/Heart
            // Piece/Eraser instance ids), no per-category handling needed.
            //
            // Kept null-safe: check-detection is deliberately able to
            // fire before persistence manager is ready (see
            // AbilityUnlockPatch), so this can't assume _persistence is
            // set. If it genuinely isn't ready yet, this specific check
            // just won't have its "reported" marker persisted -- a narrow
            // edge case (only matters if the player quits before
            // persistence manager is ever found this session), not the
            // common path.
            if (_persistence != null)
            {
                var reportedMarkerId = new ConPersistenceId("ps_ap_reported_" + idString);
                if (_persistence.Save.Contains(reportedMarkerId) && (_persistence.Save.GetInt(reportedMarkerId) ?? 0) != 0)
                {
                    _reportedChecks.Add(idString);
                    return;
                }
                _persistence.Save.SetInt(reportedMarkerId, 1, default(PersistenceEntry.Options));
            }

            _reportedChecks.Add(idString);

            if (localOnly)
            {
                // Position still recorded (that's what puts an icon on the
                // map at all), but no UiLog, no notification and nothing
                // sent over the bridge. Previously these DID reach the
                // client, which then queued them as pending location
                // checks that could never resolve, since the apworld never
                // generated those locations for this seed.
                RecordCheckPosition(idString);
                Log.LogInfo("ReportCheck: '" + idString + "' is not an AP location this seed (category not randomized) -- tracked locally for the map only.");
                return;
            }

            UiLog("Check: " + idString);
            _bridge.SendCheck(idString);
            RecordCheckPosition(idString);
        }

        // Even when a category (Paint Flask/Heart Piece/
        // Eraser) isn't randomized, a real, vanilla pickup should still
        // get its position recorded for map icon tracking -- just
        // without ever being reported to AP as a check, since it isn't a
        // real AP location at all in that case. Deliberately bypasses
        // ReportCheck entirely (not just "ReportCheck but skip
        // SendCheck") -- ReportCheck's own "already reported" bookkeeping
        // (_reportedChecks, the persisted ps_ap_reported_ marker) is
        // specifically for real AP checks, and mixing a non-AP recording
        // into that same tracking would be its own new source of bugs.
        private void RecordPositionOnly(string idString)
        {
            try
            {
                RecordCheckPosition(idString);
                // Exactly what non-randomized chests do --
                // the icon goes "found" even though there is no AP check
                // behind it, so the map stays a complete record of what
                // has actually been picked up this file. Still nothing
                // player-facing beyond the icon itself: no message, no
                // notification, nothing sent to the server.
                MapIconInjector.MarkLocallyChecked(idString);
            }
            catch (Exception e)
            {
                Log.LogWarning("RecordPositionOnly: error recording position for '" + idString + "' (category disabled, no AP check involved): " + e);
            }
        }

        // Map-tracking foundation, direct request: builds up real
        // world-position data for every check organically, just by
        // playing normally -- no separate action needed, since this
        // hooks directly into the same ReportCheck every check already
        // goes through. Deliberately a SEPARATE, plain text file next to
        // the mod DLL, not the game's own save system -- easy to
        // inspect, easy to hand to an external tool later, and immune to
        // anything going wrong with persistence manager. Append-only
        // (one line per check, flushed immediately) rather than
        // read-modify-write a single growing file, specifically so
        // neither a mid-write crash nor two checks landing in the same
        // instant can corrupt or lose earlier entries -- each line
        // stands completely on its own. Deliberately best-effort: any
        // failure here is caught and logged, never allowed to interfere
        // with the actual check report above, which must always succeed
        // regardless of whether position tracking does.
        private static readonly string CheckPositionsFilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "check_positions.jsonl");

        // Shrine link persistence -- OwnShrineIcons'
        // LinkedCheckpoint state used to be purely in-memory, reset every
        // time the map root resets (a fresh scene/session), so exiting
        // and reentering the game lost every shrine link, requiring a
        // full re-visit of every shrine already linked in a previous
        // session. Same append-only .jsonl pattern as check_positions
        // above, in the same directory. Stores {checkpointId, x, y} --
        // the LOCAL icon-parent position a link was made at, not a world
        // position -- since MapIconInjector.PrePlaceOwnShrineIcons
        // re-derives the exact same local positions every session (a
        // direct copy of the game's own existing shrine icon positions,
        // which are stable/unchanging across sessions), a saved link can
        // be matched back to its newly re-placed icon by position alone,
        // with no need for the player to be physically present -- avoids
        // the world-position coordinate-conversion issues real testing
        // found unreliable for anything checkpoint-related earlier in
        // this project.
        internal static readonly string ShrineLinksFilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "shrine_links.jsonl");

        // Checks whether a checkpoint already has ANY
        // saved link at all, regardless of what position it's at --
        // MapIconInjector.OnShrineActivated uses this to refuse touching
        // a checkpoint a second time, since the game re-firing this event
        // automatically on respawn (not a genuine new meditation) was
        // exactly what let a shrine's saved position silently drift with
        // every single scene reload until it stopped matching anything.
        internal static bool HasSavedShrineLink(string checkpointIdString)
        {
            if (string.IsNullOrEmpty(checkpointIdString) || !File.Exists(ShrineLinksFilePath)) return false;
            try
            {
                foreach (string line in File.ReadLines(ShrineLinksFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (ExtractStringFieldStatic(line, "checkpointId") == checkpointIdString) return true;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("HasSavedShrineLink: error checking for an existing link for '" + checkpointIdString + "': " + e);
            }
            return false;
        }

        // Position-based identity for Paint Flask/Heart
        // Piece/Eraser -- see PositionBasedItemTracker for the full
        // reasoning. One tracker instance per item type, each with its
        // own persisted database file next to check_positions.jsonl.
        // Lazily initialized (not in a field initializer) since
        // PaintFlaskMaxInstances etc. and Log both need to already exist.
        private PositionBasedItemTracker _paintFlaskTracker;
        private PositionBasedItemTracker _heartPieceTracker;
        private PositionBasedItemTracker _eraserTracker;

        private static string TrackerFilePath(string fileName) => Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            fileName);

        private bool _loggedMissingPlayerComponentWarning = false;
        private bool _loggedFirstPositionRecorded = false;


        private IEnumerator WarpCrossScene(string checkpointIdString)
        {
            IConSceneRegistry registry = CConSceneRegistry.Instance;
            if (registry == null)
            {
                Log.LogWarning("WarpCrossScene: no scene registry available -- warp failed.");
                yield break;
            }

            IConPlayerEntity player = registry.PlayerOne;
            var concretePlayer = player as CConPlayerEntity;
            if (concretePlayer == null)
            {
                Log.LogWarning("WarpCrossScene: could not cast PlayerOne to CConPlayerEntity -- warp failed.");
                yield break;
            }

            CConPlayerLevelController levelController = concretePlayer.Level;
            if (levelController == null)
            {
                Log.LogWarning("WarpCrossScene: player has no CConPlayerLevelController -- warp failed.");
                yield break;
            }

            var transitionManager = registry.Get<CConTransitionManager>();
            if (transitionManager == null)
            {
                Log.LogWarning("WarpCrossScene: no CConTransitionManager available -- warp failed.");
                yield break;
            }

            if (transitionManager.IsRunning)
            {
                Log.LogWarning("WarpCrossScene: a transition is already running -- try again in a moment.");
                yield break;
            }

            var checkpointId = new ConCheckPointId(checkpointIdString);

            // Comprehensive diagnostic logging through
            // this whole chain -- real testing has now shown two
            // different "still doesn't work" reports even after fixing
            // two separate real, confirmed root causes (the missing
            // CConPlayerLevelController.Current update, then the wrong
            // checkpoint field being captured), and both fixes were
            // correct as far as static analysis could confirm. Rather
            // than keep guessing at a third theory blind, this logs every
            // intermediate value CreateTransitionCommand's own IL is
            // confirmed to depend on (IsValid(), TryExtractLevelId()) so
            // the NEXT attempt's BepInEx log actually shows exactly which
            // step breaks, instead of only "it still didn't work."
            Log.LogInfo("WarpCrossScene: checkpointId.StringValue = '" + checkpointId.StringValue + "', IsValid() = " + checkpointId.IsValid());
            bool extracted = checkpointId.TryExtractLevelId(out ConLevelId extractedLevelId);
            Log.LogInfo("WarpCrossScene: TryExtractLevelId -> " + extracted + (extracted ? ", level = '" + extractedLevelId.StringValue + "'" : ""));

            IConTransitionCommand command = levelController.CreateTransitionCommand(checkpointId);
            if (command == null)
            {
                Log.LogWarning("WarpCrossScene: CreateTransitionCommand returned null -- see IsValid()/TryExtractLevelId() results just logged above for which check failed. Warp did not start.");
                yield break;
            }
            Log.LogInfo("WarpCrossScene: command created -- ToLevel = '" + command.ToLevel.StringValue + "', ForceReload = " + command.ForceReload);

            bool started = transitionManager.Init(command);
            if (!started)
            {
                Log.LogWarning("WarpCrossScene: CConTransitionManager.Init returned false (a transition was already running, checked just above -- this shouldn't normally happen) -- warp did not start.");
                yield break;
            }

            Log.LogInfo("WarpCrossScene: real room transition started toward checkpoint '" + checkpointIdString + "'.");

            // Also log the ACTUAL outcome a few seconds
            // later, not just "started" -- confirms or refutes whether
            // the transition genuinely completed and the level controller
            // ended up pointing at the right place, straight from the
            // game's own state, not from assuming Init succeeding means
            // the whole sequence succeeded.
            for (int i = 0; i < 180; i++) yield return null; // ~3s at 60fps
            string currentSceneAfter = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            Log.LogInfo("WarpCrossScene: ~3s after Init -- active Unity scene is now '" + currentSceneAfter +
                        "', CConPlayerLevelController.Current = '" + levelController.Current.StringValue +
                        "', transitionManager.IsRunning = " + transitionManager.IsRunning);
        }

        // Shared by RecordCheckPosition and the new position-based Flask/
        // Heart/Eraser identity system below -- same underlying read,
        // extracted once rather than duplicated.
        private Vector3? GetCurrentPlayerPosition()
        {
            IConPlayerEntity player = CConSceneRegistry.Instance?.PlayerOne;
            var playerComponent = player as Component;
            if (playerComponent == null)
            {
                if (!_loggedMissingPlayerComponentWarning)
                {
                    _loggedMissingPlayerComponentWarning = true;
                    Log.LogWarning("GetCurrentPlayerPosition: PlayerOne did not cast to a Component -- position tracking unavailable this session (checks themselves are unaffected).");
                }
                return null;
            }
            return playerComponent.transform.position;
        }

        // Direct request: temporarily disabled -- flip back to true to
        // re-enable. Leaves the function and all its call sites fully
        // intact, just gates the one line that actually writes anything,
        // so re-enabling later is a one-line change back.
        private const bool AutomaticPositionRecordingEnabled = false;

        private void RecordCheckPosition(string idString)
        {
            if (!AutomaticPositionRecordingEnabled) return;

            try
            {
                Vector3? posOpt = GetCurrentPlayerPosition();
                if (!posOpt.HasValue) return;
                Vector3 pos = posOpt.Value;
                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                // Also capture the player's checkpoint at
                // record time, not just raw scene/x/y -- this is what
                // makes a proper, real room transition possible later
                // (see WarpToPosition/WarpCrossScene) instead of a
                // reconstructed teleport, which moved the player's
                // position but never told the game they'd genuinely
                // arrived in a new level -- confirmed by real testing to
                // leave the camera/room behind.
                //
                // This
                // originally read CurrentCheckpoint, but traced the real
                // OnPlayerMeditate method (what actually runs when the
                // player activates a shrine) and confirmed it NEVER
                // touches CurrentCheckpoint at all -- only
                // ShrineCheckPoint and PersevereCheckPoint get set.
                // CurrentCheckpoint was very likely stale/unrelated to
                // whichever shrine the player was actually standing at,
                // which is exactly why the resulting warp still didn't
                // work. ShrineCheckPoint is the field actually written by
                // shrine activation, so it's the correct one to capture
                // here.
                string checkpointId = "";
                string levelId = "";
                try
                {
                    IConCheckPointManager checkPointManager = CConSceneRegistry.Instance?.CheckPointManager;
                    checkpointId = checkPointManager?.ShrineCheckPoint.StringValue ?? "";
                }
                catch (Exception ce)
                {
                    Log.LogWarning("RecordCheckPosition: could not read shrine checkpoint (recording position without one): " + ce);
                }

                // This mod was
                // using Unity's own SceneManager.GetActiveScene().name to
                // decide "same room or different room" -- but confirmed
                // via a real live log that the ENTIRE GAME WORLD runs in
                // a single Unity scene ("ProdMainScene"), regardless of
                // which in-game room/level the player is actually in.
                // That comparison was therefore ALWAYS true, meaning the
                // cross-scene warp path (and every fix made inside it)
                // never actually ran even once, no matter how far apart
                // the player and the target genuinely were. The game's
                // own concept of "which room" is a completely separate
                // thing, ConLevelId, tracked via
                // CConPlayerLevelController.Current -- captured here
                // directly so WarpToPosition can compare against the
                // right thing.
                try
                {
                    var concretePlayer = CConSceneRegistry.Instance?.PlayerOne as CConPlayerEntity;
                    levelId = concretePlayer?.Level?.Current.StringValue ?? "";
                }
                catch (Exception le)
                {
                    Log.LogWarning("RecordCheckPosition: could not read current level (recording position without one): " + le);
                }

                string escapedId = idString.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string escapedScene = sceneName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string escapedCheckpoint = checkpointId.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string escapedLevel = levelId.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string line = "{\"id\":\"" + escapedId + "\",\"scene\":\"" + escapedScene + "\",\"x\":" +
                              pos.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"y\":" +
                              pos.y.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                              ",\"checkpoint\":\"" + escapedCheckpoint + "\",\"level\":\"" + escapedLevel + "\"}";
                File.AppendAllText(CheckPositionsFilePath, line + Environment.NewLine);
                if (!_loggedFirstPositionRecorded)
                {
                    _loggedFirstPositionRecorded = true;
                    Log.LogInfo("Position tracking active -- writing to " + CheckPositionsFilePath);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Error recording check position for " + idString + " (check report itself was unaffected): " + e);
            }
        }

        private void GrantLockedAbilitySafetyNet(string idString, string displayName)
        {
            // Deliberately just calls ApplyItem directly -- it already
            // queues itself correctly if persistence manager isn't ready
            // yet (the exact scenario from real logs: touching this altar
            // well before persistence manager finished initializing), and
            // is already idempotent, so a later "official" AP grant for
            // the same id safely no-ops. No need to duplicate any of that
            // logic here.
            Log.LogInfo(displayName + " safety net triggered (always-correct, locked location).");
            ApplyItem(idString);
        }

        private bool WasGrantedByUs(string idString)
        {
            if (_persistence == null) return false;
            var grantedId = new ConPersistenceId("ps_ap_granted_" + idString);
            return _persistence.Save.Contains(grantedId) && (_persistence.Save.GetInt(grantedId) ?? 0) != 0;
        }

        private void MarkGrantedByUs(string idString)
        {
            if (_persistence == null) return;
            _persistence.Save.SetInt(new ConPersistenceId("ps_ap_granted_" + idString), 1, default(PersistenceEntry.Options));
        }

        // Real fix, for real testing: repeatable grants (Paint Flask/
        // Heart Piece/Eraser) used to rely entirely on Python only ever
        // sending each grant's "apply" message once -- but a message can
        // be sent (from Python's perspective, successfully) and never
        // actually reach or get processed by the mod (e.g. a scene
        // transition invalidating persistence manager mid-transmission),
        // with no way for Python to know that happened. Since these
        // grants aren't idempotent by idString alone (they all share one
        // sentinel), a lost grant had no way to safely recover -- the
        // earlier fix for regular items (resending on reconnect) was
        // deliberately NOT extended to these, since blindly resending an
        // increment-based grant could over-grant. This makes the mod
        // itself the source of truth for "has this exact instance
        // already been processed," using a persisted marker keyed by the
        // same unique instance key Python already computes internally
        // (location+player) -- so it's now safe to resend these too.
        private bool WasInstanceProcessed(string instanceKey)
        {
            if (_persistence == null || instanceKey == null) return false;
            var id = new ConPersistenceId("ps_ap_processed_" + instanceKey);
            return _persistence.Save.Contains(id) && (_persistence.Save.GetInt(id) ?? 0) != 0;
        }

        private void MarkInstanceProcessed(string instanceKey)
        {
            if (_persistence == null || instanceKey == null) return;
            _persistence.Save.SetInt(new ConPersistenceId("ps_ap_processed_" + instanceKey), 1, default(PersistenceEntry.Options));
        }

        private void ReportAlreadyTrueLocations()
        {
            // Real bug, found by real testing, deeper than it first
            // looked: this used to call _bridge.SendCheck directly,
            // completely bypassing ReportCheck (and its persisted
            // "already reported" marker) -- meaning that fix never
            // actually covered this code path at all. Worse: even
            // routed through ReportCheck, IsTruthy alone isn't enough
            // to mean "the player discovered this location." For
            // anything that's BOTH a location's underlying flag AND a
            // receivable item type (abilities, tears, key items,
            // inspirations, Health Vial), AP can set that same flag
            // from a completely different location's reward -- that's
            // not a discovery of THIS location, it's just a grant. Only
            // report a check here if the flag is true AND we didn't
            // ourselves set it via ApplyItem --
            // WasGrantedByUs correctly returns false for pure-location
            // ids (chests, bosses, inspirations discovered directly)
            // that never go through ApplyItem as a target, so this
            // doesn't change behavior for those at all.
            foreach (string idString in KnownPersistenceIds)
            {
                if (IsTruthy(idString) && !WasGrantedByUs(idString))
                {
                    ReportCheck(idString);
                }
            }
            InitPaintFlaskTracking();
            InitHeartPieceTracking();
            InitEraserTracking();
        }

        // --- DeathLink ---
        //
        // _suppressNextDeathReport breaks a real, well-known DeathLink
        // failure mode: without it, killing the local player in response
        // to a received death would trigger our OWN death detection,
        // re-sending it, which could kill other linked players again,
        // ping-ponging indefinitely. Same defensive pattern already used
        // for ability-grant and Paint Flask counter suppression elsewhere
        // in this mod.
        private bool _suppressNextDeathReport;

        private void OnLocalPlayerDeath()
        {
            if (_suppressNextDeathReport)
            {
                _suppressNextDeathReport = false;
                return;
            }
            UiLog("Local player died -- sending DeathLink.");
            _bridge.SendDeath();
        }

        internal void KillLocalPlayer()
        {
            try
            {
                IConPlayerEntity player = CConSceneRegistry.Instance?.PlayerOne;
                if (player?.Health == null)
                {
                    Log.LogWarning("Cannot kill local player for DeathLink -- player or health not available yet.");
                    return;
                }
                _suppressNextDeathReport = true;
                player.Health.ChangeHealth(-9999f);
                UiLog("Received DeathLink -- killing local player.");
            }
            catch (Exception e)
            {
                Log.LogWarning("Error killing local player for DeathLink: " + e);
            }
        }

        /// True if this persistence id currently has a non-zero/true value,
        /// checking both int and bool representations (matches the two
        /// value types actually observed for tracked ids in the real save).
        private bool IsTruthy(string idString)
        {
            if (_persistence == null) return false;
            var id = new ConPersistenceId(idString);
            var save = _persistence.Save;
            if (!save.Contains(id)) return false;

            int? asInt = save.GetInt(id);
            if (asInt.HasValue) return asInt.Value != 0;

            bool? asBool = save.GetBool(id);
            if (asBool.HasValue) return asBool.Value;

            return false;
        }

        /// Called by the bridge server (from its background thread's
        /// queued command, dispatched on the main thread) to apply a
        /// received item live, no save/reload required.
        internal void ApplyItem(string idString, string instanceKey = null)
        {
            try
            {
                Log.LogInfo("ApplyItem called: " + idString + (instanceKey != null ? " (instance: " + instanceKey + ")" : ""));

                // This save file is linked to a different
                // seed than the one connected right now -- refuse to
                // apply anything. See ValidateSaveSeedLink's own comment
                // for the full reasoning. Placed before the queuing check
                // below so this also catches anything that was queued
                // earlier and is only now being flushed.
                if (_apIntegrationBlockedForWrongFile)
                {
                    Log.LogWarning("ApplyItem: BLOCKED ('" + idString + "') -- " + _apIntegrationBlockedReason);
                    return;
                }

                if (_persistence == null)
                {
                    Log.LogInfo("Persistence manager not ready yet -- queuing '" + idString + "' to apply once it's available.");
                    _pendingApplyRequests.Add(Tuple.Create("apply", idString, instanceKey, (string)null));
                    return;
                }

                if (idString == PaintFlaskGrantSentinel)
                {
                    // Paint Flask is repeatable (up to 12 copies) -- unlike
                    // every other item here, it must NOT be deduped by
                    // idString (they'd all share the same sentinel string).
                    // instanceKey (if provided) makes this genuinely
                    // idempotent per-received-instance now -- see
                    // GrantBankedCounter's WasInstanceProcessed check --
                    // rather than relying entirely on the Python side
                    // only ever sending each grant once, which real
                    // testing showed isn't a safe assumption (a message
                    // can be sent but never actually reach/get processed
                    // by the mod, and Python has no way to know that).
                    GrantPaintFlask(instanceKey);
                    return;
                }

                if (idString == HeartPieceGrantSentinel)
                {
                    // Same repeatable-item handling as Paint Flask.
                    GrantHeartPiece(instanceKey);
                    return;
                }

                if (idString == EraserGrantSentinel)
                {
                    GrantEraser(instanceKey);
                    return;
                }

                if (!ApplicableItemIds.Contains(idString))
                {
                    Log.LogWarning("Unknown/unmapped item persistence id: " + idString);
                    return;
                }
                if (_appliedItems.Contains(idString))
                {
                    Log.LogInfo("Already in _appliedItems, skipping: " + idString);
                    return;
                }

                var id = new ConPersistenceId(idString);
                bool alreadyTruthy = IsTruthy(idString);
                Log.LogInfo("ApplyItem: " + idString + " -- IsTruthy() returned " + alreadyTruthy);
                if (alreadyTruthy)
                {
                    // Real, still-open question: is this because suppression
                    // correctly did nothing (vanilla never set it, we're
                    // about to) -- or because something OUTSIDE our patched
                    // code path set it anyway (the complex cinematic-style
                    // ability interactions are the prime suspect, being the
                    // one interaction type we haven't fully traced). This
                    // log line exists specifically to help tell those apart
                    // next time this is reported.
                    Log.LogInfo("Skipping SetInt for " + idString + " -- already truthy before we touched it.");
                    _appliedItems.Add(idString);
                    MarkGrantedByUs(idString);
                    return;
                }

                _suppressNextChangeFor.Add(idString);
                _persistence.Save.SetInt(id, 1, default(PersistenceEntry.Options));
                _appliedItems.Add(idString);
                MarkGrantedByUs(idString);
                UiLog("Received: " + idString);

                // Real bug, found by real testing: receiving "Health Vial"
                // or "Health Vial Upgrade" via AP correctly set the SHOP
                // item's own "purchased" flag (ps_shopItem_Potion1/2, so
                // the shop UI itself showed it as bought), but never
                // touched the SEPARATE underlying flag the game actually
                // reads to determine whether the vial itself is usable
                // (ps_item_Potion, confirmed via a real save file as an
                // int, not a bool -- stored "I1" for the base tier). This
                // is the same two-flag split already known from vanilla
                // shop purchases themselves (Collect() fires for both the
                // shop item AND ps_item_Potion separately) -- the AP grant
                // path only ever handled the shop-item half.
                // CONFIRMED via real save data: base tier stores 1.
                // UNVERIFIED, best-supported guess: the upgrade tier
                // likely stores a higher value (2) in the same field,
                // matching the shop item's own numbering -- no real save
                // data with the upgrade purchased was available to
                // confirm this specific part. Worth testing directly.
                if (idString == "ps_shopItem_Potion1" || idString == "ps_shopItem_Potion2")
                {
                    // This used to set an ABSOLUTE target tier
                    // (1 for the base vial, 2 for the upgrade) -- correct
                    // if Health Vial always arrives before Health Vial
                    // Upgrade, but AP items can arrive in either order.
                    // Receiving the Upgrade FIRST (currentTier still 0)
                    // jumped straight to 2, silently granting BOTH tiers
                    // at once from a single item. Fixed to always
                    // increment by exactly 1 relative to whatever tier is
                    // already there, regardless of which of the two
                    // items this call is for or what order they arrive
                    // in -- capped at 2 (the real, actual maximum), so a
                    // duplicate/late-arriving grant past the cap is a
                    // safe no-op rather than reading past what the tier
                    // actually means.
                    var potionId = new ConPersistenceId("ps_item_Potion");
                    int currentTier = _persistence.Save.GetInt(potionId) ?? 0;
                    int newTier = Math.Min(currentTier + 1, 2);
                    if (newTier > currentTier)
                    {
                        _persistence.Save.SetInt(potionId, newTier, default(PersistenceEntry.Options));
                        Log.LogInfo("Also set ps_item_Potion to " + newTier + " (the actual usable vial, separate from the shop's own purchased flag) -- +1 from " + currentTier + ", not jumping straight to an absolute tier.");
                        TriggerCollectAnimation("ps_item_Potion", currentTier, newTier);
                    }
                    // Real, reported bug: even with ps_item_Potion set
                    // correctly above, the vial capacity increase didn't
                    // actually apply in-game until exiting to the main
                    // menu and re-entering. Confirmed via IL: the live
                    // CConPlayerPotion component only ever re-reads the
                    // inventory's Potion count from its own Start()
                    // method -- there is no other trigger for it anywhere
                    // in the game's own code. Forced immediately here
                    // instead of waiting for a scene reload that might
                    // never happen this session.
                    RefreshPotionCapacity();
                }

                // Real, reported bug: receiving an Inspiration item granted
                // its own flag correctly, but the game has a SEPARATE,
                // one-time "have you ever gotten one" flag that gates
                // whether the equip-inspirations menu unlocks at all --
                // confirmed real (ps_collected_first_inspiration, a bool,
                // its timestamp in a real save landing right alongside the
                // very first inspiration's own timestamp). We only ever
                // granted the individual inspiration flag, never this
                // separate menu-unlock one, so the menu never appeared.
                if (idString.StartsWith("ps_inspDrawing_"))
                {
                    var menuUnlockId = new ConPersistenceId("ps_collected_first_inspiration");
                    if (_persistence.Save.GetBool(menuUnlockId) != true)
                    {
                        _persistence.Save.SetBool(menuUnlockId, true, default(PersistenceEntry.Options));
                        Log.LogInfo("Also set ps_collected_first_inspiration (unlocks the equip-inspirations menu).");
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Error applying item " + idString + ": " + e);
            }
        }

        // Forces the live CConPlayerPotion component to
        // immediately re-read the updated Potion inventory count and
        // recompute its own effective capacity, instead of waiting for
        // the next scene load (Start()) to do it -- see the comment where
        // this is called from ApplyItem for the full IL-traced reasoning.
        // UpdatePotionCount() (the parameterless overload) is private, so
        // this goes through reflection; the type itself is public, so no
        // reflection needed to locate/reference CConPlayerPotion itself.
        // The reflection-based "force NotifyListeners" fix below
        // still didn't work live, even though going to the main menu and
        // reentering (a full scene reload, Start() running fresh) DID
        // fix it. That's the real, decisive clue -- it means SOME part of
        // the entity-listener chain (NotifyListeners -> CConPlayerHudController
        // -> the HUD sub-controller) isn't reliably wired up or firing
        // for an AP-side grant specifically, even though it's traced and
        // provably correct on paper. Rather than keep guessing at WHICH
        // link in that chain is unreliable, this bypasses the whole
        // chain: CConUiPlayerPotionController.UpdatePotionFill (the HUD
        // widget itself) is public, and so is CConPlayerPotion.
        // CalcPotionFills() (confirmed via IL) -- calling the widget's
        // own update method directly, with freshly computed fill values,
        // needs nothing to be listening for anything.
        private static void RefreshPotionCapacity()
        {
            try
            {
                var potionComponent = UnityEngine.Object.FindObjectOfType<CConPlayerPotion>();
                if (potionComponent == null)
                {
                    Log.LogWarning("RefreshPotionCapacity: no CConPlayerPotion found in the scene -- capacity will only update on next scene load.");
                    return;
                }

                MethodInfo updateMethod = typeof(CConPlayerPotion).GetMethod(
                    "UpdatePotionCount", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                updateMethod?.Invoke(potionComponent, null);

                IConPlayerEntity player = CConSceneRegistry.Instance?.PlayerOne;
                var concretePlayer = player as CConPlayerEntity;
                if (concretePlayer == null)
                {
                    Log.LogWarning("RefreshPotionCapacity: no CConPlayerEntity available -- HUD widget update skipped (data model still updated correctly).");
                    return;
                }

                var potionUi = UnityEngine.Object.FindObjectOfType<CConUiPlayerPotionController>();
                if (potionUi == null)
                {
                    Log.LogWarning("RefreshPotionCapacity: no CConUiPlayerPotionController found in the scene -- HUD widget update skipped (data model still updated correctly).");
                    return;
                }

                float[] fills = potionComponent.CalcPotionFills();
                potionUi.UpdatePotionFill(fills, concretePlayer);

                Log.LogInfo("RefreshPotionCapacity: forced an immediate CConPlayerPotion.UpdatePotionCount() + direct CConUiPlayerPotionController.UpdatePotionFill() call.");
            }
            catch (Exception e)
            {
                Log.LogWarning("RefreshPotionCapacity: error forcing immediate update (capacity will still apply on next scene load): " + e);
            }
        }

        // Same class of fix as RefreshPotionCapacity
        // above, for Paint Flask this time. Traced via IL: CConPlayerPaint
        // has its own RecalcCap() (private), which DOES have a real,
        // automatic trigger -- IConSceneListener_Inventory.OnPlayerCollect
        // -- but that only fires from the game's own REAL Collect()
        // pipeline, which this mod's AP grants deliberately bypass
        // entirely (writing the persistence int directly instead).
        // That's exactly why a rest point was needed before: Start() is
        // the only OTHER caller, so nothing else ever re-triggers it for
        // an AP-granted flask specifically.
        //
        // Even a forced
        // NotifyListeners() call didn't reliably reach the HUD live,
        // despite a full scene reload (main menu and back) fixing it
        // every time -- the decisive clue that something about the
        // entity-listener chain itself isn't reliable for an AP-side
        // grant, not just a "did the value change" timing issue. Same
        // fix as Potion above: bypass the listener chain entirely and
        // call the HUD widget's own public update method directly.
        // Confirmed via IL that OnColorChanged reads CConPlayerPaint's
        // current Max() itself internally, so this correctly redraws the
        // capacity too, not just current fill -- not just an animation
        // replay of stale data.
        private static void RefreshPaintCapacity()
        {
            try
            {
                var paintComponent = UnityEngine.Object.FindObjectOfType<CConPlayerPaint>();
                if (paintComponent == null)
                {
                    Log.LogWarning("RefreshPaintCapacity: no CConPlayerPaint found in the scene -- capacity will only update on next scene load.");
                    return;
                }

                // Definitively traced via actual IL disassembly of the
                // real game assembly (not assumption, not a guess this
                // time -- confirmed by reading CConPlayerInventoryManager.
                // Collect() and UpdateBuffs() directly). The real,
                // complete mechanism: Collect() calls UpdateBuffs()
                // specifically and only when the collectable is
                // HealthPiece or ManaPiece (Paint Flask's real internal
                // name) -- UpdateBuffs() computes
                // healthPieces = Get(HealthPiece) / 3 and
                // paintPieces = Get(ManaPiece) / 4, then applies that as
                // the actual buff to every player. This exact same
                // UpdateBuffs() is also what Start() calls on scene load
                // -- confirming it's genuinely the one and only place
                // "banked collectible count" ever turns into a capacity
                // bonus, in either case. RecalcCap() itself (what this
                // function called before) was never capable of doing this
                // on its own -- it only ever reads whatever UpdateBuffs()
                // already set up. Since this mod suppresses the ENTIRE
                // vanilla Collect() call to prevent double-granting,
                // UpdateBuffs() never runs at all for an AP-granted flask
                // -- the actual root cause, not a symptom. Fixed by
                // calling the real, public UpdateBuffs() directly, the
                // same one Collect() itself would have called.
                try
                {
                    var inventoryManager = CConSceneRegistry.Instance?.Inventory as CConPlayerInventoryManager;
                    inventoryManager?.UpdateBuffs();
                }
                catch (Exception buffsEx)
                {
                    Log.LogWarning("RefreshPaintCapacity: error calling UpdateBuffs() (continuing anyway): " + buffsEx);
                }

                MethodInfo recalcMethod = typeof(CConPlayerPaint).GetMethod(
                    "RecalcCap", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                recalcMethod?.Invoke(paintComponent, null);

                var paintUi = UnityEngine.Object.FindObjectOfType<CConUiPlayerPaintController>();
                if (paintUi == null)
                {
                    Log.LogWarning("RefreshPaintCapacity: no CConUiPlayerPaintController found in the scene -- HUD widget update skipped (data model still updated correctly).");
                    return;
                }

                float current = paintComponent.Current;
                paintUi.OnColorChanged(current, current, false, paintComponent);

                Log.LogInfo("RefreshPaintCapacity: forced an immediate CConPlayerInventoryManager.UpdateBuffs() + CConPlayerPaint.RecalcCap() + direct CConUiPlayerPaintController.OnColorChanged() call.");
            }
            catch (Exception e)
            {
                Log.LogWarning("RefreshPaintCapacity: error forcing immediate update (capacity will still apply on next scene load): " + e);
            }
        }

        // Same class of fix again, for Heart Piece. Traced
        // via IL: CConCharacterHealth.RecalcCap() (private) has NO
        // inventory-collect-driven trigger at all (unlike Paint's) -- its
        // only callers are Start(), ChangeBaseHealth() (a generic, unused-
        // for-this-purpose helper), and a status-effect listener. Health
        // capacity from a Heart Piece was therefore ALWAYS only ever
        // recalculated at the next scene load, with no live trigger
        // existing anywhere in the game's own code to hook instead --
        // forcing it directly here is the only way to make this apply
        // immediately.
        //
        // Even
        // ChangeHealth's own forceNotify=true parameter didn't reliably
        // reach the HUD live -- same decisive clue as Potion/Paint above
        // (a full scene reload always fixes it, a forced notification
        // sometimes doesn't). Same fix: CConUiPlayerHealthController.
        // UpdateHealth(float, int) is public -- called directly, with no
        // dependency on the listener chain being correctly wired at this
        // exact moment.
        private static void RefreshHealthCapacity()
        {
            try
            {
                IConPlayerEntity player = CConSceneRegistry.Instance?.PlayerOne;
                var concretePlayer = player as CConPlayerEntity;
                var health = concretePlayer?.Health as CConCharacterHealth;
                if (health == null)
                {
                    Log.LogWarning("RefreshHealthCapacity: no CConCharacterHealth found for the player -- capacity will only update on next scene load.");
                    return;
                }

                // Same real, IL-confirmed root cause and fix as
                // RefreshPaintCapacity's own comment above -- Collect()
                // calls the real UpdateBuffs() specifically when the
                // collectable is HealthPiece or ManaPiece, and this mod's
                // suppression of the entire Collect() call means it never
                // runs for an AP-granted Heart Piece either. Same fix:
                // call the real, public UpdateBuffs() directly.
                try
                {
                    var inventoryManager = CConSceneRegistry.Instance?.Inventory as CConPlayerInventoryManager;
                    inventoryManager?.UpdateBuffs();
                }
                catch (Exception buffsEx)
                {
                    Log.LogWarning("RefreshHealthCapacity: error calling UpdateBuffs() (continuing anyway): " + buffsEx);
                }

                MethodInfo recalcMethod = typeof(CConCharacterHealth).GetMethod(
                    "RecalcCap", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                recalcMethod?.Invoke(health, null);

                var healthUi = UnityEngine.Object.FindObjectOfType<CConUiPlayerHealthController>();
                if (healthUi == null)
                {
                    Log.LogWarning("RefreshHealthCapacity: no CConUiPlayerHealthController found in the scene -- HUD widget update skipped (data model still updated correctly).");
                    return;
                }

                healthUi.UpdateHealth(health.Current, health.Max);

                Log.LogInfo("RefreshHealthCapacity: forced an immediate CConPlayerInventoryManager.UpdateBuffs() + CConCharacterHealth.RecalcCap() + direct CConUiPlayerHealthController.UpdateHealth() call.");
            }
            catch (Exception e)
            {
                Log.LogWarning("RefreshHealthCapacity: error forcing immediate update (capacity will still apply on next scene load): " + e);
            }
        }



        // The real, confirmed persistence id for total currency (Glimmer)
        // held -- verified as an accumulating int counter in a real save
        // file (I2202 = 2202 currency), not a simple unlock flag. Not one
        // of our tracked location ids, so no self-triggered-check risk
        // from writing it (nothing listens for changes to it).
        private const string CurrencyPersistenceId = "ps_item_Currency";
        private const int MinGlimmerAmount = 20;
        private const int MaxGlimmerAmount = 100;

        // Same reasoning as currency -- ps_item_LightStone is a real,
        // confirmed accumulating counter (I386 in a real save), not
        // watched by any listener, so freely incrementable.
        private const string LightstoneCounterPersistenceId = "ps_item_LightStone";
        private const int MinLightstoneAmount = 15;
        private const int MaxLightstoneAmount = 40;

        private readonly System.Random _fillerRandom = new System.Random();

        /// Gives filler items a real in-game effect instead of doing
        /// nothing. "Handful of Glimmer" grants a varied amount of
        /// currency (real feedback: a fixed amount every time felt flat).
        /// "Handful of Lightstones" grants Lightstones -- brought back
        /// specifically as a receive-only effect after Lightstones were
        /// removed as their own location type (completeness concerns).
        /// explicitAmount, when supplied, is the quantity the live client
        /// already committed to and already announced in the AP message
        /// ("found their Handful of Glimmer (75)"). Using it verbatim is
        /// what makes the announced number and the granted number the
        /// same -- rolling here would contradict a message the player has
        /// already read, and would roll differently again on any resend.
        /// null keeps the original behaviour (roll in range), which is
        /// what an older client, or an amount-less filler like a trap,
        /// will produce.
        internal void ApplyFiller(string itemName, string instanceKey = null, int? explicitAmount = null)
        {
            try
            {
                // Same block as ApplyItem above.
                if (_apIntegrationBlockedForWrongFile)
                {
                    Log.LogWarning("ApplyFiller: BLOCKED ('" + itemName + "') -- " + _apIntegrationBlockedReason);
                    return;
                }

                if (WasInstanceProcessed(instanceKey))
                {
                    Log.LogInfo("Filler instance '" + instanceKey + "' already processed -- skipping (safe resend).");
                    return;
                }

                if (_persistence == null)
                {
                    Log.LogInfo("Persistence manager not ready yet -- queuing filler '" + itemName + "' to apply once it's available.");
                    _pendingApplyRequests.Add(Tuple.Create("apply_filler", itemName, instanceKey,
                        explicitAmount.HasValue ? explicitAmount.Value.ToString() : null));
                    return;
                }

                if (itemName == "Handful of Lightstones")
                {
                    // Fallback roll (only used if the
                    // Python client didn't send an explicit amount, e.g.
                    // an older client) now also steps by 5, matching
                    // filler_amount_for's own quantization -- both bounds
                    // are already multiples of 5 with a span divisible by
                    // 5, so this picks which step rather than any integer.
                    int lsAmount = explicitAmount ?? (MinLightstoneAmount + 5 * _fillerRandom.Next(0, (MaxLightstoneAmount - MinLightstoneAmount) / 5 + 1));
                    var lsId = new ConPersistenceId(LightstoneCounterPersistenceId);
                    int currentLs = _persistence.Save.GetInt(lsId) ?? 0;
                    _persistence.Save.SetInt(lsId, currentLs + lsAmount, default(PersistenceEntry.Options));
                    TriggerCollectAnimation(LightstoneCounterPersistenceId, currentLs, currentLs + lsAmount);
                    MarkInstanceProcessed(instanceKey);
                    // No separate "Received: X (+N)"
                    // notification -- the in-game currency counter's own
                    // collect animation (TriggerCollectAnimation above)
                    // already visibly shows the amount going up, and AP's
                    // own default "Received Handful of Lightstones"
                    // notification already announces the grant itself.
                    // UiLog kept for the BepInEx debug log only (no
                    // visible UI consumes it anymore).
                    UiLog("Received: " + itemName + " (+" + lsAmount + " lightstones)");
                    Log.LogInfo("ApplyFiller: granted " + lsAmount + " lightstone(s) (" +
                                (explicitAmount.HasValue ? "amount supplied by client" : "rolled locally") +
                                "), counter " + currentLs + " -> " + (currentLs + lsAmount) + ".");
                    return;
                }

                // Three traps, same apply_filler mechanism
                // as Glimmer/Lightstones above (no real persistence id --
                // a pure mod-side effect, not a game item at all). No
                // separate "Received: X Trap" notification either -- AP's
                // own default "Received X" notification already
                // announces it.
                if (itemName == "1 Hit Constance Trap")
                {
                    ApplyOneHitTrap();
                    MarkInstanceProcessed(instanceKey);
                    UiLog("Received: " + itemName);
                    return;
                }
                if (itemName == "Checkpoint Trap")
                {
                    ApplyCheckpointTrap();
                    MarkInstanceProcessed(instanceKey);
                    UiLog("Received: " + itemName);
                    return;
                }
                if (itemName == "Perseverance Trap")
                {
                    ApplyPerseveranceTrap();
                    MarkInstanceProcessed(instanceKey);
                    UiLog("Received: " + itemName);
                    return;
                }

                // Default / "Handful of Glimmer". Same reasoning as
                // Lightstones above -- no separate notification needed.
                // Same steps-of-5 fallback as Lightstones above.
                int amount = explicitAmount ?? (MinGlimmerAmount + 5 * _fillerRandom.Next(0, (MaxGlimmerAmount - MinGlimmerAmount) / 5 + 1));
                var id = new ConPersistenceId(CurrencyPersistenceId);
                int current = _persistence.Save.GetInt(id) ?? 0;
                _persistence.Save.SetInt(id, current + amount, default(PersistenceEntry.Options));
                TriggerCollectAnimation(CurrencyPersistenceId, current, current + amount);
                MarkInstanceProcessed(instanceKey);
                UiLog("Received: " + itemName + " (+" + amount + " currency)");
                Log.LogInfo("ApplyFiller: granted " + amount + " glimmer (" +
                            (explicitAmount.HasValue ? "amount supplied by client" : "rolled locally") +
                            "), counter " + current + " -> " + (current + amount) + ".");
            }
            catch (Exception e)
            {
                Log.LogWarning("Error applying filler item '" + itemName + "': " + e);
            }
        }

        // "1 Hit Constance" trap. Traced via IL:
        // IConCharacterHealth.ChangeHealth(value, source, ...) sets
        // Current directly to Clamp(value, 0, Cap ?? Max) -- despite the
        // name, this is an absolute-value setter with full listener
        // notification (health bar UI etc.), not a damage delta. Passing
        // 1f here sets current health to exactly 1, regardless of max
        // health, with the UI updating correctly since this goes through
        // the real, public API rather than the bare backing-field setter
        // (set_Current, private, no notifications at all -- confirmed via
        // IL not to update anything visible).
        private void ApplyOneHitTrap()
        {
            try
            {
                IConPlayerEntity player = CConSceneRegistry.Instance?.PlayerOne;
                IConCharacterHealth health = player?.Health as IConCharacterHealth;
                if (health == null)
                {
                    Log.LogWarning("ApplyOneHitTrap: no player health component available -- trap not applied.");
                    return;
                }
                health.ChangeHealth(1f, null);
                Log.LogInfo("ApplyOneHitTrap: set current health to 1.");
            }
            catch (Exception e)
            {
                Log.LogWarning("ApplyOneHitTrap: error applying trap: " + e);
            }
        }

        // "Checkpoint Trap" -- respawns the player at
        // their last visited shrine. Reuses the exact same real-
        // transition warp system built and confirmed working for /warp
        // (CConTransitionManager.Init via CreateTransitionCommand), just
        // targeting IConCheckPointManager.ShrineCheckPoint directly
        // instead of a recorded position -- no need to look anything up
        // in check_positions.jsonl, the game already tracks this itself.
        private void ApplyCheckpointTrap()
        {
            try
            {
                IConCheckPointManager checkPointManager = CConSceneRegistry.Instance?.CheckPointManager;
                string shrineCheckPoint = checkPointManager?.ShrineCheckPoint.StringValue ?? "";
                if (string.IsNullOrEmpty(shrineCheckPoint))
                {
                    Log.LogWarning("ApplyCheckpointTrap: no shrine checkpoint recorded yet (player hasn't meditated at any shrine this save) -- trap not applied.");
                    return;
                }
                Log.LogInfo("ApplyCheckpointTrap: warping to last shrine checkpoint '" + shrineCheckPoint + "'.");
                StartCoroutine(WarpCrossScene(shrineCheckPoint));
            }
            catch (Exception e)
            {
                Log.LogWarning("ApplyCheckpointTrap: error applying trap: " + e);
            }
        }

        // "Perseverance Trap" -- applies the same
        // PersevereSickness status effect the real game gives when
        // choosing to Persevere at the death screen instead of
        // Meditating. Traced via IL (ConState_Player_GameOver.
        // InitGameOverRespawn): the real game conditions this on an
        // accessibility/assist setting (ConGameSetting_AssistOptions_
        // PuppetsCurse) when the player genuinely chooses Persevere --
        // deliberately NOT checked here, since a trap should always apply
        // its effect regardless of that unrelated assist option.
        private void ApplyPerseveranceTrap()
        {
            try
            {
                IConPlayerEntity player = CConSceneRegistry.Instance?.PlayerOne;
                var concretePlayer = player as CConCharacterEntity;
                CConEntityStatusEffectController statusEffect = concretePlayer?.StatusEffect;
                if (statusEffect == null)
                {
                    Log.LogWarning("ApplyPerseveranceTrap: no player status effect controller available -- trap not applied.");
                    return;
                }
                statusEffect.Set(ConStatusEffectInstance_PersevereSickness.Descriptor, true);
                Log.LogInfo("ApplyPerseveranceTrap: applied PersevereSickness status effect.");
            }
            catch (Exception e)
            {
                Log.LogWarning("ApplyPerseveranceTrap: error applying trap: " + e);
            }
        }

        internal void UiLog(string line)
        {
            Log.LogInfo(line);
            _uiLog.Add(line);
            while (_uiLog.Count > MaxUiLogLines)
                _uiLog.RemoveAt(0);
        }

        // UiLog above only ever shows
        // up in the mod's own in-game overlay, never in the actual
        // Archipelago client's log/chat window -- which is specifically
        // what was asked for. Sends the same text through the bridge to
        // the Python client, which now displays it via its own logger
        // (see ConstanceLiveClient.py's client_message handling).
        internal void ClientLog(string line)
        {
            _bridge?.SendClientMessage(line);
        }

        private void Update()
        {
            // Real finding from real log analysis: this log's very first
            // line is "Unable to start Unity log writer" -- meaning
            // Unity's OWN automatic exception logging (which normally
            // fires for any uncaught exception in a MonoBehaviour
            // callback) may never reach this log file at all. If
            // something below were silently throwing every single frame,
            // it would look exactly like what's been observed: earlier
            // parts of Update() clearly work (checks/items/notifications
            // all flow), but a diagnostic log placed after some point
            // never once appears, across an entire multi-minute session.
            // Isolating each piece with its own try/catch and an EXPLICIT
            // Log.LogWarning call (BepInEx's own logger, confirmed
            // working regardless of the Unity log writer issue) removes
            // that blind spot for good, rather than trusting Unity's
            // automatic logging to have caught it.
            try
            {
                _bridge.PumpMainThreadWork(this);
            }
            catch (Exception e)
            {
                Log.LogWarning("[HoverDiag] Update(): PumpMainThreadWork threw: " + e);
            }

            // The top-right log panel this toggled is
            // gone entirely now -- this F9 keybind is a no-op with
            // nothing left to show/hide, removed along with it.
            //
            // A dev-only debug key was added here temporarily (granting
            // one Paint Flask and one Heart Piece per press, no
            // instanceKey so it could be pressed repeatedly) specifically
            // to speed up testing the Paint Flask/Heart Piece HUD update
            // issue -- removed now that the real fix (UpdateBuffs(), see
            // RefreshPaintCapacity/RefreshHealthCapacity) is confirmed
            // working and the debug key has served its purpose.

            try
            {
                MapIconInjector.UpdateHoverState();
            }
            catch (Exception e)
            {
                Log.LogWarning("[HoverDiag] Update(): UpdateHoverState() threw: " + e);
            }

            try
            {
                // T warps to a hovered, visited shrine
                // icon. Only checked when a mouse device exists (same
                // precondition MapIconInjector.UpdateHoverState already
                // uses) -- pointless to check hover distance against a
                // mouse position that doesn't exist.
                var keyboard = Keyboard.current;
                var mouse = Mouse.current;
                if (keyboard != null && mouse != null && keyboard[Key.T].wasPressedThisFrame)
                {
                    Vector2 mousePos = mouse.position.ReadValue();
                    MapIconInjector.CheckShrineWarpKeyPress(mousePos, checkpointIdString =>
                    {
                        StartCoroutine(WarpCrossScene(checkpointIdString));
                    });
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[HoverDiag] Update(): T key (shrine warp) check threw: " + e);
            }
        }

        private void OnGUI()
        {
            // Persistent, unmissable warning banner when
            // this save file doesn't belong to the connected seed --
            // drawn first, unconditionally, on top of everything else and
            // regardless of any other UI toggle, for the entire rest of
            // the session (there's no legitimate way to "dismiss" this
            // short of loading the correct file). See
            // ValidateSaveSeedLink's own comment for the full reasoning
            // behind why this exists instead of blocking file selection
            // at the main menu directly.
            if (_apIntegrationBlockedForWrongFile)
            {
                float bannerWidth = Mathf.Min(700f, Screen.width - 40f);
                var bannerRect = new Rect((Screen.width - bannerWidth) / 2f, 10f, bannerWidth, 54f);
                var oldColor = GUI.color;
                GUI.color = new Color(0.55f, 0f, 0f, 0.92f);
                GUI.Box(bannerRect, "");
                GUI.color = Color.white;
                var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(bannerRect.x + 10, bannerRect.y + 4, bannerRect.width - 20, bannerRect.height - 8),
                    "Archipelago integration disabled: " + (_apIntegrationBlockedReason ?? "this save file doesn't match the connected seed."), style);
                GUI.color = oldColor;
            }

            // Bottom-left notification overlay -- rendered FIRST, before
            // the early return below, so it's genuinely independent of
            // the Hide Log toggle. Real bug, found by real testing: this
            // used to sit after the "if (!_showUi) return;" line, so
            // hiding the top-right log silently hid this too, despite
            // the intent (and an earlier, wrong claim) that it wouldn't.
            // No background box, per direct request -- just floating
            // rich-text lines.
            if (_richTextLabelStyle == null)
            {
                _richTextLabelStyle = new GUIStyle(GUI.skin.label) { richText = true };
            }

            int notifWidth = 520;
            int notifX = 10;
            int notifLineHeight = 20;
            int notifY = Screen.height - (MaxNotificationLines * notifLineHeight) - 20;
            for (int i = 0; i < _notificationLog.Count; i++)
            {
                GUI.Label(new Rect(notifX, notifY, notifWidth, notifLineHeight), _notificationLog[i], _richTextLabelStyle);
                notifY += notifLineHeight;
            }

            // Hover tooltip for map icons. Rendered here
            // too, before the Hide Log early-return below, for the same
            // reason as the notification overlay above -- must stay
            // visible regardless of that toggle. Follows the mouse
            // position directly, with a small offset so the cursor itself
            // doesn't sit on top of the text.
            if (!string.IsNullOrEmpty(MapIconInjector.HoveredTooltipText))
            {
                if (MapIconInjector.HoveredTooltipText != _lastLoggedOnGuiTooltipText)
                {
                    // Edge-triggered, separate from MapIconInjector's own
                    // hover logging -- confirms OnGUI itself actually
                    // reaches this draw call with real text, isolating
                    // whether a failure is in detection (MapIconInjector)
                    // or in rendering (here).
                    _lastLoggedOnGuiTooltipText = MapIconInjector.HoveredTooltipText;
                    Log.LogInfo("[HoverDiag] OnGUI drawing tooltip box with text: " + MapIconInjector.HoveredTooltipText.Replace("\n", " | "));
                }
                // Real fix: same legacy-Input problem as above -- this
                // fallback path (Event.current null, rare inside OnGUI
                // but not impossible) used to read Input.mousePosition
                // directly, which throws in this game. Mouse.current is
                // the safe equivalent; falls back to zero if genuinely no
                // mouse device is present, rather than throwing.
                Vector2 mouse;
                if (Event.current != null)
                {
                    mouse = Event.current.mousePosition;
                }
                else
                {
                    var m = Mouse.current;
                    Vector2 rawPos = m != null ? m.position.ReadValue() : Vector2.zero;
                    mouse = new Vector2(rawPos.x, Screen.height - rawPos.y);
                }
                // Adapt to the actual text instead of a
                // fixed box -- some entries (long location names, long
                // requirement lists) were overflowing a fixed 320x80 box.
                //
                // Real bug found by real testing, twice: the previous
                // "measure natural width, cap at maxWidth" approach used
                // CalcSize on a style that already had wordWrap=true --
                // a known Unity IMGUI quirk where CalcSize doesn't
                // reliably report true width once wordWrap is already on,
                // so the computed box was sometimes narrower than the
                // text actually needed once CalcHeight wrapped it,
                // visibly cutting text off mid-sentence. A second attempt
                // at fixing this with a separate non-wrapping measurement
                // style still relies on CalcSize/CalcHeight agreeing
                // exactly, which isn't guaranteed to hold in every case.
                //
                // Sidestepped the whole class of bug instead: always use
                // the fixed maxWidth for the box, never try to measure a
                // tighter "natural" width at all. Only CalcHeight (at
                // that one, always-known width) determines the box size
                // now -- short tooltips get a wider box than strictly
                // needed, but nothing can ever be cut off again, which
                // matters far more than visual tightness here.
                var wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
                var content = new GUIContent(MapIconInjector.HoveredTooltipText);
                float textWidth = 360f;
                float textHeight = wrapStyle.CalcHeight(content, textWidth);
                float padding = 12f;

                float boxX = mouse.x + 16;
                float boxY = mouse.y + 8;
                float boxWidth = textWidth + padding;
                float boxHeight = textHeight + padding;
                // Keep it on-screen rather than running off the right or
                // bottom edge when hovering near them.
                if (boxX + boxWidth > Screen.width) boxX = Screen.width - boxWidth - 4;
                if (boxY + boxHeight > Screen.height) boxY = Screen.height - boxHeight - 4;

                var tooltipRect = new Rect(boxX, boxY, boxWidth, boxHeight);
                GUI.Box(tooltipRect, "");
                GUI.Label(new Rect(tooltipRect.x + 6, tooltipRect.y + 6, textWidth, textHeight), MapIconInjector.HoveredTooltipText, wrapStyle);
            }
            else if (_lastLoggedOnGuiTooltipText != null)
            {
                _lastLoggedOnGuiTooltipText = null;
                Log.LogInfo("[HoverDiag] OnGUI tooltip box no longer drawn (HoveredTooltipText went empty).");
            }

            // The top-right log panel (with its "Hide
            // Log"/"Show Log" toggle) is no longer needed -- removed
            // entirely, not just toggled off by default. The bottom-left
            // notification overlay and the hover tooltip above are both
            // fully independent of it already (see their own comments),
            // so nothing else here needs to change.
        }
    }
}
