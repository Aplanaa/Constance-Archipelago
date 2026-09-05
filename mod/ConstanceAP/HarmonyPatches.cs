// Suppresses the vanilla grant for randomized (AP-tracked) collectables by
// patching the ONE shared low-level method both interaction styles found
// in the game ultimately call through: CConPlayerInventoryManager.Collect.
//
// This replaces an earlier version of this file that patched
// CConUnlockAbilityOnTouch specifically. Real testing (a fresh save,
// touching the Paintbrush altar) showed that interaction doesn't even
// go through that class -- it uses a different, more elaborate
// CConUnlockAbilityCanvas (an async/cinematic tutorial-style interaction),
// which calls the same underlying Collect() method but via a completely
// different, harder-to-patch async state machine. Rather than chase every
// UI variant individually, patching the shared low-level method they all
// funnel through is more robust: it doesn't matter which UI class
// triggered the collection, only whether the collectable's persistence id
// is one we're tracking.
//
// Suppression is filtered strictly to KnownPersistenceIds, so anything
// outside our 47 tracked AP locations (currency, healing potions, other
// pickups) is completely unaffected -- the original method's other
// branches (HealFeather, HealFeatherPotion, etc.) are never touched for
// those, since the check happens before any of that logic runs.
//
// HONEST STATUS: compiles cleanly against the real assemblies. The
// specific residual uncertainty: for the CConUnlockAbilityCanvas
// (cinematic/tutorial) interaction path, it's not fully confirmed whether
// that async flow checks Collect()'s return value and would behave
// differently on failure (e.g. abort the reveal animation) versus just
// proceeding regardless, the way CConUnlockAbilityOnTouch demonstrably
// does (it discards the return value entirely). This needs a real
// playtest specifically on a tutorial-style ability to confirm the visual
// flow isn't disrupted, separately from confirming the ability itself
// doesn't get vanilla-granted.

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Constance;

namespace ConstanceAP
{
    internal static class AbilityUnlockPatch
    {
        public static Func<string, bool> IsTrackedPersistenceId = _ => false;
        public static Action<string> OnVanillaGrantSuppressed = _ => { };

        private static MethodInfo _targetMethod;

        public static void ApplyPatch(Harmony harmony)
        {
            Type targetType = typeof(CConPlayerInventoryManager);

            _targetMethod = AccessTools.Method(
                targetType, "Collect",
                new[] { typeof(IConPlayerEntity), typeof(SConCollectable), typeof(int) });

            if (_targetMethod == null)
            {
                Plugin.Log.LogError("AbilityUnlockPatch: could not find CConPlayerInventoryManager.Collect to patch.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(AbilityUnlockPatch).GetMethod(
                nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static));

            harmony.Patch(_targetMethod, prefix: prefix);
            Plugin.Log.LogInfo("Patched CConPlayerInventoryManager.Collect.");
        }

        // Returning false skips the original method entirely.
        // __result must be set explicitly (Collect returns bool).
        private static bool Prefix(SConCollectable toCollect, ref bool __result)
        {
            try
            {
                if (toCollect == null)
                    return true; // let original handle the null case / its own warning

                string persistenceId = toCollect.PersistenceId.StringValue;
                if (!IsTrackedPersistenceId(persistenceId))
                    return true; // not one of ours -- fully vanilla behavior, zero effect

                OnVanillaGrantSuppressed(persistenceId);

                __result = false; // matches how the original signals "not collected" elsewhere (e.g. CanPay-false path)
                return false; // skip the original entirely -- the persistence flag is never set
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("AbilityUnlockPatch.Prefix error, falling back to vanilla behavior: " + e);
                return true;
            }
        }
    }
}

// Detects local player death for DeathLink, by patching the entry point
// of the real player state machine's "GameOver" state -- the same state
// machine pattern already used for abilities (ConStateAbility_Player_X),
// just the death-specific one (ConState_Player_GameOver).
//
// Unlike AbilityUnlockPatch, this is a pure Postfix (observe only, never
// skip or alter the original method) -- DeathLink needs to know a real
// death happened, not prevent one, so this is a much lower-risk patch
// than the suppression logic above.
namespace ConstanceAP
{
    internal static class DeathDetectionPatch
    {
        public static Action OnLocalPlayerDeath = () => { };

        private static MethodInfo _targetMethod;

