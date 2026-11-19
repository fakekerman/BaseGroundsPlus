using System;
using System.Collections.Generic;
using UnityEngine;

namespace BaseGroundsPlus
{
    /// <summary>
    /// Runs on every loaded vessel in flight. For protected landed vessels:
    ///  1. HOLD    - keeps the vessel on rails until the terrain colliders under it have stopped changing
    ///               (stock only waits a fixed 75 frames, which is not enough on big Kopernicus/RSS terrain).
    ///  2. RELEASE - measures real collider penetration with the ground and lifts the vessel out of it,
    ///               then lets the game unpack it (and skips the stock re-positioning, which uses the analytic height).
    ///  3. SETTLE  - for a few seconds: no part can be destroyed by impacts, PhysX may only push parts out of the
    ///               ground slowly, and every part's speed relative to the ground is clamped.
    ///  4. LIVE    - kraken guard for vessels you are not flying, watcher that re-lifts the vessel if the terrain under it
    ///               is rebuilt at a higher detail, and the ground lock (anchor part pinned to its saved surface pose).
    /// "Static" (frozen) vessels simply stay on rails while you are not controlling them.
    /// </summary>
    public class GroundGuard : VesselModule
    {
        public enum Phase { Idle, Holding, Released, Settling, Live }

        private Phase phase = Phase.Idle;
        private string status = "";
        private string statusColor = Grey;

        // status colours for the UI
        public const string Grey = "#b0b0b0", Yellow = "#ffd966", Cyan = "#7fd4ff", Green = "#8be08b", Orange = "#ffb366", Ice = "#bfe6ff";

        public Phase CurrentPhase { get { return phase; } }
        /// <summary>Plain-language status for the UI.</summary>
        public string Status { get { return status; } }
        public string StatusColor { get { return statusColor; } }
        public float LastLift { get; private set; }
        public int GuardEvents { get; private set; }
        /// <summary>One line about what happened the last time this vessel loaded, for the UI.</summary>
        public string LastLoadReport { get; private set; }
        public bool IsPinned { get { return pinned; } }
        public bool ParkingBrakeActive { get { return parkingBrake; } }

        private void SetStatus(string text, string color)
        {
            status = text;
            statusColor = color;
        }

        // record and evaluated flags
        private BaseRecord rec;
        private int recVersion = -1;
        private float nextResolveTime;
        private bool vesselDirty = true;
        private bool protect, anchor, isStatic, active, groundMode;

        // hold
        private VesselRanges savedRanges, heldRanges;
        private bool rangesOverridden;
        private float holdTime;
        private int stableCount;
        private int maxDeficit;
        private float noHitTime;
        private List<Part> sampleParts;
        private float[] lastHeights;
        private int releasedFrames;
        private float nextHoldMsg;

        // settle
        private float settleTimer;
        private float settleDuration;
        private readonly Dictionary<Part, SettleSave> settleSaved = new Dictionary<Part, SettleSave>();
        private readonly Dictionary<Rigidbody, float> origDepen = new Dictionary<Rigidbody, float>();
        private int depenPartCount = -1;

        private class SettleSave
        {
            public float crashTolerance;
            public List<ModuleWheels.ModuleWheelDamage> wheels;
            public List<Vector2> wheelTol;
        }

        // ground-relative velocity of the world frame at the vessel (handles rotating/non-rotating frame and Krakensbane)
        private CelestialBody refBody;
        private double refLat, refLon;
        private Vector3d lastSurfPos;
        private bool hasSurfPos, surfValid;
        private Vector3 surfVel;
        private int surfRejects;

        // ground lock (anchor)
        private bool pinned;
        private Part pinPart;
        private bool pinWasKinematic;

        // Which GroundGuard made which rigidbody kinematic. Needed because a locked part can change vessel (undocking,
        // decoupling, docking merges): without this, the next owner would read "already kinematic" as the part's original
        // state and leave it frozen forever after unlocking.
        private static readonly Dictionary<Rigidbody, GroundGuard> pinOwners = new Dictionary<Rigidbody, GroundGuard>();

        public static void ClearPinOwners() { pinOwners.Clear(); }
        private bool firstPin = true;
        private float calmTime;

        // static: let go while your kerbal is on EVA right next to the vessel (ladders, boarding)
        private bool evaThaw;

        // live terrain watcher
        private int watchCounter;
        private float[] watchHeights;

        // compat
        private float nextCompatCheck;
        private bool parkingBrake, vmMoving, hasClamp;
        private int clampPartCount = -1;
        private bool wantStaticPack;

        private float lastLandedTime = -1000f;
        private float lastGuardMsgTime = -1000f;

        // Kraken guard only protects vessels that were sitting still a moment ago. A kraken hit takes a resting vessel to
        // high speed within a frame or two; a stage falling back, crash debris or a rover driven by kOS arrives already
        // moving (or speeds up gradually) and must be left to normal physics.
        private const float KickWindow = 0.5f;
        private float lastRestTime = -1000f;

        private static float RestSpeed
        {
            get { return Mathf.Min(Settings.GuardSpeed * 0.5f, Mathf.Max(1f, Settings.SettleMaxSpeed + 0.5f)); }
        }

