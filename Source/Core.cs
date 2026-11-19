using System;
using UnityEngine;

namespace BaseGroundsPlus
{
    /// <summary>Loads settings once and keeps vessel records in step with docking, undocking, recovery and scene changes.</summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class BaseGroundsCore : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(this);
            Settings.Load();
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onPartCoupleComplete.Add(OnCoupleComplete);
            GameEvents.onPartUndockComplete.Add(OnUndockComplete);
            GameEvents.onPartDeCoupleNewVesselComplete.Add(OnDecoupleNewVessel);
            GameEvents.onVesselRecovered.Add(OnRecovered);
            GameEvents.onVesselTerminated.Add(OnTerminated);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
            Log.Info("Started");
        }

        private void OnLevelReady(GameScenes scene)
        {
            // a game that never had our scenario node: start with an empty record list instead of the previous game's
            if (!BaseRegistry.Ready && (scene == GameScenes.FLIGHT || scene == GameScenes.SPACECENTER || scene == GameScenes.TRACKSTATION))
                BaseRegistry.Load(null);
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onPartCoupleComplete.Remove(OnCoupleComplete);
            GameEvents.onPartUndockComplete.Remove(OnUndockComplete);
            GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnDecoupleNewVessel);
            GameEvents.onVesselRecovered.Remove(OnRecovered);
            GameEvents.onVesselTerminated.Remove(OnTerminated);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            // the next scene's scenario module loads the records again
            BaseRegistry.Ready = false;
            GroundGuard.ClearPinOwners();
        }

        private void OnCoupleComplete(GameEvents.FromToAction<Part, Part> data)
        {
            try { if (data.to != null) BaseRegistry.OnDocked(data.to.vessel); }
            catch (Exception e) { Log.Warn("OnCoupleComplete: " + e.Message); }
        }

        private void OnUndockComplete(Part p)
        {
            BaseRegistry.Changed();
        }

        private void OnDecoupleNewVessel(Vessel original, Vessel created)
        {
            BaseRegistry.Changed();
        }

        private void OnRecovered(ProtoVessel pv, bool quick)
        {
            if (pv != null) BaseRegistry.Remove(pv.persistentId);
        }

        private void OnTerminated(ProtoVessel pv)
        {
            if (pv != null) BaseRegistry.Remove(pv.persistentId);
        }
    }
}
