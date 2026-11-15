using System;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // The whiplash seen from outside: each ghost's rope from its left hand out to where the whiplash's end was, and
    // the hook's head there, turned the way it flew (the game's own head: its mesh as it's bound to the hook's bone).
    // The ghosts drawn as the real thing (the followed attempt, the winner) get the rope's and the hook's own
    // materials; the rest of the crowd's are see-through in their colour.
    internal sealed class WhiplashDrawer
    {
        private const int MaxRopes = 8;        // ropes in their own material at once (lines; the rest are drawn see-through)
        private const int RopePoints = 9;      // (as many as the game's rope has)

        private readonly Camera cam;
        private readonly int layer;
        private readonly Material seeThrough;
        private readonly Mesh cube;
        private readonly Transform root;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock ropeBlock = new MaterialPropertyBlock();
        private readonly int colorId = Shader.PropertyToID("_Color");
        private readonly int mainTexST = Shader.PropertyToID("_MainTex_ST");
        private readonly LineRenderer[] ropes = new LineRenderer[MaxRopes];
        private int used;

        // The game's own rope and hook (from your whiplash).
        private readonly Material ropeMaterial;
        private readonly LineRenderer ropeSource;
        private readonly float ropeWidth = 0.06f;
        private readonly Mesh head;
        private readonly Material[] headMaterials;
        private readonly Matrix4x4 headBind = Matrix4x4.identity;
        private readonly Vector3 headScale = Vector3.one;

        public string Report { get; }

        public WhiplashDrawer(Transform root, Camera cam, int layer, Material seeThrough, Mesh cube)
        {
            this.root = root;
            this.cam = cam;
            this.layer = layer;
            this.seeThrough = seeThrough;
            this.cube = cube;
            HookArm hook = Game.Get<HookArm>();
            if (hook == null)
            {
                Report = "no whiplash here (ropes see-through only)";
                return;
            }
            try
            {
                ropeSource = hook.GetComponent<LineRenderer>();
                ropeMaterial = ropeSource != null ? ropeSource.sharedMaterial : null;
                if (ropeSource != null)
                {
                    ropeWidth = Mathf.Clamp(ropeSource.widthMultiplier * ropeSource.widthCurve.Evaluate(0.5f), 0.01f, 0.5f);
                }
                SkinnedMeshRenderer smr = hook.hookModel != null ? hook.hookModel.GetComponent<SkinnedMeshRenderer>() : null;
                int bone = smr != null && hook.hook != null ? Array.IndexOf(smr.bones, hook.hook) : -1;
                Matrix4x4[] binds = bone >= 0 && smr.sharedMesh != null ? smr.sharedMesh.bindposes : null;
                if (binds != null && bone < binds.Length)
                {
                    head = smr.sharedMesh;
                    headMaterials = smr.sharedMaterials;
                    headBind = binds[bone];
                    headScale = hook.hook.lossyScale;
                }
            }
            catch (Exception e)
            {
                head = null;
                ropeSource = null;
                ropeMaterial = null;
                Plugin.Log.LogDebug("the whiplash's rope and head for the ghosts: " + e.Message);
            }
            Report = $"whiplash from outside: {(ropeMaterial != null ? "its rope" : "a plain rope")}, {(head != null ? "its hook" : "a plain hook")}";
        }

        private bool broken;

        // (never into the ghosts' drawing: the whiplash goes for the rest of this replay)
        private void Fail(Exception e)
        {
            if (!broken)
            {
                broken = true;
                Plugin.Log.LogError("drawing the ghosts' whiplashes (off for the rest of this replay; the ghosts still play): " + e);
            }
            End();
        }

        // Once a frame, before any ghost's.
        public void Begin()
        {
            used = 0;
        }

        // Once a frame, after every ghost's: the ropes not used this frame go.
        public void End()
        {
            for (int i = used; i < ropes.Length; i++)
            {
                if (ropes[i] != null && ropes[i].enabled)
                {
                    ropes[i].enabled = false;
                }
            }
        }

        // g's whiplash at t, if it was out: from its hand (look: where it looked, for the rope's snaking).
        public void Draw(Ghost g, float t, Vector3 hand, Quaternion look, Color tint, bool real)
        {
            if (broken || !g.ThrowsWhiplash || !g.HookAt(t, out Vector3 end, out HookPhase phase, out float ago))
            {
                return;
            }
            Vector3 away = end - hand;
            if (away.sqrMagnitude < 1e-4f)
            {
                return;
            }
            try
            {
                float warp = phase == HookPhase.Throwing || phase == HookPhase.Caught ? Mathf.Max(0f, 1f - 6.5f * ago) : 0f;
                Rope(hand, end, warp, look * Vector3.up, tint, real);
                Head(end, away.normalized, tint, real);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void Rope(Vector3 from, Vector3 to, float warp, Vector3 up, Color tint, bool real)
        {
            if (real && ropeMaterial != null && used < ropes.Length)
            {
                LineRenderer line = ropes[used] != null ? ropes[used] : (ropes[used] = MakeRope());
                used++;
                line.enabled = true;
                float scroll = Vector3.Distance(from, to) / 300f;
                line.textureScale = new Vector2(scroll + 1f, 1f);
                ropeBlock.SetVector(mainTexST, new Vector4(1f, 1f, -scroll * 10f, 1f));
                line.SetPropertyBlock(ropeBlock);
                line.SetPosition(0, from);
                for (int i = 1; i < RopePoints - 1; i++)
                {
                    line.SetPosition(i, Vector3.Lerp(from, to, i / (float)RopePoints) + 1f / i * (i % 2 == 0 ? -3f : 3f) * warp * up);
                }
                line.SetPosition(RopePoints - 1, to);
                return;
            }
            Vector3 d = to - from;
            float length = d.magnitude;
            block.SetColor(colorId, new Color(tint.r, tint.g, tint.b, Mathf.Min(1f, tint.a * 1.6f)));
            var m = Matrix4x4.TRS(from + d * 0.5f, Quaternion.LookRotation(d / length), new Vector3(ropeWidth, ropeWidth, length));
            Graphics.DrawMesh(cube, m, seeThrough, layer, cam, 0, block, false, false, false);
        }

        // Its head at its end, turned the way it went (HookArm: hook.up along the throw).
        private void Head(Vector3 at, Vector3 dir, Color tint, bool real)
        {
            Quaternion turn = Quaternion.FromToRotation(Vector3.up, dir);
            if (head == null)
            {
                block.SetColor(colorId, new Color(tint.r, tint.g, tint.b, Mathf.Min(1f, tint.a * 1.8f)));
                Graphics.DrawMesh(cube, Matrix4x4.TRS(at, turn, new Vector3(0.12f, 0.3f, 0.12f)), seeThrough, layer, cam, 0, block, false, false, false);
                return;
            }
            Matrix4x4 m = Matrix4x4.TRS(at, turn, headScale) * headBind;
            for (int s = 0; s < head.subMeshCount; s++)
            {
                Material own = headMaterials != null && headMaterials.Length > 0 ? headMaterials[Mathf.Min(s, headMaterials.Length - 1)] : null;
                if (real && own != null)
                {
                    Graphics.DrawMesh(head, m, own, layer, cam, s, null, false, false, false);
                }
                else
                {
                    block.SetColor(colorId, new Color(tint.r, tint.g, tint.b, Mathf.Min(1f, tint.a * 1.8f)));
                    Graphics.DrawMesh(head, m, seeThrough, layer, cam, s, block, false, false, false);
                }
            }
        }

        private LineRenderer MakeRope()
        {
            var go = new GameObject("UltraAttempts whiplash rope") { layer = layer };
            go.transform.SetParent(root, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = ropeMaterial;
            line.useWorldSpace = true;
            line.positionCount = RopePoints;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (ropeSource != null)
            {
                line.widthCurve = ropeSource.widthCurve;
                line.widthMultiplier = ropeSource.widthMultiplier;
                line.colorGradient = ropeSource.colorGradient;
                line.textureMode = ropeSource.textureMode;
                line.alignment = ropeSource.alignment;
                line.numCapVertices = ropeSource.numCapVertices;
            }
            else
            {
                line.widthMultiplier = ropeWidth;
            }
            return line;
        }

        public void Dispose()
        {
            foreach (LineRenderer line in ropes)
            {
                if (line != null)
                {
                    Object.Destroy(line.gameObject);
                }
            }
        }
    }
}
