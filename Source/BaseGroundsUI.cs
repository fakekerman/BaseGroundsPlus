using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace BaseGroundsPlus
{
    /// <summary>
    /// The BaseGrounds+ window: a uGUI window (KSP's own dialog system, same look as the stock windows) opened from
    /// the toolbar button on the right. Works in flight, the Space Center and the Tracking Station.
    ///
    /// Performance note: every changing text goes through Cached(), which rebuilds the string at most a few times per
    /// second and otherwise returns the very same string. TextMeshPro then skips the update, so the window does not
    /// re-layout every frame (which is what made the text lag).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class BaseGroundsUI : MonoBehaviour
    {
        private const string DialogName = "BaseGroundsPlusWindow";
        private const float Width = 440f;
        private const float ListHeight = 460f;
        private const float RowWidth = 390f;
        private const int NearbySlots = 6;
        private const int BaseSlots = 20;

        private const string Small = "<size=12><color=#a8a8a8>";
        private const string SmallEnd = "</color></size>";

        private static int page;              // 0 = this vessel (flight only), 1 = my bases, 2 = settings
        private static bool showAllLanded;

        private ApplicationLauncherButton button;
        private PopupDialog popup;
        private int positionFrames;
        private bool settingsDirty;
        private float settingsSaveAt;
        private float nextRefresh;

        private readonly Target activeTarget = new Target();
        private readonly Target[] nearby = new Target[NearbySlots];
        private readonly Target[] bases = new Target[BaseSlots];
        private readonly List<Target> allListed = new List<Target>();
        private int nearbyCount, basesCount;

        /// <summary>One vessel shown in the window: a live Vessel in flight, or a saved ProtoVessel elsewhere.</summary>
        private class Target
        {
            public Vessel V;
            public ProtoVessel PV;
            private BaseRecord rec;
            private int recVersion = -1;
            private float recTime;
            private uint recFor;

            public bool Valid { get { return V != null || PV != null; } }
            public bool Loaded { get { return V != null && V.loaded; } }
            public uint Id { get { return V != null ? V.persistentId : (PV != null ? PV.persistentId : 0u); } }
            public string Name { get { return V != null ? V.vesselName : (PV != null ? PV.vesselName : ""); } }
            public VesselType Type { get { return V != null ? V.vesselType : (PV != null ? PV.vesselType : VesselType.Unknown); } }
            public Vessel.Situations Situation { get { return V != null ? V.situation : (PV != null ? PV.situation : Vessel.Situations.LANDED); } }
            public bool IsEva { get { return V != null ? V.isEVA : Type == VesselType.EVA; } }
            public bool Manageable { get { return Valid && Type != VesselType.Flag && Type != VesselType.SpaceObject; } }

            public string Body
            {
                get
                {
                    if (V != null) return BodyLabel(V.mainBody);
                    if (PV != null && PV.orbitSnapShot != null && FlightGlobals.Bodies != null)
                    {
                        int i = PV.orbitSnapShot.ReferenceBodyIndex;
                        if (i >= 0 && i < FlightGlobals.Bodies.Count) return BodyLabel(FlightGlobals.Bodies[i]);
                    }
                    return "?";
                }
            }

            public GroundGuard Guard { get { return Loaded ? V.FindVesselModuleImplementing<GroundGuard>() : null; } }

            public BaseRecord Record
            {
                get
                {
                    uint id = Id;
                    if (recVersion != BaseRegistry.Version || recFor != id || Time.unscaledTime > recTime)
                    {
                        rec = V != null ? BaseRegistry.Resolve(V) : (PV != null ? BaseRegistry.Find(PV.persistentId) : null);
                        recVersion = BaseRegistry.Version;
                        recFor = id;
                        recTime = Time.unscaledTime + 1f;
                    }
                    return rec;
                }
            }

            public BaseRecord GetOrCreate()
            {
                rec = V != null ? BaseRegistry.GetOrCreate(V) : BaseRegistry.GetOrCreate(PV);
                return rec;
            }

            public void Set(Vessel v, ProtoVessel pv)
            {
                V = v;
                PV = pv;
            }
        }

        // ------------------------------------------------------------------------------------------------ lifecycle

        private void Start()
        {
            GameScenes s = HighLogic.LoadedScene;
            if (s != GameScenes.FLIGHT && s != GameScenes.SPACECENTER && s != GameScenes.TRACKSTATION)
            {
                Destroy(this);
                return;
            }
            Settings.EnsureLoaded();
            for (int i = 0; i < NearbySlots; i++) nearby[i] = new Target();
            for (int i = 0; i < BaseSlots; i++) bases[i] = new Target();
            if (!HighLogic.LoadedSceneIsFlight && page == 0) page = 1;
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(OnLauncherUnreadifying);
            if (ApplicationLauncher.Ready) AddButton();
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Remove(OnLauncherUnreadifying);
            CloseWindow(false);
            RemoveButton();
            if (settingsDirty) Settings.Save();
        }

        private void OnLauncherUnreadifying(GameScenes scene)
        {
            CloseWindow(false);
            RemoveButton();
        }

        private void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            button = ApplicationLauncher.Instance.AddModApplication(
                OpenWindow, () => CloseWindow(false), null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW |
                ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.TRACKSTATION,
                LoadIcon());
        }

        private void RemoveButton()
        {
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        private static Texture2D LoadIcon()
        {
            Texture2D t = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture("BaseGroundsPlus/Icons/toolbar", false) : null;
            if (t != null) return t;
            t = new Texture2D(38, 38, TextureFormat.ARGB32, false);
            Color clear = new Color(0, 0, 0, 0), c = new Color(0.55f, 0.9f, 0.55f, 1f);
            for (int x = 0; x < 38; x++)
                for (int y = 0; y < 38; y++)
                {
                    bool ground = y >= 8 && y <= 11 && x >= 4 && x <= 33;
                    bool stake = x >= 17 && x <= 20 && y >= 8 && y <= 31;
                    bool bar = y >= 26 && y <= 28 && x >= 11 && x <= 26;
                    t.SetPixel(x, y, ground || stake || bar ? c : clear);
                }
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------------------------------------ window

        private void OpenWindow()
        {
            if (popup != null) return;
            if (!HighLogic.LoadedSceneIsFlight && page == 0) page = 1;
            activeTarget.Set(HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null, null);
            RefreshLists();
            MultiOptionDialog dlg = new MultiOptionDialog(DialogName, "", "BaseGrounds+", HighLogic.UISkin,
                new Rect(0.5f, 0.5f, Width, 120f), BuildContent());
            popup = PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dlg, false, HighLogic.UISkin, false, "");
            if (popup != null)
            {
                popup.OnDismiss = OnPopupDismissed;
                positionFrames = 0;
            }
        }

        private void CloseWindow(bool fromWindow)
        {
            if (popup != null)
            {
                PopupDialog p = popup;
                popup = null;
                p.OnDismiss = null;
                p.Dismiss();
            }
            if (fromWindow && button != null) button.SetFalse(false);
            if (settingsDirty)
            {
                Settings.Save();
                settingsDirty = false;
            }
        }

        private void OnPopupDismissed()
        {
            popup = null;
            if (button != null) button.SetFalse(false);
        }

        private void Update()
        {
            if (popup == null) return;
            activeTarget.Set(HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null, null);
            if (Time.unscaledTime > nextRefresh) RefreshLists();
            if (positionFrames >= 0)
            {
                positionFrames++;
                if (positionFrames >= 2 && PlaceNextToButton()) positionFrames = -1;
                else if (positionFrames > 30) positionFrames = -1;
            }
            if (settingsDirty && Time.unscaledTime > settingsSaveAt)
            {
                Settings.Save();
                settingsDirty = false;
            }
        }

        /// <summary>Put the window next to the toolbar button (left of a vertical toolbar, above/below a horizontal one).</summary>
        private bool PlaceNextToButton()
        {
            try
            {
                RectTransform rt = popup != null ? popup.RTrf : null;
                if (rt == null) return false;
                Camera wcam = CanvasCamera(rt);
                Vector3[] c = new Vector3[4];
                rt.GetWorldCorners(c);
                Vector2 bl = RectTransformUtility.WorldToScreenPoint(wcam, c[0]);
                Vector2 tr = RectTransformUtility.WorldToScreenPoint(wcam, c[2]);
                Vector2 size = tr - bl;
                if (size.x < 50f || size.y < 50f) return false; // not laid out yet

                Vector2 target = new Vector2(Screen.width - size.x - 70f, Screen.height - size.y - 70f);
                RectTransform brt = button != null ? button.transform as RectTransform : null;
                if (brt != null)
                {
                    Camera bcam = CanvasCamera(brt);
                    brt.GetWorldCorners(c);
                    Vector2 bbl = RectTransformUtility.WorldToScreenPoint(bcam, c[0]);
                    Vector2 btr = RectTransformUtility.WorldToScreenPoint(bcam, c[2]);
                    bool vertical = true;
                    RectTransform space = ApplicationLauncher.Instance != null ? ApplicationLauncher.Instance.launcherSpace : null;
                    if (space != null) vertical = space.rect.height > space.rect.width;
                    bool atTop = btr.y > Screen.height * 0.5f;
                    if (vertical) target = new Vector2(bbl.x - 8f - size.x, btr.y - size.y);   // left of the toolbar, tops aligned
                    else if (atTop) target = new Vector2(btr.x - size.x, bbl.y - 8f - size.y);  // below a top toolbar
                    else target = new Vector2(btr.x - size.x, btr.y + 8f);                      // above a bottom toolbar
                }
                target.x = Mathf.Clamp(target.x, 8f, Mathf.Max(8f, Screen.width - size.x - 8f));
                target.y = Mathf.Clamp(target.y, 8f, Mathf.Max(8f, Screen.height - size.y - 8f));

                Vector3 w0, w1;
                if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, bl, wcam, out w0)) return false;
                if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, target, wcam, out w1)) return false;
                rt.position += w1 - w0;
                return true;
            }
            catch (Exception e)
            {
                Log.Dbg("Window placement failed: " + e.Message);
                return true;
            }
        }

        private static Camera CanvasCamera(Transform t)
        {
            Canvas canvas = t.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            canvas = canvas.rootCanvas;
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        // ------------------------------------------------------------------------------------------------ content

        private DialogGUIBase[] BuildContent()
        {
            bool flight = HighLogic.LoadedSceneIsFlight;
            List<DialogGUIBase> tabs = new List<DialogGUIBase>();
            if (flight) tabs.Add(Tab("This vessel", 0));
            tabs.Add(Tab("My bases", 1));
            tabs.Add(Tab("Settings", 2));

            List<DialogGUIBase> pages = new List<DialogGUIBase>();
            pages.Add(new DialogGUIContentSizer(ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true));
            if (flight) pages.Add(Page(0, VesselPage()));
            pages.Add(Page(1, BasesPage()));
            pages.Add(Page(2, SettingsPage()));

            return new DialogGUIBase[]
            {
                new DialogGUIHorizontalLayout(true, false, 4f, new RectOffset(0, 0, 0, 0), TextAnchor.MiddleCenter, tabs.ToArray()),
                new DialogGUISpace(4f),
                new DialogGUIScrollList(new Vector2(Width - 20f, ListHeight), false, true,
                    new DialogGUIVerticalLayout(10f, 10f, 4f, new RectOffset(8, 24, 6, 6), TextAnchor.UpperLeft, pages.ToArray())),
                new DialogGUIHorizontalLayout(true, false, 4f, new RectOffset(0, 0, 0, 0), TextAnchor.MiddleRight,
                    new DialogGUILabel(Cached(() => Settings.Enabled ? "<color=#8be08b>Protection is on</color>" : "<color=#ff8080>Protection is OFF (see Settings)</color>", 0.5f), true, false),
                    new DialogGUIButton("Close", () => CloseWindow(true), 90f, 26f, false))
            };
        }

        private DialogGUIBase Tab(string label, int index)
        {
            int i = index;
            return new DialogGUIToggleButton(() => page == i, label, b => { if (b) page = i; }, 132f, 28f);
        }

        private static DialogGUIBase Page(int index, DialogGUIBase[] children)
        {
            int i = index;
            DialogGUIVerticalLayout l = new DialogGUIVerticalLayout(true, false, 4f, new RectOffset(0, 0, 0, 0), TextAnchor.UpperLeft, children);
            l.OptionEnabledCondition = () => page == i;
            return l;
        }

        // ---------------- page: this vessel

        private DialogGUIBase[] VesselPage()
        {
            Target t = activeTarget;
            List<DialogGUIBase> c = new List<DialogGUIBase>();

            DialogGUILabel none = new DialogGUILabel("No vessel selected.", true, false);
            none.OptionEnabledCondition = () => !t.Valid;
            c.Add(none);

            c.Add(Text(() => t.Valid ? "<size=17><b>" + Esc(t.Name) + "</b></size>   " + Small + t.Body + " · " + SituationText(t.Situation) + SmallEnd : "", 0.5f));
            c.Add(Text(() => StatusLine(t, true), 0.25f));
            DialogGUILabel report = Text(() =>
            {
                GroundGuard g = t.Guard;
                return g != null && !string.IsNullOrEmpty(g.LastLoadReport) ? Small + "Last load: " + g.LastLoadReport + "." + SmallEnd : "";
            }, 0.5f);
            report.OptionEnabledCondition = () => { GroundGuard g = t.Guard; return g != null && !string.IsNullOrEmpty(g.LastLoadReport); };
            c.Add(report);
            c.Add(new DialogGUISpace(6f));

            c.Add(OptionToggle(t, "This is a base", GetBase, SetBase, CanBase,
                "Gives it the Base icon on the map and applies your base settings" +
                (Settings.BaseDefaultAnchor ? " (locks it to the ground)." : ".")));
            c.Add(OptionToggle(t, "Lock to the ground", GetLock, SetLock, CanBase,
                "Pins it to this exact spot so it can't slide, sink or be thrown. Untick it before you drive away."));
            c.Add(OptionToggle(t, "Freeze when I'm not flying it", GetStatic, SetStatic, CanBase,
                "Keeps it on rails while you fly something else, so nothing can hit it. You can't dock to it while it's frozen."));
            c.Add(OptionToggle(t, "Kraken protection", GetProtect, SetProtect, CanProtect,
                "Waits for the ground to load, lifts it out if it's stuck, then eases it in. On for every landed vessel by default."));

            c.Add(new DialogGUISpace(8f));
            c.Add(new DialogGUILabel("<b>Quick fixes</b>", true, false));
            DialogGUIButton lift = new DialogGUIButton("Lift out of ground", () => { GroundGuard g = t.Guard; if (g != null) g.ManualLift(); }, 130f, 26f, false);
            lift.OptionInteractableCondition = () => t.Guard != null;
            c.Add(ButtonRow(lift, "Use it if the vessel is stuck in or sinking into the ground."));
            DialogGUIButton calm = new DialogGUIButton("Stabilize", () => { GroundGuard g = t.Guard; if (g != null) g.ManualSettle(); }, 130f, 26f, false);
            calm.OptionInteractableCondition = () => t.Guard != null && !t.V.packed;
            c.Add(ButtonRow(calm, "Calms a shaking or bouncing vessel for a few seconds (no crash damage)."));
            DialogGUIButton relock = new DialogGUIButton("Re-lock here", () => { GroundGuard g = t.Guard; if (g != null) g.AnchorHere(); }, 130f, 26f, false);
            relock.OptionInteractableCondition = () => t.Guard != null && GetLock(t);
            c.Add(ButtonRow(relock, "Moves the ground lock to where the vessel is right now."));

            // other vessels in physics range
            c.Add(new DialogGUISpace(8f));
            DialogGUILabel nearHeader = new DialogGUILabel("<b>Other vessels nearby</b>", true, false);
            nearHeader.OptionEnabledCondition = () => nearbyCount > 0;
            c.Add(nearHeader);
            for (int i = 0; i < NearbySlots; i++) c.Add(CompactRow(nearby[i], true));
            return c.ToArray();
        }

        // ---------------- page: my bases

        private DialogGUIBase[] BasesPage()
        {
            List<DialogGUIBase> c = new List<DialogGUIBase>();
            c.Add(new DialogGUIToggle(() => showAllLanded, "Show every landed vessel, not just bases",
                b => { if (b != showAllLanded) { showAllLanded = b; nextRefresh = 0f; } }, RowWidth, 24f));

            DialogGUIButton freezeAll = new DialogGUIButton("Freeze all", () => SetAllStatic(true), 100f, 26f, false);
            freezeAll.OptionInteractableCondition = () => allListed.Count > 0 && Settings.Enabled;
            DialogGUIButton thawAll = new DialogGUIButton("Unfreeze all", () => SetAllStatic(false), 100f, 26f, false);
            thawAll.OptionInteractableCondition = () => allListed.Count > 0;
            DialogGUIHorizontalLayout bulk = new DialogGUIHorizontalLayout(false, false, 6f, new RectOffset(0, 0, 2, 2), TextAnchor.MiddleLeft,
                freezeAll, thawAll, new DialogGUILabel(Small + "for every vessel in this list" + SmallEnd, true, false));
            bulk.OptionEnabledCondition = () => allListed.Count > 0;
            c.Add(bulk);

            DialogGUILabel empty = new DialogGUILabel(Cached(() => showAllLanded ? "No landed vessels." :
                "No bases yet. Tick \"This is a base\" on a vessel" + (HighLogic.LoadedSceneIsFlight ? "" : " (in flight)") +
                ", or tick \"Show every landed vessel\" above.", 0.5f), true, false);
            empty.OptionEnabledCondition = () => basesCount == 0;
            c.Add(empty);
            for (int i = 0; i < BaseSlots; i++) c.Add(CompactRow(bases[i], false));
            DialogGUILabel more = new DialogGUILabel(Cached(() => Small + "...and " + (allListed.Count - basesCount) + " more (the bulk buttons include them)." + SmallEnd, 0.5f), true, false);
            more.OptionEnabledCondition = () => allListed.Count > basesCount;
            c.Add(more);
            return c.ToArray();
        }

        // ---------------- page: settings

        private DialogGUIBase[] SettingsPage()
        {
            List<DialogGUIBase> c = new List<DialogGUIBase>();
            c.Add(SettingToggle("BaseGrounds+ is on", () => Settings.Enabled, b => Settings.Enabled = b,
                "Turns every feature on or off."));
            c.Add(SettingToggle("Protect every landed vessel automatically", () => Settings.ProtectAllLanded, b => Settings.ProtectAllLanded = b,
                "If off, only bases and vessels you tick \"Kraken protection\" on are protected."));
            c.Add(SettingToggle("Show messages on screen", () => Settings.ShowMessages, b => Settings.ShowMessages = b,
                "For example when a vessel is lifted out of the ground or a kraken hit is stopped."));

            c.Add(new DialogGUISpace(6f));
            c.Add(new DialogGUILabel("<b>When I tick \"This is a base\"</b>", true, false));
            c.Add(SettingToggle("Also lock it to the ground", () => Settings.BaseDefaultAnchor, b => Settings.BaseDefaultAnchor = b, ""));
            c.Add(SettingToggle("Also freeze it when I'm not flying it", () => Settings.BaseDefaultStatic, b => Settings.BaseDefaultStatic = b, ""));
            c.Add(SettingToggle("Give it the Base icon on the map", () => Settings.BaseSetsVesselType, b => Settings.BaseSetsVesselType = b, ""));

            c.Add(new DialogGUISpace(6f));
            c.Add(new DialogGUILabel("<b>Fine tuning</b>", true, false));
            c.Add(SliderRow("Max wait for the ground", () => Settings.HoldTimeout, v => Settings.HoldTimeout = Mathf.Round(v), 5f, 120f,
                () => Settings.HoldTimeout.ToString("0") + " s",
                "Longest a vessel waits on rails for its ground to load. Usually it only takes a second."));
            c.Add(SliderRow("Settle time", () => Settings.SettleTime, v => Settings.SettleTime = Mathf.Round(v * 2f) / 2f, 0f, 15f,
                () => Settings.SettleTime.ToString("0.0") + " s",
                "How long a vessel is eased in after physics starts."));
            c.Add(SliderRow("Kraken guard trigger", () => Settings.GuardSpeed, v => Settings.GuardSpeed = Mathf.Round(v * 2f) / 2f, 1f, 50f,
                () => Settings.GuardSpeed.ToString("0.0") + " m/s",
                "A parked vessel that suddenly moves faster than this gets stopped."));
            c.Add(SettingToggle("Fix broken right-click menus from other mods", () => Settings.FixPartMenus, b => Settings.FixPartMenus = b,
                "Backup check: if a part menu setting still shows an on/off switch it can't use, it is turned into read-only text so the menu opens. (KSP's own menu mix-up is always fixed at startup.)"));
            c.Add(SettingToggle("Detailed log (for bug reports)", () => Settings.DebugLogging, b => Settings.DebugLogging = b,
                "Writes every hold, release and lift to KSP.log."));

            c.Add(new DialogGUISpace(6f));
            c.Add(ButtonRow(new DialogGUIButton("Reset to defaults", () => { Settings.ResetToDefaults(); settingsDirty = false; }, 150f, 26f, false),
                "Restores the shipped settings (with the Real Solar System adjustments)."));
            return c.ToArray();
        }

        // ------------------------------------------------------------------------------------------------ building blocks

        /// <summary>Rebuild a string at most every 'interval' seconds; in between return the same instance so the text
        /// component sees no change and the layout is not rebuilt.</summary>
        private static Func<string> Cached(Func<string> source, float interval)
        {
            string value = null;
            float next = 0f;
            return () =>
            {
                float now = Time.unscaledTime;
                if (value == null || now >= next)
                {
                    string s = source() ?? "";
                    if (value == null || s != value) value = s;
                    next = now + interval;
                }
                return value;
            };
        }

        private static DialogGUILabel Text(Func<string> text, float interval)
        {
            return new DialogGUILabel(Cached(text, interval), true, false);
        }

        private static DialogGUIBase Description(string text)
        {
            return new DialogGUIHorizontalLayout(false, false, 0f, new RectOffset(26, 0, 0, 4), TextAnchor.UpperLeft,
                new DialogGUILabel(Small + text + SmallEnd, RowWidth - 30f, -1f));
        }

        private static DialogGUIBase ButtonRow(DialogGUIButton b, string text)
        {
            return new DialogGUIHorizontalLayout(false, false, 8f, new RectOffset(0, 0, 1, 1), TextAnchor.MiddleLeft,
                b, new DialogGUILabel(Small + text + SmallEnd, RowWidth - 140f, -1f));
        }

        private DialogGUIBase OptionToggle(Target t, string label, Func<Target, bool> get, Action<Target, bool> set, Func<Target, bool> can, string description)
        {
            DialogGUIToggle tg = new DialogGUIToggle(() => t.Valid && get(t), label, b =>
            {
                if (!t.Valid || b == get(t)) return;
                set(t, b);
            }, RowWidth, 24f);
            tg.OptionInteractableCondition = () => t.Valid && can(t);
            return new DialogGUIVerticalLayout(true, false, 0f, new RectOffset(0, 0, 0, 0), TextAnchor.UpperLeft, tg, Description(description));
        }

        private DialogGUIBase SmallToggle(Target t, string label, Func<Target, bool> get, Action<Target, bool> set, Func<Target, bool> can)
        {
            DialogGUIToggle tg = new DialogGUIToggle(() => t.Valid && get(t), label, b =>
            {
                if (!t.Valid || b == get(t)) return;
                set(t, b);
            }, 100f, 22f);
            tg.OptionInteractableCondition = () => t.Valid && can(t);
            return tg;
        }

        private DialogGUIBase CompactRow(Target t, bool showDistance)
        {
            DialogGUILabel title = Text(() =>
            {
                if (!t.Valid) return "";
                string s = "<b>" + Esc(t.Name) + "</b>   " + Small + t.Body + " · " + SituationText(t.Situation);
                if (showDistance && t.Loaded && FlightGlobals.ActiveVessel != null)
                {
                    float d = Vector3.Distance(t.V.transform.position, FlightGlobals.ActiveVessel.transform.position);
                    s += " · " + (d < 100f ? d.ToString("0") : (Mathf.Round(d / 10f) * 10f).ToString("0")) + " m";
                }
                return s + SmallEnd;
            }, 0.5f);
            DialogGUILabel status = Text(() => StatusLine(t, false), 0.25f);
            DialogGUIHorizontalLayout toggles = new DialogGUIHorizontalLayout(false, false, 6f, new RectOffset(0, 0, 0, 0), TextAnchor.MiddleLeft,
                SmallToggle(t, "Base", GetBase, SetBase, CanBase),
                SmallToggle(t, "Lock", GetLock, SetLock, CanBase),
                SmallToggle(t, "Freeze", GetStatic, SetStatic, CanBase));
            DialogGUIVerticalLayout row = new DialogGUIVerticalLayout(true, false, 1f, new RectOffset(4, 4, 4, 6), TextAnchor.UpperLeft, title, status, toggles);
            row.OptionEnabledCondition = () => t.Valid;
            return row;
        }

        private DialogGUIBase SettingToggle(string label, Func<bool> get, Action<bool> set, string description)
        {
            DialogGUIToggle tg = new DialogGUIToggle(get, label, b =>
            {
                if (b == get()) return;
                set(b);
                MarkSettingsDirty();
            }, RowWidth, 24f);
            if (string.IsNullOrEmpty(description)) return tg;
            return new DialogGUIVerticalLayout(true, false, 0f, new RectOffset(0, 0, 0, 0), TextAnchor.UpperLeft, tg, Description(description));
        }

        private DialogGUIBase SliderRow(string label, Func<float> get, Action<float> set, float min, float max, Func<string> valueText, string description)
        {
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(false, false, 6f, new RectOffset(0, 0, 0, 0), TextAnchor.MiddleLeft,
                new DialogGUILabel(label, 165f, 22f),
                new DialogGUISlider(get, min, max, false, 140f, 22f, v =>
                {
                    if (Math.Abs(v - get()) < 1e-4f) return;
                    set(v);
                    MarkSettingsDirty();
                }),
                new DialogGUILabel(Cached(valueText, 0.1f), 70f, 22f));
            return new DialogGUIVerticalLayout(true, false, 0f, new RectOffset(0, 0, 0, 0), TextAnchor.UpperLeft, row, Description(description));
        }

        private void MarkSettingsDirty()
        {
            settingsDirty = true;
            settingsSaveAt = Time.unscaledTime + 2f;
        }

        // ------------------------------------------------------------------------------------------------ vessel options

        private static bool GetBase(Target t) { BaseRecord r = t.Record; return r != null && r.IsBase; }
        private static bool GetProtect(Target t) { BaseRecord r = t.Record; return r != null ? r.ProtectOn : Settings.ProtectAllLanded; }
        private static bool GetLock(Target t) { BaseRecord r = t.Record; return r != null && r.Anchored; }
        private static bool GetStatic(Target t) { BaseRecord r = t.Record; return r != null && r.Static; }

        private static bool CanProtect(Target t) { return Settings.Enabled && t.Manageable; }
        private static bool CanBase(Target t) { return Settings.Enabled && t.Manageable && !t.IsEva; }

        private static void SetProtect(Target t, bool on)
        {
            BaseRecord r = t.GetOrCreate();
            r.ProtectOverride = on ? 1 : 0;
            BaseRegistry.Changed();
        }

        private static void SetStatic(Target t, bool on)
        {
            BaseRecord r = t.GetOrCreate();
            r.Static = on;
            BaseRegistry.Changed();
            if (on && t.Loaded && t.V.isActiveVessel)
                Log.Screen(t.Name + " will freeze when you switch to another vessel");
        }

        private void SetAllStatic(bool on)
        {
            int n = 0;
            for (int i = 0; i < allListed.Count; i++)
            {
                Target t = allListed[i];
                if (!CanBase(t) || GetStatic(t) == on) continue;
                BaseRecord r = t.GetOrCreate();
                r.Static = on;
                n++;
            }
            BaseRegistry.Changed();
            if (n == 0) Log.Screen(on ? "every vessel in the list is already frozen" : "none of the vessels in the list were frozen");
            else Log.Screen(on ? "froze " + n + " vessel(s); each stays on rails while you fly something else" : "unfroze " + n + " vessel(s)");
        }

        private static void SetLock(Target t, bool on)
        {
            GroundGuard g = t.Guard;
            if (on)
            {
                if (g != null) g.AnchorHere();
                else
                {
                    BaseRecord r = t.GetOrCreate();
                    r.Anchored = true;
                    r.HasPose = false; // locks once it has loaded, landed and stopped
                }
            }
            else
            {
                if (g != null) g.ReleaseAnchor();
                else
                {
                    BaseRecord r = t.GetOrCreate();
                    r.Anchored = false;
                    r.HasPose = false;
                }
            }
            BaseRegistry.Changed();
        }

        private static void SetBase(Target t, bool on)
        {
            BaseRecord r = t.GetOrCreate();
            r.IsBase = on;
            VesselType current = t.Type;
            if (on)
            {
                if (Settings.BaseSetsVesselType && current != VesselType.Base)
                {
                    r.OrigType = current.ToString();
                    SetVesselType(t, VesselType.Base);
                }
                if (Settings.BaseDefaultAnchor && !r.Anchored) SetLock(t, true);
                if (Settings.BaseDefaultStatic) r.Static = true;
                Log.Screen(t.Name + " is now a base");
            }
            else
            {
                if (!string.IsNullOrEmpty(r.OrigType) && current == VesselType.Base)
                {
                    try { SetVesselType(t, (VesselType)Enum.Parse(typeof(VesselType), r.OrigType)); }
                    catch { }
                }
                r.OrigType = "";
            }
            BaseRegistry.Changed();
        }

        private static void SetVesselType(Target t, VesselType type)
        {
            if (t.V != null)
            {
                t.V.vesselType = type;
                if (t.V.protoVessel != null) t.V.protoVessel.vesselType = type;
            }
            else if (t.PV != null)
            {
                t.PV.vesselType = type;
                if (t.PV.vesselRef != null) t.PV.vesselRef.vesselType = type;
            }
        }

        private static string StatusLine(Target t, bool big)
        {
            if (!t.Valid) return "";
            if (!Settings.Enabled) return "<color=#b0b0b0>BaseGrounds+ is turned off</color>";
            GroundGuard g = t.Guard;
            string s;
            if (g != null && !string.IsNullOrEmpty(g.Status))
            {
                s = "<color=" + g.StatusColor + ">" + g.Status + "</color>";
                if (g.GuardEvents > 0) s += "  <color=#ffb366>(" + g.GuardEvents + " kraken hit" + (g.GuardEvents == 1 ? "" : "s") + " stopped)</color>";
            }
            else
            {
                BaseRecord r = t.Record;
                if (r != null && r.Static) s = "<color=#bfe6ff>Frozen while you're away</color>";
                else if (r != null && r.Anchored) s = r.HasPose ? "<color=#8be08b>Locked to the ground</color>" : "<color=#ffd966>Will lock once it has landed and stopped</color>";
                else if (GetProtect(t) && t.Situation == Vessel.Situations.LANDED) s = "<color=#8be08b>Protected</color>";
                else s = "<color=#b0b0b0>" + (t.Loaded ? "Not on the ground" : "Not loaded right now") + "</color>";
            }
            return big ? "<size=15>" + s + "</size>" : "<size=13>" + s + "</size>";
        }

        // ------------------------------------------------------------------------------------------------ lists

        private void RefreshLists()
        {
            nextRefresh = Time.unscaledTime + 1f;
            try
            {
                RefreshNearby();
                RefreshBases();
            }
            catch (Exception e)
            {
                Log.Dbg("RefreshLists: " + e.Message);
            }
        }

        private void RefreshNearby()
        {
            nearbyCount = 0;
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
            {
                Vessel av = FlightGlobals.ActiveVessel;
                List<Vessel> list = new List<Vessel>();
                List<Vessel> loaded = FlightGlobals.VesselsLoaded;
                for (int i = 0; i < loaded.Count; i++)
                {
                    Vessel v = loaded[i];
                    if (v == null || v == av || v.vesselType == VesselType.Flag || v.vesselType == VesselType.SpaceObject || v.vesselType == VesselType.Debris) continue;
                    list.Add(v);
                }
                Vector3 p = av.transform.position;
                list.Sort((a, b) => (a.transform.position - p).sqrMagnitude.CompareTo((b.transform.position - p).sqrMagnitude));
                for (int i = 0; i < list.Count && nearbyCount < NearbySlots; i++) nearby[nearbyCount++].Set(list[i], null);
            }
            for (int i = nearbyCount; i < NearbySlots; i++) nearby[i].Set(null, null);
        }

        private void RefreshBases()
        {
            List<KeyValuePair<string, Target>> found = new List<KeyValuePair<string, Target>>();
            if (HighLogic.LoadedSceneIsFlight)
            {
                List<Vessel> vs = FlightGlobals.Vessels;
                for (int i = 0; i < vs.Count; i++)
                {
                    Vessel v = vs[i];
                    if (v == null || v.vesselType == VesselType.Flag || v.vesselType == VesselType.SpaceObject) continue;
                    BaseRecord r = BaseRegistry.Resolve(v);
                    if (!((r != null && !r.IsTrivial) || (showAllLanded && v.situation == Vessel.Situations.LANDED))) continue;
                    Target t = new Target();
                    t.Set(v, null);
                    found.Add(new KeyValuePair<string, Target>(t.Body + "\u0001" + t.Name, t));
                }
            }
            else if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.flightState != null)
            {
                List<ProtoVessel> pvs = HighLogic.CurrentGame.flightState.protoVessels;
                for (int i = 0; i < pvs.Count; i++)
                {
                    ProtoVessel pv = pvs[i];
                    if (pv == null || pv.vesselType == VesselType.Flag || pv.vesselType == VesselType.SpaceObject) continue;
                    BaseRecord r = BaseRegistry.Find(pv.persistentId);
                    if (!((r != null && !r.IsTrivial) || (showAllLanded && pv.situation == Vessel.Situations.LANDED))) continue;
                    Target t = new Target();
                    t.Set(null, pv);
                    found.Add(new KeyValuePair<string, Target>(t.Body + "\u0001" + t.Name, t));
                }
            }
            found.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            allListed.Clear();
            for (int i = 0; i < found.Count; i++) allListed.Add(found[i].Value);
            basesCount = Math.Min(found.Count, BaseSlots);
            for (int i = 0; i < BaseSlots; i++)
            {
                if (i < basesCount) bases[i].Set(found[i].Value.V, found[i].Value.PV);
                else bases[i].Set(null, null);
            }
        }

        // ------------------------------------------------------------------------------------------------ text helpers

        private static string BodyLabel(CelestialBody b)
        {
            if (b == null) return "?";
            try { return KSP.Localization.Localizer.Format("<<1>>", b.displayName); }
            catch { return b.bodyName; }
        }

        private static string SituationText(Vessel.Situations s)
        {
            switch (s)
            {
                case Vessel.Situations.LANDED: return "Landed";
                case Vessel.Situations.SPLASHED: return "Splashed";
                case Vessel.Situations.PRELAUNCH: return "On the pad";
                case Vessel.Situations.FLYING: return "Flying";
                case Vessel.Situations.SUB_ORBITAL: return "Sub-orbital";
                case Vessel.Situations.ORBITING: return "Orbiting";
                case Vessel.Situations.ESCAPING: return "Escaping";
                case Vessel.Situations.DOCKED: return "Docked";
                default: return s.ToString();
            }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("<", "‹").Replace(">", "›");
        }
    }
}