        public override Activation GetActivation()
        {
            return Activation.FlightScene | Activation.LoadedVessels;
        }

        public override bool ShouldBeActive()
        {
            return HighLogic.LoadedSceneIsFlight && vessel != null && vessel.loaded;
        }

        protected override void OnStart()
        {
            GameEvents.onVesselWasModified.Add(OnVesselModified);
            GameEvents.onVesselChange.Add(OnVesselChange);
        }

        private void OnDestroy()
        {
            GameEvents.onVesselWasModified.Remove(OnVesselModified);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            try { if (vessel != null) Shutdown(); } catch { }
        }

        private void OnVesselModified(Vessel v)
        {
            if (v != vessel) return;
            vesselDirty = true;
            sampleParts = null;
            watchHeights = null;
            clampPartCount = -1;
            depenPartCount = -1;
        }

        private void OnVesselChange(Vessel v)
        {
            vesselDirty = true;
        }

        public override void OnLoadVessel()
        {
            phase = Phase.Idle;
            firstPin = true;
            vesselDirty = true;
            hasSurfPos = false;
            surfValid = false;
            refBody = null;
            ResetStability();
            holdTime = 0f;
            noHitTime = 0f;
            calmTime = 0f;
            Settings.EnsureLoaded();
            Evaluate();
            if (anchor && rec != null && rec.HasPose) vessel.skipGroundPositioning = true;
            if (vessel.packed && WantsHold())
            {
                // get in before the stock unpack logic sees the vessel
                phase = Phase.Holding;
                OverrideRanges();
                if (vessel.isActiveVessel) OrbitPhysicsManager.HoldVesselUnpack(5);
                SetStatus("Waiting for the ground to load...", Yellow);
            }
        }

        public override void OnUnloadVessel()
        {
            Shutdown();
            SetStatus("", Grey);
        }

        public override void OnGoOffRails()
        {
            Evaluate();
            if (rangesOverridden) RestoreRanges();
            if (WantsSettle()) BeginSettle(phase == Phase.Released ? "released after hold" : "unpacked", Settings.SettleTime);
            else phase = Phase.Live;
        }

        public override void OnGoOnRails()
        {
            RestoreSettle();
            LimitDepenetration(false);
            UnpinState(false);
            phase = Phase.Idle;
            ResetStability();
            holdTime = 0f;
            noHitTime = 0f;
            firstPin = true;
            lastRestTime = -1000f;
        }

        private bool WantsHold()
        {
            return groundMode && !vmMoving && (protect || isStatic || (anchor && rec != null && rec.HasPose));
        }

        private bool WantsSettle()
        {
            return groundMode && (protect || (anchor && rec != null && rec.HasPose));
        }

        // ------------------------------------------------------------------------------------------------ main loop

        private void FixedUpdate()
        {
            Vessel v = vessel;
            if (v == null || !HighLogic.LoadedSceneIsFlight) return;
            if (!v.loaded)
            {
                if (phase != Phase.Idle) Shutdown();
                return;
            }
            float dt = TimeWarp.fixedDeltaTime;
            Evaluate();
            SampleSurfaceVelocity(dt);
            if (v.situation == Vessel.Situations.LANDED) lastLandedTime = Time.time;
            wantStaticPack = false;

            if (v.packed)
            {
                if (!WantsHold())
                {
                    if (rangesOverridden) RestoreRanges();
                    phase = Phase.Idle;
                    SetStatus("On rails", Grey);
                    return;
                }
                if (phase == Phase.Released) UpdateReleased();
                else UpdateHold(dt);
                return;
            }

            if (rangesOverridden) RestoreRanges();

            if (isStatic && !vmMoving)
            {
                wantStaticPack = true; // packed in LateUpdate, where the stock code packs vessels too
                SetStatus("Freezing...", Ice);
                return;
            }

            if (phase == Phase.Idle || phase == Phase.Holding || phase == Phase.Released)
            {
                // unpacked without passing through OnGoOffRails: a vessel that was just created (EVA, undocking, decoupling)
                // or one another mod unpacked. Ease in the ones you are not flying; never slow down the one you are, and
                // never catch a vessel that is created already moving fast (crash debris, a stage breaking up on impact).
                if (WantsSettle() && !active && MaxRelativeSpeed() <= RestSpeed) BeginSettle("late start", Mathf.Min(2f, Settings.SettleTime));
                else phase = Phase.Live;
            }

            if (phase == Phase.Settling) UpdateSettle(dt);
            else if (phase == Phase.Live) UpdateLive(dt);
        }

        private void LateUpdate()
        {
            if (!wantStaticPack) return;
            wantStaticPack = false;
            Vessel v = vessel;
            if (v == null || !v.loaded || v.packed || v.isActiveVessel) return;
            RestoreSettle();
            LimitDepenetration(false);
            UnpinState(true);
            v.GoOnRails();
            OverrideRanges();
            phase = Phase.Holding;
            SetStatus("Frozen (on rails)", Ice);
            Log.Dbg(v.vesselName + " is static: kept on rails while not focused");
        }

