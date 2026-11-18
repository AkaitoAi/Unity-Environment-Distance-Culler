using System;
using System.Collections.Generic;
using UnityEngine;

namespace EnvironmentCulling
{
    public enum CullMode
    {
        [Tooltip("Fully enables/disables the GameObject (disables graphics, colliders, scripts). Maximum performance.")]
        GameObjectSetActive,

        [Tooltip("Only enables/disables Renderers (keeps physics colliders and scripts active).")]
        RendererOnly
    }

    [Serializable]
    public class CullCategory
    {
        public string categoryName = "New Category";

        [Tooltip("Maximum visible distance in meters before culling.")]
        public float maxDistance = 200f;

        [Tooltip("Buffer distance in meters added to maxDistance when deactivating to prevent flickering at edges.")]
        public float hysteresis = 15f;

        [Tooltip("Method used to cull objects in this category.")]
        public CullMode cullMode = CullMode.GameObjectSetActive;

        [Tooltip("If true, objects matching this category will never be culled.")]
        public bool alwaysVisible = false;

        [Tooltip("If true, camera frustum culling will be applied to this category (when global Frustum Culling is enabled). If false, objects in this category bypass camera frustum culling.")]
        public bool enableFrustumCulling = true;

        [Tooltip("Name patterns (keywords) used to auto-classify GameObjects into this category.")]
        public List<string> nameKeywords = new List<string>();

        [Tooltip("Gizmo color used in Scene View for this distance tier.")]
        public Color gizmoColor = Color.green;

        public CullCategory() { }

        public CullCategory(string name, float dist, float hyst, Color color, params string[] keywords)
        {
            categoryName = name;
            maxDistance = dist;
            hysteresis = hyst;
            gizmoColor = color;
            nameKeywords = new List<string>(keywords);
        }
    }

    public class CulledObjectData
    {
        public GameObject gameObject;
        public Transform transform;
        public Renderer[] renderers;
        public Vector3 cachedWorldPosition;
        public Bounds cachedBounds;
        public bool hasBounds;
        public int categoryIndex;
        public bool isCurrentlyActive;
        public CullMode lastAppliedMode;
        public ICullResettable resettable;

        public CulledObjectData(GameObject go, int catIdx)
        {
            gameObject = go;
            transform = go.transform;
            cachedWorldPosition = transform.position;
            categoryIndex = catIdx;
            isCurrentlyActive = go.activeSelf;
            renderers = go.GetComponentsInChildren<Renderer>(true);
            resettable = go.GetComponent<ICullResettable>() ?? go.GetComponentInParent<ICullResettable>();
            ComputeBounds();
        }

        private void ComputeBounds()
        {
            if (renderers == null || renderers.Length == 0)
            {
                hasBounds = false;
                cachedBounds = new Bounds(cachedWorldPosition, Vector3.one * 2f);
                return;
            }

            hasBounds = false;
            Bounds combinedBounds = new Bounds();
            bool initialized = false;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;

                Bounds rBounds = (r.gameObject.activeInHierarchy && r.enabled)
                    ? r.bounds
                    : GetRendererWorldBounds(r);

                if (!initialized)
                {
                    combinedBounds = rBounds;
                    initialized = true;
                    hasBounds = true;
                }
                else
                {
                    combinedBounds.Encapsulate(rBounds);
                }
            }

            cachedBounds = initialized
                ? combinedBounds
                : new Bounds(cachedWorldPosition, Vector3.one * 2f);
        }

        private Bounds GetRendererWorldBounds(Renderer r)
        {
            Transform t = r.transform;
            Bounds localBounds = new Bounds();
            bool foundLocalBounds = false;

            if (r is MeshRenderer mr)
            {
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    localBounds = mf.sharedMesh.bounds;
                    foundLocalBounds = true;
                }
            }
            else if (r is SkinnedMeshRenderer smr)
            {
                localBounds = smr.localBounds;
                foundLocalBounds = true;
            }
            else if (r is SpriteRenderer sr && sr.sprite != null)
            {
                localBounds = sr.sprite.bounds;
                foundLocalBounds = true;
            }

            if (foundLocalBounds)
            {
                Vector3 center = localBounds.center;
                Vector3 extents = localBounds.extents;

                Bounds worldBounds = new Bounds(
                    t.TransformPoint(center + new Vector3(-extents.x, -extents.y, -extents.z)),
                    Vector3.zero);
                worldBounds.Encapsulate(t.TransformPoint(center + new Vector3(-extents.x, -extents.y, extents.z)));
                worldBounds.Encapsulate(t.TransformPoint(center + new Vector3(-extents.x, extents.y, -extents.z)));
                worldBounds.Encapsulate(t.TransformPoint(center + new Vector3(-extents.x, extents.y, extents.z)));
                worldBounds.Encapsulate(t.TransformPoint(center + new Vector3(extents.x, -extents.y, -extents.z)));
                worldBounds.Encapsulate(t.TransformPoint(center + new Vector3(extents.x, -extents.y, extents.z)));
                worldBounds.Encapsulate(t.TransformPoint(center + new Vector3(extents.x, extents.y, -extents.z)));
                worldBounds.Encapsulate(t.TransformPoint(center + new Vector3(extents.x, extents.y, extents.z)));
                return worldBounds;
            }

            return new Bounds(t.position, Vector3.one * 2f);
        }

        public void SetVisible(bool visible, CullMode mode)
        {
            if (!visible && resettable != null && !(resettable is UnityEngine.Object uo && uo == null) && resettable.IsBroken)
            {
                resettable.ResetOnCull();
            }

            // Allow re-application if the cull mode changed (e.g. switching from
            // frustum RendererOnly to distance SetActive while already hidden)
            if (isCurrentlyActive == visible && lastAppliedMode == mode) return;

            isCurrentlyActive = visible;
            lastAppliedMode = mode;

            if (visible)
            {
                if (gameObject != null && !gameObject.activeSelf)
                {
                    gameObject.SetActive(true);
                }

                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        if (renderers[i] != null) renderers[i].enabled = true;
                    }
                }
            }
            else if (mode == CullMode.GameObjectSetActive)
            {
                if (gameObject != null && gameObject.activeSelf)
                {
                    gameObject.SetActive(false);
                }
            }
            else if (mode == CullMode.RendererOnly)
            {
                // Keep GameObject active so colliders/physics/scripts continue running.
                if (gameObject != null && !gameObject.activeSelf)
                {
                    gameObject.SetActive(true);
                }

                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        if (renderers[i] != null) renderers[i].enabled = false;
                    }
                }
            }
        }
    }
}
