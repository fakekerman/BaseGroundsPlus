using System;
using System.Reflection;

namespace BaseGroundsPlus
{
    /// <summary>Soft (reflection) detection of other mods that also hold vessels in place, so we never fight them.</summary>
    internal static class Compat
    {
        private static bool initDone;

        // ParkingBrake (ParkingBrake.ParkingBrakeModule.BrakeActive): it teleports the vessel to its own saved spot every frame
        private static Type pbModuleType;
        private static PropertyInfo pbActive;

        // VesselMover (VesselMover.VesselMove.Instance.IsMovingVessel / MovingVessel)
        private static FieldInfo vmInstance;
        private static FieldInfo vmIsMoving;
        private static FieldInfo vmMovingVessel;

        public static bool HasParkingBrake { get { Init(); return pbActive != null; } }
        public static bool HasVesselMover { get { Init(); return vmInstance != null; } }

        private static void Init()
        {
            if (initDone) return;
            initDone = true;
            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    if (la == null || la.assembly == null) continue;
                    string n = la.assembly.GetName().Name;
                    if (n == "ParkingBrake")
                    {
                        pbModuleType = la.assembly.GetType("ParkingBrake.ParkingBrakeModule", false);
                        if (pbModuleType != null) pbActive = pbModuleType.GetProperty("BrakeActive", BindingFlags.Public | BindingFlags.Instance);
                    }
                    else if (n == "VesselMover")
                    {
                        Type t = la.assembly.GetType("VesselMover.VesselMove", false);
                        if (t != null)
                        {
                            vmInstance = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                            vmIsMoving = t.GetField("IsMovingVessel", BindingFlags.Public | BindingFlags.Instance);
                            vmMovingVessel = t.GetField("MovingVessel", BindingFlags.Public | BindingFlags.Instance);
                            if (vmIsMoving == null || vmMovingVessel == null) vmInstance = null;
                        }
                    }
                }
                if (pbActive != null) Log.Info("ParkingBrake detected: anchors pause while its brake is engaged");
                if (vmInstance != null) Log.Info("VesselMover detected: anchors pause while it moves a vessel");
            }
            catch (Exception e)
            {
                Log.Warn("Compat init failed: " + e.Message);
            }
        }

        public static bool ParkingBrakeEngaged(Vessel v)
        {
            Init();
            if (pbActive == null || v == null || !v.loaded) return false;
            try
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null) continue;
                    for (int j = 0; j < p.Modules.Count; j++)
                    {
                        PartModule m = p.Modules[j];
                        if (m != null && m.GetType() == pbModuleType)
                            return (bool)pbActive.GetValue(m, null);
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool VesselMoverMoving(Vessel v)
        {
            Init();
            if (vmInstance == null || v == null) return false;
            try
            {
                object inst = vmInstance.GetValue(null);
                if (inst == null) return false;
                if (!(bool)vmIsMoving.GetValue(inst)) return false;
                Vessel mv = vmMovingVessel.GetValue(inst) as Vessel;
                return mv == v;
            }
            catch { return false; }
        }
    }
}
