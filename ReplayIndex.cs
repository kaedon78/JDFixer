using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IPA.Utilities;

namespace JDFixer
{
    internal struct ReplayValue
    {
        internal long Timestamp;
        internal float JumpDistance;

        // Song name and mapper, which is what ties a re-upload to the map it is a copy of.
        // The replay carries both, so a seeded entry gets this for free.
        internal string Identity;
    }


    // BeatLeader records the jump distance the game actually used -- ReplayRecorder reads
    // VariableMovementDataProvider.jumpDistance at OnBeatSpawnControllerDidInit, which is after
    // JDFixer's patch has run -- and by default deletes older replays for the same beatmap
    // (OverrideOldReplays), so the replay folder is already a "last played, one per beatmap" index.
    //
    // That makes it a better source than our own record for anything played before this feature
    // existed, and it costs nothing to read: only the info block is parsed, which is the first few
    // hundred bytes of each file. Frames are never touched.
    internal static class ReplayIndex
    {
        private const int Magic = 0x442D3D69;

        // Only the head of the file is read. The info block is well under this.
        private const int Header_Bytes = 4096;

        // Outside this range it is not a setting anyone chose. Measured against a real folder,
        // 1 file in 70 read -66.4, which the game cannot have played.
        private const float JD_Min = 1f;
        private const float JD_Max = 60f;

        internal static string Replays_Path => Path.Combine(UnityGame.UserDataPath, "BeatLeader", "Replays");

        // The BeatLeader player id seen in the local replays, which is what the download fallback
        // needs to ask about. Taken from the replays rather than from BeatLeader's config, which
        // does not store it, or from the platform user model, which this game version does not
        // expose as an injectable interface. Null until a scan has run and found one.
        internal static string Player_Id { get; private set; }

        // The last scan, kept so a single beatmap can be looked up without re-reading the folder.
        // Seeding drops its copy; this is what makes a forgotten value retrievable afterwards.
        private static Dictionary<string, ReplayValue> _last_scan;


        internal static Dictionary<string, ReplayValue> Scan()
        {
            Dictionary<string, ReplayValue> found = new Dictionary<string, ReplayValue>();

            string dir = Replays_Path;

            if (!Directory.Exists(dir))
            {
                Plugin.Log.Debug("ReplayIndex: no BeatLeader replay folder, nothing to read");
                return found;
            }

            string[] files;

            try
            {
                files = Directory.GetFiles(dir, "*.bsor");
            }
            catch (Exception e)
            {
                Plugin.Log.Warn("ReplayIndex: could not list replays: " + e.Message);
                return found;
            }

            Dictionary<string, int> player_ids = new Dictionary<string, int>();

            int skipped_unreadable = 0;
            int corrected_speed = 0;
            int skipped_practice = 0;
            int skipped_jd = 0;

            foreach (string file in files)
            {
                ReplayInfo info;

                try
                {
                    info = Read_Info(file);
                }
                catch (Exception)
                {
                    // A truncated or half-written replay is not an error worth a line each.
                    skipped_unreadable++;
                    continue;
                }

                if (info == null)
                {
                    skipped_unreadable++;
                    continue;
                }

                if (!string.IsNullOrEmpty(info.PlayerId))
                {
                    int seen;
                    player_ids.TryGetValue(info.PlayerId, out seen);
                    player_ids[info.PlayerId] = seen + 1;
                }

                // info.speed is the PRACTICE speed and is 0 for a normal play. Unlike a speed
                // modifier it is not a fixed factor, so this one really is unrecoverable.
                if (info.Speed != 0f)
                {
                    skipped_practice++;
                    continue;
                }

                // A speed modifier multiplies the recorded value by a known constant, so it is
                // divided back out rather than thrown away.
                float jd = SongSpeed.Remove(info.Modifiers, info.JumpDistance);

                if (jd != info.JumpDistance)
                {
                    corrected_speed++;
                }

                // Checked after the correction: the raw value under a modifier is not the thing
                // being stored.
                if (jd < JD_Min || jd > JD_Max || float.IsNaN(jd))
                {
                    skipped_jd++;
                    continue;
                }

                string key = Key_For(info);

                if (key == null)
                {
                    skipped_unreadable++;
                    continue;
                }

                ReplayValue existing;
                if (found.TryGetValue(key, out existing) && existing.Timestamp >= info.Timestamp)
                {
                    continue;
                }

                found[key] = new ReplayValue
                {
                    Timestamp = info.Timestamp,
                    JumpDistance = jd,
                    Identity = MapMemory.Identity_Of(info.SongName, info.Mapper)
                };
            }

            // The most common id in the folder. A shared install can hold more than one player;
            // the one who plays here is the one worth asking about.
            string best_id = null;
            int best_count = 0;
            foreach (KeyValuePair<string, int> pair in player_ids)
            {
                if (pair.Value > best_count)
                {
                    best_count = pair.Value;
                    best_id = pair.Key;
                }
            }

            if (best_id != null)
            {
                Player_Id = best_id;
            }

            _last_scan = found;

            // Every reason a replay contributed nothing is reported. A silent skip would make a
            // partial read look like a complete one.
            Plugin.Log.Debug("ReplayIndex: " + files.Length + " replay(s) -> " + found.Count +
                             " beatmap(s); corrected " + corrected_speed + " for song speed; skipped " +
                             skipped_unreadable + " unreadable, " + skipped_practice + " practice, " +
                             skipped_jd + " implausible JD");

            return found;
        }


