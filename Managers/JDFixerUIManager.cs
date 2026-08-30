using CustomCampaigns.Campaign.Missions;
using JDFixer.Interfaces;
using System;
using System.Collections.Generic;
using Zenject;


namespace JDFixer.Managers
{
    internal class JDFixerUIManager : IInitializable, IDisposable
    {
        private static StandardLevelDetailViewController levelDetail;
        private static MissionSelectionMapViewController missionSelection;
        private static BeatmapLevelsModel levelsModel;
        private static MainMenuViewController mainMenu;

        private readonly List<IBeatmapInfoUpdater> beatmapInfoUpdaters;

        // Optional on purpose. MainThreadDispatcher is the game's own marshaller, but whether it is
        // bound in the menu container is not something a build can prove. InjectOptional means a
        // missing binding leaves this null and costs a UI refresh, rather than failing the whole
        // installer and taking the mod down with it.
        private readonly MainThreadDispatcher mainThread;

        // The selection the UI is currently showing, so a value arriving late can be checked
        // against it.
        private BeatmapInfo lastInfo = BeatmapInfo.Empty;


        [Inject]
        private JDFixerUIManager(StandardLevelDetailViewController standardLevelDetailViewController, MissionSelectionMapViewController missionSelectionMapViewController, BeatmapLevelsModel beatmapLevelsModel, MainMenuViewController mainMenuViewController, List<IBeatmapInfoUpdater> iBeatmapInfoUpdaters, [InjectOptional] MainThreadDispatcher mainThreadDispatcher)
        {
            mainThread = mainThreadDispatcher;
            //Plugin.Log.Debug("JDFixerUIManager()");

            levelDetail = standardLevelDetailViewController;
            missionSelection = missionSelectionMapViewController;
            levelsModel = beatmapLevelsModel;
            mainMenu = mainMenuViewController;

            beatmapInfoUpdaters = iBeatmapInfoUpdaters;
        }


        public void Initialize()
        {
            //Plugin.Log.Debug("Initialize()");

            levelDetail.didChangeDifficultyBeatmapEvent += LevelDetail_didChangeDifficultyBeatmapEvent;
            levelDetail.didChangeContentEvent += LevelDetail_didChangeContentEvent;

            if (Plugin.CheckForCustomCampaigns())
            {
                missionSelection.didSelectMissionLevelEvent += MissionSelection_didSelectMissionLevelEvent_CC;
            }
            else
            {
                missionSelection.didSelectMissionLevelEvent += MissionSelection_didSelectMissionLevelEvent_Base;
            }

            mainMenu.didDeactivateEvent += MainMenu_didDeactivateEvent; ;

            ReplayDownloader.ValueArrived += Downloaded_Value_Arrived;
        }


        public void Dispose()
        {
            //Plugin.Log.Debug("Dispose()");

            levelDetail.didChangeDifficultyBeatmapEvent -= LevelDetail_didChangeDifficultyBeatmapEvent;
            levelDetail.didChangeContentEvent -= LevelDetail_didChangeContentEvent;

            missionSelection.didSelectMissionLevelEvent -= MissionSelection_didSelectMissionLevelEvent_CC;
            missionSelection.didSelectMissionLevelEvent -= MissionSelection_didSelectMissionLevelEvent_Base;

            mainMenu.didDeactivateEvent -= MainMenu_didDeactivateEvent;

            ReplayDownloader.ValueArrived -= Downloaded_Value_Arrived;
        }


        private void LevelDetail_didChangeDifficultyBeatmapEvent(StandardLevelDetailViewController arg1)
        {
            //Plugin.Log.Debug("LevelDetail_didChangeDifficultyBeatmapEvent()");

            if (arg1 != null)
            {
                DiffcultyBeatmapUpdated(arg1.beatmapKey, arg1.beatmapLevel);
            }
        }


        private void LevelDetail_didChangeContentEvent(StandardLevelDetailViewController arg1, StandardLevelDetailViewController.ContentType arg2)
        {
            //Plugin.Log.Debug("LevelDetail_didChangeContentEvent()");          
            
            if (arg1 != null && arg1.beatmapLevel != null)//selectedDifficultyBeatmap != null)
            {
                //Plugin.Log.Debug("NJS: " + arg1.selectedDifficultyBeatmap.noteJumpMovementSpeed);
                //Plugin.Log.Debug("Offset: " + arg1.selectedDifficultyBeatmap.noteJumpStartBeatOffset);

                DiffcultyBeatmapUpdated(arg1.beatmapKey, arg1.beatmapLevel); //selectedDifficultyBeatmap);
            }
        }


