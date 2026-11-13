using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraAttempts.Replay
{
    // Draws the replay: every ghost (a crystal body, a head with a visor, its weapon), the burst where
    // each one dies, red markers left behind, tracers for shots, and trails. Built from code (no assets),
    // drawn straight to ULTRAKILL's main camera, so it picks up the game's own pixelation and palette.
    internal sealed class GhostRenderer
    {
        public static readonly Color Gold = new Color(1f, 0.8f, 0.2f);
        public static readonly Color Visor = new Color(1f, 0.85f, 0.3f);
        public static readonly Color MarkerRed = new Color(1f, 0.15f, 0.12f);

        public static Color VariantColor(int variant)
        {
            switch (variant)
            {
                case 0: return new Color(0.3f, 0.65f, 1f);
                case 1: return new Color(0.35f, 1f, 0.4f);
                case 2: return new Color(1f, 0.3f, 0.25f);
                case 3: return new Color(1f, 0.82f, 0.2f);
                default: return Color.white;
            }
        }

        private readonly Camera cam;
        private readonly int layer;
        private readonly Material ghost;
        private readonly Material xray;
        private readonly Material markerMat;
        private readonly Material lineMat;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private readonly int colorId = Shader.PropertyToID("_Color");

        private readonly Mesh body = Meshes.Crystal();
        private readonly Mesh cube = Meshes.Cube();
        private readonly Mesh sphere = Meshes.Sphere();
        private Mesh markers;
        private int markersShown = -1;

        private readonly List<Tracer> tracers = new List<Tracer>();
        private readonly LineRenderer bestTrail;
        private readonly LineRenderer followTrail;
        private readonly Vector3[] trailPoints = new Vector3[512];

        private struct Tracer
        {
            public Vector3 From;
            public Vector3 To;
            public Color Color;
            public float Born;
            public float Life;
            public float Width;
        }

        public GhostRenderer(Camera cam, Transform root)
        {
            this.cam = cam;
            layer = (cam.cullingMask & 1) != 0 ? 0 : FirstLayer(cam.cullingMask);
            ghost = MakeMaterial(CullMode.Back, CompareFunction.LessEqual);
            xray = MakeMaterial(CullMode.Back, CompareFunction.Always);
            markerMat = MakeMaterial(CullMode.Off, CompareFunction.LessEqual);
            lineMat = MakeMaterial(CullMode.Off, CompareFunction.LessEqual);
            bestTrail = MakeTrail(root, "best trail");
            followTrail = MakeTrail(root, "follow trail");
        }

        public void Dispose()
        {
            Object.Destroy(ghost);
            Object.Destroy(xray);
            Object.Destroy(markerMat);
            Object.Destroy(lineMat);
            Object.Destroy(body);
            Object.Destroy(cube);
            Object.Destroy(sphere);
            if (markers != null)
            {
                Object.Destroy(markers);
            }
        }

        private static int FirstLayer(int mask)
        {
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    return i;
                }
            }
            return 0;
        }

        // Unlit, see-through, vertex colours times _Color. Internal-Colored is in every Unity build.
        private static Material MakeMaterial(CullMode cull, CompareFunction zTest)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)cull);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)zTest);
            mat.renderQueue = 3000;
            return mat;
        }

        private LineRenderer MakeTrail(Transform root, string name)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(root, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = lineMat;
            line.useWorldSpace = true;
            line.widthMultiplier = 0.12f;
            line.numCapVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.positionCount = 0;
            return line;
        }

        private void Draw(Mesh mesh, Matrix4x4 matrix, Color color, Material material = null)
        {
            block.SetColor(colorId, color);
            Graphics.DrawMesh(mesh, matrix, material ?? ghost, layer, cam, 0, block, false, false, false);
        }

        // One ghost. featured: the one the camera follows (or the winner): clearer, and seen through walls.
        // pov: the camera is inside its head (draw nothing but what's in front of the eyes).
        public void DrawGhost(Ghost g, in Ghost.Pose p, float alpha, bool featured, bool pov, bool weapons)
        {
            Color color = g.Color;
            if (g.HurtAt(p.Sample))
            {
                color = Color.Lerp(color, Color.red, 0.7f);
            }
            Material mat = featured && !pov ? xray : ghost;

            Vector3 up = p.Up;
            Vector3 forward = Vector3.ProjectOnPlane(p.Look * Vector3.forward, up);
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.ProjectOnPlane(p.Look * Vector3.up, up);
            }
            Quaternion yaw = Quaternion.LookRotation(forward.normalized, up);

            if (!pov)
            {
                // Body: from the capsule's bottom up to just under the eyes.
                float half = g.CapsuleHeight * 0.5f;
                Vector3 center = p.Body + up * g.CapsuleCenterY;
                Vector3 bottom = center - up * half;
                float length = Mathf.Max(0.6f, Vector3.Dot(p.Eye - bottom, up) - 0.4f);
                float width = Mathf.Max(0.5f, g.CapsuleRadius * 1.8f);
                Vector3 scale = new Vector3(width, length, width);
                if ((p.Flags & SampleFlags.Sliding) != 0)
                {
                    scale = new Vector3(width, length * 0.45f, width * 1.4f);
                }
                if ((p.Flags & SampleFlags.Dashing) != 0)
                {
                    scale.z *= 1.8f;
                    color = Color.Lerp(color, Color.white, 0.35f);
                }
                Vector3 mid = bottom + up * (scale.y * 0.5f);
                Draw(body, Matrix4x4.TRS(mid, yaw, scale), new Color(color.r, color.g, color.b, alpha), mat);

                // Head and visor, looking where it looked.
                Draw(cube, Matrix4x4.TRS(p.Eye, p.Look, new Vector3(0.6f, 0.45f, 0.6f)), new Color(color.r * 0.8f, color.g * 0.8f, color.b * 0.8f, alpha), mat);
                Draw(cube, Matrix4x4.TRS(p.Eye + p.Look * new Vector3(0f, 0.03f, 0.31f), p.Look, new Vector3(0.48f, 0.12f, 0.03f)), new Color(Visor.r, Visor.g, Visor.b, Mathf.Min(1f, alpha * 2f)), mat);
            }

            if (weapons)
            {
                Ghost.WeaponChange w = g.WeaponAt(p.Sample);
                if (w.Slot > 0)
                {
                    Color gun = VariantColor(w.Variant);
                    Vector3 offset = pov ? new Vector3(0.35f, -0.32f, 0.75f) : new Vector3(0.42f, -0.3f, 0.55f);
                    Draw(cube, Matrix4x4.TRS(p.Eye + p.Look * offset, p.Look, new Vector3(0.13f, 0.13f, 0.8f)), new Color(gun.r, gun.g, gun.b, pov ? 0.6f : Mathf.Min(1f, alpha * 1.8f)), mat);
                }
            }
        }

        // The pop where an attempt ends: a sphere bursting out, and shards flying off. age: seconds since.
        public void DrawBurst(Ghost g, Vector3 at, float age)
        {
            const float life = 0.55f;
            if (age < 0f || age > life)
            {
                return;
            }
            float k = age / life;
            float ease = 1f - (1f - k) * (1f - k);
            bool finish = g.Finished;
            Color c = finish ? Gold : g.Color;
            float size = g.EndReason == AttemptEnd.Death || finish ? 1f : 0.5f;
            Draw(sphere, Matrix4x4.TRS(at, Quaternion.identity, Vector3.one * Mathf.Lerp(0.3f, 3.4f * size, ease)), new Color(c.r, c.g, c.b, (1f - k) * 0.55f), ghost);
            if (g.EndReason != AttemptEnd.Death && !finish)
            {
                return;
            }
            var random = new System.Random(g.Number * 7919 + g.Order);
            for (int i = 0; i < 7; i++)
            {
                var dir = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 1.6f - 0.4f, (float)random.NextDouble() * 2f - 1f).normalized;
                Vector3 pos = at + dir * (ease * (finish ? 7f : 4.5f));
                Quaternion spin = Quaternion.LookRotation(dir) * Quaternion.Euler(age * 720f, age * 540f, 0f);
                Draw(cube, Matrix4x4.TRS(pos, spin, Vector3.one * (0.3f * (1f - k))), new Color(c.r, c.g, c.b, (1f - k) * 0.9f), ghost);
            }
        }

        // Every death's marker, built once; only the first `shown` (deaths so far, in order) are drawn.
        public void BuildMarkers(IList<Ghost> deathsInOrder)
        {
            if (markers != null)
            {
                Object.Destroy(markers);
            }
            markers = Meshes.Markers(deathsInOrder, 0.32f, MarkerRed);
            markersShown = -1;
        }

        public void DrawMarkers(int shown)
        {
            if (markers == null || shown <= 0)
            {
                return;
            }
            if (shown != markersShown)
            {
                markersShown = shown;
                markers.SetSubMesh(0, new SubMeshDescriptor(0, shown * Meshes.MarkerIndices), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
            }
            Draw(markers, Matrix4x4.identity, new Color(1f, 1f, 1f, 0.75f), markerMat);
        }

        public void AddTracer(Vector3 from, Vector3 to, Color color, float now, float life, float width)
        {
            if (tracers.Count >= 256)
            {
                tracers.RemoveAt(0);
            }
            tracers.Add(new Tracer { From = from, To = to, Color = color, Born = now, Life = life, Width = width });
        }

        public void ClearTracers() => tracers.Clear();

        public void DrawTracers(float now)
        {
            for (int i = tracers.Count - 1; i >= 0; i--)
            {
                Tracer tr = tracers[i];
                float k = (now - tr.Born) / tr.Life;
                if (k >= 1f || k < 0f)
                {
                    tracers.RemoveAt(i);
                    continue;
                }
                Vector3 d = tr.To - tr.From;
                float length = d.magnitude;
                if (length < 0.01f)
                {
                    continue;
                }
                var m = Matrix4x4.TRS(tr.From + d * 0.5f, Quaternion.LookRotation(d / length), new Vector3(tr.Width, tr.Width, length));
                Draw(cube, m, new Color(tr.Color.r, tr.Color.g, tr.Color.b, (1f - k) * 0.85f), ghost);
            }
        }

        // A ghost's path from `from` seconds to `to` seconds in.
        public void SetTrail(bool best, Ghost g, float from, float to, Color color)
        {
            LineRenderer line = best ? bestTrail : followTrail;
            if (g == null || to <= from)
            {
                line.positionCount = 0;
                return;
            }
            int first = Mathf.Clamp(Mathf.FloorToInt(from * g.Rate), 0, g.Count - 1);
            int last = Mathf.Clamp(Mathf.FloorToInt(to * g.Rate), 0, g.Count - 1);
            int span = last - first + 1;
            int step = Mathf.Max(1, Mathf.CeilToInt(span / (float)(trailPoints.Length - 1)));
            int n = 0;
            for (int i = first; i <= last && n < trailPoints.Length - 1; i += step)
            {
                trailPoints[n++] = g.Body[i];
            }
            trailPoints[n++] = g.PoseAt(to).Body;
            line.positionCount = n;
            line.SetPositions(trailPoints);
            line.startColor = new Color(color.r, color.g, color.b, 0.05f);
            line.endColor = new Color(color.r, color.g, color.b, 0.7f);
        }
    }

    // The meshes, made in code: grey shading baked into the vertex colours (the material is unlit),
    // tinted per ghost through _Color.
    internal static class Meshes
    {
        public const int MarkerIndices = 24;

        private static readonly Vector3 Light = new Vector3(0.35f, 0.85f, 0.4f).normalized;

        private static Color Shade(Vector3 normal)
        {
            float b = 0.55f + 0.45f * Mathf.Max(0f, Vector3.Dot(normal, Light));
            return new Color(b, b, b, 1f);
        }

        private sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> I = new List<int>();

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Color? tint = null)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                Color col = Shade(n);
                if (tint.HasValue)
                {
                    col = new Color(col.r * tint.Value.r, col.g * tint.Value.g, col.b * tint.Value.b, tint.Value.a);
                }
                int start = V.Count;
                V.Add(a);
                V.Add(b);
                V.Add(c);
                C.Add(col);
                C.Add(col);
                C.Add(col);
                I.Add(start);
                I.Add(start + 1);
                I.Add(start + 2);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                Tri(a, b, c);
                Tri(a, c, d);
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
                if (V.Count > 65000)
                {
                    mesh.indexFormat = IndexFormat.UInt32;
                }
                mesh.SetVertices(V);
                mesh.SetColors(C);
                mesh.SetTriangles(I, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        // A six-sided crystal, 1 tall and 1 wide, centred: the ghost's body.
        public static Mesh Crystal()
        {
            var b = new Builder();
            const int sides = 6;
            var top = new Vector3(0f, 0.5f, 0f);
            var bottom = new Vector3(0f, -0.5f, 0f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides;
                float a1 = (i + 1) * Mathf.PI * 2f / sides;
                var p0 = new Vector3(Mathf.Sin(a0) * 0.5f, 0f, Mathf.Cos(a0) * 0.5f);
                var p1 = new Vector3(Mathf.Sin(a1) * 0.5f, 0f, Mathf.Cos(a1) * 0.5f);
                Vector3 hi0 = p0 + Vector3.up * 0.22f, hi1 = p1 + Vector3.up * 0.22f;
                Vector3 lo0 = p0 * 0.8f - Vector3.up * 0.3f, lo1 = p1 * 0.8f - Vector3.up * 0.3f;
                // (clockwise seen from outside: Unity's front faces)
                b.Tri(top, hi0, hi1);
                b.Quad(hi0, lo0, lo1, hi1);
                b.Tri(bottom, lo1, lo0);
            }
            return b.Build("UltraAttempts crystal");
        }

        public static Mesh Cube()
        {
            var b = new Builder();
            Vector3 h = Vector3.one * 0.5f;
            Vector3[] c =
            {
                new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z), new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z),
                new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z), new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z),
            };
            b.Quad(c[0], c[3], c[2], c[1]);  // back
            b.Quad(c[4], c[5], c[6], c[7]);  // front
            b.Quad(c[0], c[4], c[7], c[3]);  // left
            b.Quad(c[1], c[2], c[6], c[5]);  // right
            b.Quad(c[3], c[7], c[6], c[2]);  // top
            b.Quad(c[0], c[1], c[5], c[4]);  // bottom
            return b.Build("UltraAttempts cube");
        }

        // An octahedron split once and puffed out: a low-poly ball, radius 0.5.
        public static Mesh Sphere()
        {
            var b = new Builder();
            Vector3[] o = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            int[,] faces = { { 0, 4, 3 }, { 0, 3, 5 }, { 0, 5, 2 }, { 0, 2, 4 }, { 1, 3, 4 }, { 1, 5, 3 }, { 1, 2, 5 }, { 1, 4, 2 } };
            for (int f = 0; f < 8; f++)
            {
                Vector3 a = o[faces[f, 0]], c = o[faces[f, 1]], d = o[faces[f, 2]];
                Vector3 ac = (a + c).normalized, cd = (c + d).normalized, da = (d + a).normalized;
                b.Tri(a * 0.5f, ac * 0.5f, da * 0.5f);
                b.Tri(ac * 0.5f, c * 0.5f, cd * 0.5f);
                b.Tri(da * 0.5f, cd * 0.5f, d * 0.5f);
                b.Tri(ac * 0.5f, cd * 0.5f, da * 0.5f);
            }
            return b.Build("UltraAttempts sphere");
        }

        // A small diamond where each attempt died, in the order they died (MarkerIndices each).
        public static Mesh Markers(IList<Ghost> deaths, float size, Color color)
        {
            var b = new Builder();
            Vector3[] o = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            int[,] faces = { { 0, 4, 3 }, { 0, 3, 5 }, { 0, 5, 2 }, { 0, 2, 4 }, { 1, 3, 4 }, { 1, 5, 3 }, { 1, 2, 5 }, { 1, 4, 2 } };
            foreach (Ghost g in deaths)
            {
                Ghost.Pose end = g.EndPose;
                Vector3 at = end.Body + end.Up * 0.3f;
                for (int f = 0; f < 8; f++)
                {
                    Vector3 s = new Vector3(size, size * 1.6f, size);
                    b.Tri(at + Vector3.Scale(o[faces[f, 0]], s), at + Vector3.Scale(o[faces[f, 1]], s), at + Vector3.Scale(o[faces[f, 2]], s), color);
                }
            }
            if (b.V.Count == 0)
            {
                return null;
            }
            Mesh mesh = b.Build("UltraAttempts markers");
            mesh.bounds = new Bounds(mesh.bounds.center, mesh.bounds.size + Vector3.one * 10f);
            return mesh;
        }
    }
}
