using System;

namespace JDFixer
{
    // Undoing a speed modifier on a recorded jump distance.
    //
    // JDFixer's own SpawnMovementDataUpdateHelper.Get_Modified_DesiredJD converts the setpoint
    // through reaction time and back:
    //     newRT = RT(jd) * songSpeedMul   then   jd' = JD(newRT)
    // which, since RT(jd) = jd/(2*njs)*1000 and JD(rt) = rt*(2*njs)/1000, is exactly
    //     jd' = jd * songSpeedMul.
    // So a recorded value carries the multiplier as a plain factor and dividing recovers the
    // setpoint.
    //
    // Measured against this install's own replays, which run RT setpoint 455 ms with
    // song_speed_setting 1 (always compensate). 61 unmodified plays read a median RT of 460 ms,
    // spread 403-511 by the 1/16-beat snapping. The speed-modified ones read:
    //     SS  Caelestiveritas   RT 387.7   /455 = 0.852
    //     SS  ENUMA ELIS        RT 395.0   /455 = 0.868
    //     SS  Denkoh-Sekka      RT 413.2   /455 = 0.908
    //     FS  Boom, Boom...     RT 532.4   /455 = 1.170
    // Dividing each by its multiplier puts all four back inside the unmodified band, three of
    // them within about 10 ms of the setpoint.
    //
    // The multipliers themselves are 0.85 / 1.20 / 1.50, confirmed against the game's published
    // values for all three including SuperFast. No local replay used SuperFast, so that constant
    // is confirmed while the path it takes through this class is the same one FS and SS exercise.
    internal static class SongSpeed
    {
        // BeatLeader's MapEnhancer.modifiers() emits exactly these three for the speed modifiers.
        private const string Slower = "SS";
        private const string Faster = "FS";
        private const string Super_Fast = "SF";

        private static bool _resolved = false;
        private static float _slower = 0.85f;
        private static float _faster = 1.2f;
        private static float _super_fast = 1.5f;


        // Asked of the game rather than hardcoded. The fallbacks above are the published values
        // and are what gets used if this fails, but a version that changed them would silently
        // corrupt every corrected value, so the real numbers are read from GameplayModifiers
        // itself and the source is logged.
        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;

            try
            {
                _slower = Multiplier_For(GameplayModifiers.SongSpeed.Slower);
                _faster = Multiplier_For(GameplayModifiers.SongSpeed.Faster);
                _super_fast = Multiplier_For(GameplayModifiers.SongSpeed.SuperFast);

                Plugin.Log.Debug("SongSpeed: read from the game -- SS " + _slower + ", FS " + _faster +
                                 ", SF " + _super_fast);
            }
            catch (Exception e)
            {
                Plugin.Log.Warn("SongSpeed: could not read the multipliers from GameplayModifiers (" +
                                e.Message + "); falling back to SS 0.85, FS 1.2, SF 1.5");
            }
        }


        // GameplayModifiers.noModifiers is a public static readonly instance and CopyWith takes
        // an optional, named songSpeed, so the whole question is one ordinary compile-checked call.
        // An earlier version built the instance through FormatterServices and set the private
        // _songSpeed by reflection; this depends on nothing private, nothing positional, and a
        // rename would fail the build instead of silently falling back to the constants below.
        private static float Multiplier_For(GameplayModifiers.SongSpeed speed)
        {
            float mul = GameplayModifiers.noModifiers.CopyWith(songSpeed: speed).songSpeedMul;

            if (mul <= 0f || float.IsNaN(mul))
            {
                throw new Exception("implausible multiplier " + mul);
            }

            return mul;
        }


        // 1 when there is no speed modifier in the list.
        internal static float Multiplier(string modifiers)
        {
            if (string.IsNullOrEmpty(modifiers))
            {
                return 1f;
            }

            foreach (string raw in modifiers.Split(','))
            {
                string part = raw.Trim();

                if (part == Slower || part == Faster || part == Super_Fast)
                {
                    Resolve();

                    if (part == Slower)
                    {
                        return _slower;
                    }

                    return part == Faster ? _faster : _super_fast;
                }
            }

            return 1f;
        }


        // Whether JDFixer would have folded the song speed into the setpoint at all. This mirrors
        // Get_Modified_DesiredJD exactly, and it has to: with song_speed_setting 0 the recorded
        // value never carried the multiplier, so dividing would move a correct value.
        //
        // The honest limit of this: it reads the config as it is NOW, and the play being corrected
        // happened under whatever the config was THEN -- possibly with no JDFixer at all. There is
        // nothing in a replay or a score that records the setting, so the current config is the
        // best evidence available. It is right for anyone who has not changed the setting, which is
        // the ordinary case.
        internal static bool Folded_Into_Setpoint()
        {
            if (PluginConfig.Instance.song_speed_setting == 1)
            {
                return true;
            }

            if (PluginConfig.Instance.song_speed_setting == 2 &&
                (PluginConfig.Instance.use_rt_pref ||
                 (PluginConfig.Instance.slider_setting == 1 && PluginConfig.Instance.use_jd_pref == false)))
            {
                return true;
            }

            return false;
        }


        // Recovers the speed-independent setpoint from a recorded jump distance.
        internal static float Remove(string modifiers, float jumpDistance)
        {
            float mul = Multiplier(modifiers);

            if (mul == 1f || !Folded_Into_Setpoint())
            {
                return jumpDistance;
            }

            return jumpDistance / mul;
        }
    }
}
