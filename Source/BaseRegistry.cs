using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BaseGroundsPlus
{
    /// <summary>Per-vessel choices made by the player (base / protect / anchor / static) plus the anchor pose.</summary>
    public class BaseRecord
    {
        public uint VesselId;
        public uint KeyPart;          // a part (normally the root) that identifies the vessel across docking/undocking
        public string Name = "";
        public bool IsBase;
        public int ProtectOverride = -1; // -1 = follow global setting, 0 = off, 1 = on
        public bool Anchored;
        public bool Static;           // stay on rails whenever you are not controlling it

        // anchor pose: surface-relative pose of AnchorPart (double precision lat/lon/alt so it holds up on RSS-sized bodies)
        public bool HasPose;
        public uint AnchorPart;
        public string Body = "";
        public double Lat, Lon, Alt;
        public Quaternion Rot = Quaternion.identity;

        public string OrigType = "";  // vessel type before it was marked as a base

        public bool ProtectOn
        {
            get
            {
                if (ProtectOverride >= 0) return ProtectOverride == 1;
                return IsBase || Anchored || Static || Settings.ProtectAllLanded;
            }
        }

        public bool IsTrivial
        {
            get { return !IsBase && !Anchored && !Static && ProtectOverride < 0 && OrigType.Length == 0; }
        }

        public int Score
        {
            get { return (Anchored ? 8 : 0) + (IsBase ? 4 : 0) + (Static ? 2 : 0) + (ProtectOverride == 1 ? 1 : 0); }
        }

        public void Save(ConfigNode n)
        {
            n.AddValue("vessel", VesselId);
            n.AddValue("keyPart", KeyPart);
            n.AddValue("name", Name ?? "");
            n.AddValue("isBase", IsBase);
            n.AddValue("protect", ProtectOverride);
            n.AddValue("anchored", Anchored);
            n.AddValue("static", Static);
            n.AddValue("hasPose", HasPose);
            if (HasPose)
            {
                n.AddValue("anchorPart", AnchorPart);
                n.AddValue("body", Body ?? "");
                n.AddValue("lat", D(Lat));
                n.AddValue("lon", D(Lon));
                n.AddValue("alt", D(Alt));
                n.AddValue("rot", D(Rot.x) + "," + D(Rot.y) + "," + D(Rot.z) + "," + D(Rot.w));
            }
            if (!string.IsNullOrEmpty(OrigType)) n.AddValue("origType", OrigType);
        }

        public static BaseRecord FromNode(ConfigNode n)
        {
            BaseRecord r = new BaseRecord();
            if (!uint.TryParse(n.GetValue("vessel") ?? "", out r.VesselId)) return null;
            uint.TryParse(n.GetValue("keyPart") ?? "", out r.KeyPart);
            r.Name = n.GetValue("name") ?? "";
            bool.TryParse(n.GetValue("isBase") ?? "", out r.IsBase);
            int p;
            if (int.TryParse(n.GetValue("protect") ?? "", out p)) r.ProtectOverride = Math.Max(-1, Math.Min(1, p));
            bool.TryParse(n.GetValue("anchored") ?? "", out r.Anchored);
            bool.TryParse(n.GetValue("static") ?? "", out r.Static);
            bool.TryParse(n.GetValue("hasPose") ?? "", out r.HasPose);
            if (r.HasPose)
            {
                uint.TryParse(n.GetValue("anchorPart") ?? "", out r.AnchorPart);
                r.Body = n.GetValue("body") ?? "";
                bool ok = PD(n.GetValue("lat"), out r.Lat) & PD(n.GetValue("lon"), out r.Lon) & PD(n.GetValue("alt"), out r.Alt);
                string[] q = (n.GetValue("rot") ?? "").Split(',');
                double x, y, z, w;
                if (q.Length == 4 && PD(q[0], out x) && PD(q[1], out y) && PD(q[2], out z) && PD(q[3], out w))
                    r.Rot = new Quaternion((float)x, (float)y, (float)z, (float)w);
                else ok = false;
                if (!ok) r.HasPose = false;
            }
            r.OrigType = n.GetValue("origType") ?? "";
            return r;
        }

        private static string D(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        private static bool PD(string s, out double v)
        {
            v = 0;
            return s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }

    /// <summary>Keeps all BaseRecords and maps them to vessels, following parts through docking and undocking.</summary>
    public static class BaseRegistry
    {
        private static readonly List<BaseRecord> records = new List<BaseRecord>();
        private static readonly Dictionary<uint, BaseRecord> byVessel = new Dictionary<uint, BaseRecord>();
        private static readonly Dictionary<uint, BaseRecord> byPart = new Dictionary<uint, BaseRecord>();
        private static bool indexDirty = true;

        /// <summary>Bumped on every change so vessel modules know when to look their record up again.</summary>
        public static int Version { get; private set; }

        /// <summary>True once the scenario has loaded the records of the current game.</summary>
        public static bool Ready { get; set; }

        public static List<BaseRecord> All { get { return records; } }

        public static void Changed()
        {
            indexDirty = true;
            Version++;
        }

        public static void Load(ConfigNode node)
        {
            records.Clear();
            if (node != null)
            {
                ConfigNode[] nodes = node.GetNodes("BG_VESSEL");
                for (int i = 0; i < nodes.Length; i++)
                {
                    BaseRecord r = BaseRecord.FromNode(nodes[i]);
                    if (r != null) records.Add(r);
                }
            }
            Ready = true;
            Changed();
            Log.Info("Loaded " + records.Count + " vessel record(s)");
        }

        public static void Save(ConfigNode node)
        {
            Prune();
            for (int i = 0; i < records.Count; i++)
                if (!records[i].IsTrivial) records[i].Save(node.AddNode("BG_VESSEL"));
        }

        private static void Reindex()
        {
            byVessel.Clear();
            byPart.Clear();
            for (int i = 0; i < records.Count; i++)
            {
                BaseRecord r = records[i];
                BaseRecord existing;
                if (!byVessel.TryGetValue(r.VesselId, out existing) || r.Score > existing.Score) byVessel[r.VesselId] = r;
                if (r.KeyPart != 0) byPart[r.KeyPart] = r;
                if (r.AnchorPart != 0 && !byPart.ContainsKey(r.AnchorPart)) byPart[r.AnchorPart] = r;
            }
            indexDirty = false;
        }

        public static BaseRecord Find(uint vesselId)
        {
            if (indexDirty) Reindex();
            BaseRecord r;
            return byVessel.TryGetValue(vesselId, out r) ? r : null;
        }

        /// <summary>Find the record for a vessel. Re-keys records whose parts moved to another vessel (docking/undocking).</summary>
        public static BaseRecord Resolve(Vessel v)
        {
            if (v == null) return null;
            if (indexDirty) Reindex();
            bool flight = HighLogic.LoadedSceneIsFlight && FlightGlobals.fetch != null;

            BaseRecord r;
            if (byVessel.TryGetValue(v.persistentId, out r))
            {
                if (flight && v.loaded && r.KeyPart != 0 && v.PartsContain(r.KeyPart) == null)
                {
                    Part kp;
                    if (FlightGlobals.FindLoadedPart(r.KeyPart, out kp) && kp != null && kp.vessel != null && kp.vessel != v)
                    {
                        // the identifying part now lives in another vessel (undocked/decoupled): the record goes with it
                        Rekey(r, kp.vessel);
                        r = null;
                    }
                    else if (v.rootPart != null)
                    {
                        r.KeyPart = v.rootPart.persistentId; // key part was destroyed; heal
                        Changed();
                    }
                }
                if (r != null) return r;
            }

            // claim a record whose key part is in this vessel and whose old owner no longer has it
            if (v.loaded && v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null && byPart.TryGetValue(p.persistentId, out r) && !OwnedElsewhere(r, v, flight))
                    {
                        Rekey(r, v);
                        return r;
                    }
                }
            }
            else if (v.protoVessel != null && v.protoVessel.protoPartSnapshots != null)
            {
                List<ProtoPartSnapshot> pps = v.protoVessel.protoPartSnapshots;
                for (int i = 0; i < pps.Count; i++)
                {
                    if (byPart.TryGetValue(pps[i].persistentId, out r) && !OwnedElsewhere(r, v, flight))
                    {
                        Rekey(r, v);
                        return r;
                    }
                }
            }
            return null;
        }

        private static bool OwnedElsewhere(BaseRecord r, Vessel v, bool flight)
        {
            if (r.VesselId == v.persistentId) return false;
            if (!flight) return true; // can't tell outside flight; leave it alone
            Vessel owner;
            if (!FlightGlobals.FindVessel(r.VesselId, out owner) || owner == null) return false;
            if (owner.loaded) return owner.PartsContain(r.KeyPart) != null;
            if (owner.protoVessel != null)
            {
                List<ProtoPartSnapshot> pps = owner.protoVessel.protoPartSnapshots;
                for (int i = 0; i < pps.Count; i++) if (pps[i].persistentId == r.KeyPart) return true;
            }
            return false;
        }

        private static void Rekey(BaseRecord r, Vessel v)
        {
            Log.Dbg("Record '" + r.Name + "' now belongs to vessel '" + v.vesselName + "' (" + v.persistentId + ")");
            r.VesselId = v.persistentId;
            r.Name = v.vesselName;
            Changed();
        }

        public static BaseRecord GetOrCreate(Vessel v)
        {
            BaseRecord r = Resolve(v);
            if (r != null) return r;
            r = new BaseRecord();
            r.VesselId = v.persistentId;
            r.Name = v.vesselName;
            if (v.loaded && v.rootPart != null) r.KeyPart = v.rootPart.persistentId;
            else if (v.protoVessel != null) r.KeyPart = RootPartId(v.protoVessel);
            records.Add(r);
            Changed();
            return r;
        }

        public static BaseRecord GetOrCreate(ProtoVessel pv)
        {
            if (pv.vesselRef != null) return GetOrCreate(pv.vesselRef);
            BaseRecord r = Find(pv.persistentId);
            if (r != null) return r;
            r = new BaseRecord();
            r.VesselId = pv.persistentId;
            r.Name = pv.vesselName;
            r.KeyPart = RootPartId(pv);
            records.Add(r);
            Changed();
            return r;
        }

        private static uint RootPartId(ProtoVessel pv)
        {
            if (pv.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0) return 0;
            int idx = pv.rootIndex >= 0 && pv.rootIndex < pv.protoPartSnapshots.Count ? pv.rootIndex : 0;
            return pv.protoPartSnapshots[idx].persistentId;
        }

        public static void Remove(uint vesselId)
        {
            int n = records.RemoveAll(r => r.VesselId == vesselId);
            if (n > 0) Changed();
        }

        /// <summary>After docking, make sure the merged vessel carries the most important record of the two.</summary>
        public static void OnDocked(Vessel merged)
        {
            if (merged == null || merged.parts == null) return;
            if (indexDirty) Reindex();
            BaseRecord best = null;
            for (int i = 0; i < merged.parts.Count; i++)
            {
                BaseRecord r;
                if (merged.parts[i] != null && byPart.TryGetValue(merged.parts[i].persistentId, out r))
                    if (best == null || r.Score > best.Score) best = r;
            }
            BaseRecord current;
            byVessel.TryGetValue(merged.persistentId, out current);
            if (best != null && best != current && (current == null || best.Score > current.Score))
            {
                Rekey(best, merged);
                if (current != null)
                {
                    // keep the other record dormant under its old key part; it comes back on undocking
                    current.VesselId = 0;
                    Changed();
                }
            }
        }

        /// <summary>Drop records whose vessel and parts are gone (recovered, destroyed...).</summary>
        private static void Prune()
        {
            HashSet<uint> vesselIds = new HashSet<uint>();
            HashSet<uint> partIds = new HashSet<uint>();
            bool reliable = false;
            try
            {
                if (HighLogic.LoadedSceneIsFlight)
                {
                    if (FlightGlobals.ready && FlightGlobals.Vessels != null)
                    {
                        reliable = true;
                        List<Vessel> vs = FlightGlobals.Vessels;
                        for (int i = 0; i < vs.Count; i++)
                        {
                            Vessel v = vs[i];
                            if (v == null) continue;
                            vesselIds.Add(v.persistentId);
                            if (v.loaded && v.parts != null)
                            {
                                for (int j = 0; j < v.parts.Count; j++) if (v.parts[j] != null) partIds.Add(v.parts[j].persistentId);
                            }
                            else if (v.protoVessel != null)
                            {
                                for (int j = 0; j < v.protoVessel.protoPartSnapshots.Count; j++) partIds.Add(v.protoVessel.protoPartSnapshots[j].persistentId);
                            }
                        }
                    }
                }
                else if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.flightState != null)
                {
                    reliable = true;
                    List<ProtoVessel> pvs = HighLogic.CurrentGame.flightState.protoVessels;
                    for (int i = 0; i < pvs.Count; i++)
                    {
                        vesselIds.Add(pvs[i].persistentId);
                        for (int j = 0; j < pvs[i].protoPartSnapshots.Count; j++) partIds.Add(pvs[i].protoPartSnapshots[j].persistentId);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("Prune skipped: " + e.Message);
                reliable = false;
            }

            int removed = records.RemoveAll(r => r.IsTrivial || (reliable && !vesselIds.Contains(r.VesselId) && !partIds.Contains(r.KeyPart) && !partIds.Contains(r.AnchorPart)));
            if (removed > 0)
            {
                Log.Dbg("Pruned " + removed + " record(s)");
                Changed();
            }
        }
    }

    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class BaseGroundsScenario : ScenarioModule
    {
        public override void OnLoad(ConfigNode node)
        {
            BaseRegistry.Load(node);
        }

        public override void OnSave(ConfigNode node)
        {
            BaseRegistry.Save(node);
        }
    }
}
