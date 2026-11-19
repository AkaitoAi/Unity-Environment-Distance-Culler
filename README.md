# Environment Distance Culler for Unity

A lightweight, zero-GC, spatial-grid **distance and frustum culling system** for Unity environments. Designed for open-world, racing, and mobile games with thousands of props, trees, buildings, and landmarks.

---

## Why Use This in Your Game Project?

In open-world, racing, survival, or city-builder games, keeping thousands of distant props, trees, and buildings active wastes both CPU and GPU budget:

| Problem Without Distance Culling | How `EnvironmentDistanceCuller` Solves It |
| :--- | :--- |
| **Excessive Draw Calls & Shadow Casters:** Unity's camera frustum culler still processes far-away renderers and directional shadow cascades. | **`GameObjectSetActive` & `RendererOnly` Modes:** Disables far renderers (and optionally colliders/scripts) so they never enter the render queue or shadow pass. |
| **Physics Broadphase Overhead:** Thousands of static `Collider`s on distant props bloat PhysX broadphase updates. | **`GameObjectSetActive`:** Fully deactivates out-of-range props, removing their colliders and components from active simulation. |
| **Mobile GC Spikes & Frame Drops:** Running `Vector3.Distance` on every object in `Update()` allocates or burns main-thread time every frame. | **Zero-GC Coroutine + 2D Spatial Grid:** Evaluates at a configurable `checkInterval` (default `0.2s` = 5 Hz) using pre-allocated buffers and skips ~92.5%+ of objects at the cell level. |
| **Pop-In Flickering at Edges:** Objects near the threshold flicker on/off as the camera jitters. | **Per-Category Hysteresis:** Separate `maxDistance` (activate) and `maxDistance + hysteresis` (deactivate) bands eliminate boundary popping. |
| **Tedious Manual Tagging:** Manually assigning LODs or cull volumes to 10,000+ scene objects takes hours. | **Keyword Auto-Classification:** Drop it on your `Env` root and click **Scan & Categorize Hierarchy Now**—objects are grouped automatically by name keywords. |

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
| **Trees & Vegetation** | 220 m | 20 m | On | `tree`, `bush`, `foliage`, `grass`, `plant`, `flower` |
| **Buildings & Structures** | 450 m | 30 m | On | `building`, `house`, `structure`, `bridge`, `shop`, `tower`, `hangar`, `wall`, `ramp` |
| **Landmarks & Roads** | 1000 m | 50 m | Always Visible | `road`, `terrain`, `ground`, `zebra`, `base` |

> **Notes:**
> - **Static Position Caching:** `ScanAndInitialize()` caches world positions and bounds once at startup/scan for zero-interop performance. If you move environment objects at runtime, call `ScanAndInitialize()` again.
> - **Frustum Culling Safety:** Frustum-only hides automatically use `RendererOnly` (keeping colliders/scripts active) and do not trigger `ICullResettable.ResetOnCull()` or corrupt distance hysteresis.
> - Pure container `GameObject`s (with children and no `Renderer` of their own) are recursively traversed when `Cull Children Independently` is enabled, and unclassified children inherit their parent container's category (or fall back to category index `2` if available, else `0`).

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
Measured using the included Roslyn offline benchmark harness (`Tests/Offline/run-tests.ps1`) in a `2000 m × 2000 m` simulated world with 4 default categories and `60 m` spatial cell size (averaged across 100 iterations per configuration):

| Object Count | Active Objects (% Culled) | Linear Evals / Check | Spatial Grid Avg Evals / Check | Linear Time (C# Double) | Spatial Grid Time (C# Double) | Speedup | Amortized @ 60 FPS (5 Hz) |
| :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **1,000** | 306 (**69.4% culled**) | 1,000 | **74** (-92.6%) | 0.0184 ms | 0.0233 ms | 0.79× | ~0.0019 ms/frame |
| **5,000** | 1,509 (**69.8% culled**) | 5,000 | **361** (-92.8%) | 0.0901 ms | 0.1266 ms | 0.71× | ~0.0106 ms/frame |
| **10,000** | 3,024 (**69.8% culled**) | 10,000 | **724** (-92.8%) | 0.1605 ms | 0.1452 ms | **1.10×** | ~0.0121 ms/frame |
| **25,000** | 7,578 (**69.7% culled**) | 25,000 | **1,840** (-92.6%) | 0.4023 ms | **0.1941 ms** | **2.07×** | **~0.0162 ms/frame** |

> **Why real Unity Editor / Device gains are even larger:**
> - **~70% of scene objects are disabled** in a typical 2 km × 2 km world, directly cutting active `Renderer`s, shadow-caster submissions, and `Collider`s.
> - In the offline C# test harness, `GameObject.SetActive` and `Transform.position` are plain C# field accesses. Inside Unity's native C++ engine, skipping **~92.7% of object evaluations** eliminates native C#-to-C++ interop calls and `Transform.IsChildOf` traversals.
> - **Profiling in your own Unity scene:** Open **Window → Analysis → Profiler** (check `Rendering` → `Batches` / `SetPass Calls`, and `Physics` → `Active Rigidbodies/Colliders`) with `EnvironmentDistanceCuller` enabled vs disabled to measure exact GPU/CPU gains for your specific art assets.

---

## Running Offline Tests & Benchmarks

You can verify the culling logic and run the benchmark suite without opening Unity (requires Windows + Visual Studio Roslyn `csc.exe` or .NET SDK):

```powershell
powershell -ExecutionPolicy Bypass -File Tests/Offline/run-tests.ps1
```

---

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE) for details.
