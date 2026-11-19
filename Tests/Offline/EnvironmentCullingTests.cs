using System;
using System.Collections.Generic;
using System.Diagnostics;
using EnvironmentCulling;

namespace UnityEngine
{
    public class SerializeField : Attribute { }
    public class DisallowMultipleComponent : Attribute { }
    public class HeaderAttribute : Attribute { public HeaderAttribute(string s) { } }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string s) { } }
    public class RangeAttribute : Attribute { public RangeAttribute(float a, float b) { } }
    public class ContextMenu : Attribute { public ContextMenu(string s) { } }
    public class Coroutine { }
    public class WaitForSeconds { public WaitForSeconds(float s) { } }

    public class Object
    {
        public string name = "test";
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject != null ? gameObject.transform : null;
        public T GetComponent<T>() where T : class => gameObject != null ? gameObject.GetComponent<T>() : null;
    }

    public class MonoBehaviour : Component
    {
        public new Transform transform = new Transform();
        protected Coroutine StartCoroutine(System.Collections.IEnumerator e) => null;
        protected void StopCoroutine(Coroutine c) { }
    }

    public class Transform : Component
    {
        public Vector3 position;
        public Vector3 forward = Vector3.forward;
        public Transform parent;
        public readonly List<Transform> children = new List<Transform>();
        public int childCount => children.Count;
        public Transform GetChild(int i) => children[i];

        public bool IsChildOf(Transform other)
        {
            Transform cur = this;
            while (cur != null)
            {
                if (cur == other) return true;
                cur = cur.parent;
            }
            return false;
        }

        public Vector3 TransformPoint(Vector3 p) => position + p;
    }

    public class GameObject : Object
    {
        public Transform transform = new Transform();
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf;
        private readonly List<Component> components = new List<Component>();

        public GameObject()
        {
            transform.gameObject = this;
        }

        public GameObject(string name) : this()
        {
            this.name = name;
            transform.name = name;
        }

        public void SetActive(bool active) => activeSelf = active;

        public void AddComponentInstance(Component c)
        {
            c.gameObject = this;
            components.Add(c);
        }

        public T GetComponent<T>() where T : class
        {
            for (int i = 0; i < components.Count; i++)
                if (components[i] is T match) return match;
            return null;
        }

        public T GetComponentInParent<T>() where T : class
        {
            Transform cur = transform;
            while (cur != null)
            {
                if (cur.gameObject != null)
                {
                    T found = cur.gameObject.GetComponent<T>();
                    if (found != null) return found;
                }
                cur = cur.parent;
            }
            return null;
        }

        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : class
        {
            var list = new List<T>();
            CollectInChildren(transform, list);
            return list.ToArray();
        }

        private static void CollectInChildren<T>(Transform t, List<T> list) where T : class
        {
            if (t.gameObject != null)
            {
                for (int i = 0; i < t.gameObject.components.Count; i++)
                    if (t.gameObject.components[i] is T match) list.Add(match);
            }
            for (int i = 0; i < t.children.Count; i++)
                CollectInChildren(t.children[i], list);
        }

        public static GameObject FindGameObjectWithTag(string t) => null;
    }

    public class Renderer : Component
    {
        public bool enabled = true;
        public Bounds bounds;
    }

    public class MeshRenderer : Renderer { }
    public class SkinnedMeshRenderer : Renderer { public Bounds localBounds; }
    public class SpriteRenderer : Renderer { public Sprite sprite; }
    public class MeshFilter : Component { public Mesh sharedMesh; }
    public class Mesh { public Bounds bounds; }
    public class Sprite { public Bounds bounds; }

    public class Camera : Component
    {
        public static Camera main;
    }

    public struct Plane { }

    public struct Bounds
    {
        public Vector3 center;
        public Vector3 extents;

        public Bounds(Vector3 center, Vector3 size)
        {
            this.center = center;
            this.extents = size * 0.5f;
        }

        public void Encapsulate(Vector3 point)
        {
            Vector3 min = center - extents;
            Vector3 max = center + extents;
            min.x = Math.Min(min.x, point.x);
            min.y = Math.Min(min.y, point.y);
            min.z = Math.Min(min.z, point.z);
            max.x = Math.Max(max.x, point.x);
            max.y = Math.Max(max.y, point.y);
            max.z = Math.Max(max.z, point.z);
            center = (min + max) * 0.5f;
            extents = (max - min) * 0.5f;
        }

        public void Encapsulate(Bounds b)
        {
            Encapsulate(b.center - b.extents);
            Encapsulate(b.center + b.extents);
        }

        public void Expand(float padding) => extents += Vector3.one * (padding * 0.5f);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 forward => new Vector3(0, 0, 1);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 zero => new Vector3(0, 0, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    }

    public struct Vector3Int : IEquatable<Vector3Int>
    {
        public int x, y, z;
        public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(Vector3Int b) => x == b.x && y == b.y && z == b.z;
        public override bool Equals(object b) => b is Vector3Int v && Equals(v);
        public override int GetHashCode() => unchecked(x * 73856093 ^ y * 19349663 ^ z * 83492791);
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; a = 1f; }
        public static Color green => new Color(0, 1, 0);
    }

    public static class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Abs(float a) => Math.Abs(a);
        public static int FloorToInt(float a) => (int)Math.Floor(a);
        public static bool Approximately(float a, float b) => Math.Abs(a - b) < 0.00001f;
    }

    public static class GeometryUtility
    {
        public static void CalculateFrustumPlanes(Camera c, Plane[] p) { }
        public static bool TestPlanesAABB(Plane[] p, Bounds b) => b.center.z + b.extents.z >= 0f;
    }

    public static class Debug
    {
        public static void Log(string s) { }
        public static void LogWarning(string s) { }
    }

    public static class Gizmos
    {
        public static Color color;
        public static void DrawWireSphere(Vector3 p, float d) { }
    }
}

