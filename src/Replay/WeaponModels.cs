using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // One of V1's weapons as a ghost holds it: the gun's own meshes and materials, placed as they sit in
    // front of the camera (its "view space": barrel down +z, V1's arm included for first person only).
    internal sealed class WeaponModel
    {
        public struct Part
        {
            public Mesh Mesh;
            public Matrix4x4 View;          // mesh → view space
            public Material[] Materials;
            public MaterialPropertyBlock Block;
            public bool Arm;
        }

        public readonly string Name;
        public readonly List<Part> Parts = new List<Part>();
        public readonly List<Mesh> Owned = new List<Mesh>();
        public Mesh Tinted;                                     // the gun as one shaded mesh, for see-through ghosts (null: its meshes can't be read)
        public Matrix4x4 Hold = Matrix4x4.identity;             // view space → the hand (the grip at the origin)
        public float HoldScale = 1f;
        public float Reach = 0.7f;                              // from the grip to the end of the barrel, held (at size 1)
        public Matrix4x4 CameraFromView = Matrix4x4.identity;   // view space → the camera's (first person)

        public WeaponModel(string name)
        {
            Name = name;
        }

        public bool HasGun => Parts.Any(p => !p.Arm);

        // The grip and a sane size from the gun's bounds, and its see-through mesh.
        public void Finish()
        {
            var bounds = new Bounds();
            bool any = false;
            foreach (Part p in Parts)
            {
                if (p.Arm)
                {
                    continue;
                }
                Bounds local = p.Mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 c = local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) != 0 ? 1f : -1f, (corner & 2) != 0 ? 1f : -1f, (corner & 4) != 0 ? 1f : -1f));
                    c = p.View.MultiplyPoint3x4(c);
                    if (!any)
                    {
                        bounds = new Bounds(c, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(c);
                    }
                }
            }
            float longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            HoldScale = longest > 2.4f ? 2.4f / longest : longest < 0.5f && longest > 1e-4f ? 0.5f / longest : 1f;
            // (held about a third of the way up from the back, a little under the middle)
            var grip = new Vector3(bounds.center.x, bounds.center.y - bounds.extents.y * 0.3f, bounds.min.z + bounds.size.z * 0.3f);
            Hold = Matrix4x4.Translate(-grip);
            Reach = any ? Mathf.Max(0.1f, (bounds.max.z - grip.z) * HoldScale) : 0.7f;
            Tinted = BuildTinted();
        }

        private Mesh BuildTinted()
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            foreach (Part p in Parts)
            {
                if (p.Arm)
                {
                    continue;
                }
                Mesh mesh = p.Mesh;
                if (!mesh.isReadable)
                {
                    mesh = ModelMeshes.ReadableCopy(p.Mesh, null);
                    if (mesh == null)
                    {
                        return null;
                    }
                    Owned.Add(mesh);
                }
                ModelMeshes.Append(mesh, p.View, verts, normals, tris);
            }
            return verts.Count > 0 ? ModelMeshes.Build("UltraAttempts " + Name, verts, normals, tris) : null;
        }

        // The gun in its own materials (and V1's arm, in first person).
        public void DrawReal(Matrix4x4 m, int layer, Camera cam, bool firstPerson)
        {
            foreach (Part p in Parts)
            {
                if (p.Arm && !firstPerson)
                {
                    continue;
                }
                Matrix4x4 at = m * p.View;
                for (int s = 0; s < p.Mesh.subMeshCount; s++)
                {
                    Material mat = p.Materials.Length > 0 ? p.Materials[Mathf.Min(s, p.Materials.Length - 1)] : null;
                    if (mat != null)
                    {
                        Graphics.DrawMesh(p.Mesh, at, mat, layer, cam, s, p.Block, false, false, false);
                    }
                }
            }
        }

        // The gun see-through in one colour, part by part (when there's no Tinted mesh).
        public void DrawParts(Matrix4x4 m, Material material, MaterialPropertyBlock block, int layer, Camera cam)
        {
            foreach (Part p in Parts)
            {
                if (p.Arm)
                {
                    continue;
                }
                for (int s = 0; s < p.Mesh.subMeshCount; s++)
                {
                    Graphics.DrawMesh(p.Mesh, m * p.View, material, layer, cam, s, block, false, false, false);
                }
            }
        }

        public void Dispose()
        {
            foreach (Mesh mesh in Owned)
            {
                Object.Destroy(mesh);
            }
            Owned.Clear();
            if (Tinted != null)
            {
                Object.Destroy(Tinted);
            }
        }
    }

    // The weapons the ghosts hold, made once each from V1's real ones: the loadout you have now
    // (GunControl.allWeapons), else the gun's prefab (GunSetter's list of every weapon), matched by the
    // name the recording kept (the weapon descriptor's, else the object's).
    internal sealed class WeaponModels
    {
        private static readonly FieldInfo VariationRenderers = typeof(WeaponIcon).GetField("variationColoredRenderers", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly Transform parent;
        private readonly int layer;
        private readonly Dictionary<(string, int), WeaponModel> made = new Dictionary<(string, int), WeaponModel>();
        private Dictionary<string, GameObject> loadout;
        private Dictionary<string, GameObject> prefabs;
        private Matrix4x4 cameraFromGuns = Matrix4x4.identity;

        public WeaponModels(Transform parent, int layer)
        {
            this.parent = parent;
            this.layer = layer;
        }

        // A weapon by its recorded name, made the first time it's asked for; null if it can't be.
        public WeaponModel Get(string name, int variant)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            (string, int) key = (name, variant);
            if (made.TryGetValue(key, out WeaponModel model))
            {
                return model;
            }
            model = null;
            try
            {
                model = Make(name, variant);
            }
            catch (Exception e)
            {
                ModelMeshes.LogOnce($"weapon '{name}': couldn't make its model ({e.GetBaseException().Message}); a plain shape stands in");
                Plugin.Log.LogDebug(e);
            }
            made[key] = model;
            return model;
        }

        private WeaponModel Make(string name, int variant)
        {
            GameObject source = Find(name, out string from);
            if (source == null)
            {
                ModelMeshes.LogOnce($"weapon '{name}': not in your loadout or the game's weapon list; a plain shape stands in");
                return null;
            }
            var holder = new GameObject("UltraAttempts weapon template");
            holder.transform.SetParent(parent, false);
            holder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var model = new WeaponModel(name) { CameraFromView = cameraFromGuns };
            try
            {
                // A copy sits where the weapon sits under GunControl, so its parts' matrices are view space.
                GameObject copy = Puppet.Make(source, holder.transform, layer);
                if (copy == null)
                {
                    return null;
                }
                List<Renderer> originals = MeshRenderers(source);
                List<Renderer> copies = MeshRenderers(copy);
                HashSet<string> colored = VariationRendererNames(source);
                Color variation = VariationColor(variant);
                for (int i = 0; i < copies.Count; i++)
                {
                    Renderer r = copies[i];
                    if (!r.enabled || !r.gameObject.activeInHierarchy || Effect(r.name))
                    {
                        continue;
                    }
                    Mesh mesh;
                    Matrix4x4 view;
                    if (r is SkinnedMeshRenderer skinned)
                    {
                        mesh = ModelMeshes.Snapshot(skinned, model.Owned, out view);
                    }
                    else
                    {
                        MeshFilter filter = r.GetComponent<MeshFilter>();
                        mesh = filter != null ? filter.sharedMesh : null;
                        view = r.transform.localToWorldMatrix;
                    }
                    if (mesh == null || r.sharedMaterials.Length == 0)
                    {
                        continue;
                    }
                    // Its variant's colour (and custom gun colours), as WeaponIcon and GunColorGetter set them.
                    MaterialPropertyBlock block = null;
                    Renderer original = i < originals.Count ? originals[i] : null;
                    if (original != null && original.HasPropertyBlock())
                    {
                        block = new MaterialPropertyBlock();
                        original.GetPropertyBlock(block);
                    }
                    else if (colored.Contains(r.name) && r.sharedMaterial != null)
                    {
                        block = new MaterialPropertyBlock();
                        block.SetColor(r.sharedMaterial.HasProperty("_EmissiveColor") ? "_EmissiveColor" : "_Color", variation);
                    }
                    model.Parts.Add(new WeaponModel.Part
                    {
                        Mesh = mesh,
                        View = view,
                        Materials = r.sharedMaterials,
                        Block = block,
                        Arm = Arm(r.name),
                    });
                }
            }
            finally
            {
                holder.SetActive(false);
                Object.Destroy(holder);
            }
            if (!model.HasGun)
            {
                model.Dispose();
                ModelMeshes.LogOnce($"weapon '{name}': nothing to draw on {source.name}; a plain shape stands in");
                return null;
            }
            model.Finish();
            ModelMeshes.LogOnce($"weapon '{name}': {model.Parts.Count} parts from {from} {Game.CleanName(source.name)}" + (model.Tinted == null ? " (its meshes can't be read: see-through ghosts draw it part by part)" : ""));
            return model;
        }

        private GameObject Find(string name, out string from)
        {
            if (loadout == null)
            {
                loadout = new Dictionary<string, GameObject>();
                GunControl guns = Game.Get<GunControl>();
                if (guns != null)
                {
                    foreach (GameObject weapon in guns.allWeapons)
                    {
                        Add(loadout, weapon);
                    }
                    CameraController cc = Game.Get<CameraController>();
                    if (cc != null && guns.transform.IsChildOf(cc.transform))
                    {
                        cameraFromGuns = cc.transform.worldToLocalMatrix * guns.transform.localToWorldMatrix;
                    }
                }
            }
            from = "your loadout:";
            if (loadout.TryGetValue(name, out GameObject found) && found != null)
            {
                return found;
            }
            if (prefabs == null)
            {
                prefabs = new Dictionary<string, GameObject>();
                GunSetter setter = Game.Get<GunSetter>();
                if (setter != null)
                {
                    AssetReference[][] lists =
                    {
                        setter.revolverPierce, setter.revolverRicochet, setter.revolverTwirl,
                        setter.shotgunGrenade, setter.shotgunPump, setter.shotgunRed,
                        setter.nailMagnet, setter.nailOverheat, setter.nailRed,
                        setter.railCannon, setter.railHarpoon, setter.railMalicious,
                        setter.rocketBlue, setter.rocketGreen, setter.rocketRed,
                    };
                    foreach (AssetReference reference in lists.Where(l => l != null).SelectMany(l => l))
                    {
                        try
                        {
                            Add(prefabs, reference != null ? AssetHelper.LoadPrefab(reference) : null);
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogDebug("weapon prefab: " + e.Message);
                        }
                    }
                }
            }
            from = "the prefab";
            return prefabs.TryGetValue(name, out found) ? found : null;
        }

        private static void Add(Dictionary<string, GameObject> into, GameObject weapon)
        {
            if (weapon == null)
            {
                return;
            }
            WeaponIcon icon = weapon.GetComponent<WeaponIcon>();
            string named = icon != null && icon.weaponDescriptor != null ? icon.weaponDescriptor.weaponName : null;
            if (!string.IsNullOrEmpty(named) && !into.ContainsKey(named))
            {
                into[named] = weapon;
            }
            string clean = Game.CleanName(weapon.name);
            if (!into.ContainsKey(clean))
            {
                into[clean] = weapon;
            }
        }

        private static List<Renderer> MeshRenderers(GameObject go) =>
            go.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToList();

        private static HashSet<string> VariationRendererNames(GameObject weapon)
        {
            var names = new HashSet<string>();
            WeaponIcon icon = weapon.GetComponent<WeaponIcon>();
            if (icon != null && VariationRenderers != null && VariationRenderers.GetValue(icon) is Renderer[] renderers)
            {
                foreach (Renderer r in renderers)
                {
                    if (r != null)
                    {
                        names.Add(r.name);
                    }
                }
            }
            return names;
        }

        private static Color VariationColor(int variant)
        {
            ColorBlindSettings colors = Game.Get<ColorBlindSettings>();
            if (colors != null && colors.variationColors != null && variant >= 0 && variant < colors.variationColors.Length)
            {
                return colors.variationColors[variant];
            }
            return GhostRenderer.VariantColor(variant);
        }

        // Muzzle flashes, charge glows and the like: not part of the gun.
        private static bool Effect(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("effect") || n.Contains("flash") || n.Contains("muzzle") || n.Contains("glow") || n.Contains("light")
                || n.Contains("beam") || n.Contains("smoke") || n.Contains("spark") || n.Contains("particle") || n.Contains("trail");
        }

        // V1's own arm, which some weapons carry (the revolver's RightArm): first person only.
        private static bool Arm(string name)
        {
            string n = name.ToLowerInvariant();
            return n.EndsWith("arm") || n.StartsWith("arm") || n.Contains("arm.") || n.Contains("arm_") || n.Contains("arm ");
        }

        public void Dispose()
        {
            foreach (WeaponModel model in made.Values)
            {
                model?.Dispose();
            }
            made.Clear();
        }
    }
}