        public static void ApplyPatch(Harmony harmony)
        {
            Type targetType = typeof(ConState_Player_GameOver);

            _targetMethod = AccessTools.Method(
                targetType, "Enter",
                new[] { typeof(IConState), typeof(ConAttackRequest) });

            if (_targetMethod == null)
            {
                Plugin.Log.LogError("DeathDetectionPatch: could not find ConState_Player_GameOver.Enter to patch.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(DeathDetectionPatch).GetMethod(
                nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static));

            harmony.Patch(_targetMethod, postfix: postfix);
            Plugin.Log.LogInfo("Patched ConState_Player_GameOver.Enter.");
        }

        private static void Postfix()
        {
            try
            {
                OnLocalPlayerDeath();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("DeathDetectionPatch.Postfix error: " + e);
            }
        }
    }
}

// Suppresses vanilla rewards from "drop"-style collectibles (chests,
// hearts, inspirations, and anything else placed via
// CConEntityDropBehaviour_TouchToCollect/_AutoCollect), which is a
// GENUINELY DIFFERENT call path from ability altars and needed its own
// investigation to find.
//
// Real, important discovery this patch is built on: AConEntityDropBehaviour
// itself has no per-instance persistence id of its own -- its own
// "collectable" field is a SHARED reward-type asset (e.g. "item_LightStone",
// used by many different pickups), not unique per placement. The real
// per-instance id (the one we track as a location, e.g. a specific chest's
// GUID) lives on a SEPARATE sibling component, retrieved via
// GetComponent<CConPersistable>() on the same GameObject -- confirmed by
// tracing AConEntityDropBehaviour's own IConCompletionEntryProvider
// implementation, which does exactly that lookup internally for its own
// purposes.
//
// This explains a real, reported bug: the earlier AbilityUnlockPatch (on
// CConPlayerInventoryManager.Collect) checks the SHARED collectable's id,
// which happens to coincide with what we track for abilities (each
// ability has its own dedicated 1:1 asset) but is wrong for chests/hearts/
// inspirations (many different pickups sharing one generic reward-type
// asset) -- so that patch never suppressed drop-style pickups at all,
// and touching a chest gave both the real vanilla contents AND whatever
// AP separately decided to send.
namespace ConstanceAP
{
    internal static class DropCollectPatch
    {
        public static Func<string, bool> IsTrackedPersistenceId = _ => false;
        public static Action<string> OnVanillaGrantSuppressed = _ => { };

        private static MethodInfo _targetMethod;

        public static void ApplyPatch(Harmony harmony)
        {
            Type targetType = typeof(AConEntityDropBehaviour);

            _targetMethod = AccessTools.Method(targetType, "Collect", Type.EmptyTypes);

            if (_targetMethod == null)
            {
                Plugin.Log.LogError("DropCollectPatch: could not find AConEntityDropBehaviour.Collect to patch.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(DropCollectPatch).GetMethod(
                nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static));

            harmony.Patch(_targetMethod, prefix: prefix);
            Plugin.Log.LogInfo("Patched AConEntityDropBehaviour.Collect.");
        }

        private static bool Prefix(object __instance)
        {
            try
            {
                var component = __instance as UnityEngine.Component;
                if (component == null) return true;

                var persistable = component.GetComponent<CConPersistable>();
                if (persistable == null)
                    return true;

                string persistenceId = persistable.PersistenceId.StringValue;
                if (!IsTrackedPersistenceId(persistenceId))
                    return true;

                OnVanillaGrantSuppressed(persistenceId);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("DropCollectPatch.Prefix error, falling back to vanilla behavior: " + e);
                return true;
            }
        }
    }
}

// Suppresses vanilla loot from chests specifically, which turned out to
// use a THIRD, genuinely different mechanism from both abilities and the
// generic drop system: CConChestEntity.SpawnLoot(), called via a Unity
// animation event partway through the chest-opening animation, using its
// own direct "persistable" field (not even needing a GetComponent lookup,
// unlike the drop-behaviour case) for its real per-instance id.
//
// Found because real testing reported chests still weren't suppressed
// even after DropCollectPatch -- confirming DropCollectPatch's coverage
// assumption (that all per-instance pickups use AConEntityDropBehaviour)
// was wrong specifically for chests, which have their own dedicated
// CConChestEntity class with multiple loot bags, a hit-counter, and its
// own animation-driven opening sequence.
namespace ConstanceAP
{
    internal static class ChestLootPatch
    {
        public static Func<string, bool> IsTrackedPersistenceId = _ => false;
        public static Action<string> OnVanillaGrantSuppressed = _ => { };

