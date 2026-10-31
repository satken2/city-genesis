using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Moving parts carried inside part models, collected while assembling one building (driven by <see cref="BuildingMotion"/>).
/// - Rotor: a child object whose name starts with `rotor`. Left out of the combined mesh and spun about the axis through its origin
///   along Blender +X (the nose side; local -X after import).
/// - Smoke outlet: an empty whose name starts with `smoke`. Its position is the outlet centre and its uniform scale the outlet radius.
///
/// Parts are separated only while <see cref="Sink"/> is set (generation of buildings shown in the city). Otherwise, e.g. previews,
/// rotors stay merged into the building mesh and outlets are ignored.
/// Assembly reports each part through the same transform it applies to that part's vertices. When stacking repeats a part,
/// the same marker at the same XZ keeps only its highest copy, so a chimney built from stacked segments emits once at the top.
/// </summary>
public sealed class BuildingMotionParts
{
    private const string ROTOR_PREFIX = "rotor";
    private const string OUTLET_PREFIX = "smoke";
    private const float SAME_XZ = 0.01f;

    public readonly struct Rotor
    {
        /// <summary>Rotor node inside the shared part prefab. Never modify or destroy it.</summary>
        public readonly GameObject Node;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public Rotor(GameObject node, Vector3 position, Quaternion rotation)
        {
            Node = node;
            Position = position;
            Rotation = rotation;
        }
    }

    public readonly struct Outlet
    {
        public readonly Vector3 Position;
        public readonly float Radius;

        public Outlet(Vector3 position, float radius)
        {
            Position = position;
            Radius = radius;
        }
    }

    public static BuildingMotionParts Sink { get; set; }

    public readonly List<Rotor> Rotors = new List<Rotor>();
    public readonly List<Outlet> Outlets = new List<Outlet>();
    private readonly List<Transform> rotorSources = new List<Transform>();
    private readonly List<Transform> outletSources = new List<Transform>();

    public bool IsEmpty => Rotors.Count == 0 && Outlets.Count == 0;

    /// <summary>Whether part assembly must leave this mesh out of the combined mesh (it belongs to a rotor being separated).</summary>
    public static bool IsSeparated(Transform node) => Sink != null && RotorRoot(node) != null;

    /// <summary>Extend bounds in prefab-root space by the rotors left out of the combined mesh.</summary>
    public static void EncapsulateSeparated(GameObject prefab, ref Bounds bounds)
    {
        if (Sink == null || prefab == null) return;
        Matrix4x4 rootInverse = prefab.transform.worldToLocalMatrix;
        foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null || RotorRoot(mf.transform) == null) continue;
            Matrix4x4 toRoot = rootInverse * mf.transform.localToWorldMatrix;
            Bounds local = mf.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 p = new Vector3((corner & 1) == 0 ? local.min.x : local.max.x,
                    (corner & 2) == 0 ? local.min.y : local.max.y,
                    (corner & 4) == 0 ? local.min.z : local.max.z);
                bounds.Encapsulate(toRoot.MultiplyPoint3x4(p));
            }
        }
    }

    /// <summary>
    /// Record the rotors and outlets of one placed part.
    /// frame is the space of the part's vertices before placement (the prefab root for stacked parts, the mesh node for roofprops);
    /// place / placeRotation is the placement applied to those vertices.
    /// </summary>
    public static void Collect(GameObject prefab, Transform frame, Matrix4x4 place, Quaternion placeRotation)
    {
        BuildingMotionParts sink = Sink;
        if (sink == null || prefab == null || frame == null) return;
        Matrix4x4 toBuilding = place * frame.worldToLocalMatrix;
        Quaternion frameInverse = Quaternion.Inverse(frame.rotation);
        foreach (Transform node in prefab.GetComponentsInChildren<Transform>(true))
        {
            Vector3 position = toBuilding.MultiplyPoint3x4(node.position);
            if (RotorRoot(node) == node)
            {
                var rotor = new Rotor(node.gameObject, position, placeRotation * (frameInverse * node.rotation));
                Put(sink.rotorSources, sink.Rotors, node, rotor, r => r.Position);
            }
            else if (node.name.StartsWith(OUTLET_PREFIX, StringComparison.Ordinal) && node.GetComponent<MeshFilter>() == null)
            {
                var outlet = new Outlet(position, Mathf.Abs(node.lossyScale.x / frame.lossyScale.x));
                Put(sink.outletSources, sink.Outlets, node, outlet, o => o.Position);
            }
        }
    }

    private static void Put<T>(List<Transform> sources, List<T> items, Transform source, T item, Func<T, Vector3> positionOf)
    {
        Vector3 position = positionOf(item);
        for (int i = 0; i < sources.Count; i++)
        {
            Vector3 existing = positionOf(items[i]);
            if (sources[i] != source || Mathf.Abs(existing.x - position.x) > SAME_XZ || Mathf.Abs(existing.z - position.z) > SAME_XZ) continue;
            if (position.y > existing.y) items[i] = item;
            return;
        }
        sources.Add(source);
        items.Add(item);
    }

    /// <summary>Outermost ancestor (or the node itself) named as a rotor, or null.</summary>
    private static Transform RotorRoot(Transform node)
    {
        Transform root = null;
        for (Transform t = node; t != null; t = t.parent)
            if (t.name.StartsWith(ROTOR_PREFIX, StringComparison.Ordinal)) root = t;
        return root;
    }
}
