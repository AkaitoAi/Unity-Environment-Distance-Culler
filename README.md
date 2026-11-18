# Environment Distance Culler for Unity

A lightweight, zero-GC, spatial-grid **distance and frustum culling system** for Unity environments. Designed for open-world, racing, and mobile games with thousands of props, trees, buildings, and landmarks.

---

## Features

- **2D XZ Spatial Grid Broad-Phase:** Partitions objects into `(cellX, cellZ, categoryIndex)` cells so out-of-range cells are skipped in a single check without iterating their objects.
- **Category-Based Distance Tiers:** Auto-classifies scene children by GameObject name keywords into configurable distance tiers (e.g., Small Props, Trees & Vegetation, Buildings & Structures, Always-Active Roads/Landmarks).
- **Hysteresis Anti-Flicker Band:** Separate activation (`maxDistance`) and deactivation (`maxDistance + hysteresis`) thresholds prevent objects from popping on/off at range boundaries.
- **Dual Cull Modes:**
  - `GameObjectSetActive` — Disables the entire `GameObject` (graphics, colliders, scripts) for maximum CPU/GPU savings.
  - `RendererOnly` — Disables only `Renderer` components while keeping physics colliders and scripts active.
- **Optional Camera Frustum Culling:** Supports fast *Behind-Camera Plane* culling (with configurable `frustumPadding`) or full 6-plane AABB frustum culling.
- **Protected Transforms & Resettable Props:** Keep mission objectives or player-attached objects always visible via `RegisterProtectedTransform` / `SetProtectedPredicate`, and automatically reset broken destructible props when culled via `ICullResettable`.
- **Custom Inspector & Scene View Gizmos:** Live play-mode stats (`Active`, `Culled`, `Evaluated Last Check`, `Cells Visited`) and color-coded distance rings in the Scene View.
- **Optional Gley Traffic Adapter:** Included in `Optional/GleyTraffic/` (guarded by `#if GLEY_TRAFFIC_SYSTEM`).

---

## Installation in a New Unity Project

### Option A: Copy into `Assets/` (Simplest)
1. Copy the `Runtime/` and `Editor/` folders into your Unity project under `Assets/EnvironmentDistanceCuller/`.
2. *(Optional)* If you use **Gley Traffic System**, also copy `Optional/GleyTraffic/` and ensure `GLEY_TRAFFIC_SYSTEM` is defined in **Project Settings → Player → Scripting Define Symbols**.

### Option B: Unity Package Manager (Git URL)
1. Open **Window → Package Manager**.
2. Click **+** → **Add package from git URL...**
3. Paste your repository URL (e.g., `https://github.com/<your-username>/Unity-Environment-Distance-Culler.git`).

---

## Quick Start Guide

1. **Organize your environment** under a single root `GameObject` (e.g., `Env`).
2. **Attach the culler** using either:
   - Menu bar: **Tools → Environment Culler → Attach Distance Culler to 'Env'**, or
   - Manually add the `EnvironmentDistanceCuller` component to your environment root `GameObject`.
3. **Click `Scan & Categorize Hierarchy Now`** in the Inspector (or let it auto-scan on `OnEnable`).
4. **Assign `Target Transform`** (usually your Player or Main Camera). If left empty and `Auto Find Target` is enabled, it automatically falls back to `Camera.main` or the `"Player"` tag.
5. **Press Play** and watch the live **Optimization Statistics** in the Inspector.

---

## Default Categories

| Category | Max Distance | Hysteresis | Frustum Culling | Keywords |
| :--- | :---: | :---: | :---: | :--- |
| **Small Props** | 100 m | 15 m | Off | `prop`, `light`, `barrier`, `sign`, `lamp`, `fence`, `trash`, `bench`, `post`, `barrel`, `box`, `debris` |
| **Trees & Vegetation** | 220 m | 20 m | On | `tree`, `bush`, `foliage`, `grass`, `plant`, `lake`, `flower` |
| **Buildings & Structures** | 450 m | 30 m | On | `building`, `house`, `stunt`, `structure`, `bridge`, `shop`, `tower`, `hangar`, `wall`, `ramp` |
| **Landmarks & Roads** | 1000 m | 50 m | On (`alwaysVisible`) | `road`, `terrain`, `ground`, `zebra`, `base`, `env` |