        private void Evaluate()
        {
            Vessel v = vessel;
            active = v.isActiveVessel;
            if (vesselDirty || recVersion != BaseRegistry.Version || Time.time > nextResolveTime)
            {
                rec = BaseRegistry.Resolve(v);
                recVersion = BaseRegistry.Version;
                vesselDirty = false;
                nextResolveTime = Time.time + 2f;
            }
            if (Time.time > nextCompatCheck)
            {
                nextCompatCheck = Time.time + 1f;
                parkingBrake = Compat.ParkingBrakeEngaged(v);
            }
            vmMoving = Compat.VesselMoverMoving(v);

            // a kerbal on EVA right next to a frozen vessel needs it physical (ladders, boarding, KIS)
            Vessel av = FlightGlobals.ActiveVessel;
            if (av != null && av != v && av.isEVA && av.loaded)
            {
                float d = Vector3.Distance(av.transform.position, v.transform.position);
                if (d < 60f) evaThaw = true;
                else if (d > 90f) evaThaw = false;
            }
            else evaThaw = false;

            bool manageable = Settings.Enabled && IsManageable(v);
            bool landed = v.situation == Vessel.Situations.LANDED;
            bool protectFlag = rec != null ? rec.ProtectOn : (Settings.ProtectAllLanded || !BaseRegistry.Ready);
            anchor = manageable && rec != null && rec.Anchored;
            groundMode = landed || (anchor && rec.HasPose);
            protect = manageable && protectFlag && groundMode;
            isStatic = manageable && rec != null && rec.Static && groundMode && !active && !evaThaw;
        }

        private bool IsManageable(Vessel v)
        {
            if (v.vesselType == VesselType.Flag || v.vesselType == VesselType.SpaceObject) return false;
            CelestialBody b = v.mainBody;
            if (b == null || b.pqsController == null) return false;     // no solid surface
            if (clampPartCount != v.parts.Count)
            {
                clampPartCount = v.parts.Count;
                hasClamp = false;
                for (int i = 0; i < v.parts.Count; i++)
                    if (v.parts[i] != null && v.parts[i].FindModuleImplementing<LaunchClamp>() != null) { hasClamp = true; break; }
            }
            return !hasClamp; // launch clamps hold their vessel themselves
        }

        // ------------------------------------------------------------------------------------------------ hold

