using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using IPA.Utilities;
using Newtonsoft.Json;

namespace JDFixer
{
    // One remembered setpoint per playable beatmap: the jump distance the beatmap was last played
    // at. Two sources, in this order:
    //
    //   1. BeatLeader replays, where they exist. They record what the game actually used, they
    //      cover every map played before this feature existed, and BeatLeader keeps exactly one
    //      per beatmap by default (OverrideOldReplays), so the folder is already a
    //      "last played, one per beatmap" index.
    //   2. Our own record, written by the movement-data patch on every play. The fallback for
    //      maps with no replay, and for anyone without BeatLeader.
    //
    // Deliberately NOT in JDFixer.json. That file is a BSIPA generated store, rewritten whole on
    // every change, and this collection grows with the song library rather than with the settings.
    internal class MapMemoryEntry
    {
        // The applied jump distance. From our own record this is the value BEFORE the song-speed
        // adjustment -- storing the post-speed value would let one play at 1.2x permanently move
        // the map's setpoint. Replays with a speed modifier are skipped for the same reason.
        [JsonProperty("jd")]
        public float JumpDistance { get; set; }

        // The map's NJS at the time, which is what converts this to a reaction time. Kept so a
        // remapped chart can be spotted rather than silently reinterpreted. 0 for a replay-seeded
        // entry, which does not record it.
        [JsonProperty("njs")]
        public float NJS { get; set; }

        // Which slider the value was set with. Informational: JD and RT are the same setpoint seen
        // through NJS, so the restore does not need it.
        [JsonProperty("mode")]
        public int SliderSetting { get; set; }

        // Forgotten by the player. A tombstone rather than a deletion, because a deleted entry
        // whose replay is still on disk would be seeded straight back in on the next launch.
        [JsonProperty("ignored")]
        public bool Ignored { get; set; }

        // BeatLeader has already been asked about this beatmap and gave a definitive answer.
        // Together with a JumpDistance of 0 that is the negative cache: asked, no score there,
        // never ask again. Only a definitive answer sets this -- a timeout or a server error must
        // not be remembered as "nothing there".
        [JsonProperty("fetched")]
        public bool Fetched { get; set; }

        // Song name and mapper, lowercased. What lets a value follow a map across a re-upload:
        // the hash changes, this does not.
        [JsonProperty("song")]
        public string Identity { get; set; }

        // A stored value only counts if something actually set one. A fetched-but-empty entry
        // exists purely to stop the request being repeated.
        [JsonIgnore]
        public bool HasValue => !Ignored && JumpDistance > 0f;
    }


    internal class MapMemoryFile
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        // Newest replay already taken into account. Replays at or below this are never read again,
        // which is what stops Forget All from being undone on the next launch.
        [JsonProperty("replayWatermark")]
        public long ReplayWatermark { get; set; }