> **Tip:** Pure container `GameObject`s (with children and no `Renderer` of their own) are recursively traversed when `Cull Children Independently` is enabled, and unclassified children inherit their parent container's category.

---

## Public API & Integration

### Dynamic Target / Camera Switching
```csharp
using EnvironmentCulling;
using UnityEngine;

public class CullerBootstrap : MonoBehaviour
{
    [SerializeField] private EnvironmentDistanceCuller culler;

    public void OnPlayerSpawned(Transform playerRoot, Camera activeCam)
    {
        culler.SetTarget(playerRoot);
        culler.SetCamera(activeCam);
    }
}
```

### Protecting Specific Objects (e.g. Mission Objectives)
```csharp
// Keep a specific transform visible regardless of distance/frustum
culler.RegisterProtectedTransform(objectiveTransform);
culler.UnregisterProtectedTransform(objectiveTransform);

// Or supply a custom predicate
culler.SetProtectedPredicate(t => t.CompareTag("MissionCritical"));
```

### Resetting Destructible Props on Cull
Implement `ICullResettable` on any prop component:
```csharp
using EnvironmentCulling;
using UnityEngine;

public class DestructibleProp : MonoBehaviour, ICullResettable
{
    public bool IsBroken { get; private set; }

    public void ResetOnCull()
    {
        IsBroken = false;
        // Restore unbroken mesh/physics state
    }
}
```

---

## Benchmarks & Performance Improvements

### Architectural Optimizations
1. **Per-Category 2D XZ Spatial Grid (`Vector3Int(cellX, cellZ, categoryIndex)`):**
   Small props (`100 m`) and buildings (`450 m`) are indexed into separate category cells. Out-of-range cells are skipped with a single squared-distance AABB check, and exited cells are revisited **once** (`cell.needsEvaluation`) to hide their remaining objects.
2. **Zero-GC Culling Loop:**
   - Category thresholds (`cachedActiveSqrDists`, `cachedDeactiveSqrDists`, `cachedAlwaysVis`, `cachedCatFrustum`, `cachedModes`) are pre-allocated in reusable arrays.
   - `WaitForSeconds` is cached across coroutine iterations.
   - Squared distance comparisons (`sqrMagnitude`) eliminate `Mathf.Sqrt` calls.
   - `Bounds` are pre-computed once during `ScanAndInitialize()` (even for inactive children via local mesh/sprite bounds).
3. **Coroutine Interval (`checkInterval = 0.2f`):**
   Runs 5× per second instead of every frame (`Update()`), reducing per-frame CPU overhead by ~90%+ while remaining imperceptible with hysteresis.

### Measured Offline Benchmark Results
Measured using the included Roslyn offline benchmark harness (`Tests/Offline/run-tests.ps1`) in a `2000 m × 2000 m` simulated world with 4 default categories and `60 m` spatial cell size (100 iterations per configuration):

| Object Count | Linear Scan Evals / Check | Spatial Grid Evals / Check | Object Evaluations Saved | Linear Time (C# Double) | Spatial Grid Time (C# Double) |
| :---: | :---: | :---: | :---: | :---: | :---: |
| **1,000** | 1,000 | **75** | **92.5%** | 0.0211 ms | 0.0224 ms |
| **5,000** | 5,000 | **368** | **92.6%** | 0.0987 ms | 0.0964 ms |
| **10,000** | 10,000 | **719** | **92.8%** | 0.1964 ms | 0.1492 ms |

> **Why real Unity Editor / Device gains are even larger:** In the offline C# test harness, `GameObject.SetActive` and `Transform.position` are plain C# field accesses. Inside Unity's native C++ engine, skipping **~93% of object evaluations** directly eliminates native interop calls, `Transform.IsChildOf` traversals, and redundant state checks—making `SpatialGrid` significantly faster on mobile CPUs as object counts grow.

---

## Running Offline Tests & Benchmarks

You can verify the culling logic and run the benchmark suite without opening Unity (requires Windows + Visual Studio Roslyn `csc.exe` or .NET SDK):

```powershell
powershell -ExecutionPolicy Bypass -File Tests/Offline/run-tests.ps1
```

---

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE) for details.