        private void UpdateHold(float dt)
        {
            Vessel v = vessel;
            phase = Phase.Holding;
            OverrideRanges();
            if (active) OrbitPhysicsManager.HoldVesselUnpack(5);

            if (isStatic)
            {
                SetStatus("Frozen (on rails)", Ice);
                ResetStability();
                return;
            }
            if (TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
            {
                SetStatus("On rails (time warp)", Grey);
                ResetStability();
                return;
            }
            if (!active)
            {
                Vessel av = FlightGlobals.ActiveVessel;
                if (av == null) { ResetStability(); return; }
                float dist = Vector3.Distance(v.transform.position, av.transform.position);
                if (dist >= OriginalUnpack())
                {
                    SetStatus("On rails (" + dist.ToString("0") + " m away)", Grey);
                    ResetStability();
                    holdTime = 0f;
                    noHitTime = 0f;
                    return;
                }
            }

            holdTime += dt;
            bool stable = CheckTerrainStable(dt);
            SetStatus(holdTime < 1f ? "Waiting for the ground to load..." : "Waiting for the ground to load... (" + Mathf.FloorToInt(holdTime) + " s)", Yellow);
            if (!BaseRegistry.Ready && holdTime < Settings.HoldTimeout) return;

            bool noGround = noHitTime > 10f; // nothing solid under it at all (e.g. sitting on something exotic): don't wait forever
            if ((stable && holdTime >= Settings.MinHoldTime) || noGround || holdTime >= Settings.HoldTimeout)
            {
                Release(stable);
                return;
            }
            if (active && holdTime > 1.5f && Time.time > nextHoldMsg && Settings.ShowMessages)
            {
                nextHoldMsg = Time.time + 1f;
                ScreenMessages.PostScreenMessage("BaseGrounds+: waiting for the ground under " + v.vesselName + " to finish loading... (" +
                    holdTime.ToString("0") + "/" + Settings.HoldTimeout.ToString("0") + " s)", 1.1f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        private void ResetStability()
        {
            stableCount = 0;
            if (lastHeights != null) for (int i = 0; i < lastHeights.Length; i++) lastHeights[i] = float.NaN;
        }

        private const float CastUp = 50f;
        private const float CastDown = 400f;

        private bool CheckTerrainStable(float dt)
        {
            Vessel v = vessel;
            Vector3 up = GroundUtil.Up(v.mainBody, v.transform.position);
            if (sampleParts == null || lastHeights == null || sampleParts.Count != lastHeights.Length)
            {
                sampleParts = GroundUtil.PickSampleParts(v, up, 6);
                lastHeights = new float[sampleParts.Count];
                for (int i = 0; i < lastHeights.Length; i++) lastHeights[i] = float.NaN;
                stableCount = 0;
            }
            bool allStable = true;
            int hits = 0;
            maxDeficit = 0;
            for (int i = 0; i < sampleParts.Count; i++)
            {
                Part p = sampleParts[i];
                if (p == null) { sampleParts = null; return false; }
                Vector3 pt;
                int deficit;
                float d = GroundUtil.GroundDistanceBelow(p.transform.position + up * CastUp, -up, CastUp + CastDown, out pt, out deficit);
                if (d < 0f)
                {
                    if (!float.IsNaN(lastHeights[i])) allStable = false;
                    lastHeights[i] = float.NaN;
                    continue;
                }
                hits++;
                if (deficit > maxDeficit) maxDeficit = deficit;
                if (float.IsNaN(lastHeights[i]) || Mathf.Abs(d - lastHeights[i]) > Settings.StableTolerance) allStable = false;
                lastHeights[i] = d;
            }
            noHitTime = hits == 0 ? noHitTime + dt : 0f;
            stableCount = (allStable && hits > 0) ? stableCount + 1 : 0;
            if (stableCount < Settings.StableFrames) return false;
            // The vessel you are flying is the terrain's focus, so its ground should reach full detail: wait for it
            // (for at most half the timeout, in case the terrain focus is elsewhere, e.g. map view).
            if (active && maxDeficit > 0 && holdTime < Settings.HoldTimeout * 0.5f) return false;
            return true;
        }

        private void Release(bool stable)
        {
            Vessel v = vessel;
            CelestialBody body = v.mainBody;
            Vector3 up = GroundUtil.Up(body, v.transform.position);
            float worst = 0f;
            int pairs = 0;
            float lift = 0f;
            try { lift = GroundUtil.ComputeRequiredLift(v, up, out worst, out pairs); }
            catch (Exception e) { Log.Warn("Lift computation failed for " + v.vesselName + ": " + e.Message); }
            LastLift = lift;
            if (lift > 0.005f)
            {
                v.altitude += lift;
                v.SetPosition(body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude));
                if (anchor && rec != null && rec.HasPose) rec.Alt += lift;
                if (lift > 0.1f) Log.Screen(v.vesselName + " was " + lift.ToString("0.00") + " m inside the ground; lifted out before physics started");
                else Log.Dbg(v.vesselName + ": lifted " + lift.ToString("0.000") + " m (" + pairs + " contacts, worst " + worst.ToString("0.000") + " m)");
            }
            // Our check used the real colliders, so skip the stock re-positioning (it uses the analytic terrain height and
            // can tilt the vessel to the terrain normal). Stock only runs it when these levels differ from the PQS.
            // (RSS runs it anyway on every unpack; by now the terrain is final, so its raycast is accurate.)
            if (body.pqsController != null)
            {
                v.PQSminLevel = body.pqsController.minLevel;
                v.PQSmaxLevel = body.pqsController.maxLevel;
            }
            if (anchor && rec != null && rec.HasPose) v.skipGroundPositioning = true;

            string why = stable ? "terrain stable" : (noHitTime > 10f ? "no ground found below" : "TIMEOUT");
            if (stable) LastLoadReport = "ground was ready after " + holdTime.ToString("0.0") + " s";
            else if (noHitTime > 10f) LastLoadReport = "no ground found under it; started anyway (gently)";
            else LastLoadReport = "ground kept changing for " + holdTime.ToString("0") + " s; started anyway (gently)";
            LastLoadReport += lift > 0.005f ? ", lifted " + lift.ToString("0.00") + " m out of the ground" : ", nothing was stuck";
            Log.Dbg(v.vesselName + ": released after " + holdTime.ToString("0.00") + " s (" + why + ")");
            if (!stable) Log.Warn(v.vesselName + ": released without stable terrain (" + why + "); settle protection still applies");
            RestoreRanges();
            phase = Phase.Released;
            releasedFrames = 0;
            SetStatus("Starting physics...", Yellow);
        }

        private void UpdateReleased()
        {
            releasedFrames++;
            SetStatus("Starting physics...", Yellow);
            if (!active)
            {
                Vessel av = FlightGlobals.ActiveVessel;
                if (av != null && Vector3.Distance(vessel.transform.position, av.transform.position) >= OriginalUnpack())
                {
                    phase = Phase.Holding; // moved out of range before unpacking
                    ResetStability();
                    holdTime = 0f;
                    return;
                }
            }
            if (releasedFrames > 250)
            {
                // something else is keeping it packed (stock physics hold, warp...); re-check the terrain before letting go
                phase = Phase.Holding;
                ResetStability();
            }
        }

        private void OverrideRanges()
        {
            Vessel v = vessel;
            if (rangesOverridden && v.vesselRanges == heldRanges) return;
            if (!rangesOverridden || v.vesselRanges != heldRanges)
                savedRanges = v.vesselRanges ?? new VesselRanges();
            heldRanges = new VesselRanges(savedRanges);
            heldRanges.landed.unpack = -1f;
            heldRanges.prelaunch.unpack = -1f;
            heldRanges.splashed.unpack = -1f;
            v.vesselRanges = heldRanges;
            rangesOverridden = true;
        }

        private void RestoreRanges()
        {
            if (!rangesOverridden) return;
            if (vessel != null && vessel.vesselRanges == heldRanges) vessel.vesselRanges = savedRanges;
            rangesOverridden = false;
        }

        private float OriginalUnpack()
        {
            VesselRanges r = rangesOverridden ? savedRanges : vessel.vesselRanges;
            if (r == null) return 200f;
            return r.GetSituationRanges(vessel.situation).unpack;
        }

        // ------------------------------------------------------------------------------------------------ settle

        private void BeginSettle(string why, float duration)
        {
            Vessel v = vessel;
            RestoreSettle();
            phase = Phase.Settling;
            settleTimer = 0f;
            settleDuration = duration;
            int frames = Mathf.CeilToInt(duration / Mathf.Max(0.005f, TimeWarp.fixedDeltaTime)) + 25;
            v.IgnoreGForces(frames);
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                SettleSave s = new SettleSave();
                s.crashTolerance = p.crashTolerance;
                p.crashTolerance = Mathf.Max(p.crashTolerance, 1000f);
                List<ModuleWheels.ModuleWheelDamage> wd = p.FindModulesImplementing<ModuleWheels.ModuleWheelDamage>();
                if (wd != null && wd.Count > 0)
                {
                    s.wheels = wd;
                    s.wheelTol = new List<Vector2>();
                    for (int j = 0; j < wd.Count; j++)
                    {
                        s.wheelTol.Add(new Vector2(wd[j].impactTolerance, wd[j].stressTolerance));
                        wd[j].impactTolerance = Mathf.Max(wd[j].impactTolerance, 1e6f);
                        wd[j].stressTolerance = Mathf.Max(wd[j].stressTolerance, 1e9f);
                    }
                }
                settleSaved[p] = s;
            }
            depenPartCount = -1;
            LimitDepenetration(true);
            Log.Dbg(v.vesselName + ": settling for " + duration.ToString("0.0") + " s (" + why + ")");
        }

        private void UpdateSettle(float dt)
        {
            settleTimer += dt;
            LimitDepenetration(true);
            if (anchor) UpdateAnchor(dt);
            ClampVelocities(Settings.SettleMaxSpeed, Settings.SettleMaxAngular);
            SetStatus("Settling in gently (" + Mathf.CeilToInt(Mathf.Max(0f, settleDuration - settleTimer)) + " s)", Cyan);
            if (settleTimer >= settleDuration)
            {
                RestoreSettle();
                phase = Phase.Live;
                watchHeights = null;
                Log.Dbg(vessel.vesselName + ": settled");
            }
        }

        private void RestoreSettle()
        {
            foreach (KeyValuePair<Part, SettleSave> kv in settleSaved)
            {
                Part p = kv.Key;
                if (p == null) continue;
                p.crashTolerance = kv.Value.crashTolerance;
                if (kv.Value.wheels != null)
                    for (int j = 0; j < kv.Value.wheels.Count; j++)
                    {
                        ModuleWheels.ModuleWheelDamage w = kv.Value.wheels[j];
                        if (w == null) continue;
                        w.impactTolerance = kv.Value.wheelTol[j].x;
                        w.stressTolerance = kv.Value.wheelTol[j].y;
                    }
            }
            settleSaved.Clear();
        }

        /// <summary>Limit how fast PhysX may push parts out of the ground (the actual "yeet" mechanism).</summary>
        private void LimitDepenetration(bool on)
        {
            Vessel v = vessel;
            if (!on)
            {
                if (origDepen.Count == 0) return;
                foreach (KeyValuePair<Rigidbody, float> kv in origDepen)
                    if (kv.Key != null) kv.Key.maxDepenetrationVelocity = kv.Value;
                origDepen.Clear();
                depenPartCount = -1;
                return;
            }
            if (v == null || depenPartCount == v.parts.Count) return;
            depenPartCount = v.parts.Count;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                LimitRb(p.rb);
                LimitRb(p.servoRb);
            }
        }

        private void LimitRb(Rigidbody rb)
        {
            if (rb == null || origDepen.ContainsKey(rb)) return;
            origDepen[rb] = rb.maxDepenetrationVelocity;
            rb.maxDepenetrationVelocity = Mathf.Min(rb.maxDepenetrationVelocity, Settings.DepenetrationSpeed);
        }

        // ------------------------------------------------------------------------------------------------ live

        private void UpdateLive(float dt)
        {
            Vessel v = vessel;
            bool eligible = protect && !active && (Time.time - lastLandedTime < 3f || (anchor && rec.HasPose));
            float max = eligible ? MaxRelativeSpeed() : 0f;
            if (eligible && max <= RestSpeed) lastRestTime = Time.time;
            // only a vessel that was at rest a moment ago gets the guard (and the slow depenetration that goes with it)
            bool guardOn = eligible && Time.time - lastRestTime < KickWindow;
            LimitDepenetration(guardOn);

            if (anchor) UpdateAnchor(dt);
            else UnpinState(true);

            if (guardOn)
            {
                if (max > Settings.GuardSpeed)
                {
                    ClampVelocities(Settings.SettleMaxSpeed, Settings.SettleMaxAngular);
                    lastRestTime = Time.time; // keep holding it while the impulse lasts
                    GuardEvents++;
                    if (Time.time - lastGuardMsgTime > 10f)
                    {
                        lastGuardMsgTime = Time.time;
                        Log.Screen("stopped a kraken impulse on " + v.vesselName + " (" + max.ToString("0") + " m/s)");
                    }
                }
                WatchTerrain();
            }

            if (anchor)
            {
                if (vmMoving) SetStatus("Lock paused while VesselMover moves it", Orange);
                else if (parkingBrake) SetStatus("Lock paused: ParkingBrake is engaged", Orange);
                else if (pinned) SetStatus("Locked to the ground", Green);
                else SetStatus("Will lock once it has landed and stopped", Yellow);
            }
            else if (protect) SetStatus("Protected", Green);
            else if (Settings.Enabled && v.situation == Vessel.Situations.LANDED) SetStatus("Not protected", Grey);
            else SetStatus("Not on the ground", Grey);
        }

        /// <summary>The terrain under a vessel you are approaching can be rebuilt at a higher detail after it unpacked.
        /// When that happens, lift it out again instead of letting PhysX do it violently.</summary>
        private void WatchTerrain()
        {
            if (++watchCounter < 10) return;
            watchCounter = 0;
            Vessel v = vessel;
            Vector3 up = GroundUtil.Up(v.mainBody, v.transform.position);
            if (sampleParts == null) sampleParts = GroundUtil.PickSampleParts(v, up, 6);
            if (watchHeights == null || watchHeights.Length != sampleParts.Count)
            {
                watchHeights = new float[sampleParts.Count];
                for (int i = 0; i < watchHeights.Length; i++) watchHeights[i] = float.NaN;
            }
            bool changed = false;
            for (int i = 0; i < sampleParts.Count; i++)
            {
                Part p = sampleParts[i];
                if (p == null) { sampleParts = null; watchHeights = null; return; }
                Vector3 pt;
                float d = GroundUtil.GroundDistanceBelow(p.transform.position + up * CastUp, -up, CastUp + CastDown, out pt);
                if (d < 0f) continue;
                if (!float.IsNaN(watchHeights[i]) && Mathf.Abs(d - watchHeights[i]) > 0.05f) changed = true;
                watchHeights[i] = d;
            }
            if (!changed) return;
            float worst;
            int pairs;
            float lift = GroundUtil.ComputeRequiredLift(v, up, out worst, out pairs);
            if (lift > 0.01f)
            {
                ShiftVessel(up * lift);
                if (anchor && rec != null && rec.HasPose) rec.Alt += lift;
                Log.Dbg(v.vesselName + ": terrain rebuilt under it, lifted " + lift.ToString("0.000") + " m");
                BeginSettle("terrain rebuilt", Mathf.Min(2f, Settings.SettleTime));
            }
        }

        // ------------------------------------------------------------------------------------------------ ground lock

        private void UpdateAnchor(float dt)
        {
            if (rec == null) return;
            if (vmMoving)
            {
                UnpinState(true);
                rec.HasPose = false; // re-lock where VesselMover drops it
                calmTime = 0f;
                return;
            }
            if (parkingBrake)
            {
                UnpinState(true);
                return;
            }
            if (!rec.HasPose)
            {
                // lock only once it is really sitting on the ground and has been still for a moment
                bool onGround = vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH;
                bool calm = MaxRelativeSpeed() < 0.3f;
                calmTime = onGround && calm ? calmTime + dt : 0f;
                if (calmTime >= 1f || (onGround && phase == Phase.Settling && settleTimer > settleDuration * 0.5f))
                {
                    CaptureAnchor(vessel.rootPart);
                    if (vessel.isActiveVessel) Log.Screen(vessel.vesselName + " is now locked to the ground");
                }
                return;
            }
            Pin();
        }

        private Part FindAnchorPart()
        {
            if (rec.AnchorPart != 0)
            {
                Part p = vessel.PartsContain(rec.AnchorPart);
                if (p != null) return p;
            }
            // anchor part was lost (destroyed / undocked): let go of it properly, then re-lock on the current root
            UnpinState(true);
            rec.HasPose = false;
            return null;
        }

        private void Pin()
        {
            Vessel v = vessel;
            Part ap = FindAnchorPart();
            if (ap == null || ap.rb == null) return;
            CelestialBody body = v.mainBody;
            if (rec.Body != body.bodyName)
            {
                rec.HasPose = false;
                return;
            }
            Rigidbody rb = ap.rb;
            Vector3d target = body.GetWorldSurfacePosition(rec.Lat, rec.Lon, rec.Alt);
            Quaternion targetRot = body.bodyTransform.rotation * rec.Rot;

            if (firstPin)
            {
                firstPin = false;
                Physics.SyncTransforms(); // other code (stock / RSS ground positioning) may have just moved the transforms
                double dev = ((Vector3d)rb.position - target).magnitude;
                float ang = Quaternion.Angle(rb.rotation, targetRot);
                if (dev > Settings.AnchorStaleDistance || ang > 15f)
                {
                    // Moved on purpose by something else (BonVoyage, VesselMover, HyperEdit, the F12 cheat menu,
                    // Making History re-placing vessels on its launch pads...). Don't drag it back, and don't lock it
                    // where it is now either: it may be in the air. Lock again once it has landed and stopped.
                    Log.Screen(v.vesselName + " was moved " + dev.ToString("0") + " m since it was locked; it will lock again where it comes to rest");
                    rec.HasPose = false;
                    calmTime = 0f;
                    BaseRegistry.Changed();
                    return;
                }
                if (dev > 0.01 || ang > 0.2f)
                {
                    // put the whole vessel back where it was locked, rotation included (not just the anchor part)
                    RigidMove(rb.position, rb.rotation, (Vector3)target, targetRot);
                    Log.Dbg(v.vesselName + ": restored locked pose (" + dev.ToString("0.000") + " m, " + ang.ToString("0.00") + " deg)");
                }
            }

            if (!pinned || pinPart != ap)
            {
                if (pinned && pinPart != null) UnpinState(true); // switching to another anchor part
                // Remember the part's own kinematic state (e.g. Breaking Ground ground parts already are kinematic),
                // but never mistake a lock we (or another vessel's guard) put on it for that state.
                pinWasKinematic = rb.isKinematic && !pinOwners.ContainsKey(rb);
            }
            if (!rb.isKinematic) rb.isKinematic = true;
            pinOwners[rb] = this;
            rb.position = (Vector3)target;
            rb.rotation = targetRot;
            if (!v.permanentGroundContact) v.permanentGroundContact = true;
            v.skipGroundPositioning = true;
            pinned = true;
            pinPart = ap;
        }

        private void UnpinState(bool restoreKinematic)
        {
            if (!pinned) return;
            if (pinPart != null && pinPart.rb != null)
            {
                Rigidbody rb = pinPart.rb;
                GroundGuard owner;
                // only undo a lock that is still ours (the part may now be locked by another vessel's guard)
                if (pinOwners.TryGetValue(rb, out owner) && owner == this)
                {
                    pinOwners.Remove(rb);
                    // a packed part is kinematic anyway and KSP clears that itself when it unpacks
                    bool partPacked = pinPart.vessel != null && pinPart.vessel.packed;
                    if (restoreKinematic && !partPacked) rb.isKinematic = pinWasKinematic;
                }
            }
            if (vessel != null && !parkingBrake)
            {
                bool pgc = false;
                for (int i = 0; i < vessel.parts.Count; i++)
                    if (vessel.parts[i] != null && vessel.parts[i].PermanentGroundContact) { pgc = true; break; }
                vessel.permanentGroundContact = pgc;
            }
            pinned = false;
            pinPart = null;
        }

        private void CaptureAnchor(Part p)
        {
            Vessel v = vessel;
            if (rec == null || p == null) return;
            CelestialBody body = v.mainBody;
            Vector3d pos = p.rb != null && !v.packed ? (Vector3d)p.rb.position : (Vector3d)p.transform.position;
            Quaternion rot = p.rb != null && !v.packed ? p.rb.rotation : p.transform.rotation;
            rec.Lat = body.GetLatitude(pos, false);
            rec.Lon = body.GetLongitude(pos, false);
            rec.Alt = body.GetAltitude(pos);
            rec.Rot = Quaternion.Inverse(body.bodyTransform.rotation) * rot;
            rec.AnchorPart = p.persistentId;
            rec.Body = body.bodyName;
            rec.HasPose = true;
            firstPin = false;
            calmTime = 0f;
            BaseRegistry.Changed();
            Log.Info(v.vesselName + " locked at " + rec.Lat.ToString("0.000000") + ", " + rec.Lon.ToString("0.000000") + ", " + rec.Alt.ToString("0.00") + " m on " + rec.Body);
        }

        // ------------------------------------------------------------------------------------------------ physics helpers

        private void SampleSurfaceVelocity(float dt)
        {
            Vessel v = vessel;
            CelestialBody b = v.mainBody;
            if (b == null) return;
            if (refBody != b)
            {
                refBody = b;
                refLat = v.latitude;
                refLon = v.longitude;
                hasSurfPos = false;
                surfValid = false;
            }
            Vector3d p = b.GetWorldSurfacePosition(refLat, refLon, 0);
            if (((Vector3d)v.transform.position - p).sqrMagnitude > 4e6 + (v.altitude * v.altitude))
            {
                // vessel travelled far from the reference point: move it (keeps the rotation term accurate)
                refLat = v.latitude;
                refLon = v.longitude;
                p = b.GetWorldSurfacePosition(refLat, refLon, 0);
                hasSurfPos = false;
            }
            if (hasSurfPos && dt > 0f)
            {
                Vector3 vel = (Vector3)((p - lastSurfPos) / dt);
                // reject floating-origin shifts (one-frame jumps of kilometres)
                if (vel.sqrMagnitude < 4e8f && (!surfValid || surfRejects > 5 || (vel - surfVel).sqrMagnitude < 2500f))
                {
                    surfVel = surfValid ? Vector3.Lerp(surfVel, vel, 0.5f) : vel;
                    surfValid = true;
                    surfRejects = 0;
                }
                else surfRejects++;
            }
            lastSurfPos = p;
            hasSurfPos = true;
        }

        private Vector3 GroundVelocity
        {
            get { return surfValid ? surfVel : -Krakensbane.GetFrameVelocityV3f(); }
        }

        private float MaxRelativeSpeed()
        {
            Vessel v = vessel;
            Vector3 g = GroundVelocity;
            float max = 0f;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.rb == null || p.rb.isKinematic) continue;
                float s = (p.rb.velocity - g).magnitude;
                if (s > max) max = s;
            }
            return max;
        }

