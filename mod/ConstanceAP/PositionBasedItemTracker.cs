using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ConstanceAP
{
    // Give Paint Flask/Heart Piece/Eraser instances real,
    // distinct identity based on WHERE they physically are, instead of an
    // arbitrary sequential number assigned in whatever order they
    // happened to be collected in a given session. When a real pickup is
    // detected, this matches the player's current position against a
    // database of known positions for that item type (built up and
    // persisted across sessions), and grants the specific, correct
    // instance for that exact spot -- not just "the next number in line".
    // This also means the SAME physical flask always resolves to the SAME
    // AP location, regardless of which order different players/sessions
    // happen to find things in.
    internal class PositionBasedItemTracker
    {
        private readonly string _filePath;
        private readonly string _checkPositionsFilePath;
        private readonly string _idPrefix;
        private readonly HashSet<int> _validInstances;
        private readonly ManualLogSource _log;
        private readonly float _matchRadiusSq;

        private readonly Dictionary<int, Vector2> _knownPositions = new Dictionary<int, Vector2>();
        private readonly HashSet<int> _reportedInstances = new HashSet<int>();
        private bool _loaded = false;

        // 8 world units as the default match radius -- generous enough to
        // absorb minor position noise (e.g. exact pixel the pickup
        // animation triggers at), tight enough that two distinct flasks
        // placed reasonably apart won't be confused for each other. Real
        // player positions observed so far range in the hundreds, so this
        // is a small fraction of typical inter-item spacing.
        //
        // Bootstrap known positions from the general
        // check_positions.jsonl file too -- that file already has entries
        // for these exact instances from all the earlier sequential-
        // system testing, no reason to discard that and relearn from
        // scratch. idPrefix is the persistence id prefix to match against
        // (e.g. "ps_item_PaintPiece"), since that file has entries for
        // every location type, not just this one.
        //
        // Real, important fix: validInstances is an explicit set, NOT
        // assumed to be a contiguous 1..N range. Once each instance got
        // its own real, distinct AP location, the surviving numbers
        // genuinely have gaps (e.g. Eraser's valid instances are
        // {2,3,4,5}, not {1,2,3,4} -- instance 1 is the one permanently
        // absorbed by a chest and was never a real AP location). Treating
        // this as "1 through maxInstances" would let the tracker try to
        // assign a real pickup to a persistence id that doesn't
        // correspond to any actual location.
        public PositionBasedItemTracker(string filePath, string checkPositionsFilePath, string idPrefix,
            IEnumerable<int> validInstances, ManualLogSource log, float matchRadius = 8f)
        {
            _filePath = filePath;
            _checkPositionsFilePath = checkPositionsFilePath;
            _idPrefix = idPrefix;
            _validInstances = new HashSet<int>(validInstances);
            _log = log;
            _matchRadiusSq = matchRadius * matchRadius;
        }

        public int ReportedCount
        {
            get { Load(); return _reportedInstances.Count; }
        }

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var obj = JObject.Parse(json);

                        var positions = obj["positions"] as JObject;
                        if (positions != null)
                        {
                            foreach (var prop in positions.Properties())
                            {
                                if (!int.TryParse(prop.Name, out int instance)) continue;
                                var posObj = prop.Value;
                                float x = posObj["x"]?.ToObject<float>() ?? 0f;
                                float y = posObj["y"]?.ToObject<float>() ?? 0f;
                                _knownPositions[instance] = new Vector2(x, y);
                            }
                        }

                        var reported = obj["reported"] as JArray;
                        if (reported != null)
                        {
                            foreach (var val in reported)
                            {
                                _reportedInstances.Add(val.ToObject<int>());
                            }
                        }
                    }
                }

                _log.LogInfo("PositionBasedItemTracker (" + Path.GetFileName(_filePath) + "): loaded " +
                             _knownPositions.Count + " known position(s), " + _reportedInstances.Count +
                             " already reported (own file).");
            }
            catch (Exception e)
            {
                _log.LogWarning("PositionBasedItemTracker: error loading " + _filePath + " (starting fresh): " + e);
            }

            BootstrapFromCheckPositions();
        }

        // Check_positions.jsonl already has entries for
        // these exact instances (recorded by the old, purely sequential
        // system across all earlier testing) -- import any positions this
        // tracker doesn't already know about, rather than discarding that
        // and relearning everything from a blank slate. Only ever fills
        // in positions, never touches _reportedInstances -- whether an
        // instance has been reported by THIS specific save is a separate
        // question, driven by the real banked/counter state, not by
        // whatever happened across whichever sessions produced this file.
        private void BootstrapFromCheckPositions()
        {
            if (string.IsNullOrEmpty(_checkPositionsFilePath) || !File.Exists(_checkPositionsFilePath)) return;

            int imported = 0;
            try
            {
                string prefix = _idPrefix + "#instance";
                foreach (string line in File.ReadLines(_checkPositionsFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject entry;
                    try
                    {
                        entry = JObject.Parse(line);
                    }
                    catch
                    {
                        continue; // one malformed line must never stop the rest
                    }

                    string id = entry["id"]?.ToObject<string>();
                    if (id == null || !id.StartsWith(prefix, StringComparison.Ordinal)) continue;

                    string instancePart = id.Substring(prefix.Length);
                    if (!int.TryParse(instancePart, out int instance)) continue;
                    if (!_validInstances.Contains(instance)) continue; // stale/invalid entry -- not (or no longer) a real location
                    if (_knownPositions.ContainsKey(instance)) continue; // don't overwrite what we already have

                    float x = entry["x"]?.ToObject<float>() ?? 0f;
                    float y = entry["y"]?.ToObject<float>() ?? 0f;
                    _knownPositions[instance] = new Vector2(x, y);
                    imported++;
                }

                if (imported > 0)
                {
                    _log.LogInfo("PositionBasedItemTracker (" + Path.GetFileName(_filePath) + "): imported " +
                                 imported + " position(s) from check_positions.jsonl.");
                    Save();
                }
            }
            catch (Exception e)
            {
                _log.LogWarning("PositionBasedItemTracker: error bootstrapping from " + _checkPositionsFilePath + ": " + e);
            }
        }

        private void Save()
        {
            try
            {
                var obj = new JObject();
                var positions = new JObject();
                foreach (var kvp in _knownPositions)
                {
                    var posObj = new JObject
                    {
                        ["x"] = kvp.Value.x,
                        ["y"] = kvp.Value.y
                    };
                    positions[kvp.Key.ToString()] = posObj;
                }
                obj["positions"] = positions;
                obj["reported"] = new JArray(_reportedInstances);
                File.WriteAllText(_filePath, obj.ToString());
            }
            catch (Exception e)
            {
                _log.LogWarning("PositionBasedItemTracker: error saving " + _filePath + ": " + e);
            }
        }

        // Given a real pickup just happened at playerPos, determine WHICH
        // specific instance it was, mark it reported, and persist
        // immediately. Returns the instance number (1-based), or null if
        // every instance is already accounted for (shouldn't normally
        // happen if maxInstances is accurate).
        public int? ResolvePickup(Vector2 playerPos)
        {
            Load();

            // First: does this match an ALREADY-KNOWN, not-yet-reported
            // position closely? If several sit within the radius, the
            // genuinely closest one wins.
            int? closest = null;
            float closestDistSq = float.MaxValue;
            foreach (var kvp in _knownPositions)
            {
                if (_reportedInstances.Contains(kvp.Key)) continue;
                float distSq = (kvp.Value - playerPos).sqrMagnitude;
                if (distSq <= _matchRadiusSq && distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closest = kvp.Key;
                }
            }

            if (closest.HasValue)
            {
                _reportedInstances.Add(closest.Value);
                Save();
                return closest;
            }

            // No known position matched closely -- either a genuinely new
            // physical spot never seen before, or a spot whose position
            // just hasn't been learned yet, OR (real, confirmed bug found
            // by real testing) a spot whose recorded position is simply
            // STALE -- left over from a completely different seed tested
            // earlier, where this same instance number happened to sit
            // somewhere else physically. This file persists across every
            // seed/session ever run, not just the current one, so after
            // enough testing every valid instance can end up with SOME
            // known position on file even though most of them were never
            // actually reported in the CURRENT seed. The old version of
            // this loop skipped any instance with a known position at
            // all, regardless of whether it had been reported -- once
            // every single valid instance had accumulated some position
            // from testing, there was nothing left to ever fall back to,
            // even with several genuinely unreported slots sitting right
            // there. Reported (not merely "has a position on file") is
            // the only thing that actually means "already used in this
            // playthrough" -- so the check here is just that, with the
            // stale position overwritten with this pickup's real one.
            foreach (int i in SortedValidInstances())
            {
                if (_reportedInstances.Contains(i)) continue;
                _knownPositions[i] = playerPos;
                _reportedInstances.Add(i);
                Save();
                return i;
            }

            return null;
        }

        // Fallback for when no player position is available at all (rare
        // -- PlayerOne failed to resolve). Just claims the lowest-numbered
        // unreported valid instance, position-blind, same as the old
        // purely sequential behavior. Does not touch _knownPositions.
        public int? ResolvePickupWithoutPosition()
        {
            Load();
            foreach (int i in SortedValidInstances())
            {
                if (_reportedInstances.Contains(i)) continue;
                _reportedInstances.Add(i);
                Save();
                return i;
            }
            return null;
        }

        private IEnumerable<int> SortedValidInstances()
        {
            var sorted = new List<int>(_validInstances);
            sorted.Sort();
            return sorted;
        }
    }
}