        // Reads the folder only if no scan has happened yet. On the ordinary path this is a
        // dictionary lookup, because boot has already scanned.
        internal static bool Try_Lookup(string id, out ReplayValue value)
        {
            if (_last_scan == null)
            {
                Scan();
            }

            value = default(ReplayValue);
            return _last_scan != null && _last_scan.TryGetValue(id, out value);
        }


        // Must produce the same string as MapMemory.Key_For. MapEnhancer writes
        // levelId.Replace("custom_level_", "") into hash and characteristic.SerializedName() into
        // mode, so re-adding the prefix recovers the levelId exactly and the mode needs no
        // translation.
        private static string Key_For(ReplayInfo info)
        {
            if (string.IsNullOrEmpty(info.Hash) || string.IsNullOrEmpty(info.Mode) ||
                string.IsNullOrEmpty(info.Difficulty))
            {
                return null;
            }

            string levelId = Is_Sha1(info.Hash) ? "custom_level_" + info.Hash : info.Hash;

            return levelId + "|" + info.Mode + "|" + info.Difficulty;
        }


        private static bool Is_Sha1(string text)
        {
            if (text.Length != 40)
            {
                return false;
            }

            foreach (char c in text)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }


        private class ReplayInfo
        {
            internal long Timestamp;
            internal string PlayerId;
            internal string Hash;
            internal string SongName;
            internal string Mapper;
            internal string Mode;
            internal string Difficulty;
            internal string Modifiers;
            internal float JumpDistance;
            internal float Speed;
        }


        private static ReplayInfo Read_Info(string path)
        {
            byte[] buffer;

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int length = (int)Math.Min(Header_Bytes, stream.Length);
                buffer = new byte[length];

                int read = 0;
                while (read < length)
                {
                    int got = stream.Read(buffer, read, length - read);

                    if (got <= 0)
                    {
                        break;
                    }

                    read += got;
                }

                if (read < length)
                {
                    return null;
                }
            }

            int p = 0;

            if (Read_Int(buffer, ref p) != Magic)
            {
                return null;
            }

            if (Read_Byte(buffer, ref p) != 1)
            {
                return null;
            }

            Read_Byte(buffer, ref p); // struct type; info is always first

            ReplayInfo info = new ReplayInfo();

            Read_String(buffer, ref p);                            // version
            Read_String(buffer, ref p);                            // gameVersion
            string timestamp = Read_String(buffer, ref p);
            info.PlayerId = Read_String(buffer, ref p);
            Read_Name(buffer, ref p);                              // playerName
            Read_String(buffer, ref p);                            // platform
            Read_String(buffer, ref p);                            // trackingSystem
            Read_String(buffer, ref p);                            // hmd
            Read_String(buffer, ref p);                            // controller
            info.Hash = Read_String(buffer, ref p);
            info.SongName = Read_String(buffer, ref p);
            info.Mapper = Read_String(buffer, ref p);
            info.Difficulty = Read_String(buffer, ref p);
            Read_Int(buffer, ref p);                               // score
            info.Mode = Read_String(buffer, ref p);
            Read_String(buffer, ref p);                            // environment
            info.Modifiers = Read_String(buffer, ref p);
            info.JumpDistance = Read_Float(buffer, ref p);
            Read_Byte(buffer, ref p);                              // leftHanded
            Read_Float(buffer, ref p);                             // height
            Read_Float(buffer, ref p);                             // startTime
            Read_Float(buffer, ref p);                             // failTime
            info.Speed = Read_Float(buffer, ref p);

            long parsed;
            info.Timestamp = long.TryParse(timestamp, out parsed) ? parsed : 0L;

            return info;
        }


        private static int Read_Int(byte[] buffer, ref int p)
        {
            int v = BitConverter.ToInt32(buffer, p);
            p += 4;
            return v;
        }

        private static float Read_Float(byte[] buffer, ref int p)
        {
            float v = BitConverter.ToSingle(buffer, p);
            p += 4;
            return v;
        }

        private static byte Read_Byte(byte[] buffer, ref int p)
        {
            return buffer[p++];
        }


        // Mirrors BeatLeader's DecodeString, which resynchronises by one byte on an implausible
        // length rather than failing. Reading a shorter prefix than the writer wrote would
        // desynchronise everything after it, so this has to match.
        private static string Read_String(byte[] buffer, ref int p)
        {
            while (true)
            {
                int length = BitConverter.ToInt32(buffer, p);

                if (length > 300 || length < 0)
                {
                    p += 1;
                    continue;
                }

                string s = Encoding.UTF8.GetString(buffer, p + 4, length);
                p += length + 4;
                return s;
            }
        }


        // Mirrors DecodeName: a player name can contain bytes that break its own declared length,
        // so the decoder scans forward for the next plausible field header. Bounded here -- the
        // original is not, and this reads a fixed-size head rather than the whole file.
        private static string Read_Name(byte[] buffer, ref int p)
        {
            int length = BitConverter.ToInt32(buffer, p);
            int offset = 0;

            if (length > 0)
            {
                while (p + length + 4 + offset + 4 <= buffer.Length)
                {
                    int next = BitConverter.ToInt32(buffer, length + p + 4 + offset);

                    if (next == 5 || next == 6 || next == 8)
                    {
                        break;
                    }

                    offset++;
                }
            }

            string s = Encoding.UTF8.GetString(buffer, p + 4, length + offset);
            p += length + 4 + offset;
            return s;
        }
    }
}
