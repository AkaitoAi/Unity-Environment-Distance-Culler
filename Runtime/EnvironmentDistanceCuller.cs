using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EnvironmentCulling
{
    /// <summary>
    /// High-performance, zero-GC environment distance and frustum culler using a local-window
    /// 2D XZ spatial grid, category-based distance tiers, asymmetric distance/frustum hysteresis,
    /// dynamic object tracking, and dual cull modes.
    /// </summary>
    [DisallowMultipleComponent]
    public class EnvironmentDistanceCuller : MonoBehaviour
    {
        private const int MaxWindowProbeRadius = 20;

        [Header("Target Tracking")]
        [Tooltip("Target transform to calculate distances from (usually Player or main Camera).")]
        [SerializeField] private Transform targetTransform;

        [Tooltip("Automatically attempt to find main camera or 'Player'-tagged object when target is unassigned.")]
        [SerializeField] private bool autoFindTarget = true;

        [Header("Update Performance Settings")]
        [Tooltip("Time interval in seconds between culling distance checks (e.g. 0.15s - 0.25s).")]
        [Min(0.02f)]
        [SerializeField] private float checkInterval = 0.2f;

        [Tooltip("Enable 2D Spatial Grid Chunking for high object counts.")]
        [SerializeField] private bool useSpatialGrid = true;

        [Tooltip("Size of spatial grid cells in meters.")]
        [Min(5f)]
        [SerializeField] private float gridCellSize = 60f;

        [Tooltip("When enabled, recurse into pure container GameObjects so each individual child (tree, building, prop) is culled independently.")]
        [SerializeField] private bool cullChildrenIndependently = true;

        [Tooltip("Optional cap on visibility state transitions per check (0 = unlimited). Helps smooth activation spikes on mobile.")]
        [Min(0)]
        [SerializeField] private int maxVisibilityChangesPerCheck = 0;

        [Header("Frustum Culling")]
        [Tooltip("Also cull objects that are outside the camera's view frustum.")]
        [SerializeField] private bool enableFrustumCulling = false;

        [Tooltip("Camera to use for frustum checks. If unassigned, uses Camera.main.")]
        [SerializeField] private Camera frustumCamera;

        [Tooltip("Extra padding in meters added to object bounds before frustum testing, to prevent popping at screen edges.")]
        [Range(0f, 50f)]
        [SerializeField] private float frustumPadding = 10f;

        [Tooltip("Hysteresis buffer in meters added to frustumPadding when hiding, preventing camera-jitter flicker.")]
        [Range(0f, 30f)]
        [SerializeField] private float frustumHysteresis = 4f;

        [Tooltip("When enabled, only culls objects that are behind the camera instead of fully outside the frustum. More forgiving — avoids popping at screen edges.")]
        [SerializeField] private bool onlyCullBehindCamera = true;

        [Header("Distance Categories")]
        [SerializeField] private List<CullCategory> categories = new List<CullCategory>();

        [Header("Debug & Visuals")]
        [SerializeField] private bool drawGizmosInScene = true;
        [SerializeField] private bool showDebugLogs = false;

        private readonly List<CulledObjectData> trackedObjects = new List<CulledObjectData>();
        private readonly List<CulledObjectData> dynamicObjects = new List<CulledObjectData>();
        private readonly Dictionary<GameObject, CulledObjectData> objectLookup = new Dictionary<GameObject, CulledObjectData>();

        // Separate categories inside each XZ cell so small props do not inherit
        // the search radius of buildings or always-visible roads.
        private readonly Dictionary<Vector3Int, SpatialCell> spatialGrid = new Dictionary<Vector3Int, SpatialCell>();
        private readonly List<Vector3Int> activeCells = new List<Vector3Int>();
        private readonly HashSet<Transform> protectedTransforms = new HashSet<Transform>();
        private readonly HashSet<Transform> cachedProtectedHierarchy = new HashSet<Transform>();
        private Func<Transform, bool> protectedPredicate;

        private float indexedCellSize = -1f;
        private bool gridWasUsed;
        private int scannedCategoryCount;
        private int liveTrackedCount;
        private int alwaysVisibleStaticCount;
        private int currentPassId;
        private string[][] cachedLowerKeywords;

        private sealed class SpatialCell
        {
            public readonly List<CulledObjectData> objects = new List<CulledObjectData>();
            public int lastVisitedPass;
            public int activeCount;
            public bool inActiveList;
        }

        private Coroutine cullingCoroutine;
        private readonly Plane[] frustumPlanes = new Plane[6];

        // Cached reusable buffer arrays to keep PerformCullingCheck zero-GC
        private float[] cachedActiveSqrDists;
        private float[] cachedDeactiveSqrDists;
        private int[] cachedCellRadii;
        private bool[] cachedAlwaysVis;
        private bool[] cachedCatFrustum;
        private CullMode[] cachedModes;
        private WaitForSecondsRealtime cachedWaitInstruction;
        private float lastCachedCheckInterval = -1f;

        // Statistics
        public int TotalTrackedCount => liveTrackedCount;
        public int ActiveCount { get; private set; }
        public int CulledCount => Mathf.Max(0, TotalTrackedCount - ActiveCount);
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
                cachedCellRadii = new int[count];
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
            RestoreAllObjects();
        }

        private void Start()
        {
            if (targetTransform == null && autoFindTarget)
            {
                FindTargetFallback();
            }
        }

        /// <summary>
        /// Restores visibility on all tracked objects (e.g., when disabling the culler).
        /// </summary>
        public void RestoreAllObjects()
        {
            for (int i = 0; i < trackedObjects.Count; i++)
            {
                CulledObjectData obj = trackedObjects[i];
                if (obj.gameObject == null) continue;
                obj.isWithinDistance = true;
                obj.isWithinFrustum = true;
                obj.SetVisible(true, cachedModes != null && obj.categoryIndex < cachedModes.Length
                    ? cachedModes[obj.categoryIndex]
                    : CullMode.GameObjectSetActive, allowResetOnCull: false);
            }
            ActiveCount = liveTrackedCount;
        }

        /// <summary>
        /// Sets the transform used as the distance origin for culling.
        /// </summary>
        public void SetTarget(Transform newTarget)
        {
            targetTransform = newTarget;
            RebuildProtectedHierarchyCache();
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
                RebuildProtectedHierarchyCache();
            }
        }

        /// <summary>
        /// Unregisters a previously protected transform.
        /// </summary>
        public void UnregisterProtectedTransform(Transform protectedTarget)
        {
            if (protectedTarget != null && protectedTransforms.Remove(protectedTarget))
            {
                RebuildProtectedHierarchyCache();
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
                RebuildProtectedHierarchyCache();
            }
        }

        /// <summary>
        /// Optional callback for custom protection rules (e.g. active mission objectives).
        /// </summary>
        public void SetProtectedPredicate(Func<Transform, bool> predicate)
        {
            protectedPredicate = predicate;
        }

        /// <summary>
        /// Registers a runtime-spawned GameObject with the culler without re-scanning the entire hierarchy.
        /// </summary>
        public void RegisterObject(GameObject go, bool? forceDynamic = null)
        {
            if (go == null || objectLookup.ContainsKey(go)) return;

            EnsureDefaultCategories();
            if (cachedLowerKeywords == null || cachedLowerKeywords.Length != categories.Count)
                CacheLowerKeywords();

            int catIndex = CategorizeGameObject(go, -1);
            bool isDyn = forceDynamic ?? (go.GetComponentInChildren<Rigidbody>(true) != null);
            CulledObjectData data = new CulledObjectData(go, catIndex, isDyn);

            trackedObjects.Add(data);
            objectLookup[go] = data;
            liveTrackedCount++;
            if (data.isCurrentlyActive) ActiveCount++;

            if (isDyn)
            {
                dynamicObjects.Add(data);
            }
            else if (useSpatialGrid)
            {
                AddObjectToSpatialGrid(data);
            }
        }

        /// <summary>
        /// Unregisters a GameObject from the culler (e.g., before pooling or despawning).
        /// </summary>
        public void UnregisterObject(GameObject go)
        {
            if (go == null || !objectLookup.TryGetValue(go, out CulledObjectData data)) return;

            objectLookup.Remove(go);
            data.gameObject = null;
            if (liveTrackedCount > 0) liveTrackedCount--;
        }

        private void RebuildProtectedHierarchyCache()
        {
            cachedProtectedHierarchy.Clear();
            if (targetTransform != null)
            {
                AddTransformHierarchyToProtected(targetTransform);
            }
            foreach (Transform p in protectedTransforms)
            {
                if (p != null) AddTransformHierarchyToProtected(p);
            }
        }

        private void AddTransformHierarchyToProtected(Transform root)
        {
            Transform cur = root;
            while (cur != null)
            {
                cachedProtectedHierarchy.Add(cur);
                cur = cur.parent;
            }
            AddDescendantsToProtected(root);
        }

        private void AddDescendantsToProtected(Transform parent)
        {
            cachedProtectedHierarchy.Add(parent);
            int count = parent.childCount;
            for (int i = 0; i < count; i++)
            {
                AddDescendantsToProtected(parent.GetChild(i));
            }
        }

        public void FindTargetFallback()
        {
            if (Camera.main != null)
            {
                SetTarget(Camera.main.transform);
                return;
            }

            GameObject playerGo = GameObject.FindGameObjectWithTag("Player");
            if (playerGo != null)
            {
                SetTarget(playerGo.transform);
            }
        }

        public void EnsureDefaultCategories()
        {
            if (categories != null && categories.Count > 0) return;

            categories = new List<CullCategory>
            {
                new CullCategory("Small Props", 100f, 15f, new Color(1f, 0.4f, 0.4f), "prop", "light", "barrier", "sign", "lamp", "fence", "trash", "bench", "post", "barrel", "box", "debris"),
                new CullCategory("Trees & Vegetation", 220f, 20f, new Color(0.4f, 1f, 0.4f), "tree", "bush", "foliage", "grass", "plant", "flower"),
                new CullCategory("Buildings & Structures", 450f, 30f, new Color(0.4f, 0.7f, 1f), "building", "house", "structure", "bridge", "shop", "tower", "hangar", "wall", "ramp"),
                new CullCategory("Landmarks & Roads (Always Active)", 1000f, 50f, new Color(1f, 1f, 0.4f), "road", "terrain", "ground", "zebra", "base")
            };
            categories[0].enableFrustumCulling = false;
            categories[3].alwaysVisible = true;
        }

        [ContextMenu("Scan and Initialize Hierarchy")]
        public void ScanAndInitialize()
        {
            trackedObjects.Clear();
            dynamicObjects.Clear();
            objectLookup.Clear();
            spatialGrid.Clear();
            activeCells.Clear();
            indexedCellSize = -1f;
            gridWasUsed = false;
            ActiveCount = 0;

            EnsureDefaultCategories();
            CacheLowerKeywords();
            RebuildProtectedHierarchyCache();
            scannedCategoryCount = categories.Count;

            int childCount = transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = transform.GetChild(i);
                RegisterObjectRecursive(child.gameObject, -1);
            }

            liveTrackedCount = trackedObjects.Count;
            if (useSpatialGrid) RebuildSpatialGrid();

            if (showDebugLogs)
            {
                Debug.Log($"[EnvironmentDistanceCuller] Scanned {transform.name}. Static: {liveTrackedCount - dynamicObjects.Count}, Dynamic: {dynamicObjects.Count}");
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
            objectLookup[go] = data;
            if (data.isDynamic) dynamicObjects.Add(data);
            if (data.isCurrentlyActive) ActiveCount++;
        }

        private void CacheLowerKeywords()
        {
            cachedLowerKeywords = new string[categories.Count][];
            for (int i = 0; i < categories.Count; i++)
            {
                List<string> kw = categories[i].nameKeywords;
                if (kw == null || kw.Count == 0)
                {
                    cachedLowerKeywords[i] = Array.Empty<string>();
                    continue;
                }

                string[] lower = new string[kw.Count];
                for (int j = 0; j < kw.Count; j++)
                {
                    lower[j] = string.IsNullOrEmpty(kw[j]) ? string.Empty : kw[j].ToLowerInvariant();
                }
                cachedLowerKeywords[i] = lower;
            }
        }

        private int CategorizeGameObject(GameObject go, int parentCategoryIndex = -1)
        {
            string nameLower = go.name.ToLowerInvariant();

            for (int i = 0; i < categories.Count; i++)
            {
                if (IsKeywordMatch(nameLower, cachedLowerKeywords[i]))
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

        private static bool IsKeywordMatch(string text, string[] keywordsLower)
        {
            if (keywordsLower == null) return false;
            for (int i = 0; i < keywordsLower.Length; i++)
            {
                if (keywordsLower[i].Length > 0 && text.Contains(keywordsLower[i])) return true;
            }
            return false;
        }

        private void RebuildSpatialGrid()
        {
            spatialGrid.Clear();
            activeCells.Clear();
            indexedCellSize = Mathf.Max(5f, gridCellSize);

            for (int i = 0; i < trackedObjects.Count; i++)
            {
                CulledObjectData obj = trackedObjects[i];
                if (obj.gameObject == null || obj.isDynamic) continue;
                AddObjectToSpatialGrid(obj);
            }
        }

        private void AddObjectToSpatialGrid(CulledObjectData obj)
        {
            if (indexedCellSize <= 0f) indexedCellSize = Mathf.Max(5f, gridCellSize);
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
            if (obj.isCurrentlyActive)
            {
                cell.activeCount++;
                if (obj.categoryIndex < categories.Count && !categories[obj.categoryIndex].alwaysVisible && !cell.inActiveList)
                {
                    cell.inActiveList = true;
                    activeCells.Add(key);
                }
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
                    cachedWaitInstruction = new WaitForSecondsRealtime(checkInterval);
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
            int remainingTransitionBudget = maxVisibilityChangesPerCheck > 0 ? maxVisibilityChangesPerCheck : int.MaxValue;
            currentPassId++;

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
            float cellSize = Mathf.Max(5f, gridCellSize);
            bool policyChangedOrFirstGrid = !gridWasUsed;
            bool anyWideCategory = false;

            for (int i = 0; i < catCount; i++)
            {
                if (cachedAlwaysVis[i] != categories[i].alwaysVisible || cachedModes[i] != categories[i].cullMode)
                {
                    policyChangedOrFirstGrid = true;
                }

                cachedAlwaysVis[i] = categories[i].alwaysVisible;
                cachedModes[i] = categories[i].cullMode;
                cachedCatFrustum[i] = categories[i].enableFrustumCulling;
                if (cachedAlwaysVis[i])
                {
                    cachedCellRadii[i] = 0;
                    continue;
                }

                float maxD = Mathf.Max(0f, categories[i].maxDistance);
                float hystD = maxD + Mathf.Max(0f, categories[i].hysteresis);

                cachedActiveSqrDists[i] = maxD * maxD;
                cachedDeactiveSqrDists[i] = hystD * hystD;
                int cellR = Mathf.CeilToInt(hystD / cellSize);
                cachedCellRadii[i] = cellR;
                if (cellR > MaxWindowProbeRadius) anyWideCategory = true;
            }

            if (useSpatialGrid)
            {
                if (indexedCellSize != cellSize) RebuildSpatialGrid();

                bool hasCustomProtection = cachedProtectedHierarchy.Count > 0 || protectedPredicate != null;
                bool useFullDictionaryPass = policyChangedOrFirstGrid || protectedPredicate != null || anyWideCategory;

                if (useFullDictionaryPass)
                {
                    alwaysVisibleStaticCount = 0;
                    foreach (var entry in spatialGrid)
                    {
                        EvaluateSpatialCell(
                            entry.Key, entry.Value, targetPos, doFrustum, camForward, camPos,
                            hasCustomProtection, ref remainingTransitionBudget, ref activeCounter);
                        if (cachedAlwaysVis[entry.Key.z])
                        {
                            alwaysVisibleStaticCount += entry.Value.activeCount;
                        }
                    }
                }
                else
                {
                    activeCounter += alwaysVisibleStaticCount;
                    int cx0 = Mathf.FloorToInt(targetPos.x / indexedCellSize);
                    int cz0 = Mathf.FloorToInt(targetPos.z / indexedCellSize);

                    // 1. Probe only the local integer window per culling category (O(LocalCells)).
                    for (int c = 0; c < catCount; c++)
                    {
                        if (cachedAlwaysVis[c]) continue;
                        int r = cachedCellRadii[c];
                        for (int x = cx0 - r; x <= cx0 + r; x++)
                        {
                            for (int z = cz0 - r; z <= cz0 + r; z++)
                            {
                                Vector3Int key = new Vector3Int(x, z, c);
                                if (spatialGrid.TryGetValue(key, out SpatialCell cell) &&
                                    cell.lastVisitedPass != currentPassId)
                                {
                                    EvaluateSpatialCell(
                                        key, cell, targetPos, doFrustum, camForward, camPos,
                                        hasCustomProtection, ref remainingTransitionBudget, ref activeCounter);
                                }
                            }
                        }
                    }

                    // 2. Sweep previously-active cells that exited range this pass.
                    for (int i = activeCells.Count - 1; i >= 0; i--)
                    {
                        Vector3Int key = activeCells[i];
                        if (!spatialGrid.TryGetValue(key, out SpatialCell cell))
                        {
                            int last = activeCells.Count - 1;
                            activeCells[i] = activeCells[last];
                            activeCells.RemoveAt(last);
                            continue;
                        }

                        if (cell.lastVisitedPass == currentPassId) continue;

                        EvaluateSpatialCell(
                            key, cell, targetPos, doFrustum, camForward, camPos,
                            hasCustomProtection, ref remainingTransitionBudget, ref activeCounter);

                        if (cell.activeCount == 0)
                        {
                            cell.inActiveList = false;
                            int last = activeCells.Count - 1;
                            activeCells[i] = activeCells[last];
                            activeCells.RemoveAt(last);
                        }
                    }
                }

                // 3. Evaluate dynamic/moving objects directly from live transform positions.
                for (int i = dynamicObjects.Count - 1; i >= 0; i--)
                {
                    CulledObjectData obj = dynamicObjects[i];
                    if (obj.gameObject == null)
                    {
                        int last = dynamicObjects.Count - 1;
                        dynamicObjects[i] = dynamicObjects[last];
                        dynamicObjects.RemoveAt(last);
                        if (liveTrackedCount > 0) liveTrackedCount--;
                        continue;
                    }

                    obj.RefreshDynamicTransform();
                    LastEvaluatedObjectCount++;
                    bool visible = ApplyObjectVisibility(
                        obj, true, cachedAlwaysVis[obj.categoryIndex], true,
                        targetPos, doFrustum, camForward, camPos, ref remainingTransitionBudget);
                    if (visible) activeCounter++;
                }
            }
            else
            {
                for (int i = trackedObjects.Count - 1; i >= 0; i--)
                {
                    CulledObjectData obj = trackedObjects[i];
                    if (obj.gameObject == null)
                    {
                        int last = trackedObjects.Count - 1;
                        trackedObjects[i] = trackedObjects[last];
                        trackedObjects.RemoveAt(last);
                        if (liveTrackedCount > 0) liveTrackedCount--;
                        continue;
                    }

                    if (obj.isDynamic) obj.RefreshDynamicTransform();
                    LastEvaluatedObjectCount++;
                    bool visible = ApplyObjectVisibility(
                        obj, true, cachedAlwaysVis[obj.categoryIndex], true,
                        targetPos, doFrustum, camForward, camPos, ref remainingTransitionBudget);
                    if (visible) activeCounter++;
                }
            }

            gridWasUsed = useSpatialGrid;
            ActiveCount = activeCounter;
        }

        private void EvaluateSpatialCell(
            Vector3Int key,
            SpatialCell cell,
            Vector3 targetPos,
            bool doFrustum,
            Vector3 camForward,
            Vector3 camPos,
            bool hasCustomProtection,
            ref int remainingTransitionBudget,
            ref int activeCounter)
        {
            cell.lastVisitedPass = currentPassId;
            LastVisitedCellCount++;

            int catIdx = key.z;
            float minX = key.x * indexedCellSize;
            float minZ = key.y * indexedCellSize;
            float dx = Mathf.Max(0f, Mathf.Max(minX - targetPos.x, targetPos.x - (minX + indexedCellSize)));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - targetPos.z, targetPos.z - (minZ + indexedCellSize)));

            bool inRange = !cachedAlwaysVis[catIdx] &&
                dx * dx + dz * dz <= cachedDeactiveSqrDists[catIdx];

            if (!cachedAlwaysVis[catIdx] && !inRange && !cell.inActiveList)
            {
                return;
            }

            cell.activeCount = 0;
            for (int i = cell.objects.Count - 1; i >= 0; i--)
            {
                CulledObjectData obj = cell.objects[i];
                if (obj.gameObject == null)
                {
                    int last = cell.objects.Count - 1;
                    cell.objects[i] = cell.objects[last];
                    cell.objects.RemoveAt(last);
                    if (liveTrackedCount > 0) liveTrackedCount--;
                    continue;
                }

                LastEvaluatedObjectCount++;
                bool visible = ApplyObjectVisibility(
                    obj, inRange, cachedAlwaysVis[catIdx], hasCustomProtection,
                    targetPos, doFrustum, camForward, camPos, ref remainingTransitionBudget);
                if (visible) cell.activeCount++;
            }

            if (!cachedAlwaysVis[catIdx] && cell.activeCount > 0 && !cell.inActiveList)
            {
                cell.inActiveList = true;
                activeCells.Add(key);
            }
            activeCounter += cell.activeCount;
        }

        private bool ApplyObjectVisibility(
            CulledObjectData obj,
            bool inRange,
            bool alwaysVisible,
            bool checkProtection,
            Vector3 targetPos,
            bool doFrustum,
            Vector3 camForward,
            Vector3 camPos,
            ref int remainingTransitionBudget)
        {
            int catIdx = obj.categoryIndex;

            if (alwaysVisible || (checkProtection && IsProtected(obj.gameObject.transform)))
            {
                obj.isWithinDistance = true;
                obj.isWithinFrustum = true;
                if (remainingTransitionBudget > 0 && obj.SetVisible(true, cachedModes[catIdx], allowResetOnCull: false))
                    remainingTransitionBudget--;
                return obj.isCurrentlyActive;
            }

            if (!inRange)
            {
                obj.isWithinDistance = false;
                if (remainingTransitionBudget > 0 && obj.SetVisible(false, cachedModes[catIdx], allowResetOnCull: true))
                    remainingTransitionBudget--;
                return obj.isCurrentlyActive;
            }

            float sqrDist = (obj.cachedWorldPosition - targetPos).sqrMagnitude;
            bool withinDistance = obj.isWithinDistance
                ? sqrDist <= cachedDeactiveSqrDists[catIdx]
                : sqrDist <= cachedActiveSqrDists[catIdx];

            obj.isWithinDistance = withinDistance;

            if (!withinDistance)
            {
                if (remainingTransitionBudget > 0 && obj.SetVisible(false, cachedModes[catIdx], allowResetOnCull: true))
                    remainingTransitionBudget--;
                return obj.isCurrentlyActive;
            }

            if (doFrustum && cachedCatFrustum[catIdx])
            {
                bool inFrustum = IsWithinFrustum(obj, camForward, camPos);
                obj.isWithinFrustum = inFrustum;
                if (!inFrustum)
                {
                    if (remainingTransitionBudget > 0 && obj.SetVisible(false, CullMode.RendererOnly, allowResetOnCull: false))
                        remainingTransitionBudget--;
                    return obj.isCurrentlyActive;
                }
            }
            else
            {
                obj.isWithinFrustum = true;
            }

            if (remainingTransitionBudget > 0 && obj.SetVisible(true, cachedModes[catIdx], allowResetOnCull: false))
                remainingTransitionBudget--;
            return obj.isCurrentlyActive;
        }

        private bool IsProtected(Transform t)
        {
            if (t == null) return false;
            if (cachedProtectedHierarchy.Count > 0 && cachedProtectedHierarchy.Contains(t))
            {
                return true;
            }
            return protectedPredicate != null && protectedPredicate(t);
        }

        private bool IsWithinFrustum(CulledObjectData obj, Vector3 camForward, Vector3 camPos)
        {
            float pad = obj.isWithinFrustum ? (frustumPadding + frustumHysteresis) : frustumPadding;

            if (onlyCullBehindCamera)
            {
                if (obj.hasBounds)
                {
                    Bounds bounds = obj.cachedBounds;
                    float extentsProj = bounds.extents.x * Mathf.Abs(camForward.x) +
                                        bounds.extents.y * Mathf.Abs(camForward.y) +
                                        bounds.extents.z * Mathf.Abs(camForward.z);
                    float centerProj = Vector3.Dot(camForward, bounds.center - camPos);
                    return centerProj + extentsProj >= -pad;
                }

                Vector3 toObject = obj.cachedWorldPosition - camPos;
                return Vector3.Dot(camForward, toObject) >= -pad;
            }

            Bounds testBounds = obj.cachedBounds;
            if (pad > 0f) testBounds.Expand(pad * 2f);
            return GeometryUtility.TestPlanesAABB(frustumPlanes, testBounds);
        }

        private Camera FindFrustumCamera()
        {
            if (frustumCamera != null) return frustumCamera;
            return Camera.main;
        }
    }
}
