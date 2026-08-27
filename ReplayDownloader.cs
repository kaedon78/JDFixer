using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace JDFixer
{
    // The last resort for a beatmap with no local replay and no play of our own: ask BeatLeader
    // what jump distance the player's score on that map was set at.
    //
    // OPT-IN AND DEFAULT OFF, because unlike everything else here it talks to a third party. When
    // it is on, selecting a map the mod has no value for sends that map's hash and the player's
    // BeatLeader id to api.beatleader.xyz.
    //
    // Three rules keep that from becoming a stream of traffic while someone browses a 14,000-map
    // library:
    //   * nothing is sent until the selection has been still for Settle_Ms, so scrolling past a
    //     map costs nothing;
    //   * one request at a time, and never for a beatmap already being asked about;
    //   * every definitive answer is cached permanently, INCLUDING "no score there", so a given
    //     beatmap is asked about exactly once, ever.
    //
    // The value this returns is the jump distance of the score BeatLeader holds, which is the
    // player's best rather than literally their last play. A local replay is the last play, so it
    // always wins where one exists.
    internal static class ReplayDownloader
    {
        private const string Api_Url = "https://api.beatleader.xyz";

        // Both endpoints are taken from BeatLeader's own source rather than guessed, so the shapes
        // match what their mod asks for:
        //   ScoresRequest.cs     /v3/scores/{hash}/{diff}/{mode}/{context}/{scope}/page?player=...
        //   ScoreStatsRequest.cs /score/statistic/{scoreId}
        // Context is ScoresContexts.General.Key and scope is ScoresScope.Global lowercased, which
        // is what LeaderboardManager sends.
        private const string Context = "modifiers";
        private const string Scope = "global";

        // Long enough that scrolling a list never fires a request.
        private const int Settle_Ms = 1500;

        private const int Timeout_Seconds = 15;

        // A transient failure -- a timeout, a 5xx, no network -- is NOT cached as "nothing there".
        // It is held off for this long in memory only, so a flaky minute does not permanently
        // blank a map.
        private const int Retry_After_Minutes = 10;

        private const float JD_Min = 1f;
        private const float JD_Max = 60f;

        private static readonly object _lock = new object();
        private static readonly HashSet<string> _in_flight = new HashSet<string>();
        private static readonly Dictionary<string, DateTime> _failed_until = new Dictionary<string, DateTime>();

        private static HttpClient _http;
        private static Timer _settle_timer;
        private static BeatmapKey _pending = default(BeatmapKey);
        private static string _pending_identity;

        // Set when a download lands for the beatmap still on screen, so the UI can be nudged from
        // the main thread rather than from the completion callback.
        internal static event Action<BeatmapKey, float> ValueArrived;


        internal static void Shutdown()
        {
            lock (_lock)
            {
                if (_settle_timer != null)
                {
                    _settle_timer.Dispose();
                    _settle_timer = null;
                }

                if (_http != null)
                {
                    _http.Dispose();
                    _http = null;
                }
            }
        }


        // Called on every selection change. Cheap by design: it only restarts a timer.
        internal static void Note_Selection(BeatmapKey key, string identity)
        {
            if (!PluginConfig.Instance.remember_per_map || !PluginConfig.Instance.download_replay_values)
            {
                return;
            }

            lock (_lock)
            {
                _pending = key;
                _pending_identity = identity;

                if (_settle_timer == null)
                {
                    _settle_timer = new Timer(On_Settled, null, Settle_Ms, System.Threading.Timeout.Infinite);
                }
                else
                {
                    // Restart. Browsing past twenty maps queues one request, not twenty.
                    _settle_timer.Change(Settle_Ms, System.Threading.Timeout.Infinite);
                }
            }
        }


        private static void On_Settled(object _)
        {
            BeatmapKey key;
            string identity;

            lock (_lock)
            {
                key = _pending;
                identity = _pending_identity;
            }

            try
            {
                Consider(key, identity);
            }
            catch (Exception e)
            {
                Plugin.Log.Warn("Replay download failed to start: " + e.Message);
            }
        }


        private static void Consider(BeatmapKey key, string identity)
        {
            if (!PluginConfig.Instance.remember_per_map || !PluginConfig.Instance.download_replay_values)
            {
                return;
            }

            string id = MapMemory.Key_For(key);

            if (id == null)
            {
                return;
            }

            // Already have a value from a local replay or from a play of our own.
            if (MapMemory.Has(key))
            {
                return;
            }

            // Already asked, whatever the answer was.
            if (MapMemory.Was_Fetched(key))
            {
                return;
            }

            // Forgotten on purpose. A tombstone reads as "no value" to Has(), and a map seeded
            // from a local replay has never been fetched, so without this the one thing that
            // follows pressing Forget is a request to a third party about that very map. The
            // value could not have come back -- Record_Fetch will not write over a tombstone --
            // so this saves nothing but the request itself, which is the point.
            if (MapMemory.Is_Forgotten(key))
            {
                return;
            }

            string player = ReplayIndex.Player_Id;

            if (string.IsNullOrEmpty(player))
            {
                // No local replay has been read, so there is no id to ask about. Not an error --
                // this is simply not available for a player with no BeatLeader history here.
                return;
            }

            lock (_lock)
            {
                if (_in_flight.Contains(id))
                {
                    return;
                }

                DateTime until;
                if (_failed_until.TryGetValue(id, out until) && DateTime.UtcNow < until)
                {
                    return;
                }

                _in_flight.Add(id);
            }

            ThreadPool.QueueUserWorkItem(_ => Fetch(key, id, player, identity));
        }


        private static HttpClient Client()
        {
            lock (_lock)
            {
                if (_http == null)
                {
                    _http = new HttpClient();
                    _http.Timeout = TimeSpan.FromSeconds(Timeout_Seconds);

                    // Identify honestly. This is a mod asking on a player's behalf, and a server
                    // operator should be able to see that in their logs.
                    _http.DefaultRequestHeaders.Add("User-Agent", "JDFixer-MapMemory (Beat Saber mod)");
                    _http.DefaultRequestHeaders.Add("Accept", "application/json");
                }

                return _http;
            }
        }


        private static void Fetch(BeatmapKey key, string id, string player, string identity)
        {
            try
            {
                // MapEnhancer and NetworkingUtils build these three the same way, which is why the
                // key this mod stores and the one BeatLeader queries agree without translation.
                string hash = key.levelId.Replace("custom_level_", "");
                string difficulty = key.difficulty.ToString();
                string mode = key.characteristic.SerializedName();

                string url = Api_Url + "/v3/scores/" + Uri.EscapeDataString(hash) + "/" +
                             Uri.EscapeDataString(difficulty) + "/" + Uri.EscapeDataString(mode) + "/" +
                             Context + "/" + Scope + "/page?player=" + Uri.EscapeDataString(player) +
                             "&page=1&count=10";

                string body = Get(url);

                if (body == null)
                {
                    Hold_Off(id);
                    return;
                }

                JObject page = JObject.Parse(body);
                JToken scores = page["data"];

                int score_id = 0;
                string modifiers = "";

                if (scores != null)
                {
                    foreach (JToken score in scores)
                    {
                        JToken owner = score["player"];
                        string owner_id = owner == null ? null : (string)owner["id"];

                        if (owner_id == player)
                        {
                            score_id = (int?)score["id"] ?? 0;
                            modifiers = (string)score["modifiers"] ?? "";
                            break;
                        }
                    }
                }

                if (score_id == 0)
                {
                    // A definitive "no score on this map". Cached so it is never asked again.
                    MapMemory.Record_No_Score(key);
                    Plugin.Log.Debug("ReplayDownloader: no score for " + id);
                    return;
                }

                string stats_body = Get(Api_Url + "/score/statistic/" + score_id);

                if (stats_body == null)
                {
                    Hold_Off(id);
                    return;
                }

                JObject stats = JObject.Parse(stats_body);
                JToken tracker = stats["winTracker"];
                float jd = tracker == null ? 0f : (float?)tracker["jumpDistance"] ?? 0f;

                // The score object carries the modifiers this run used, and a speed modifier is a
                // known constant factor on the recorded jump distance, so it is divided back out.
                // This is the reason the modifiers are read off the score at all.
                float raw = jd;
                jd = SongSpeed.Remove(modifiers, jd);

                if (jd != raw)
                {
                    Plugin.Log.Debug("ReplayDownloader: " + id + " ran with " + modifiers + ", " +
                                     raw.ToString("0.##") + " -> " + jd.ToString("0.##"));
                }

                // Checked after the correction, not before.
                if (jd < JD_Min || jd > JD_Max || float.IsNaN(jd))
                {
                    MapMemory.Record_No_Score(key);
                    Plugin.Log.Debug("ReplayDownloader: implausible JD " + jd + " for " + id);
                    return;
                }

                MapMemory.Record_Downloaded(key, jd, identity);
                Plugin.Log.Debug("ReplayDownloader: " + jd.ToString("0.##") + " for " + id);

                Action<BeatmapKey, float> arrived = ValueArrived;
                if (arrived != null)
                {
                    arrived(key, jd);
                }
            }
            catch (Exception e)
            {
                // Never a cached negative: an exception here says nothing about whether a score
                // exists.
                Plugin.Log.Warn("ReplayDownloader: " + id + ": " + e.Message);
                Hold_Off(id);
            }
            finally
            {
                lock (_lock)
                {
                    _in_flight.Remove(id);
                }
            }
        }


        private static string Get(string url)
        {
            HttpResponseMessage response = Client().GetAsync(url).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Debug("ReplayDownloader: HTTP " + (int)response.StatusCode + " for " + url);
                return null;
            }

            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }


        private static void Hold_Off(string id)
        {
            lock (_lock)
            {
                _failed_until[id] = DateTime.UtcNow.AddMinutes(Retry_After_Minutes);
            }
        }
    }
}