        private void MissionSelection_didSelectMissionLevelEvent_CC(MissionSelectionMapViewController arg1, MissionNode arg2)
        {
            // Yes, we must check for both arg2.missionData and arg2.missionData.beatmapCharacteristic:
            // If a map is not dled, missionID and beatmapDifficulty will be correct, but beatmapCharacteristic will be null
            // Accessing any null values of arg1 or arg2 will crash CC horribly

            if (arg2.missionData != null && arg2.missionData.beatmapCharacteristic != null)
            {
                Plugin.Log.Debug("In CC, MissionNode exists");

                //Plugin.Log.Debug("MissionNode - missionid: " + arg2.missionId); //"<color=#0a92ea>[STND]</color> Holdin' Oneb28Easy-1"
                //Plugin.Log.Debug("MissionNode - difficulty: " + arg2.missionData.beatmapDifficulty); // "Easy" etc
                //Plugin.Log.Debug("MissionNode - characteristic: " + arg2.missionData.beatmapCharacteristic.serializedName); //"Standard" etc

                if (MissionSelectionPatch.cc_level != null) // lol null check just to print?
                {
                    // If a map is not dled, this will be the previous selected node's map
                    Plugin.Log.Debug("CC Level: " + MissionSelectionPatch.cc_level.levelID);  // For cross check with arg2.missionId

                    if (arg2.missionData is CustomMissionDataSO)
                    {
                        BeatmapLevel beatmapLevel = (arg2.missionData as CustomMissionDataSO).beatmapLevel;

                        if (beatmapLevel != null) // lol null check just to print?
                        {
                            DiffcultyBeatmapUpdated(arg2.missionData.beatmapKey, beatmapLevel);
                        }
                    }
                }
            }
            else // Map not dled
            {
                DiffcultyBeatmapUpdated(new BeatmapKey(), null);
            }
        }


        private void MissionSelection_didSelectMissionLevelEvent_Base(MissionSelectionMapViewController arg1, MissionNode arg2)
        {
            // Base campaign
            if (arg2 != null)
            {
                DiffcultyBeatmapUpdated(arg2.missionData.beatmapKey, levelsModel.GetBeatmapLevel(arg2.missionData.beatmapKey.levelId));
            }
        }


        // A download lands on a background thread, seconds after the map was selected. If the same
        // beatmap is still on screen this MUST write the value into the config: the play-time patch
        // reads the slider value, not the store, so a beatmap that now counts as remembered -- and
        // so bypasses Automated Preferences -- would otherwise be played at whatever the previous
        // map left behind.
        private void Downloaded_Value_Arrived(BeatmapKey key, float jumpDistance)
        {
            BeatmapInfo info = lastInfo;

            if (info == null || MapMemory.Key_For(info.Key) != MapMemory.Key_For(key))
            {
                return;
            }

            MapMemory.Restore(info);

            // Only the on-screen slider is left, and touching UI needs the main thread. Without the
            // dispatcher the value is still correct and the slider catches up on the next selection.
            if (mainThread == null)
            {
                return;
            }

            mainThread.DispatchOnMainThread(() =>
            {
                if (UI.ModifierUI.Instance != null)
                {
                    UI.ModifierUI.Instance.BeatmapInfoUpdated(info);
                }

                if (UI.LegacyModifierUI.Instance != null)
                {
                    UI.LegacyModifierUI.Instance.BeatmapInfoUpdated(info);
                }
            });
        }


        private void MainMenu_didDeactivateEvent(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            //Plugin.Log.Debug("MainMenu_didDeactivate");

            if (UI.LegacyModifierUI.Instance != null)
            {
                UI.LegacyModifierUI.Instance.Refresh();
            }

            if (UI.ModifierUI.Instance != null)
            {
                UI.ModifierUI.Instance.Refresh();
            }

            if (UI.CustomOnlineUI.Instance != null)
            {
                UI.CustomOnlineUI.Instance.Refresh();
            }
        }


        private void DiffcultyBeatmapUpdated(BeatmapKey beatmapKey, BeatmapLevel beatmapLevel)
        {
            //Plugin.Log.Debug("DiffcultyBeatmapUpdated()");

            BeatmapInfo info = new BeatmapInfo(beatmapKey, beatmapLevel);
            lastInfo = info;

            // Before the UI is told: the sliders bind to PluginConfig, so a remembered value has to
            // be in there by the time BeatmapInfoUpdated fires.
            MapMemory.Restore(info);

            // Debounced inside, and a no-op unless the download fallback is switched on. Nothing
            // leaves the machine until the selection has been still for over a second, so scrolling
            // a list costs nothing.
            ReplayDownloader.Note_Selection(beatmapKey, info.SongIdentity);

            foreach (var beatmapInfoUpdater in beatmapInfoUpdaters)
            {
                beatmapInfoUpdater.BeatmapInfoUpdated(info);
            }
        }
    }
}