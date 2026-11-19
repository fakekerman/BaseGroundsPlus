using System.Collections.Generic;
using UnityEngine;

namespace BaseGroundsPlus
{
    /// <summary>Terrain probing and penetration maths. Everything here works against the real colliders
    /// (PQS terrain quads, KSC buildings, Kerbal Konstructs statics), not the analytic terrain height.</summary>
    internal static class GroundUtil
    {
        private static int groundMask = -1;
        private static readonly RaycastHit[] rayBuf = new RaycastHit[32];
        private static readonly Collider[] overlapBuf = new Collider[64];
        private static readonly List<Collider> colBuf = new List<Collider>(256);

        /// <summary>Layers that hold terrain and static scenery ("Local Scenery" 15 and "TerrainColliders" 28).</summary>
        public static int GroundMask
        {
            get
            {
                if (groundMask == -1)
                {
                    int a = LayerMask.NameToLayer("Local Scenery");
                    int b = LayerMask.NameToLayer("TerrainColliders");
                    groundMask = (1 << (a >= 0 ? a : 15)) | (1 << (b >= 0 ? b : 28));
                }
                return groundMask;
            }
        }

        /// <summary>Terrain or static scenery: anything on the ground layers that is not attached to a rigidbody
        /// (parts, kerbals, and the stock per-part helper colliders all have one).</summary>
        public static bool IsGround(Collider c)
        {
            return c != null && !c.isTrigger && c.enabled && c.attachedRigidbody == null;
        }

        public static Vector3 Up(CelestialBody body, Vector3 worldPos)
        {
            Vector3d d = (Vector3d)worldPos - body.position;
            return ((Vector3)d).normalized;
        }

        /// <summary>Cast straight down from above a point. Returns the distance from the cast origin to the ground, or -1.</summary>
        public static float GroundDistanceBelow(Vector3 origin, Vector3 down, float maxDist, out Vector3 point)
        {
            int deficit;
            return GroundDistanceBelow(origin, down, maxDist, out point, out deficit);
        }

        /// <param name="levelDeficit">for terrain quads: how many subdivision levels the hit quad is below the
        /// detail the PQS is aiming for (0 = final detail, or the hit was a static / building).</param>
        public static float GroundDistanceBelow(Vector3 origin, Vector3 down, float maxDist, out Vector3 point, out int levelDeficit)
        {
            point = Vector3.zero;
            levelDeficit = 0;
            int n = Physics.RaycastNonAlloc(origin, down, rayBuf, maxDist, GroundMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Collider bestCol = null;
            for (int i = 0; i < n; i++)
            {
                if (!IsGround(rayBuf[i].collider)) continue;
                if (rayBuf[i].distance < best)
                {
                    best = rayBuf[i].distance;
                    point = rayBuf[i].point;
                    bestCol = rayBuf[i].collider;
                }
            }
            if (bestCol == null) return -1f;
            PQ quad = bestCol.GetComponent<PQ>();
            if (quad == null) quad = bestCol.GetComponentInParent<PQ>();
            if (quad != null && quad.sphereRoot != null)
            {
                int target = Mathf.Min(quad.sphereRoot.maxLevel, quad.sphereRoot.maxLevelAtCurrentTgtSpeed);
                levelDeficit = Mathf.Max(0, target - quad.subdivision);
            }
            return best;
        }

        /// <summary>Solid, enabled, non-trigger colliders of a vessel that PhysX can test against terrain
        /// (primitives and convex meshes; wheel colliders are skipped, their suspension handles the ground).</summary>
        public static List<Collider> GetSolidColliders(Vessel v)
        {
            colBuf.Clear();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                Collider[] cs = p.GetComponentsInChildren<Collider>(false);
                for (int j = 0; j < cs.Length; j++)
                {
                    Collider c = cs[j];
                    if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                    if (c is WheelCollider) continue;
                    MeshCollider mc = c as MeshCollider;
                    if (mc != null && (!mc.convex || mc.sharedMesh == null)) continue;
                    if (!(c is BoxCollider || c is SphereCollider || c is CapsuleCollider || mc != null)) continue;
                    colBuf.Add(c);
                }
            }
            return colBuf;
        }

        /// <summary>How far the vessel must move straight up so that none of its parts are inside the ground.
        /// Uses Physics.ComputePenetration, i.e. exactly what PhysX would otherwise "fix" with a kraken-sized impulse.</summary>
        public static float ComputeRequiredLift(Vessel v, Vector3 up, out float worstPenetration, out int pairs)
        {
            worstPenetration = 0f;
            pairs = 0;
            Physics.SyncTransforms();
            List<Collider> cols = GetSolidColliders(v);
            if (cols.Count == 0) return 0f;

            float lift = 0f;
            for (int iter = 0; iter < 8; iter++)
            {
                float need = 0f;
                for (int i = 0; i < cols.Count; i++)
                {
                    Collider c = cols[i];
                    if (c == null) continue;
                    Bounds b = c.bounds;
                    Vector3 offset = up * lift;
                    int n = Physics.OverlapBoxNonAlloc(b.center + offset, b.extents + new Vector3(0.05f, 0.05f, 0.05f), overlapBuf,
                        Quaternion.identity, GroundMask, QueryTriggerInteraction.Ignore);
                    Transform ct = c.transform;
                    for (int j = 0; j < n; j++)
                    {
                        Collider o = overlapBuf[j];
                        if (!IsGround(o)) continue;
                        Vector3 dir;
                        float dist;
                        if (!Physics.ComputePenetration(c, ct.position + offset, ct.rotation, o, o.transform.position, o.transform.rotation, out dir, out dist))
                            continue;
                        if (iter == 0)
                        {
                            pairs++;
                            if (dist > worstPenetration) worstPenetration = dist;
                        }
                        float cos = Vector3.Dot(dir, up);
                        if (cos < 0.2f) continue; // pushed sideways or down (wall, overhang): lifting would not help
                        float d = dist / cos;
                        if (d > need) need = d;
                    }
                }
                if (need < 0.003f) break;
                lift += need + 0.005f;
                if (lift >= Settings.MaxLift)
                {
                    lift = Settings.MaxLift;
                    break;
                }
            }
            if (lift > 0f) lift = Mathf.Min(lift + Settings.LiftMargin, Settings.MaxLift);
            return lift;
        }

        /// <summary>Pick up to 'max' sample parts: the lowest ones plus the extremes, to watch the terrain under the whole footprint.</summary>
        public static List<Part> PickSampleParts(Vessel v, Vector3 up, int max)
        {
            List<Part> parts = new List<Part>();
            for (int i = 0; i < v.parts.Count; i++) if (v.parts[i] != null) parts.Add(v.parts[i]);
            if (parts.Count <= max) return parts;
            parts.Sort((a, b) => Vector3.Dot(a.transform.position, up).CompareTo(Vector3.Dot(b.transform.position, up)));
            List<Part> result = new List<Part>();
            for (int i = 0; i < parts.Count && result.Count < max / 2; i++) result.Add(parts[i]);
            // spread the rest over the footprint
            Vector3 c = v.CoM;
            parts.Sort((a, b) => (b.transform.position - c).sqrMagnitude.CompareTo((a.transform.position - c).sqrMagnitude));
            for (int i = 0; i < parts.Count && result.Count < max; i++) if (!result.Contains(parts[i])) result.Add(parts[i]);
            return result;
        }
    }
}
