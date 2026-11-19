using System;
using System.Globalization;
using System.IO;

namespace BaseGroundsPlus
{
    /// <summary>
    /// Global settings. Defaults come from the BASEGROUNDS_DEFAULTS node in GameData/BaseGroundsPlus/Settings.cfg
    /// (ModuleManager-patchable, e.g. for RSS), and the player's own changes are stored in PluginData/settings.cfg.
    /// </summary>
    public static class Settings
    {
        // master switches
        public static bool Enabled = true;
        public static bool ProtectAllLanded = true;
        public static bool ShowMessages = true;
        public static bool DebugLogging = false;
        public static bool FixPartMenus = true;       // repair right-click menus broken by other mods' bad on/off switches

        // hold (delay physics until the terrain under the vessel is fully built)
        public static float HoldTimeout = 20f;        // seconds of game time before giving up and releasing anyway
        public static float MinHoldTime = 0.4f;       // never release sooner than this
        public static int StableFrames = 25;          // physics frames the terrain height must stay unchanged
        public static float StableTolerance = 0.02f;  // metres

        // ground fix
        public static float MaxLift = 25f;            // never lift a vessel more than this (metres)
        public static float LiftMargin = 0.03f;       // clearance added above the terrain after a lift (metres)

        // settle (first seconds of physics after unpacking)
        public static float SettleTime = 4f;          // seconds
        public static float SettleMaxSpeed = 1.5f;    // m/s relative to the ground
        public static float SettleMaxAngular = 0.6f;  // rad/s
        public static float DepenetrationSpeed = 1f;  // m/s, how fast PhysX may push parts out of the ground

        // guard (protected vessels you are not controlling)
        public static float GuardSpeed = 6f;          // m/s; anything faster is treated as a kraken impulse

        // anchor
        public static float AnchorStaleDistance = 5f; // metres; further than this on load = vessel was moved by a mod, re-anchor

        // defaults for "Mark as base"
        public static bool BaseDefaultAnchor = true;
        public static bool BaseDefaultStatic = false;
        public static bool BaseSetsVesselType = true;

        public static bool Loaded { get; private set; }

        private static string UserPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BaseGroundsPlus/PluginData/settings.cfg"); }
        }

        public static void EnsureLoaded()
        {
            if (!Loaded) Load();
        }

        private static void SetCodeDefaults()
        {
            Enabled = true; ProtectAllLanded = true; ShowMessages = true; DebugLogging = false; FixPartMenus = true;
            HoldTimeout = 20f; MinHoldTime = 0.4f; StableFrames = 25; StableTolerance = 0.02f;
            MaxLift = 25f; LiftMargin = 0.03f;
            SettleTime = 4f; SettleMaxSpeed = 1.5f; SettleMaxAngular = 0.6f; DepenetrationSpeed = 1f;
            GuardSpeed = 6f; AnchorStaleDistance = 5f;
            BaseDefaultAnchor = true; BaseDefaultStatic = false; BaseSetsVesselType = true;
        }

        /// <summary>Back to the shipped defaults (including ModuleManager patches such as the RSS one).</summary>
        public static void ResetToDefaults()
        {
            try { if (File.Exists(UserPath)) File.Delete(UserPath); }
            catch (Exception e) { Log.Warn("Could not delete " + UserPath + ": " + e.Message); }
            Load();
        }

        public static void Load()
        {
            Loaded = true;
            SetCodeDefaults();
            try
            {
                if (GameDatabase.Instance != null)
                {
                    ConfigNode[] defs = GameDatabase.Instance.GetConfigNodes("BASEGROUNDS_DEFAULTS");
                    if (defs != null)
                        for (int i = 0; i < defs.Length; i++) Apply(defs[i]);
                }
                if (File.Exists(UserPath))
                {
                    ConfigNode root = ConfigNode.Load(UserPath);
                    ConfigNode n = root != null ? root.GetNode("BASEGROUNDS_SETTINGS") : null;
                    if (n != null) Apply(n);
                }
                Sanitize();
                Log.Info("Settings loaded (protect all landed: " + ProtectAllLanded + ", hold timeout: " + HoldTimeout + "s)");
            }
            catch (Exception e)
            {
                Log.Error("Failed to load settings: " + e);
            }
        }