public class MockResettable : UnityEngine.Component, ICullResettable
{
    public bool IsBroken { get; set; }
    public int ResetCount { get; private set; }
    public void ResetOnCull()
    {
        IsBroken = false;
        ResetCount++;
    }
}

public static class CullingTestAndBenchmarkRunner
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("ASSERTION FAILED: " + message);
    }

    private static UnityEngine.GameObject CreateProp(UnityEngine.Transform root, string name, float x, float y, float z)
    {
        var go = new UnityEngine.GameObject(name);
        go.transform.position = new UnityEngine.Vector3(x, y, z);
        go.transform.parent = root;
        root.children.Add(go.transform);
        var mr = new UnityEngine.MeshRenderer();
        mr.bounds = new UnityEngine.Bounds(go.transform.position, UnityEngine.Vector3.one * 2f);
        go.AddComponentInstance(mr);
        return go;
    }

    public static void Run()
    {
        RunRegressionTests();
        RunBenchmarks();
    }

    private static void RunRegressionTests()
    {
        var rootGo = new UnityEngine.GameObject("Env");
        var culler = new EnvironmentDistanceCuller();
        culler.gameObject = rootGo;
        culler.transform = rootGo.transform;

        var target = new UnityEngine.GameObject("Player");
        culler.SetTarget(target.transform);

        var nearProp = CreateProp(rootGo.transform, "prop_barrel_near", 10f, 0f, 0f);
        var hystProp = CreateProp(rootGo.transform, "prop_sign_hyst", 108f, 0f, 0f); // within 100 + 15 hysteresis
        var farProp = CreateProp(rootGo.transform, "prop_fence_far", 140f, 0f, 0f);
        var roadObj = CreateProp(rootGo.transform, "road_main", 5000f, 0f, 0f);
        var protectedProp = CreateProp(rootGo.transform, "prop_objective", 900f, 0f, 0f);

        var resettable = new MockResettable { IsBroken = true };
        farProp.AddComponentInstance(resettable);
        culler.RegisterProtectedTransform(protectedProp.transform);

        culler.ScanAndInitialize();
        Assert(culler.TotalTrackedCount == 5, "Expected 5 tracked objects.");

        culler.PerformCullingCheck();
        Assert(nearProp.activeSelf, "Near prop should stay active.");
        Assert(hystProp.activeSelf, "Prop inside hysteresis band (108m <= 115m) should stay active when already active.");
        Assert(!farProp.activeSelf, "Far prop (140m) should be culled.");
        Assert(resettable.ResetCount == 1 && !resettable.IsBroken, "Broken ICullResettable should reset when culled.");
        Assert(roadObj.activeSelf, "AlwaysVisible road should never be culled.");
        Assert(protectedProp.activeSelf, "Protected transform should never be culled.");

        // Move farProp into hysteresis band while inactive -> should stay inactive until <= 100m
        // (verified by moving target to 32m: distance to 140m is 108m, inside hysteresis but > maxDistance 100m)
        target.transform.position = new UnityEngine.Vector3(32f, 0f, 0f);
        culler.PerformCullingCheck();
        Assert(!farProp.activeSelf, "Inactive prop at 108m (>100m maxDistance) must not pop back on in hysteresis band.");

        // Move target closer so distance <= 100m
        target.transform.position = new UnityEngine.Vector3(50f, 0f, 0f);
        culler.PerformCullingCheck();
        Assert(farProp.activeSelf, "Prop at 90m (<=100m maxDistance) should reactivate.");

        // Spatial grid vs Linear parity check
        culler.UseSpatialGrid = false;
        culler.PerformCullingCheck();
        int linearActive = culler.ActiveCount;
        culler.UseSpatialGrid = true;
        culler.PerformCullingCheck();
        Assert(culler.ActiveCount == linearActive, "Spatial grid and linear scan must yield identical ActiveCount.");

        Console.WriteLine("[PASS] All regression tests passed (6/6 assertions groups).");
    }

    private static void RunBenchmarks()
    {
        Console.WriteLine("\n--- EnvironmentDistanceCuller Offline Benchmarks ---");
        Console.WriteLine("Config: 2000m x 2000m world, 4 default categories, 60m spatial cell size, 100 iterations per run.");
        int[] counts = { 1000, 5000, 10000, 25000 };

        foreach (int count in counts)
        {
            var rootGo = new UnityEngine.GameObject("Env");
            var culler = new EnvironmentDistanceCuller();
            culler.gameObject = rootGo;
            culler.transform = rootGo.transform;
            var target = new UnityEngine.GameObject("Player");
            culler.SetTarget(target.transform);

            var rng = new Random(42);
            for (int i = 0; i < count; i++)
            {
                string prefix = (i % 4 == 0) ? "prop_" : (i % 4 == 1) ? "tree_" : (i % 4 == 2) ? "building_" : "road_";
                float x = (float)(rng.NextDouble() * 2000.0 - 1000.0);
                float z = (float)(rng.NextDouble() * 2000.0 - 1000.0);
                CreateProp(rootGo.transform, prefix + i, x, 0f, z);
            }

            culler.ScanAndInitialize();

            // Warmup
            culler.UseSpatialGrid = true;
            culler.PerformCullingCheck();

            // Measure Spatial Grid
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memBeforeGrid = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            for (int iter = 0; iter < 100; iter++)
            {
                target.transform.position = new UnityEngine.Vector3((iter % 10) * 15f, 0f, (iter / 10) * 15f);
                culler.PerformCullingCheck();
            }
            sw.Stop();
            long memAfterGrid = GC.GetTotalMemory(false);
            double gridMs = sw.Elapsed.TotalMilliseconds / 100.0;
            int gridEvaluated = culler.LastEvaluatedObjectCount;
            int activeAfter = culler.ActiveCount;
            double culledPct = 100.0 * (count - activeAfter) / count;
            long gridAllocBytes = Math.Max(0, (memAfterGrid - memBeforeGrid) / 100);

            // Measure Linear Scan
            culler.UseSpatialGrid = false;
            culler.PerformCullingCheck();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memBeforeLin = GC.GetTotalMemory(false);
            sw.Restart();
            for (int iter = 0; iter < 100; iter++)
            {
                target.transform.position = new UnityEngine.Vector3((iter % 10) * 15f, 0f, (iter / 10) * 15f);
                culler.PerformCullingCheck();
            }
            sw.Stop();
            long memAfterLin = GC.GetTotalMemory(false);
            double linMs = sw.Elapsed.TotalMilliseconds / 100.0;
            int linEvaluated = culler.LastEvaluatedObjectCount;

            double speedup = linMs / Math.Max(0.0001, gridMs);
            Console.WriteLine(
                $"Objects: {count,5} | Active: {activeAfter,5} ({culledPct:F1}% culled) | Linear: {linMs:F4} ms ({linEvaluated,5} evals) | SpatialGrid: {gridMs:F4} ms ({gridEvaluated,4} evals) | Speedup: {speedup:F2}x | Alloc/check: {gridAllocBytes} B");
        }
    }
}
