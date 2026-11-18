#if GLEY_TRAFFIC_SYSTEM
using System;
using System.Collections;
using Gley.TrafficSystem;
using UnityEngine;

namespace EnvironmentCulling.Optional.GleyTraffic
{
    /// <summary>
    /// Optional adapter that configures Gley Traffic System's native distance culling
    /// before TrafficComponent.Start and binds a tracking target.
    /// Requires the GLEY_TRAFFIC_SYSTEM scripting define symbol.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TrafficComponent))]
    public sealed class GleyTrafficCullingAdapter : MonoBehaviour
    {
        [Header("Native Gley Culling (Applied Before Initialization)")]
        [Tooltip("Use -1 to let Gley derive the spawn distance from its grid.")]
        [SerializeField] private float spawnDistance = 90f;
        [Tooltip("Use -1 to let Gley derive the removal distance from its grid.")]
        [SerializeField] private float removalDistance = 150f;
        [Min(1)] [SerializeField] private int activeSquareLevels = 1;

        [Header("Tracking")]
        [Tooltip("Optional override. Otherwise preserves TrafficComponent.player or falls back to Camera.main.")]
        [SerializeField] private Transform trackingTarget;

        [Header("Behind-Player Background Simulation")]
        [SerializeField] private bool enableBackgroundSimulation = true;
        [Min(15f)] [SerializeField] private float backgroundEnterDistance = 70f;
        [Min(10f)] [SerializeField] private float backgroundWakeDistance = 50f;
        [Min(0f)] [SerializeField] private float backgroundHiddenDelay = 0.35f;

        [Header("Frustum Culling")]
        [SerializeField] private bool cullRenderersOutsideFrustum = true;
        [Min(0f)] [SerializeField] private float frustumPadding = 4f;

        private Gley.TrafficSystem.Internal.TrafficManager configuredManager;
        private TrafficComponent traffic;
        private Transform boundTarget;
        private Transform[] targetBuffer;
        private Coroutine initializationRoutine;

        public Transform TrackingTarget => trackingTarget;
        public bool CullRenderersOutsideFrustum => cullRenderersOutsideFrustum;
        public float FrustumPadding => frustumPadding;

        private void Awake()
        {
            traffic = GetComponent<TrafficComponent>();
            targetBuffer = new Transform[1];
            SanitizeSettings();

            if (!API.IsInitialized())
            {
                traffic.minDistanceToAdd = spawnDistance;
                traffic.distanceToRemove = removalDistance;
                traffic.activeSquareLevels = activeSquareLevels;
            }
            else
            {
                Debug.LogWarning("[GleyTrafficCullingAdapter] Traffic is already initialized. Distance settings require a scene restart.", this);
            }

            if (trackingTarget == null)
            {
                trackingTarget = traffic.player != null ? traffic.player : (Camera.main != null ? Camera.main.transform : null);
            }

            if (trackingTarget != null) traffic.player = trackingTarget;
        }

        private void OnEnable()
        {
            initializationRoutine = StartCoroutine(BindAfterInitialization());
        }

        private void OnDisable()
        {
            if (initializationRoutine != null) StopCoroutine(initializationRoutine);
            initializationRoutine = null;
            boundTarget = null;
            if (configuredManager != null)
            {
                configuredManager.ConfigureBackgroundSimulation(
                    false, null, backgroundEnterDistance, backgroundWakeDistance,
                    backgroundHiddenDelay, false, frustumPadding);
            }
        }

        private IEnumerator BindAfterInitialization()
        {
            var wait = new WaitForSecondsRealtime(0.25f);
            while (!API.IsInitialized()) yield return wait;
            BindTarget();
            initializationRoutine = null;
        }

        public void SetTrackingTarget(Transform target)
        {
            if (target == null) return;
            trackingTarget = target;
            if (traffic == null) traffic = GetComponent<TrafficComponent>();
            if (traffic != null) traffic.player = target;

            if (API.IsInitialized())
            {
                BindTarget();
            }
        }

        private void BindTarget()
        {
            Transform target = trackingTarget != null ? trackingTarget : (traffic != null ? traffic.player : null);
            if (target == null && Camera.main != null) target = Camera.main.transform;
            if (target == null) return;

            var manager = Gley.TrafficSystem.Internal.TrafficManager.Instance;
            if (manager == null) return;

            configuredManager = manager;
            if (boundTarget != target)
            {
                targetBuffer[0] = target;
                API.SetActiveCameras(targetBuffer);
                boundTarget = target;
            }

            manager.ConfigureBackgroundSimulation(
                enableBackgroundSimulation,
                target,
                backgroundEnterDistance,
                backgroundWakeDistance,
                backgroundHiddenDelay,
                cullRenderersOutsideFrustum,
                frustumPadding);
        }

        private void SanitizeSettings()
        {
            if (spawnDistance >= 0f && removalDistance >= 0f && removalDistance <= spawnDistance)
            {
                removalDistance = spawnDistance + 25f;
            }

            activeSquareLevels = Mathf.Max(1, activeSquareLevels);
            backgroundEnterDistance = Mathf.Max(15f, backgroundEnterDistance);
            backgroundWakeDistance = Mathf.Clamp(backgroundWakeDistance, 10f, backgroundEnterDistance - 5f);
            backgroundHiddenDelay = Mathf.Max(0f, backgroundHiddenDelay);
            frustumPadding = Mathf.Max(0f, frustumPadding);
        }

        private void OnValidate()
        {
            SanitizeSettings();
        }
    }
}
#endif