        private void ClampVelocities(float maxV, float maxW)
        {
            Vessel v = vessel;
            Vector3 g = GroundVelocity;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                ClampRb(p.rb, g, maxV, maxW);
                ClampRb(p.servoRb, g, maxV, maxW);
            }
        }

        private static void ClampRb(Rigidbody rb, Vector3 g, float maxV, float maxW)
        {
            if (rb == null || rb.isKinematic) return;
            Vector3 rel = rb.velocity - g;
            float m = rel.magnitude;
            if (m > maxV) rb.velocity = g + rel * (maxV / m);
            Vector3 w = rb.angularVelocity;
            float wm = w.magnitude;
            if (wm > maxW) rb.angularVelocity = w * (maxW / wm);
        }

        /// <summary>Teleport an unpacked vessel by a world-space offset (all rigidbodies together).</summary>
        private void ShiftVessel(Vector3 delta)
        {
            Vessel v = vessel;
            Physics.SyncTransforms();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                if (p.rb != null) p.rb.position += delta;
                if (p.servoRb != null) p.servoRb.position += delta;
            }
        }

        /// <summary>Move an unpacked vessel rigidly so that a reference pose (from) ends up at (to).</summary>
        private void RigidMove(Vector3 fromPos, Quaternion fromRot, Vector3 toPos, Quaternion toRot)
        {
            Vessel v = vessel;
            Quaternion delta = toRot * Quaternion.Inverse(fromRot);
            Vector3 g = GroundVelocity;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                MoveRb(p.rb, fromPos, toPos, delta, g);
                MoveRb(p.servoRb, fromPos, toPos, delta, g);
            }
        }

        private static void MoveRb(Rigidbody rb, Vector3 fromPos, Vector3 toPos, Quaternion delta, Vector3 g)
        {
            if (rb == null) return;
            rb.position = toPos + delta * (rb.position - fromPos);
            rb.rotation = delta * rb.rotation;
            if (!rb.isKinematic)
            {
                rb.velocity = g;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private void Shutdown()
        {
            RestoreRanges();
            RestoreSettle();
            LimitDepenetration(false);
            // restore: if this vessel object goes away while its parts live on (docking merge), the locked part must
            // not stay kinematic; UnpinState skips packed parts, which KSP handles itself
            UnpinState(true);
            phase = Phase.Idle;
            ResetStability();
            holdTime = 0f;
            noHitTime = 0f;
            lastRestTime = -1000f;
        }

        // ------------------------------------------------------------------------------------------------ UI actions

        public void MarkDirty() { vesselDirty = true; }

        /// <summary>Lift the vessel out of the ground right now (works packed or unpacked).</summary>
        public float ManualLift()
        {
            Vessel v = vessel;
            if (v == null || !v.loaded) return 0f;
            Vector3 up = GroundUtil.Up(v.mainBody, v.transform.position);
            float worst;
            int pairs;
            float lift = GroundUtil.ComputeRequiredLift(v, up, out worst, out pairs);
            if (lift <= 0.005f)
            {
                Log.Screen(v.vesselName + " is not stuck in the ground");
                return 0f;
            }
            if (v.packed)
            {
                v.altitude += lift;
                v.SetPosition(v.mainBody.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude));
            }
            else
            {
                ShiftVessel(up * lift);
                BeginSettle("manual lift", Settings.SettleTime);
            }
            if (anchor && rec != null && rec.HasPose) rec.Alt += lift;
            Log.Screen(v.vesselName + " lifted " + lift.ToString("0.00") + " m out of the ground");
            return lift;
        }

        public void ManualSettle()
        {
            Vessel v = vessel;
            if (v == null || !v.loaded || v.packed) return;
            BeginSettle("manual", Settings.SettleTime);
            Log.Screen("stabilizing " + v.vesselName + " for " + Settings.SettleTime.ToString("0.#") + " s");
        }

        /// <summary>Lock to the ground where it is now (or as soon as it has landed and stopped).</summary>
        public void AnchorHere()
        {
            Vessel v = vessel;
            if (v == null) return;
            rec = BaseRegistry.GetOrCreate(v);
            rec.Anchored = true;
            UnpinState(true);
            bool onGround = v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.PRELAUNCH;
            if (v.loaded && v.rootPart != null && onGround && (v.packed || MaxRelativeSpeed() < 0.5f))
                CaptureAnchor(v.rootPart);
            else
            {
                rec.HasPose = false; // UpdateAnchor locks it once it has landed and stopped
                calmTime = 0f;
                if (v.loaded) Log.Screen(v.vesselName + " will lock to the ground once it has landed and stopped");
            }
            BaseRegistry.Changed();
        }

        public void ReleaseAnchor()
        {
            UnpinState(true);
            if (rec != null)
            {
                rec.Anchored = false;
                rec.HasPose = false;
                BaseRegistry.Changed();
            }
            if (vessel != null) vessel.skipGroundPositioning = false;
        }
    }
}
