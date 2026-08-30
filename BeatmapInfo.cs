namespace JDFixer
{
    public class BeatmapInfo
    {
        internal delegate void BeatmapInfoEventHandler(BeatmapInfo e);
        internal static event BeatmapInfoEventHandler SelectedChanged;

        internal static void SetSelected(BeatmapKey key, BeatmapLevel level)
        {
            var updatedMapInfo = level == null ? Empty : new BeatmapInfo(key, level);

            Selected = updatedMapInfo;
            SelectedChanged?.Invoke(updatedMapInfo);
        }

        public static BeatmapInfo Selected { get; private set; } = Empty;

        internal static BeatmapInfo Empty { get; } = new BeatmapInfo();

        private BeatmapInfo()
        {
            // To enable Campaigns and TA to show 0 instead of values from the last selected map in Solo Mode,
            // Better UX as players may forget to ignore the display.
            JumpDistance = 0f;
            MinJumpDistance = 0f;
            ReactionTime = 0f;
            MinReactionTime = 0f;

            // Ultra hack way to prevent divide by zero in Reaction Time Display
            NJS = 0.001f;

            // Experimental
            MinRTSlider = 0f;
            MaxRTSlider = 3000f;

            MinJDSlider = 0f;
            MaxJDSlider = 50f;

            // 1.26.0-1.29.0 Feature update
            JDOffsetQuantum = 0.1f;
            RTOffsetQuantum = 5f;
        }

        internal BeatmapInfo(BeatmapKey key, BeatmapLevel level)
        {
            Key = key;

            if (level != null)
            {
                // BeatLeader builds the replay's mapper field the same way, so the two agree.
                SongIdentity = MapMemory.Identity_Of(level.songName, string.Join(",", level.allMappers));
            }

            if (level == null)
            {
                return;
            }

            float bpm = level.beatsPerMinute;

            BeatmapBasicData beatmapBasicData;
            float njs = 10f;
            float offset = 0f;
            if (level.beatmapBasicData.TryGetValue((key.beatmapCharacteristic, key.difficulty), out beatmapBasicData))
            {
                njs = beatmapBasicData.noteJumpMovementSpeed;
                offset = beatmapBasicData.noteJumpStartBeatOffset;
            }

            if (njs <= 0.01f)
                njs = 10f;

            JumpDistance = BeatmapUtils.CalculateJumpDistance(bpm, njs, offset);
            MinJumpDistance = BeatmapUtils.CalculateJumpDistance(bpm, njs, -50f);
            NJS = njs;
            ReactionTime = JumpDistance * 500 / NJS;
            MinReactionTime = MinJumpDistance * 500 / NJS;

            // Experimental
            if (PluginConfig.Instance.slider_setting == 0)
            {
                MinRTSlider = PluginConfig.Instance.minJumpDistance * 500 / NJS;
                MaxRTSlider = PluginConfig.Instance.maxJumpDistance * 500 / NJS;

                MinJDSlider = PluginConfig.Instance.minJumpDistance;
                MaxJDSlider = PluginConfig.Instance.maxJumpDistance;
            }
            else
            {
                MinRTSlider = PluginConfig.Instance.minReactionTime;
                MaxRTSlider = PluginConfig.Instance.maxReactionTime;

                MinJDSlider = PluginConfig.Instance.minReactionTime * NJS / 500;
                MaxJDSlider = PluginConfig.Instance.maxReactionTime * NJS / 500;
            }

            // 1.26.0-1.29.0 Feature update
            Offset = offset;
            JDOffsetQuantum = BeatmapUtils.CalculateJumpDistance(bpm, njs, offset + 1 / PluginConfig.Instance.offset_fraction) - BeatmapUtils.CalculateJumpDistance(bpm, njs, offset);
            RTOffsetQuantum = JDOffsetQuantum * 500 / NJS;

            //Plugin.Log.Debug("BeatmapInfo minJD: " + PluginConfig.Instance.minJumpDistance);
            //Plugin.Log.Debug("BeatmapInfo maxJD: " + PluginConfig.Instance.maxJumpDistance);
            //Plugin.Log.Debug("BeatmapInfo minRT: " + MinRTSlider);
            //Plugin.Log.Debug("BeatmapInfo maxRT: " + MaxRTSlider);
        }

        // 1.29.1
        internal static float speedMultiplier = 1f;

        // Which beatmap this is, for the per-map memory. The empty instance leaves it default,
        // which MapMemory.Key_For rejects.
        internal BeatmapKey Key { get; }

        // Song name and mapper, lowercased. Two hashes sharing this are the same map uploaded
        // more than once -- measured at 441 such groups, 513 maps, in a 14,651 map library.
        internal string SongIdentity { get; }

        public float JumpDistance { get; }
        public float MinJumpDistance { get; }
        public float NJS { get; }
        public float ReactionTime { get; }
        public float MinReactionTime { get; }

        // Experimental
        internal float MinRTSlider { get; }
        internal float MaxRTSlider { get; }

        internal float MinJDSlider { get; }
        internal float MaxJDSlider { get; }

        // 1.26.0-1.29.0 Feature update
        public float Offset { get; }
        internal float JDOffsetQuantum { get; }
        internal float RTOffsetQuantum { get; }
    }
}