        public static void Save()
        {
            try
            {
                Sanitize();
                ConfigNode root = new ConfigNode();
                ConfigNode n = root.AddNode("BASEGROUNDS_SETTINGS");
                n.AddValue("enabled", Enabled);
                n.AddValue("protectAllLanded", ProtectAllLanded);
                n.AddValue("showMessages", ShowMessages);
                n.AddValue("debugLogging", DebugLogging);
                n.AddValue("fixPartMenus", FixPartMenus);
                n.AddValue("holdTimeout", F(HoldTimeout));
                n.AddValue("minHoldTime", F(MinHoldTime));
                n.AddValue("stableFrames", StableFrames);
                n.AddValue("stableTolerance", F(StableTolerance));
                n.AddValue("maxLift", F(MaxLift));
                n.AddValue("liftMargin", F(LiftMargin));
                n.AddValue("settleTime", F(SettleTime));
                n.AddValue("settleMaxSpeed", F(SettleMaxSpeed));
                n.AddValue("settleMaxAngular", F(SettleMaxAngular));
                n.AddValue("depenetrationSpeed", F(DepenetrationSpeed));
                n.AddValue("guardSpeed", F(GuardSpeed));
                n.AddValue("anchorStaleDistance", F(AnchorStaleDistance));
                n.AddValue("baseDefaultAnchor", BaseDefaultAnchor);
                n.AddValue("baseDefaultStatic", BaseDefaultStatic);
                n.AddValue("baseSetsVesselType", BaseSetsVesselType);
                string dir = Path.GetDirectoryName(UserPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                root.Save(UserPath);
            }
            catch (Exception e)
            {
                Log.Error("Failed to save settings: " + e);
            }
        }

        private static void Apply(ConfigNode n)
        {
            B(n, "enabled", ref Enabled);
            B(n, "protectAllLanded", ref ProtectAllLanded);
            B(n, "showMessages", ref ShowMessages);
            B(n, "debugLogging", ref DebugLogging);
            B(n, "fixPartMenus", ref FixPartMenus);
            Fl(n, "holdTimeout", ref HoldTimeout);
            Fl(n, "minHoldTime", ref MinHoldTime);
            I(n, "stableFrames", ref StableFrames);
            Fl(n, "stableTolerance", ref StableTolerance);
            Fl(n, "maxLift", ref MaxLift);
            Fl(n, "liftMargin", ref LiftMargin);
            Fl(n, "settleTime", ref SettleTime);
            Fl(n, "settleMaxSpeed", ref SettleMaxSpeed);
            Fl(n, "settleMaxAngular", ref SettleMaxAngular);
            Fl(n, "depenetrationSpeed", ref DepenetrationSpeed);
            Fl(n, "guardSpeed", ref GuardSpeed);
            Fl(n, "anchorStaleDistance", ref AnchorStaleDistance);
            B(n, "baseDefaultAnchor", ref BaseDefaultAnchor);
            B(n, "baseDefaultStatic", ref BaseDefaultStatic);
            B(n, "baseSetsVesselType", ref BaseSetsVesselType);
        }

        private static void Sanitize()
        {
            HoldTimeout = Clamp(HoldTimeout, 2f, 300f);
            MinHoldTime = Clamp(MinHoldTime, 0f, 10f);
            if (StableFrames < 3) StableFrames = 3;
            if (StableFrames > 500) StableFrames = 500;
            StableTolerance = Clamp(StableTolerance, 0.001f, 1f);
            MaxLift = Clamp(MaxLift, 0f, 500f);
            LiftMargin = Clamp(LiftMargin, 0f, 1f);
            SettleTime = Clamp(SettleTime, 0f, 60f);
            SettleMaxSpeed = Clamp(SettleMaxSpeed, 0.1f, 50f);
            SettleMaxAngular = Clamp(SettleMaxAngular, 0.05f, 20f);
            DepenetrationSpeed = Clamp(DepenetrationSpeed, 0.05f, 50f);
            GuardSpeed = Clamp(GuardSpeed, 0.5f, 500f);
            AnchorStaleDistance = Clamp(AnchorStaleDistance, 0.5f, 10000f);
        }

        private static float Clamp(float v, float min, float max)
        {
            if (float.IsNaN(v)) return min;
            return v < min ? min : (v > max ? max : v);
        }

        private static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        private static void B(ConfigNode n, string key, ref bool field)
        {
            string s = n.GetValue(key);
            bool v;
            if (s != null && bool.TryParse(s.Trim(), out v)) field = v;
        }

        private static void Fl(ConfigNode n, string key, ref float field)
        {
            string s = n.GetValue(key);
            float v;
            if (s != null && float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) field = v;
        }

        private static void I(ConfigNode n, string key, ref int field)
        {
            string s = n.GetValue(key);
            int v;
            if (s != null && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) field = v;
        }
    }
}
