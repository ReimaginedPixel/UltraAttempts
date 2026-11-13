using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;

namespace UltraAttempts.Replay
{
    // Visual-only copies of ULTRAKILL's own models (V1, V2, the enemies): their renderers and their
    // Animator, nothing that thinks, collides, makes a sound or touches the level. Loaded from the game's
    // Addressables, so they look exactly like the real thing.
    internal static class Puppet
    {
        // V1 in third person: the platformer secret level's player (V1Platformer.controller: bools
        // Running, InAir, Sliding, Spinning; states Jump, Dash, Landing, SlamStart, SlamEnd, Spin).
        public const string V1Address = "Assets/Prefabs/Player/PlatformerController.prefab";
        public const string V2Address = "Assets/Prefabs/Enemies/V2.prefab";
        public const string SpawnablesAddress = "Assets/Data/Sandbox/Spawnable Objects Database.asset";

        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        private static SpawnableObjectsDatabase spawnables;
        private static bool spawnablesTried;

        // An addressable prefab (the asset itself, not a copy), loaded once; null if it can't be.
        public static GameObject Prefab(string address)
        {
            if (prefabs.TryGetValue(address, out GameObject cached) && cached != null)
            {
                return cached;
            }
            GameObject prefab = null;
            try
            {
                prefab = AssetHelper.LoadPrefab(address);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"couldn't load {address}: {e.GetBaseException().Message}");
            }
            prefabs[address] = prefab;
            return prefab;
        }

        // The sandbox spawner's list of everything spawnable, which has every enemy's prefab.
        public static SpawnableObjectsDatabase Spawnables
        {
            get
            {
                if (!spawnablesTried)
                {
                    spawnablesTried = true;
                    try
                    {
                        spawnables = Addressables.LoadAssetAsync<SpawnableObjectsDatabase>(SpawnablesAddress).WaitForCompletion();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("couldn't load the enemy list: " + e.GetBaseException().Message);
                    }
                }
                return spawnables;
            }
        }

        // An enemy's prefab by its type (null if the spawner doesn't have it).
        public static GameObject EnemyPrefab(EnemyType type)
        {
            SpawnableObject entry = Spawnables?.enemies?.FirstOrDefault(s => s != null && s.enemyType == type && s.gameObject != null);
            return entry != null ? entry.gameObject : null;
        }

        // A visual-only copy of source (a prefab or a live object) under parent, on layer, active.
        // Everything but Transforms, Animators, MeshFilters and mesh renderers is removed before the copy
        // ever wakes up, so no game script on it runs. Null if source is null.
        public static GameObject Make(GameObject source, Transform parent, int layer, string name = null)
        {
            if (source == null)
            {
                return null;
            }
            var holder = new GameObject("UltraAttempts puppet holder");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject copy = UnityEngine.Object.Instantiate(source, holder.transform);
            copy.name = name ?? Game.CleanName(source.name) + " (replay)";
            Strip(copy);
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
            }
            foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (r is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                }
            }
            foreach (Animator animator in copy.GetComponentsInChildren<Animator>(true))
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;  // (the level is frozen during a replay)
                animator.fireEvents = false;   // (the scripts its animation events call were stripped)
                animator.logWarnings = false;
            }
            copy.transform.SetParent(parent, false);
            copy.SetActive(true);
            UnityEngine.Object.Destroy(holder);
            return copy;
        }

        private static bool Keep(Component c) =>
            c is Transform || c is Animator || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer;

        // Scripts first (so nothing they require is still needed), then the rest; a few passes for
        // components that depend on each other.
        private static void Strip(GameObject root)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                bool removedAny = false;
                Component[] all = root.GetComponentsInChildren<Component>(true);
                foreach (Component c in all.OrderBy(c => c is MonoBehaviour ? 0 : 1))
                {
                    if (c == null || Keep(c))
                    {
                        continue;
                    }
                    try
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                        removedAny = true;
                    }
                    catch
                    {
                        // (still needed by another component: next pass)
                    }
                }
                if (!removedAny)
                {
                    break;
                }
            }
        }

        // Every Renderer's materials swapped for one (see-through ghosts); returns the renderers.
        public static Renderer[] Recolor(GameObject puppet, Material material)
        {
            Renderer[] renderers = puppet.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in renderers)
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++)
                {
                    mats[i] = material;
                }
                r.sharedMaterials = mats;
            }
            return renderers;
        }

        // A deep search for a child by name (case-insensitive, any of the names).
        public static Transform Find(Transform root, params string[] names)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (string n in names)
                {
                    if (string.Equals(t.name, n, StringComparison.OrdinalIgnoreCase))
                    {
                        return t;
                    }
                }
            }
            return null;
        }
    }
}
