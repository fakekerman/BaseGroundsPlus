using System;
using System.Collections.Generic;
using UnityEngine;

namespace BaseGroundsPlus
{
    /// <summary>
    /// Keeps right-click part menus working when another mod shows a setting as an on/off switch (UI_Toggle) but the
    /// setting is not actually true/false. KSP then throws InvalidCastException while building the menu and the menu
    /// never opens. We find those fields, log exactly which part/module/field it is, and show them as a read-only label
    /// instead (or hide them if they can't even be read). Nothing else about the part changes.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndEditor, false)]
    public class PartMenuGuard : MonoBehaviour
    {
        private static readonly HashSet<string> reported = new HashSet<string>();

        private void Start()
        {
            Settings.EnsureLoaded();
            GameEvents.onPartActionUICreate.Add(OnPartMenu);
            GameEvents.onEditorPartPlaced.Add(OnPartMenu);
            GameEvents.onEditorShipModified.Add(OnShipModified);
            GameEvents.onVesselLoaded.Add(OnVessel);
            GameEvents.onVesselWasModified.Add(OnVessel);
        }

        private void OnDestroy()
        {
            GameEvents.onPartActionUICreate.Remove(OnPartMenu);
            GameEvents.onEditorPartPlaced.Remove(OnPartMenu);
            GameEvents.onEditorShipModified.Remove(OnShipModified);
            GameEvents.onVesselLoaded.Remove(OnVessel);
            GameEvents.onVesselWasModified.Remove(OnVessel);
        }

        private void OnPartMenu(Part p)
        {
            if (Settings.FixPartMenus) CheckPart(p);
        }

        private void OnShipModified(ShipConstruct ship)
        {
            if (!Settings.FixPartMenus || ship == null || ship.parts == null) return;
            for (int i = 0; i < ship.parts.Count; i++) CheckPart(ship.parts[i]);
        }

        private void OnVessel(Vessel v)
        {
            if (!Settings.FixPartMenus || v == null || !v.loaded || v.parts == null) return;
            for (int i = 0; i < v.parts.Count; i++) CheckPart(v.parts[i]);
        }

        public static void CheckPart(Part p)
        {
            if (p == null) return;
            try
            {
                if (p.Fields != null) CheckFields(p, null, p.Fields);
                if (p.Modules == null) return;
                for (int i = 0; i < p.Modules.Count; i++)
                {
                    PartModule m = p.Modules[i];
                    if (m != null && m.Fields != null) CheckFields(p, m, m.Fields);
                }
            }
            catch (Exception e)
            {
                Log.Dbg("Part menu check failed on " + p.name + ": " + e.Message);
            }
        }

        private static void CheckFields(Part p, PartModule m, BaseFieldList fields)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                BaseField f = fields[i];
                if (f == null) continue;
                bool inEditor = f.uiControlEditor is UI_Toggle;
                bool inFlight = f.uiControlFlight is UI_Toggle;
                if (!inEditor && !inFlight) continue;

                string problem = null;
                bool readable = true;
                try
                {
                    object v = f.GetValue(f.host);
                    if (!(v is bool)) problem = "holds " + (v == null ? "nothing" : "a " + v.GetType().Name) + " instead of true/false";
                }
                catch (Exception e)
                {
                    problem = "can't be read (" + e.GetType().Name + ")";
                    readable = false;
                }
                if (problem == null) continue;

                if (readable)
                {
                    // still show the value, just not as a switch
                    UI_Label label = new UI_Label();
                    label.scene = UI_Scene.All;
                    if (inEditor) f.uiControlEditor = label;
                    if (inFlight) f.uiControlFlight = label;
                }
                else
                {
                    if (inEditor) f.guiActiveEditor = false;
                    if (inFlight) { f.guiActive = false; f.guiActiveUnfocused = false; }
                }

                string partName = p.partInfo != null ? p.partInfo.name : p.name;
                string moduleName = m != null ? m.moduleName : "Part";
                string key = partName + "/" + moduleName + "/" + f.name;
                if (reported.Add(key))
                {
                    string title = p.partInfo != null ? p.partInfo.title : partName;
                    Log.Warn("Right-click menu fix: part '" + partName + "' (" + title + "), module " + moduleName + ", field '" + f.name +
                        "' is shown as an on/off switch but " + problem + ". KSP would throw InvalidCastException and the menu would not open; " +
                        (readable ? "showing it as a read-only label instead." : "hiding it."));
                    if (Settings.ShowMessages)
                        ScreenMessages.PostScreenMessage("BaseGrounds+: fixed a broken right-click menu on " + title + " (" + moduleName + ")", 6f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }
    }
}