        private static FieldInfo _persistableField;
        private static MethodInfo _targetMethod;

        public static void ApplyPatch(Harmony harmony)
        {
            Type targetType = typeof(CConChestEntity);

            _persistableField = AccessTools.Field(targetType, "persistable");
            _targetMethod = AccessTools.Method(targetType, "SpawnLoot", Type.EmptyTypes);

            if (_targetMethod == null || _persistableField == null)
            {
                Plugin.Log.LogError("ChestLootPatch: could not find CConChestEntity.SpawnLoot or its persistable field to patch.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(ChestLootPatch).GetMethod(
                nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static));

            harmony.Patch(_targetMethod, prefix: prefix);
            Plugin.Log.LogInfo("Patched CConChestEntity.SpawnLoot.");
        }

        private static bool Prefix(object __instance)
        {
            try
            {
                var persistable = _persistableField.GetValue(__instance) as CConPersistable;
                if (persistable == null)
                    return true;

                string persistenceId = persistable.PersistenceId.StringValue;
                if (!IsTrackedPersistenceId(persistenceId))
                    return true;

                OnVanillaGrantSuppressed(persistenceId);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ChestLootPatch.Prefix error, falling back to vanilla behavior: " + e);
                return true;
            }
        }
    }
}

// Real, serious bug found via real feedback: receiving an ability (or
// any tracked SConCollectable-based unlock) via AP from a DIFFERENT
// location permanently disables its OWN altar's interactivity too.
// Traced the mechanism: both CConUnlockAbilityOnTouch and
// CConUnlockAbilityCanvas run a Start() check -- "do I already have
// this collectable?" -- and if so, fire an event/branch that (based on
// the naming and context) disables the altar's interaction entirely.
// This makes sense in vanilla (you'll never re-visit an altar you've
// already used), but breaks the "randomized + safety net" design
// entirely: if the ability arrives from elsewhere first, its own altar
// silently becomes permanently unreachable as a check, with no player
// feedback that anything went wrong.
//
// Fixed by skipping Start() ENTIRELY for tracked persistence ids -- the
// altar simply never runs its "already have it" branch, so it always
// stays interactive regardless of what the player already owns. Start()
// has no other responsibilities in either class (confirmed via IL --
// the "already have it" check is the entire method body), so skipping
// it outright for tracked ids is safe, not a partial workaround.
namespace ConstanceAP
{
    internal static class AbilityAltarStayInteractivePatch
    {
        public static Func<string, bool> IsTrackedPersistenceId = _ => false;

        private static FieldInfo _onTouchCollectableField;
        private static FieldInfo _canvasCollectableField;
        private static FieldInfo _onTouchOnCollectField;

        public static void ApplyPatchSetupFields(Harmony harmony)
        {
            var onTouchType = typeof(CConUnlockAbilityOnTouch);
            var canvasType = typeof(CConUnlockAbilityCanvas);

            _onTouchCollectableField = AccessTools.Field(onTouchType, "collectable");
            _canvasCollectableField = AccessTools.Field(canvasType, "collectable");
            _onTouchOnCollectField = AccessTools.Field(onTouchType, "onCollect");
        }