        [JsonProperty("entries")]
        public Dictionary<string, MapMemoryEntry> Entries { get; set; } = new Dictionary<string, MapMemoryEntry>();
    }


    internal static class MapMemory
    {
        private const string File_Name = "JDFixer_MapValues.json";

        private static readonly object _lock = new object();
        private static Dictionary<string, MapMemoryEntry> _entries = new Dictionary<string, MapMemoryEntry>();
        private static long _replay_watermark = 0;
        private static bool _loaded = false;
        private static bool _dirty = false;
        private static int _write_queued = 0;

        // The beatmap the game is transitioning into. Set by the standard-level transition patch,
        // cleared by the multiplayer and campaign ones, and read by VariableMovementDataProvider's
        // patch -- which is the only place that knows the value actually applied, and the one place
        // that is not told which map it is for.
        internal static BeatmapKey Pending_Key = default(BeatmapKey);

        // The song identity of that same beatmap, so a play records what a later re-upload can
        // be matched on. Set and cleared with Pending_Key.
        internal static string Pending_Identity = null;

        internal static string File_Path => Path.Combine(UnityGame.UserDataPath, File_Name);

        // identity + characteristic + difficulty -> the entry holding a value for it. Rebuilt
        // from _entries, and the reason a re-upload can find its predecessor without a scan.
        private static readonly Dictionary<string, MapMemoryEntry> _by_identity =
            new Dictionary<string, MapMemoryEntry>();


        // Song name and mapper, normalised. Deliberately not the song author: a replay does not
        // record it, and matching has to work from both sides.
        internal static string Identity_Of(string songName, string mapper)
        {
            if (string.IsNullOrEmpty(songName))
            {
                return null;
            }

            return songName.Trim().ToLowerInvariant() + "|" +
                   (mapper == null ? "" : mapper.Trim().ToLowerInvariant());
        }


        private static string Identity_Key(string identity, BeatmapKey key)
        {
            if (identity == null || !key.IsValid())
            {
                return null;
            }

            return identity + "|" + key.characteristic.SerializedName() + "|" + key.difficulty.ToString();
        }


        // Called with the lock held.
        private static void Index(string map_key, MapMemoryEntry entry)
        {
            if (entry.Identity == null || !entry.HasValue)
            {
                return;
            }

            // The map key already ends in characteristic|difficulty, so reuse that tail rather
            // than re-deriving it from a BeatmapKey we do not have here.
            int cut = map_key.IndexOf('|');

            if (cut < 0)
            {
                return;
            }

            _by_identity[entry.Identity + map_key.Substring(cut)] = entry;
        }


        private static void Reindex()
        {
            _by_identity.Clear();

            foreach (KeyValuePair<string, MapMemoryEntry> pair in _entries)
            {
                Index(pair.Key, pair.Value);
            }
        }


        // levelId, characteristic and difficulty: NJS differs per difficulty, so this is one entry
        // per playable beatmap rather than per song.
        internal static string Key_For(BeatmapKey key)
        {
            // BeatmapCharacteristic is an enum since 1.44.3, so it cannot be null and has no
            // serializedName property. SerializedName() is the extension that gives the game's own
            // name for it -- NOT ToString(), which differs for the rotation modes ("Degree90" where
            // the game and every replay say "90Degree"). Using the serialized name keeps this id
            // comparable with anything else that records a beatmap.
            if (string.IsNullOrEmpty(key.levelId) || !key.IsValid())
            {
                return null;
            }

            return key.levelId + "|" + key.characteristic.SerializedName() + "|" + key.difficulty.ToString();
        }


        internal static void Load()
        {
            lock (_lock)
            {
                _loaded = true;
                _dirty = false;
                _entries = new Dictionary<string, MapMemoryEntry>();
                _replay_watermark = 0;

                try
                {
                    if (File.Exists(File_Path))
                    {
                        MapMemoryFile file = JsonConvert.DeserializeObject<MapMemoryFile>(File.ReadAllText(File_Path));

                        if (file != null && file.Entries != null)
                        {
                            _entries = file.Entries;
                            _replay_watermark = file.ReplayWatermark;
                        }
                    }
                }
                catch (Exception e)
                {
                    // A damaged file must not take the mod down with it: start empty and let the
                    // next play write a good one.
                    Plugin.Log.Warn("Could not read " + File_Name + ": " + e.Message);
                    _entries = new Dictionary<string, MapMemoryEntry>();
                }

                Reindex();
                Plugin.Log.Debug("MapMemory: " + _entries.Count + " remembered maps, " +
                                 _by_identity.Count + " indexed by song");
            }

            Queue_Seed_From_Replays();
        }


        private static void Ensure_Loaded()
        {
            if (!_loaded)
            {
                Load();
            }
        }


        // Off the main thread: this reads the head of every .bsor in the replay folder, and boot is
        // already the most stall-prone part of a session.
        private static void Queue_Seed_From_Replays()
        {
            if (!PluginConfig.Instance.use_replay_values)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Seed_From_Replays(ReplayIndex.Scan());
                }
                catch (Exception e)
                {
                    Plugin.Log.Warn("Could not read replays: " + e.Message);
                }
            });
        }


        internal static void Seed_From_Replays(Dictionary<string, ReplayValue> replays)
        {
            int seeded = 0;
            int backfilled = 0;
            int forgotten = 0;
            long highest;

            lock (_lock)
            {
                Ensure_Loaded();

                highest = _replay_watermark;

                foreach (KeyValuePair<string, ReplayValue> pair in replays)
                {
                    if (pair.Value.Timestamp > highest)
                    {
                        highest = pair.Value.Timestamp;
                    }

                    MapMemoryEntry existing;

                    // Backfill the song identity onto entries written before that field existed,
                    // and do it REGARDLESS of the watermark. It adds no value and restores
                    // nothing -- it only lets a value already stored here be found again after
                    // the map is re-uploaded under a new hash. Without this, everything recorded
                    // before today stays invisible to that lookup until it is played again.
                    if (_entries.TryGetValue(pair.Key, out existing) &&
                        existing.Identity == null && pair.Value.Identity != null)
                    {
                        existing.Identity = pair.Value.Identity;
                        Index(pair.Key, existing);
                        backfilled++;
                        _dirty = true;
                    }

                    // Already accounted for. Re-reading it would undo a Forget All.
                    if (pair.Value.Timestamp <= _replay_watermark)
                    {
                        continue;
                    }

                    MapMemoryEntry entry;
                    if (_entries.TryGetValue(pair.Key, out entry))
                    {
                        if (entry.Ignored)
                        {
                            // Explicitly forgotten. A replay does not bring it back.
                            forgotten++;
                            continue;
                        }
                    }
                    else
                    {
                        entry = new MapMemoryEntry();
                        _entries[pair.Key] = entry;
                    }

                    // The replay does not record NJS. Nothing reads the stored NJS to restore a
                    // value -- the live map's is used -- and the next play rewrites it.
                    entry.JumpDistance = pair.Value.JumpDistance;
                    entry.NJS = 0f;
                    entry.SliderSetting = PluginConfig.Instance.slider_setting;

                    if (pair.Value.Identity != null)
                    {
                        entry.Identity = pair.Value.Identity;
                    }

                    Index(pair.Key, entry);
                    seeded++;
                }

                if (seeded == 0 && backfilled == 0 && highest == _replay_watermark)
                {
                    return;
                }

                _replay_watermark = highest;
                _dirty = true;
            }

            Plugin.Log.Debug("MapMemory: seeded " + seeded + " map(s) from replays, backfilled " +
                             backfilled + " song identit(y/ies), " + forgotten +
                             " left forgotten; watermark now " + highest);
            Queue_Save();
        }


        internal static MapMemoryEntry Get(BeatmapKey key)
        {
            string id = Key_For(key);

            if (id == null)
            {
                return null;
            }

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                if (_entries.TryGetValue(id, out entry) && entry.HasValue)
                {
                    return entry;
                }
            }

            return null;
        }


        internal static bool Has(BeatmapKey key)
        {
            return Get(key) != null;
        }


        internal static int Count
        {
            get
            {
                lock (_lock)
                {
                    Ensure_Loaded();

                    int count = 0;
                    foreach (KeyValuePair<string, MapMemoryEntry> pair in _entries)
                    {
                        if (pair.Value.HasValue)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }


        internal static void Remember(BeatmapKey key, float jumpDistance, float njs, string identity)
        {
            string id = Key_For(key);

            if (id == null)
            {
                return;
            }

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                if (!_entries.TryGetValue(id, out entry))
                {
                    entry = new MapMemoryEntry();
                    _entries[id] = entry;
                }
                else if (!entry.Ignored && entry.JumpDistance == jumpDistance && entry.NJS == njs &&
                         entry.SliderSetting == PluginConfig.Instance.slider_setting)
                {
                    // Replaying a map at the same value is the common case. Do not rewrite the file
                    // for it.
                    return;
                }

                entry.JumpDistance = jumpDistance;
                entry.NJS = njs;
                entry.SliderSetting = PluginConfig.Instance.slider_setting;

                if (identity != null)
                {
                    entry.Identity = identity;
                }

                // Playing a map you had forgotten is how you un-forget it.
                entry.Ignored = false;

                Index(id, entry);
                _dirty = true;
            }

            Plugin.Log.Debug("MapMemory: remembered " + jumpDistance.ToString("0.##") + " for " + id);
            Queue_Save();
        }


        // Explicitly forgotten by the player. Distinct from "has no value": a tombstone means a
        // value was deliberately thrown away, and nothing should go looking for a replacement.
        internal static bool Is_Forgotten(BeatmapKey key)
        {
            string id = Key_For(key);

            if (id == null)
            {
                return false;
            }

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                return _entries.TryGetValue(id, out entry) && entry.Ignored;
            }
        }


        // True once BeatLeader has given a definitive answer for this beatmap, whether or not it
        // had a score. This is what stops the same request going out twice.
        internal static bool Was_Fetched(BeatmapKey key)
        {
            string id = Key_For(key);

            if (id == null)
            {
                return true;
            }

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                return _entries.TryGetValue(id, out entry) && entry.Fetched;
            }
        }


        // A value downloaded from BeatLeader. Note this is the jump distance of the score they
        // hold, which is the player best rather than literally the last play -- the local replay is
        // the last play, and is preferred wherever one exists.
        internal static void Record_Downloaded(BeatmapKey key, float jumpDistance, string identity)
        {
            Record_Fetch(key, jumpDistance, identity);
        }


        // Asked, and there was no score to read. Stored so the request is never repeated.
        internal static void Record_No_Score(BeatmapKey key)
        {
            Record_Fetch(key, 0f, null);
        }


        private static void Record_Fetch(BeatmapKey key, float jumpDistance, string identity)
        {
            string id = Key_For(key);

            if (id == null)
            {
                return;
            }

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                if (!_entries.TryGetValue(id, out entry))
                {
                    entry = new MapMemoryEntry();
                    _entries[id] = entry;
                }

                // A download never overrules something the player already has. Both the local
                // replay and our own record are the last play; this is only ever the fallback.
                if (!entry.HasValue && !entry.Ignored)
                {
                    entry.JumpDistance = jumpDistance;
                    entry.NJS = 0f;
                    entry.SliderSetting = PluginConfig.Instance.slider_setting;

                    if (identity != null)
                    {
                        entry.Identity = identity;
                        Index(id, entry);
                    }
                }

                entry.Fetched = true;
                _dirty = true;
            }

            Queue_Save();
        }


        internal static bool Forget(BeatmapKey key)
        {
            string id = Key_For(key);

            if (id == null)
            {
                return false;
            }

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                if (!_entries.TryGetValue(id, out entry) || entry.Ignored)
                {
                    return false;
                }

                // Tombstone, not a delete: this map's replay is probably still on disk, and a
                // deleted entry would be seeded straight back in on the next launch.
                entry.Ignored = true;
                entry.JumpDistance = 0f;
                entry.NJS = 0f;

                _dirty = true;
            }

            Queue_Save();
            return true;
        }


        // Undoes a Forget. The replay file was never touched, so the value is still on disk --
        // it just stopped being reachable once the entry was tombstoned. Looked up directly
        // rather than by re-seeding, because the watermark has long since passed this replay.
        internal static bool Restore_Forgotten(BeatmapKey key)
        {
            string id = Key_For(key);

            if (id == null)
            {
                return false;
            }

            bool from_replay = false;

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                if (!_entries.TryGetValue(id, out entry) || !entry.Ignored)
                {
                    return false;
                }

                ReplayValue replay;
                if (PluginConfig.Instance.use_replay_values && ReplayIndex.Try_Lookup(id, out replay))
                {
                    entry.JumpDistance = replay.JumpDistance;
                    entry.NJS = 0f;
                    entry.SliderSetting = PluginConfig.Instance.slider_setting;
                    from_replay = true;
                }
                else
                {
                    // Nothing local to restore -- the value may have come from BeatLeader. Let the
                    // downloader ask again the next time this map is selected, if it is enabled.
                    entry.Fetched = false;
                }

                entry.Ignored = false;
                _dirty = true;
            }

            Plugin.Log.Debug("MapMemory: un-forgot " + id +
                             (from_replay ? " from its replay" : " (no local replay; may be re-fetched)"));
            Queue_Save();
            return from_replay;
        }


        internal static void Forget_All()
        {
            long highest = 0;

            // Read the replay folder first, outside the lock. Without moving the watermark past
            // every replay on disk, the next launch would seed all of them back.
            if (PluginConfig.Instance.use_replay_values)
            {
                try
                {
                    foreach (KeyValuePair<string, ReplayValue> pair in ReplayIndex.Scan())
                    {
                        if (pair.Value.Timestamp > highest)
                        {
                            highest = pair.Value.Timestamp;
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.Warn("Could not read replays while forgetting: " + e.Message);
                }
            }

            lock (_lock)
            {
                Ensure_Loaded();

                if (_entries.Count == 0 && highest <= _replay_watermark)
                {
                    return;
                }

                _entries.Clear();

                if (highest > _replay_watermark)
                {
                    _replay_watermark = highest;
                }

                _dirty = true;
            }

            Queue_Save();
        }


        // Puts the remembered value into the config the sliders bind to. Must run before the UI is
        // told the selection changed.
        internal static bool Restore(BeatmapInfo info)
        {
            if (info == null || !PluginConfig.Instance.remember_per_map)
            {
                return false;
            }

            MapMemoryEntry entry = Get(info.Key);

            if (entry == null && !Is_Forgotten(info.Key))
            {
                // No value for this exact hash. The same map uploaded again is a different hash
                // and a different levelId, so the exact-key lookup misses it entirely -- measured
                // at 513 maps in a 14,651 map library, 3.5%. Song name plus mapper survives the
                // re-upload, and both a replay and a BeatmapLevel carry them.
                //
                // Only when the map was not deliberately forgotten: a tombstone means no value is
                // wanted here, and a sibling would quietly reinstate one.
                entry = By_Identity(info);

                if (entry != null)
                {
                    Plugin.Log.Debug("MapMemory: using a value from another upload of " + info.SongIdentity);
                }
            }

            float jd;

            if (entry != null)
            {
                jd = entry.JumpDistance;

                // Snapping reads the SLIDER and rounds it up to a beat-fraction point; the value
                // stored here is the point it landed on. Writing that point straight back is not
                // idempotent, because Calculate_Nearest_RT_Snap_Point returns the first point >=
                // the slider value -- a ceiling, despite the name -- and the jd/rt round trip can
                // land one float ULP high, which sends it a whole step up. Measured over one map:
                // 15 of 72 snap points ratcheted upward on every restore.
                //
                // So restore a slider position that SNAPS TO the stored value rather than the
                // value itself, by sitting just under it. 1% of a step is thousands of ULPs clear
                // of the error and a fifth of a millisecond on an 18 ms step, so it can never
                // reach the point below. Measured at 0 of 72 ratcheting, as is anything from 0.1%
                // to 25%.
                //
                // The alternative was making the snap a true nearest. That fixes this too and is
                // arguably what the name promises, but it moves which point 92 of 186 slider
                // positions land on -- every snapping user would find their maps re-pitched.
                if (Snapping_Active() && info.JDOffsetQuantum > 0f)
                {
                    jd -= info.JDOffsetQuantum * 0.01f;
                }
            }
            else if (PluginConfig.Instance.use_default_for_unsaved)
            {
                // Never played, never fetched. Fall back to the configured baseline rather than
                // leaving the previous map's value in the slider.
                jd = Default_Jump_Distance(info);
            }
            else
            {
                return false;
            }

            // The slider range is per map -- in RT mode it is derived from the map's NJS -- so a
            // value saved under a different range has to be brought back inside this one, or the
            // slider and the config disagree about what is set.
            if (info.MaxJDSlider > info.MinJDSlider)
            {
                jd = Math.Min(Math.Max(jd, info.MinJDSlider), info.MaxJDSlider);
            }

            PluginConfig.Instance.jumpDistance = jd;

            if (info.NJS > 0.002f)
            {
                PluginConfig.Instance.reactionTime = jd * 500f / info.NJS;
            }

            return true;
        }


        // A value stored against a different upload of the same map.
        private static MapMemoryEntry By_Identity(BeatmapInfo info)
        {
            string key = Identity_Key(info.SongIdentity, info.Key);

            if (key == null)
            {
                return null;
            }

            lock (_lock)
            {
                Ensure_Loaded();

                MapMemoryEntry entry;
                if (_by_identity.TryGetValue(key, out entry) && entry.HasValue)
                {
                    return entry;
                }
            }

            return null;
        }


        // The exact condition VariableMovementDataProviderPatch uses to decide whether a play
        // takes the snapped value instead of the slider value. Duplicated rather than shared
        // because the patch owns it; if that gate moves, this has to move with it.
        private static bool Snapping_Active()
        {
            return PluginConfig.Instance.use_offset &&
                   PluginConfig.Instance.legacy_display_enabled &&
                   PluginConfig.Instance.use_rt_pref == false &&
                   PluginConfig.Instance.use_jd_pref == false;
        }


        // The default is one setpoint held in two units. Which of them travels between maps
        // depends on the slider in use: a JD default is constant metres, an RT default is constant
        // milliseconds and so a different JD on every map.
        private static float Default_Jump_Distance(BeatmapInfo info)
        {
            if (PluginConfig.Instance.slider_setting == 0)
            {
                return PluginConfig.Instance.default_jumpDistance;
            }

            return BeatmapUtils.Calculate_JumpDistance_Setpoint_Float(
                PluginConfig.Instance.default_reactionTime, info.NJS);
        }


        private static void Queue_Save()
        {
            // One writer at a time. Anything set while a write is in flight stays dirty and is
            // picked up by the next Remember or by the flush on disable.
            if (Interlocked.CompareExchange(ref _write_queued, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Save();
                }
                finally
                {
                    Interlocked.Exchange(ref _write_queued, 0);
                }
            });
        }


        internal static void Save()
        {
            string json;

            lock (_lock)
            {
                if (!_dirty)
                {
                    return;
                }

                json = JsonConvert.SerializeObject(
                    new MapMemoryFile { ReplayWatermark = _replay_watermark, Entries = _entries },
                    Formatting.Indented);

                _dirty = false;
            }

            try
            {
                // Write a temp file and move it into place: the game is routinely killed rather than
                // quit, and a half-written file would lose every map, not just the last one.
                string path = File_Path;
                string temp = path + ".tmp";

                File.WriteAllText(temp, json);

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Plugin.Log.Warn("Could not write " + File_Name + ": " + e.Message);

                lock (_lock)
                {
                    _dirty = true;
                }
            }
        }
    }
}
