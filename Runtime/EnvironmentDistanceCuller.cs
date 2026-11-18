using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EnvironmentCulling
{
    /// <summary>
    /// High-performance, zero-GC environment distance and frustum culler using a 2D XZ spatial grid,
    /// category-based distance tiers, hysteresis bands, and dual cull modes.
    /// </summary>
    [DisallowMultipleComponent]
    public class EnvironmentDistanceCuller : MonoBehaviour
    {
        [Header("Target Tracking")]
        [Tooltip("Target transform to calculate distances from (usually Player or main Camera).")]
        [SerializeField] private Transform targetTransform;

        [Tooltip("Automatically attempt to find main camera or 'Player'-tagged object when target is unassigned.")]
        [SerializeField] private bool autoFindTarget = true;

        [Header("Update Performance Settings")]
        [Tooltip("Time interval in seconds between culling distance checks (e.g. 0.15s - 0.25s).")]
        [SerializeField] private float checkInterval = 0.2f;

        [Tooltip("Enable 2D Spatial Grid Chunking for high object counts.")]
        [SerializeField] private bool useSpatialGrid = true;

        [Tooltip("Size of spatial grid cells in meters.")]
        [SerializeField] private float gridCellSize = 60f;

        [Tooltip("When enabled, recurse into pure container GameObjects so each individual child (tree, building, prop) is culled independently.")]
        [SerializeField] private bool cullChildrenIndependently = true;

        [Header("Frustum Culling")]
        [Tooltip("Also cull objects that are outside the camera's view frustum.")]
        [SerializeField] private bool enableFrustumCulling = false;

        [Tooltip("Camera to use for frustum checks. If unassigned, uses Camera.main.")]
        [SerializeField] private Camera frustumCamera;

        [Tooltip("Extra padding in meters added to object bounds before frustum testing, to prevent popping at screen edges.")]
        [Range(0f, 50f)]
        [SerializeField] private float frustumPadding = 10f;

        [Tooltip("When enabled, only culls objects that are behind the camera instead of fully outside the frustum. More forgiving — avoids popping at screen edges.")]
        [SerializeField] private bool onlyCullBehindCamera = true;

        [Header("Distance Categories")]
        [SerializeField] private List<CullCategory> categories = new List<CullCategory>();

        [Header("Debug & Visuals")]
        [SerializeField] private bool drawGizmosInScene = true;
        [SerializeField] private bool showDebugLogs = false;

        private readonly List<CulledObjectData> trackedObjects = new List<CulledObjectData>();
        // Separate categories inside each XZ cell so small props do not inherit
        // the search radius of buildings or always-visible roads.
        private readonly Dictionary<Vector3Int, SpatialCell> spatialGrid = new Dictionary<Vector3Int, SpatialCell>();
        private readonly HashSet<Transform> protectedTransforms = new HashSet<Transform>();
        private Func<Transform, bool> protectedPredicate;

        private float indexedCellSize = -1f;
        private bool gridWasUsed;
        private int scannedCategoryCount;

        private sealed class SpatialCell
        {
            public readonly List<CulledObjectData> objects = new List<CulledObjectData>();
            public bool needsEvaluation = true;
            public int activeCount;
        }

        private Coroutine cullingCoroutine;
        private readonly Plane[] frustumPlanes = new Plane[6];

        // Cached reusable buffer arrays to keep PerformCullingCheck zero-GC
        private float[] cachedActiveSqrDists;
        private float[] cachedDeactiveSqrDists;
        private bool[] cachedAlwaysVis;
        private bool[] cachedCatFrustum;
        private CullMode[] cachedModes;
        private WaitForSeconds cachedWaitInstruction;
        private float lastCachedCheckInterval = -1f;

        // Statistics
        public int TotalTrackedCount => trackedObjects.Count;
        public int ActiveCount { get; private set; }
        public int CulledCount => TotalTrackedCount - ActiveCount;
        public int LastEvaluatedObjectCount { get; private set; }
        public int LastVisitedCellCount { get; private set; }
        public Transform Target => targetTransform;
        public bool UseSpatialGrid
        {
            get => useSpatialGrid;
            set => useSpatialGrid = value;
        }

        private void EnsureCategoryBuffers(int count)
        {
            if (cachedActiveSqrDists == null || cachedActiveSqrDists.Length < count)
            {
                cachedActiveSqrDists = new float[count];
                cachedDeactiveSqrDists = new float[count];
                cachedAlwaysVis = new bool[count];
                cachedCatFrustum = new bool[count];
                cachedModes = new CullMode[count];
            }
        }

        private void Awake()
        {
            EnsureDefaultCategories();
        }

        private void OnEnable()
        {
            ScanAndInitialize();
            StartCullingLoop();
        }

        private void OnDisable()
        {
            StopCullingLoop();
        }

        private void Start()
        {
            if (targetTransform == null && autoFindTarget)
            {
                FindTargetFallback();
            }
        }

        /// <summary>
        /// Sets the transform used as the distance origin for culling.
        /// </summary>
        public void SetTarget(Transform newTarget)
        {
            targetTransform = newTarget;
        }

        /// <summary>
        /// Sets the camera used for frustum culling checks.
        /// </summary>
        public void SetCamera(Camera newCamera)
        {
            frustumCamera = newCamera;
        }

        /// <summary>
        /// Registers a transform (and its hierarchy) to remain visible regardless of distance/frustum.
        /// </summary>
        public void RegisterProtectedTransform(Transform protectedTarget)
        {
            if (protectedTarget != null && protectedTransforms.Add(protectedTarget))
            {
                MarkAllCellsForEvaluation();
            }
        }

        /// <summary>
        /// Unregisters a previously protected transform.
        /// </summary>
        public void UnregisterProtectedTransform(Transform protectedTarget)
        {
            if (protectedTarget != null && protectedTransforms.Remove(protectedTarget))
            {
                MarkAllCellsForEvaluation();
            }
        }

        /// <summary>
        /// Clears all individually registered protected transforms.
        /// </summary>
        public void ClearProtectedTransforms()
        {
            if (protectedTransforms.Count > 0)
            {
                protectedTransforms.Clear();
                MarkAllCellsForEvaluation();
            }
        }

        /// <summary>
        /// Optional callback for custom protection rules (e.g. active mission objectives).
        /// </summary>
        public void SetProtectedPredicate(Func<Transform, bool> predicate)
        {
            protectedPredicate = predicate;
            MarkAllCellsForEvaluation();
        }

        private void MarkAllCellsForEvaluation()
        {
            foreach (var entry in spatialGrid)
            {
                entry.Value.needsEvaluation = true;
            }
        }

        public void FindTargetFallback()
        {
            if (Camera.main != null)
            {
                targetTransform = Camera.main.transform;
                return;
            }

            GameObject playerGo = GameObject.FindGameObjectWithTag("Player");
            if (playerGo != null)
            {
                targetTransform = playerGo.transform;
            }
        }

        public void EnsureDefaultCategories()
        {
            if (categories != null && categories.Count > 0) return;

            categories = new List<CullCategory>
            {
                new CullCategory("Small Props", 100f, 15f, new Color(1f, 0.4f, 0.4f), "prop", "light", "barrier", "sign", "lamp", "fence", "trash", "bench", "post", "barrel", "box", "debris"),
                new CullCategory("Trees & Vegetation", 220f, 20f, new Color(0.4f, 1f, 0.4f), "tree", "bush", "foliage", "grass", "plant", "lake", "flower"),
                new CullCategory("Buildings & Structures", 450f, 30f, new Color(0.4f, 0.7f, 1f), "building", "house", "stunt", "structure", "bridge", "shop", "tower", "hangar", "wall", "ramp"),
                new CullCategory("Landmarks & Roads (Always Active)", 1000f, 50f, new Color(1f, 1f, 0.4f), "road", "terrain", "ground", "zebra", "base", "env")
            };
            categories[0].enableFrustumCulling = false;
            categories[3].alwaysVisible = true;
        }

        [ContextMenu("Scan and Initialize Hierarchy")]
        public void ScanAndInitialize()
        {
            trackedObjects.Clear();
            spatialGrid.Clear();
            indexedCellSize = -1f;
            gridWasUsed = false;
            ActiveCount = 0;

            EnsureDefaultCategories();
            scannedCategoryCount = categories.Count;

            int childCount = transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = transform.GetChild(i);
                RegisterObjectRecursive(child.gameObject, -1);
            }

            if (useSpatialGrid) RebuildSpatialGrid();

            if (showDebugLogs)
            {
                Debug.Log($"[EnvironmentDistanceCuller] Scanned {transform.name}. Total tracked objects: {trackedObjects.Count}");
            }
        }

        private void RegisterObjectRecursive(GameObject go, int parentCategoryIndex)
        {
            if (go == null) return;

            int catIndex = CategorizeGameObject(go, parentCategoryIndex);
            bool hasChildren = go.transform.childCount > 0;
            bool hasOwnRenderer = go.GetComponent<Renderer>() != null;

            if (cullChildrenIndependently && hasChildren && !hasOwnRenderer)
            {
                int count = go.transform.childCount;
                for (int i = 0; i < count; i++)
                {
                    RegisterObjectRecursive(go.transform.GetChild(i).gameObject, catIndex);
                }
                return;
            }

            CulledObjectData data = new CulledObjectData(go, catIndex);
            trackedObjects.Add(data);

            if (data.isCurrentlyActive) ActiveCount++;
        }

        private int CategorizeGameObject(GameObject go, int parentCategoryIndex = -1)
        {
            string nameLower = go.name.ToLowerInvariant();

            for (int i = 0; i < categories.Count; i++)
            {
                if (IsKeywordMatch(nameLower, categories[i].nameKeywords))
                {
                    return i;
                }
            }

            if (parentCategoryIndex >= 0 && parentCategoryIndex < categories.Count)
            {
                return parentCategoryIndex;
            }

            return categories.Count > 2 ? 2 : 0;
        }

        private static bool IsKeywordMatch(string text, List<string> keywords)
        {
            if (keywords == null) return false;
            for (int i = 0; i < keywords.Count; i++)
            {
                if (text.Contains(keywords[i].ToLowerInvariant())) return true;
            }
            return false;
        }

        private void RebuildSpatialGrid()
        {
            spatialGrid.Clear();
            indexedCellSize = Mathf.Max(1f, gridCellSize);
            for (int i = 0; i < trackedObjects.Count; i++)
            {
                CulledObjectData obj = trackedObjects[i];
                if (obj.gameObject == null) continue;
                Vector3 pos = obj.cachedWorldPosition;
                Vector3Int key = new Vector3Int(
                    Mathf.FloorToInt(pos.x / indexedCellSize),
                    Mathf.FloorToInt(pos.z / indexedCellSize),
                    obj.categoryIndex);

                if (!spatialGrid.TryGetValue(key, out SpatialCell cell))
                {
                    cell = new SpatialCell();
                    spatialGrid.Add(key, cell);
                }
                cell.objects.Add(obj);
            }
        }

        private void StartCullingLoop()
        {
            if (cullingCoroutine != null) StopCoroutine(cullingCoroutine);
            cullingCoroutine = StartCoroutine(CullingRoutine());
        }

        private void StopCullingLoop()
        {
            if (cullingCoroutine != null)
            {
                StopCoroutine(cullingCoroutine);
                cullingCoroutine = null;
            }
        }

        private IEnumerator CullingRoutine()
        {
            while (true)
            {
                if (cachedWaitInstruction == null || !Mathf.Approximately(lastCachedCheckInterval, checkInterval))
                {
                    lastCachedCheckInterval = checkInterval;
                    cachedWaitInstruction = new WaitForSeconds(checkInterval);
                }

                PerformCullingCheck();
                yield return cachedWaitInstruction;
            }
        }

        public void PerformCullingCheck()
        {
            LastEvaluatedObjectCount = 0;
            LastVisitedCellCount = 0;
            EnsureDefaultCategories();
            if (scannedCategoryCount != categories.Count) ScanAndInitialize();
            if (targetTransform == null)
            {
                if (autoFindTarget) FindTargetFallback();
                if (targetTransform == null) return;
            }

            Vector3 targetPos = targetTransform.position;
            int activeCounter = 0;

            bool doFrustum = enableFrustumCulling;
            Camera cam = null;
            Vector3 camForward = Vector3.forward;
            Vector3 camPos = targetPos;

            if (doFrustum)
            {
                cam = FindFrustumCamera();
                if (cam != null)
                {
                    if (!onlyCullBehindCamera)
                        GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);
                    camForward = cam.transform.forward;
                    camPos = cam.transform.position;
                }
                else
                {
                    doFrustum = false;
                }
            }

            int catCount = categories.Count;
            EnsureCategoryBuffers(catCount);

            for (int i = 0; i < catCount; i++)
            {
                // Far cells must be revisited when their visibility policy changes.
                if (cachedAlwaysVis[i] != categories[i].alwaysVisible || cachedModes[i] != categories[i].cullMode)
                {
                    foreach (var entry in spatialGrid)
                        if (entry.Key.z == i) entry.Value.needsEvaluation = true;
                }

                cachedAlwaysVis[i] = categories[i].alwaysVisible;
                cachedModes[i] = categories[i].cullMode;
                cachedCatFrustum[i] = categories[i].enableFrustumCulling;
                if (cachedAlwaysVis[i]) continue;

                float maxD = Mathf.Max(0f, categories[i].maxDistance);
                float hystD = maxD + Mathf.Max(0f, categories[i].hysteresis);

                cachedActiveSqrDists[i] = maxD * maxD;
                cachedDeactiveSqrDists[i] = hystD * hystD;
            }

            if (useSpatialGrid)
            {
                if (indexedCellSize != Mathf.Max(1f, gridCellSize)) RebuildSpatialGrid();
                bool hasCustomProtection = protectedTransforms.Count > 0 || protectedPredicate != null ||
                    (targetTransform != null && targetTransform.IsChildOf(transform));

                foreach (var entry in spatialGrid)
                {
                    LastVisitedCellCount++;
                    SpatialCell cell = entry.Value;
                    int catIdx = entry.Key.z;
                    float minX = entry.Key.x * indexedCellSize;
                    float minZ = entry.Key.y * indexedCellSize;
                    float dx = Mathf.Max(0f, Mathf.Max(minX - targetPos.x, targetPos.x - (minX + indexedCellSize)));
                    float dz = Mathf.Max(0f, Mathf.Max(minZ - targetPos.z, targetPos.z - (minZ + indexedCellSize)));

                    // Conservative XZ broad phase: exact 3D distance and frustum
                    // checks still run for candidates, including the hysteresis band.
                    bool inRange = !cachedAlwaysVis[catIdx] &&
                        dx * dx + dz * dz <= cachedDeactiveSqrDists[catIdx];

                    if (inRange || cell.needsEvaluation || !gridWasUsed)
                    {
                        cell.activeCount = 0;
                        for (int i = 0; i < cell.objects.Count; i++)
                        {
                            CulledObjectData obj = cell.objects[i];
                            if (obj.gameObject == null) continue;
                            LastEvaluatedObjectCount++;
                            bool visible = cachedAlwaysVis[catIdx] ||
                                (inRange
                                    ? EvaluateVisibility(obj, targetPos, doFrustum, camForward, camPos)
                                    : (hasCustomProtection && IsProtected(obj.gameObject.transform)));
                            obj.SetVisible(visible, cachedModes[catIdx]);
                            if (visible) cell.activeCount++;
                        }
                    }

                    // Revisit an exited cell once to hide its remaining objects.
                    cell.needsEvaluation = inRange;
                    activeCounter += cell.activeCount;
                }
            }
            else
            {
                for (int i = 0; i < trackedObjects.Count; i++)
                {
                    CulledObjectData obj = trackedObjects[i];
                    if (obj.gameObject == null) continue;
                    LastEvaluatedObjectCount++;
                    bool visible = EvaluateVisibility(obj, targetPos, doFrustum, camForward, camPos);
                    obj.SetVisible(visible, cachedModes[obj.categoryIndex]);
                    if (visible) activeCounter++;
                }
            }

            gridWasUsed = useSpatialGrid;
            ActiveCount = activeCounter;
        }

        private bool IsProtected(Transform t)
        {
            if (t == null) return false;

            if (targetTransform != null &&
                (t == targetTransform || t.IsChildOf(targetTransform) || targetTransform.IsChildOf(t)))
            {
                return true;
            }

            if (protectedTransforms.Count > 0 && protectedTransforms.Contains(t))
            {
                return true;
            }

            return protectedPredicate != null && protectedPredicate(t);
        }

        private bool EvaluateVisibility(CulledObjectData obj, Vector3 targetPos, bool doFrustum,
            Vector3 camForward, Vector3 camPos)
        {
            if (obj.gameObject != null && IsProtected(obj.gameObject.transform))
            {
                return true;
            }

            int catIdx = obj.categoryIndex;
            if (cachedAlwaysVis[catIdx]) return true;

            float sqrDist = (obj.cachedWorldPosition - targetPos).sqrMagnitude;
            bool withinDistance = obj.isCurrentlyActive
                ? sqrDist <= cachedDeactiveSqrDists[catIdx]
                : sqrDist <= cachedActiveSqrDists[catIdx];

            bool shouldBeActive = withinDistance;

            if (shouldBeActive && doFrustum && cachedCatFrustum[catIdx])
            {
                if (onlyCullBehindCamera)
                {
                    if (obj.hasBounds)
                    {
                        Bounds bounds = obj.cachedBounds;
                        float extentsProj = bounds.extents.x * Mathf.Abs(camForward.x) +
                                            bounds.extents.y * Mathf.Abs(camForward.y) +
                                            bounds.extents.z * Mathf.Abs(camForward.z);
                        float centerProj = Vector3.Dot(camForward, bounds.center - camPos);

                        if (centerProj + extentsProj < -frustumPadding)
                        {
                            shouldBeActive = false;
                        }
                    }
                    else
                    {
                        Vector3 toObject = obj.cachedWorldPosition - camPos;
                        if (Vector3.Dot(camForward, toObject) < -frustumPadding)
                        {
                            shouldBeActive = false;
                        }
                    }
                }
                else
                {
                    Bounds testBounds = obj.cachedBounds;
                    if (frustumPadding > 0f) testBounds.Expand(frustumPadding * 2f);
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, testBounds))
                    {
                        shouldBeActive = false;
                    }
                }
            }

            return shouldBeActive;
        }

        private Camera FindFrustumCamera()
        {
            if (frustumCamera != null) return frustumCamera;
            return Camera.main;
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmosInScene || categories == null) return;

            Vector3 center = targetTransform != null ? targetTransform.position : transform.position;
            for (int i = 0; i < categories.Count; i++)
            {
                CullCategory cat = categories[i];
                if (cat.alwaysVisible) continue;

                Gizmos.color = cat.gizmoColor;
                Gizmos.DrawWireSphere(center, cat.maxDistance);
            }
        }
    }
}