        public static void ApplyPatchOnTouchStart(Harmony harmony)
        {
            var onTouchType = typeof(CConUnlockAbilityOnTouch);
            var onTouchStart = AccessTools.Method(onTouchType, "Start", Type.EmptyTypes);

            if (onTouchStart == null || _onTouchCollectableField == null)
            {
                Plugin.Log.LogError("AbilityAltarStayInteractivePatch: could not find CConUnlockAbilityOnTouch.Start or its collectable field.");
            }
            else
            {
                harmony.Patch(onTouchStart, prefix: new HarmonyMethod(
                    typeof(AbilityAltarStayInteractivePatch).GetMethod(nameof(OnTouchPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Plugin.Log.LogInfo("Patched CConUnlockAbilityOnTouch.Start.");
            }
        }

        public static void ApplyPatchCanvasStart(Harmony harmony)
        {
            var canvasType = typeof(CConUnlockAbilityCanvas);
            var canvasStart = AccessTools.Method(canvasType, "Start", Type.EmptyTypes);

            if (canvasStart == null || _canvasCollectableField == null)
            {
                Plugin.Log.LogError("AbilityAltarStayInteractivePatch: could not find CConUnlockAbilityCanvas.Start or its collectable field.");
            }
            else
            {
                harmony.Patch(canvasStart, prefix: new HarmonyMethod(
                    typeof(AbilityAltarStayInteractivePatch).GetMethod(nameof(CanvasPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Plugin.Log.LogInfo("Patched CConUnlockAbilityCanvas.Start.");
            }
        }

        public static void ApplyPatchOnTouchHandleIncomingAttack(Harmony harmony)
        {
            var onTouchType = typeof(CConUnlockAbilityOnTouch);

            // Real, second bug found by real testing: Start() skipping the
            // "already have it -> disable" branch was NOT enough on its
            // own. Confirmed via IL: HandleIncomingAttack -- the method
            // that actually processes the player TOUCHING the altar, a
            // completely separate code path from Start() -- has its OWN,
            // independent "already have it" check, and short-circuits to
            // ConAttackResult.Ignored immediately if so, before Collect()
            // is ever called. That means our suppression patch (on
            // Collect itself) never even got a chance to run -- the touch
            // was being silently ignored one step earlier. This explains
            // exactly the real symptom reported: receiving an ability via
            // AP makes its own altar completely unresponsive to touch,
            // not just visually "stuck".
            var handleIncomingAttack = AccessTools.Method(
                onTouchType, "Constance.IConAttackable.HandleIncomingAttack");
            if (handleIncomingAttack == null || _onTouchOnCollectField == null)
            {
                Plugin.Log.LogError("AbilityAltarStayInteractivePatch: could not find CConUnlockAbilityOnTouch's HandleIncomingAttack or its onCollect field.");
            }
            else
            {
                harmony.Patch(handleIncomingAttack, prefix: new HarmonyMethod(
                    typeof(AbilityAltarStayInteractivePatch).GetMethod(nameof(HandleIncomingAttackPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Plugin.Log.LogInfo("Patched CConUnlockAbilityOnTouch.HandleIncomingAttack.");
            }
        }

        public static void ApplyPatchCanvasHandleIncomingAttackAndHas(Harmony harmony)
        {
            var canvasType = typeof(CConUnlockAbilityCanvas);

            // CConUnlockAbilityCanvas's own HandleIncomingAttack has the
            // exact same "already have it" early-return -- confirmed via
            // IL, right at the top of the method, entirely separate from
            // (and running well before) the async task the rest of the
            // method kicks off for the actual unlock animation. Rather
            // than replicate that async logic (UniTask-based, genuinely
            // risky to reproduce correctly by hand), this suppresses the
            // one specific Has() check the vanilla method makes, letting
            // everything else -- including the async part -- run
            // completely unmodified and correctly. Narrowly scoped and
            // self-consuming: the flag is set right before the method
            // runs and cleared the moment Has() is actually called for
            // that exact collectable, not left lingering to risk
            // affecting some unrelated later check.
            var canvasHandleIncomingAttack = AccessTools.Method(
                canvasType, "Constance.IConAttackable.HandleIncomingAttack");
            var hasMethod = AccessTools.Method(
                typeof(CConPlayerInventoryManager), "Has", new[] { typeof(SConCollectable), typeof(bool) });

            if (canvasHandleIncomingAttack == null)
            {
                Plugin.Log.LogError("AbilityAltarStayInteractivePatch: could not find CConUnlockAbilityCanvas's HandleIncomingAttack.");
            }
            else if (hasMethod == null)
            {
                Plugin.Log.LogError("AbilityAltarStayInteractivePatch: could not find CConPlayerInventoryManager.Has.");
            }
            else
            {
                harmony.Patch(canvasHandleIncomingAttack, prefix: new HarmonyMethod(
                    typeof(AbilityAltarStayInteractivePatch).GetMethod(nameof(CanvasHandleIncomingAttackPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                harmony.Patch(hasMethod, prefix: new HarmonyMethod(
                    typeof(AbilityAltarStayInteractivePatch).GetMethod(nameof(HasPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Plugin.Log.LogInfo("Patched CConUnlockAbilityCanvas.HandleIncomingAttack and CConPlayerInventoryManager.Has.");
            }
        }

        public static void ApplyPatchInspirationHandleIncomingAttack(Harmony harmony)
        {
            // Direct confirmation from real testing: this exact same bug
            // affects inspirations too, not just abilities. Confirmed via
            // IL: CConInspirationTriggerBehaviour's own
            // HandleIncomingAttack has the identical "already have it"
            // early-return, same Has() call pattern, just a differently
            // named field ("inspiration" instead of "collectable"). Reuses
            // the exact same narrowly-scoped, self-consuming suppression
            // mechanism already patched onto Has() above -- only a new
            // prefix is needed here to set the flag for this class.
            var inspirationTriggerType = AccessTools.TypeByName("Constance.CConInspirationTriggerBehaviour");
            if (inspirationTriggerType == null)
            {
                Plugin.Log.LogError("AbilityAltarStayInteractivePatch: could not find type Constance.CConInspirationTriggerBehaviour.");
            }
            else
            {
                _inspirationField = AccessTools.Field(inspirationTriggerType, "inspiration");
                var inspirationHandleIncomingAttack = AccessTools.Method(
                    inspirationTriggerType, "Constance.IConAttackable.HandleIncomingAttack");

                if (_inspirationField == null || inspirationHandleIncomingAttack == null)
                {
                    Plugin.Log.LogError("AbilityAltarStayInteractivePatch: could not find CConInspirationTriggerBehaviour's inspiration field or HandleIncomingAttack.");
                }
                else
                {
                    harmony.Patch(inspirationHandleIncomingAttack, prefix: new HarmonyMethod(
                        typeof(AbilityAltarStayInteractivePatch).GetMethod(nameof(InspirationHandleIncomingAttackPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    Plugin.Log.LogInfo("Patched CConInspirationTriggerBehaviour.HandleIncomingAttack.");
                }
            }
        }

        // Split from what used to be one single ApplyPatch method into
        // the 6 pieces above, per direct request to spread this mod's
        // own Harmony patching work across even more individual frames
        // than before -- this one class alone used to apply 5 separate
        // Harmony patches (the single most concentrated group of them)
        // in one uninterrupted burst. Each piece here does the exact
        // same work as before, completely unchanged; only the grouping
        // changed, so Plugin.cs's own coroutine can yield a frame between
        // each of these instead of between only entire classes at a time.
        // Kept as a single combined method too, for anything that still
        // wants the old, all-at-once behavior in one call.
        public static void ApplyPatch(Harmony harmony)
        {
            ApplyPatchSetupFields(harmony);
            ApplyPatchOnTouchStart(harmony);
            ApplyPatchCanvasStart(harmony);
            ApplyPatchOnTouchHandleIncomingAttack(harmony);
            ApplyPatchCanvasHandleIncomingAttackAndHas(harmony);
            ApplyPatchInspirationHandleIncomingAttack(harmony);
        }

        private static FieldInfo _inspirationField;

        private static bool InspirationHandleIncomingAttackPrefix(object __instance)
        {
            try
            {
                var collectable = _inspirationField.GetValue(__instance) as SConCollectable;
                if (collectable == null) return true;

                string persistenceId = collectable.PersistenceId.StringValue;
                if (IsTrackedPersistenceId(persistenceId))
                {
                    _suppressNextHasCheckForPersistenceId = persistenceId;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("AbilityAltarStayInteractivePatch (Inspiration prefix) error: " + e);
            }
            return true; // always let the original method run -- this only ever sets a flag
        }

        private static string _suppressNextHasCheckForPersistenceId;

        private static bool CanvasHandleIncomingAttackPrefix(object __instance)
        {
            try
            {
                var collectable = _canvasCollectableField.GetValue(__instance) as SConCollectable;
                if (collectable == null) return true;

                string persistenceId = collectable.PersistenceId.StringValue;
                if (IsTrackedPersistenceId(persistenceId))
                {
                    _suppressNextHasCheckForPersistenceId = persistenceId;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("AbilityAltarStayInteractivePatch (Canvas prefix) error: " + e);
            }
            return true; // always let the original method run -- this only ever sets a flag
        }

        private static bool HasPrefix(SConCollectable collectable, ref bool __result)
        {
            if (_suppressNextHasCheckForPersistenceId == null) return true;
            try
            {
                if (collectable != null && collectable.PersistenceId.StringValue == _suppressNextHasCheckForPersistenceId)
                {
                    _suppressNextHasCheckForPersistenceId = null; // self-consuming -- only this one call gets suppressed
                    __result = false;
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("AbilityAltarStayInteractivePatch (Has prefix) error: " + e);
            }
            return true;
        }

        private static bool OnTouchPrefix(object __instance)
        {
            return ShouldRunOriginalStart(__instance, _onTouchCollectableField);
        }

        private static bool CanvasPrefix(object __instance)
        {
            return ShouldRunOriginalStart(__instance, _canvasCollectableField);
        }

        private static bool ShouldRunOriginalStart(object instance, FieldInfo collectableField)
        {
            try
            {
                var collectable = collectableField.GetValue(instance) as SConCollectable;
                if (collectable == null) return true;

                string persistenceId = collectable.PersistenceId.StringValue;
                if (!IsTrackedPersistenceId(persistenceId))
                    return true; // not one of ours -- fully vanilla behavior

                return false; // skip Start() entirely -- never run the "already have it, disable" branch
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("AbilityAltarStayInteractivePatch error, falling back to vanilla behavior: " + e);
                return true;
            }
        }

        // Real, minimal replication of the vanilla method's own logic,
        // confirmed via IL -- identical in every way EXCEPT it never
        // short-circuits on "already have it" for tracked ids, so the
        // touch always reaches Collect() (which the existing suppression
        // patch on Collect itself already correctly intercepts and
        // reports as a check). Every other branch (wrong attack type,
        // non-player attacker) is preserved exactly as vanilla.
        private static bool HandleIncomingAttackPrefix(object __instance, object request, ref ConAttackResult __result)
        {
            try
            {
                var collectable = _onTouchCollectableField.GetValue(__instance) as SConCollectable;
                if (collectable == null) return true;

                string persistenceId = collectable.PersistenceId.StringValue;
                if (!IsTrackedPersistenceId(persistenceId))
                    return true; // not one of ours -- fully vanilla behavior, including its own "already have it" check

                var attackRequest = request as ConAttackRequest;
                if (attackRequest == null) return true;

                // AttackType 12 -- the exact numeric value confirmed via
                // IL (the same constant vanilla itself compares against).
                if ((int)attackRequest.AttackType != 12)
                {
                    __result = ConAttackResult.Ignored;
                    return false;
                }

                var player = attackRequest.Attacker as IConPlayerEntity;
                if (player == null)
                {
                    __result = ConAttackResult.Ignored;
                    return false;
                }

                // Deliberately no "already have it" check here at all --
                // that's the entire point of this patch. Always proceeds
                // to Collect(), which the existing suppression patch on
                // Collect itself handles correctly and idempotently
                // either way.
                var inventory = CConSceneRegistry.Instance?.Inventory;
                inventory?.Collect(player, collectable, 1);

                var onCollect = _onTouchOnCollectField.GetValue(__instance) as UnityEngine.Events.UnityEvent;
                onCollect?.Invoke();

                __result = ConAttackResult.Hit;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("AbilityAltarStayInteractivePatch (HandleIncomingAttack) error, falling back to vanilla behavior: " + e);
                return true;
            }
        }
    }

    // Reveal the full map from the start, not needing to
    // physically visit every room first. Real, confirmed correctness fix:
    // simply setting CConUiMapLevel.IsDiscoveredCompletely = true (via
    // reflection, in MapIconInjector) is NOT enough on its own -- traced
    // via IL that UpdateVisible (the game's own map refresh) recalculates
    // this flag from REAL exploration state every time it runs, via
    // UpdateCompletionFlag(), and overwrites whatever value was there
    // before -- including any override -- right before the fog shader
    // keyword gets applied from it. That would silently undo a one-time
    // (or even periodic) override the moment the game's own refresh logic
    // ran again, which happens unpredictably (map open, level
    // transitions, etc.), not on our schedule.
    //
    // Patching UpdateCompletionFlag itself to always return true is the
    // robust fix: it makes EVERY future recalculation -- ours or the
    // game's own, for every level including ones created after this patch
    // is applied -- naturally already agree the level is fully
    // discovered, rather than needing to continuously fight and re-win
    // against the game's own logic on some polling interval.
    internal static class MapFullRevealPatch
    {
        public static void ApplyPatch(Harmony harmony)
        {
            var mapLevelType = AccessTools.TypeByName("Constance.CConUiMapLevel");
            if (mapLevelType == null)
            {
                Plugin.Log.LogError("MapFullRevealPatch: could not find type Constance.CConUiMapLevel.");
                return;
            }

            var method = AccessTools.Method(mapLevelType, "UpdateCompletionFlag");
            if (method == null)
            {
                Plugin.Log.LogError("MapFullRevealPatch: could not find CConUiMapLevel.UpdateCompletionFlag.");
                return;
            }

            harmony.Patch(method, postfix: new HarmonyMethod(
                typeof(MapFullRevealPatch).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
            Plugin.Log.LogInfo("Patched CConUiMapLevel.UpdateCompletionFlag (full map reveal).");
        }

        private static void Postfix(ref bool __result)
        {
            __result = true;
        }
    }

    // Direct suggestion, confirmed via IL: a real method fires every time
    // the player changes rooms/levels -- IConSceneListener_ChangeLevel.
    // OnPlayerChangedLevel, implemented by CConPersistenceManager among
    // others (confirmed via IL: implements list includes it directly).
    // Hooking THIS as the trigger for RevealAllLevels is far more
    // reliable than the periodic coroutine alone, since it fires exactly
    // when new level data becomes relevant, rather than hoping a 3-second
    // poll happens to land at a useful moment.
    internal static class RoomChangeMapRevealPatch
    {
        public static void ApplyPatch(Harmony harmony)
        {
            var persistenceManagerType = typeof(CConPersistenceManager);
            var method = AccessTools.Method(persistenceManagerType, "Constance.IConSceneListener_ChangeLevel.OnPlayerChangedLevel");
            if (method == null)
            {
                Plugin.Log.LogError("RoomChangeMapRevealPatch: could not find CConPersistenceManager's OnPlayerChangedLevel implementation.");
                return;
            }

            harmony.Patch(method, postfix: new HarmonyMethod(
                typeof(RoomChangeMapRevealPatch).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
            Plugin.Log.LogInfo("Patched CConPersistenceManager.OnPlayerChangedLevel (room-change map reveal trigger).");
        }

        private static void Postfix()
        {
            try
            {
                Plugin.Log.LogInfo("RoomChangeMapRevealPatch: room change detected, re-applying map reveal.");
                var registry = CConSceneRegistry.Instance;
                var mapManager = registry?.Map as CConMapManager;
                MapIconInjector.RevealAllLevels(mapManager?.MapRoot);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("RoomChangeMapRevealPatch: error triggering map reveal on room change: " + e);
            }
        }
    }

    // Hooks the exact real moment the
    // player meditates at a shrine (confirmed via IL: this is the one and
    // only method that sets IConCheckPointManager.ShrineCheckPoint), to
    // link that specific checkpoint to whichever of the mod's own,
    // pre-placed shrine icons is closest to the player's position at that
    // moment -- see MapIconInjector.OnShrineActivated. This is what makes
    // "greyed out until visited" and "T to warp" work per-shrine, without
    // ever needing to enumerate every checkpoint in the game up front
    // (which real testing showed doesn't work -- most don't exist as
    // loaded components until the player has actually been there).
    internal static class ShrineMeditatePatch
    {
        public static void ApplyPatch(Harmony harmony)
        {
            var persistenceType = typeof(CConPlayerPersistence);
            var method = AccessTools.Method(persistenceType, "OnPlayerMeditate");
            if (method == null)
            {
                Plugin.Log.LogError("ShrineMeditatePatch: could not find CConPlayerPersistence.OnPlayerMeditate.");
                return;
            }

            harmony.Patch(method, postfix: new HarmonyMethod(
                typeof(ShrineMeditatePatch).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
            Plugin.Log.LogInfo("Patched CConPlayerPersistence.OnPlayerMeditate (shrine activation trigger).");
        }

        private static void Postfix(ConCheckPointId meditationPointId, bool saveToDisk)
        {
            try
            {
                Plugin.Log.LogInfo("ShrineMeditatePatch: player meditated at '" + meditationPointId.StringValue + "'.");
                MapIconInjector.OnShrineActivated(meditationPointId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ShrineMeditatePatch: error handling shrine activation: " + e);
            }
        }
    }
}
