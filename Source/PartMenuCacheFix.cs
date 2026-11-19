using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace BaseGroundsPlus
{
    /// <summary>
    /// Root-cause fix for right-click (PAW) menus that sometimes refuse to open with
    /// "InvalidCastException ... UIPartActionToggle.GetFieldValue".
    ///
    /// KSP caches the UI controls (on/off switch, slider, ...) of every part-module setting in a static
    /// Dictionary keyed only by FieldInfo.GetHashCode(). In KSP's Mono that hash is derived from the object's memory
    /// address truncated to 32 bits, so two different settings whose FieldInfo objects sit a multiple of 4 GB apart get
    /// the same key. A large install (RP-1/RSS uses 20+ GB) makes that likely, and the colliding pair changes from
    /// launch to launch. The second setting then silently inherits the first one's controls: a number gets an on/off
    /// switch and KSP throws while building the menu, or a real switch turns into plain text.
    ///
    /// The fix: before KSP reads its cache, put the controls that really belong to this field under its key. The lookup
    /// result is exactly what KSP would compute without a collision, so nothing else changes.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class PartMenuCacheFix : MonoBehaviour
    {
        private const int MaxLogged = 40;

        private static Dictionary<int, UI_Control[]> stockCache;
        private static readonly Dictionary<FieldInfo, UI_Control[]> realControls = new Dictionary<FieldInfo, UI_Control[]>(new IdentityComparer());
        private static readonly Dictionary<int, FieldInfo> keyOwner = new Dictionary<int, FieldInfo>();
        private static bool failed;

        public static bool Active { get; private set; }
        public static int Collisions { get; private set; }

        private void Awake()
        {
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo cacheField = typeof(BaseField).GetField("fieldInfoToUIControlDictionary", any | BindingFlags.Static);
                MethodInfo target = typeof(BaseField).GetMethod("SetupUIControls", any | BindingFlags.Instance, null, Type.EmptyTypes, null);
                stockCache = cacheField != null ? cacheField.GetValue(null) as Dictionary<int, UI_Control[]> : null;
                if (target == null || stockCache == null)
                {
                    Log.Warn("Part menu cache fix not applied: this KSP version has no BaseField.SetupUIControls cache (nothing to fix).");
                    return;
                }
                Harmony harmony = new Harmony("BaseGroundsPlus.PartMenuCacheFix");
                harmony.Patch(target, new HarmonyMethod(typeof(PartMenuCacheFix).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
                Active = true;
                Log.Info("Part menu cache fix applied (KSP's UI-control cache is now collision-safe).");
            }
            catch (Exception e)
            {
                Log.Error("Part menu cache fix could not be applied: " + e);
            }
        }

        // Runs right before BaseField.SetupUIControls() looks up fieldInfoToUIControlDictionary[FieldInfo.GetHashCode()].
        private static void Prefix(BaseField __instance)
        {
            if (failed) return;
            try
            {
                FieldInfo fi = __instance.FieldInfo;
                if (fi == null) return;

                UI_Control[] controls;
                if (!realControls.TryGetValue(fi, out controls))
                {
                    // exactly what stock does on a cache miss
                    controls = (UI_Control[])fi.GetCustomAttributes(typeof(UI_Control), false);
                    realControls.Add(fi, controls);
                }

                int key = fi.GetHashCode();
                UI_Control[] cached;
                if (stockCache.TryGetValue(key, out cached) && !ReferenceEquals(cached, controls))
                {
                    FieldInfo other;
                    keyOwner.TryGetValue(key, out other);
                    if (!SameControls(cached, controls))
                    {
                        Collisions++;
                        if (Collisions <= MaxLogged)
                            Log.Warn("Prevented a KSP part-menu mix-up: setting '" + Describe(fi) + "' would have received the menu controls of '" +
                                     (other != null ? Describe(other) : "another setting") + "' (" + Names(cached) + " instead of " + Names(controls) + ").");
                    }
                }
                stockCache[key] = controls;
                keyOwner[key] = fi;
            }
            catch (Exception e)
            {
                failed = true; // never break part loading; fall back to stock behaviour
                Log.Error("Part menu cache fix disabled after an error: " + e);
            }
        }

        private static bool SameControls(UI_Control[] a, UI_Control[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] == null || b[i] == null || a[i].GetType() != b[i].GetType() || a[i].scene != b[i].scene) return false;
            return true;
        }

        private static string Describe(FieldInfo fi)
        {
            return (fi.DeclaringType != null ? fi.DeclaringType.Name : "?") + "." + fi.Name + " (" + fi.FieldType.Name + ")";
        }

        private static string Names(UI_Control[] c)
        {
            if (c == null || c.Length == 0) return "no control";
            string s = "";
            for (int i = 0; i < c.Length; i++) s += (i > 0 ? "+" : "") + (c[i] != null ? c[i].GetType().Name : "null");
            return s;
        }

        private sealed class IdentityComparer : IEqualityComparer<FieldInfo>
        {
            public bool Equals(FieldInfo x, FieldInfo y) { return ReferenceEquals(x, y); }
            public int GetHashCode(FieldInfo o) { return RuntimeHelpers.GetHashCode(o); }
        }
    }
}
