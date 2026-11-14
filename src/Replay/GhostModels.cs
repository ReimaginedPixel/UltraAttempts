using System;
using System.Collections.Generic;
using System.Linq;
using UltraAttempts.Recording;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // What a ghost is doing right now, read off its recording: how fast it moves (over the last tenth of
    // a second, so one jittery sample doesn't flip it), whether it's on the ground, rising, sliding or
    // dashing. The animated models and the crowd's pre-posed ones are both picked from this.
    internal struct GhostMotion
    {
        public const float RunSpeed = 2f;

        public Vector3 Up;
        public Vector3 Velocity;
        public Vector3 Heading;     // where it moves, flat (zero when it stands still)
        public float Speed;         // flat speed
        public float Rise;          // speed along up
        public bool Grounded;
        public bool Airborne;       // off the ground for more than a moment (the renderer fills this in)
        public bool Sliding;
        public bool Dashing;        // a dash (ULTRAKILL's boost is also on while sliding: not counted here)

        public bool Running => Grounded && !Sliding && !Dashing && Speed > RunSpeed;

        public static GhostMotion Of(Ghost g, in Ghost.Pose p, float t)
        {
            var m = new GhostMotion { Up = p.Up };
            m.Grounded = (p.Flags & SampleFlags.Grounded) != 0;
            m.Sliding = (p.Flags & SampleFlags.Sliding) != 0;
            m.Dashing = (p.Flags & SampleFlags.Dashing) != 0 && !m.Sliding;
            int i = Mathf.Clamp(p.Sample, 0, g.Count - 1);
            int k = Mathf.Max(0, i - Mathf.Max(1, Mathf.RoundToInt(g.Rate * 0.1f)));
            float span = t - k / (float)g.Rate;
            bool snapped = false;
            for (int s = k + 1; s <= Mathf.Min(i + 1, g.Count - 1); s++)
            {
                snapped |= (g.Flags[s] & (byte)SampleFlags.Snap) != 0;
            }
            if (span > 1e-3f && !snapped)
            {
                m.Velocity = Vector3.ClampMagnitude((p.Body - g.Body[k]) / span, 150f);
            }
            m.Rise = Vector3.Dot(m.Velocity, m.Up);
            Vector3 flat = m.Velocity - m.Up * m.Rise;
            m.Speed = flat.magnitude;
            m.Heading = m.Speed > 0.5f ? flat / m.Speed : Vector3.zero;
            return m;
        }

        public PoseKind Kind =>
            Sliding ? PoseKind.Slide
            : Dashing ? PoseKind.Dash
            : Airborne ? (Rise > 3f ? PoseKind.Jump : PoseKind.Fall)
            : Speed > RunSpeed ? PoseKind.Run
            : PoseKind.Idle;
    }

    internal enum PoseKind
    {
        Idle,
        Run,
        Jump,
        Fall,
        Slide,
        Dash,
    }

    // What happened since the last frame, for the animations that play once.
    internal struct GhostEvents
    {
        public bool Jumped;
        public bool Dashed;
        public bool Shot;
        public bool Punched;
    }

    // A ghost model made from the game's own V1 (or V2): a hidden template copy that the animated ghosts
    // are cloned from, and the crowd's pre-posed meshes, baked from the template once (a run cycle, idle,
    // jumping, falling, sliding, dashing).
    internal sealed class GhostModelKit
    {
        public const int RunFrames = 8;
        public const int PoseCount = RunFrames + 5;

        private static readonly string[] HandNames = { "hand.R", "hand_R", "Hand.R", "Hand_R", "RightHand", "Right Hand", "R_Hand", "Hand_Right", "mixamorig:RightHand" };
        private static readonly string[] HeadNames = { "spine.006", "head", "Head", "mixamorig:Head" };

        private enum BakeMode
        {
            Unknown,
            Direct,     // BakeMesh works on the game's mesh
            Copied,     // on a readable copy read back from the GPU
            Bones,      // neither: boxes along the bones
        }

        public readonly string Label;
        public AnimDriver Driver { get; private set; }
        public Mesh[] Poses { get; private set; }       // feet at y 0, 1 tall, facing +z, shaded in white
        public Vector3[] Hands { get; private set; }    // the right hand in each pose (same space)
        public float FloorY { get; private set; }       // the template's feet, in its own space
        public float Height { get; private set; }       // its feet to the top of its head, in its own space
        public bool LiveBakes => mode == BakeMode.Direct || mode == BakeMode.Copied;

        // The head, for deaths (DeathEffects; Head is null if it couldn't be split off): its own mesh (pose 0's,
        // around its middle, in the poses' space), and in each pose where its middle is and how it's turned
        // from pose 0. Each crowd pose then has a second submesh: the body without it.
        public Mesh Head { get; private set; }
        public Vector3[] HeadAt { get; private set; }
        public Quaternion[] HeadTurn { get; private set; }
        public float HeadSize { get; private set; }     // about its radius, in the poses' space
        public bool[] Headless { get; private set; }    // per pose: has the headless submesh
        public string HeadReport { get; private set; } = "no head bone";
        public bool Wings => Label == "V1";

        private readonly int layer;
        private BakeMode mode;
        private GameObject bakeHolder;
        private GameObject template;
        private int[] bodyIndex;            // the body's skinned renderers, among the template's
        private string handPath;            // the right hand, from the template's root
        private bool[] bakedInOwnSpace;     // per body renderer: BakeMesh took its Transform's scale out
        private Color32[][] shading;        // per body renderer: the crowd's shading, for baked animated ghosts
        private Color32[][] white;
        private float boneHeight = 1f;
        private Mesh scratch;
        private readonly List<Mesh> owned = new List<Mesh>();

        private GhostModelKit(string label, int layer)
        {
            Label = label;
            this.layer = layer;
        }

        // The model asked for, else the next one down (V1, V2, then none: crystals). Logs why once.
        public static GhostModelKit Load(GhostModel wanted, Transform parent, int layer)
        {
            if (wanted == GhostModel.Crystal)
            {
                return null;
            }
            GhostModel[] tries = wanted == GhostModel.V1 ? new[] { GhostModel.V1, GhostModel.V2 } : new[] { GhostModel.V2 };
            foreach (GhostModel model in tries)
            {
                string address = model == GhostModel.V1 ? Puppet.V1Address : Puppet.V2Address;
                var kit = new GhostModelKit(model.ToString(), layer);
                string failure;
                try
                {
                    failure = kit.Build(address, parent);
                }
                catch (Exception e)
                {
                    failure = e.GetBaseException().Message;
                    Plugin.Log.LogDebug(e);
                }
                if (failure == null)
                {
                    return kit;
                }
                kit.Dispose();
                ModelMeshes.LogOnce($"ghost model {model} unavailable: {failure}");
            }
            ModelMeshes.LogOnce("no ghost model could be loaded: the ghosts are crystals");
            return null;
        }

        private string Build(string address, Transform parent)
        {
            GameObject prefab = Puppet.Prefab(address);
            if (prefab == null)
            {
                return "couldn't load " + address;
            }

            // The Animator that moves the biggest skinned body (the prefabs also hold cameras, HUDs, guns).
            Animator source = null;
            int mostBones = 0;
            foreach (Animator a in prefab.GetComponentsInChildren<Animator>(true))
            {
                foreach (SkinnedMeshRenderer s in a.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (s.sharedMesh != null && s.bones.Length > mostBones)
                    {
                        mostBones = s.bones.Length;
                        source = a;
                    }
                }
            }
            if (source == null || mostBones < 10)
            {
                return "no animated body in " + address;
            }

            bakeHolder = new GameObject("UltraAttempts " + Label + " template");
            bakeHolder.transform.SetParent(parent, false);
            bakeHolder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            template = Puppet.Make(source.gameObject, bakeHolder.transform, layer, "UltraAttempts " + Label);
            if (template == null)
            {
                return "couldn't copy " + source.name;
            }
            template.transform.localPosition = Vector3.zero;

            // Only the body shows (not V2's own guns, effects or seasonal hats).
            SkinnedMeshRenderer[] skinned = template.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var body = new List<int>();
            for (int i = 0; i < skinned.Length; i++)
            {
                if (skinned[i].sharedMesh != null && skinned[i].bones.Length >= 10 && skinned[i].gameObject.activeInHierarchy)
                {
                    body.Add(i);
                }
            }
            if (body.Count == 0)
            {
                return "no skinned body on " + source.name;
            }
            bodyIndex = body.ToArray();
            SkinnedMeshRenderer[] bodies = bodyIndex.Select(i => skinned[i]).ToArray();
            foreach (Renderer r in template.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = r is SkinnedMeshRenderer s && bodies.Contains(s);
            }
            Matrix4x4[][] rest = bodies.Select(ModelMeshes.RestBindposes).ToArray();  // (before anything moves)

            Animator animator = template.GetComponent<Animator>();
            if (animator != null)
            {
                animator.fireEvents = false;  // (their receivers were stripped: every event would log an error)
            }
            Driver = AnimDriver.For(animator);

            PoseTemplate(animator, PoseKind.Idle, 0f);
            float turn = FacingCorrection(template.transform);
            if (turn != 0f)
            {
                template.transform.localRotation = Quaternion.Euler(0f, turn, 0f) * template.transform.localRotation;
                PoseTemplate(animator, PoseKind.Idle, 0f);
            }

            Transform hand = Puppet.Find(template.transform, HandNames);
            handPath = hand != null ? PathFrom(template.transform, hand) : null;
            ModelMeshes.LogOnce(hand != null
                ? $"ghost model {Label}: right hand found ('{hand.name}', searched {string.Join(", ", HandNames)})"
                : $"ghost model {Label}: no right hand bone (searched {string.Join(", ", HandNames)}); bones: {string.Join(", ", bodies[0].bones.Where(b => b != null).Select(b => b.name))}");
            Transform head = Puppet.Find(template.transform, HeadNames);

            scratch = new Mesh { name = "UltraAttempts bake", hideFlags = HideFlags.HideAndDontSave };
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            var counts = new List<int>();
            var raw = new Vector3[PoseCount][];
            var rawNormals = new Vector3[PoseCount][];
            var rawTris = new int[PoseCount][];
            var rawHands = new Vector3[PoseCount];
            var rawHeads = new Vector3[PoseCount];
            var rawHeadTurns = new Quaternion[PoseCount];
            int[] firstCounts = null;
            float floor = 0f, top = 0f;
            for (int pose = 0; pose < PoseCount; pose++)
            {
                PoseOf(pose, out PoseKind kind, out float phase);
                PoseTemplate(animator, kind, phase);
                verts.Clear();
                normals.Clear();
                tris.Clear();
                counts.Clear();
                if (!Snapshot(bodies, rest, verts, normals, tris, counts) || verts.Count == 0)
                {
                    return "couldn't pose " + bodies[0].name;
                }
                raw[pose] = verts.ToArray();
                rawNormals[pose] = normals.ToArray();
                rawTris[pose] = tris.ToArray();
                rawHands[pose] = hand != null ? hand.position : Vector3.zero;
                rawHeads[pose] = head != null ? head.position : Vector3.zero;
                rawHeadTurns[pose] = head != null ? head.rotation : Quaternion.identity;
                if (pose == 0)
                {
                    firstCounts = counts.ToArray();
                    // Feet to the top of the head (not the wings): the highest point near the head bone.
                    floor = verts.Min(v => v.y);
                    top = floor + (verts.Max(v => v.y) - floor) * 0.92f;
                    float reach = head != null && head.parent != null ? Vector3.Distance(head.position, head.parent.position) * 2.5f : 0f;
                    if (reach > 1e-4f)
                    {
                        float highest = float.MinValue;
                        foreach (Vector3 v in verts)
                        {
                            if ((v - head.position).sqrMagnitude < reach * reach && v.y > highest)
                            {
                                highest = v.y;
                            }
                        }
                        if (highest > floor)
                        {
                            top = highest;
                        }
                    }
                    if (LiveBakes)
                    {
                        shading = new Color32[counts.Count][];
                        white = new Color32[counts.Count][];
                        for (int i = 0, at = 0; i < counts.Count; at += counts[i], i++)
                        {
                            shading[i] = new Color32[counts[i]];
                            white[i] = new Color32[counts[i]];
                            for (int v = 0; v < counts[i]; v++)
                            {
                                shading[i][v] = Meshes.Shade(normals[at + v]);
                                white[i][v] = new Color32(255, 255, 255, 255);
                            }
                        }
                    }
                }
            }
            FloorY = floor;
            Height = Mathf.Max(0.01f, top - floor);
            Poses = new Mesh[PoseCount];
            Hands = new Vector3[PoseCount];
            for (int pose = 0; pose < PoseCount; pose++)
            {
                Poses[pose] = ModelMeshes.Build($"UltraAttempts {Label} pose {pose}", raw[pose], rawNormals[pose], rawTris[pose], FloorY, Height);
                Hands[pose] = hand != null
                    ? new Vector3(rawHands[pose].x, rawHands[pose].y - FloorY, rawHands[pose].z) / Height
                    : new Vector3(0.14f, 0.5f, 0.12f);
            }
            if (head != null)
            {
                try
                {
                    SplitHead(bodies, head, firstCounts, raw, rawNormals, rawTris, rawHeads, rawHeadTurns);
                }
                catch (Exception e)
                {
                    // (the bodies fall over whole, and the second submeshes, if any got made, aren't used)
                    Head = null;
                    Headless = null;
                    HeadAt = null;
                    HeadReport = $"couldn't split the head off {Label} at '{head.name}' ({e.GetBaseException().Message})";
                }
            }
            ModelMeshes.LogOnce("death animations: " + HeadReport);
            PoseTemplate(animator, PoseKind.Idle, 0f);
            template.SetActive(false);

            string how = mode == BakeMode.Direct ? "baked" : mode == BakeMode.Copied ? "baked from a copy read back from the GPU" : "as boxes along its bones (its mesh couldn't be read)";
            ModelMeshes.LogOnce($"ghost model {Label}: {source.name} ({string.Join(", ", bodies.Select(b => $"{b.name}: {b.sharedMesh.vertexCount} vertices, {b.bones.Length} bones"))}), " +
                $"{Height:0.00} units tall, animator {Driver.Name} [{Driver.Parameters}], {PoseCount} crowd poses {how}" + (turn != 0f ? $", turned {turn:0} degrees to face forward" : ""));
            return null;
        }

        // Which of the body's triangles are the head: mostly weighted to the head bone or anything under it (or,
        // if the weights can't be read, near it and above it); then the head on its own, and every pose's body
        // without it.
        private void SplitHead(SkinnedMeshRenderer[] bodies, Transform head, int[] counts, Vector3[][] raw, Vector3[][] rawNormals, int[][] rawTris,
            Vector3[] heads, Quaternion[] turns)
        {
            Vector3[] verts = raw[0];
            var onHead = new bool[verts.Length];
            string by = "by its bone weights";
            if (mode == BakeMode.Bones || !WeightedToHead(bodies, head, counts, onHead))
            {
                by = "by where it is: its bone weights can't be read";
                Array.Clear(onHead, 0, onHead.Length);
                float reach = Mathf.Max(head.parent != null ? Vector3.Distance(head.position, head.parent.position) * 2.5f : 0.3f, 0.05f);
                for (int v = 0; v < verts.Length; v++)
                {
                    onHead[v] = (verts[v] - heads[0]).sqrMagnitude < reach * reach && verts[v].y > heads[0].y - reach * 0.2f;
                }
            }
            int[] tris = rawTris[0];
            var headTris = new List<int>();
            var bodyTris = new List<int>(tris.Length);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                bool isHead = (onHead[a] ? 1 : 0) + (onHead[b] ? 1 : 0) + (onHead[c] ? 1 : 0) >= 2;
                List<int> into = isHead ? headTris : bodyTris;
                into.Add(a);
                into.Add(b);
                into.Add(c);
            }
            int triangles = headTris.Count / 3;
            if (triangles < 4 || headTris.Count * 2 > tris.Length)
            {
                HeadReport = $"no head split from {Label}: {triangles} of {tris.Length / 3} triangles on '{head.name}' ({by})";
                return;
            }

            // Its middle in each pose (the poses' space: feet at 0, 1 tall), from the same vertices.
            var used = new List<int>();
            var seen = new bool[verts.Length];
            foreach (int v in headTris)
            {
                if (!seen[v])
                {
                    seen[v] = true;
                    used.Add(v);
                }
            }
            HeadAt = new Vector3[PoseCount];
            HeadTurn = new Quaternion[PoseCount];
            Headless = new bool[PoseCount];
            for (int pose = 0; pose < PoseCount; pose++)
            {
                Vector3 sum = Vector3.zero;
                foreach (int v in used)
                {
                    sum += v < raw[pose].Length ? raw[pose][v] : verts[v];
                }
                Vector3 middle = sum / used.Count;
                HeadAt[pose] = new Vector3(middle.x, middle.y - FloorY, middle.z) / Height;
                HeadTurn[pose] = turns[pose] * Quaternion.Inverse(turns[0]);
                // (the same triangles in every pose: the body without the head as a second submesh)
                if (rawTris[pose].Length == tris.Length && raw[pose].Length == verts.Length)
                {
                    Poses[pose].subMeshCount = 2;
                    Poses[pose].SetTriangles(bodyTris, 1, calculateBounds: false);
                    Headless[pose] = true;
                }
            }

            var map = new Dictionary<int, int>(used.Count);
            var headVerts = new List<Vector3>(used.Count);
            var headNormals = new List<Vector3>(used.Count);
            float size = 0f;
            foreach (int v in used)
            {
                map[v] = headVerts.Count;
                Vector3 local = new Vector3(verts[v].x, verts[v].y - FloorY, verts[v].z) / Height - HeadAt[0];
                headVerts.Add(local);
                headNormals.Add(rawNormals[0][v]);
                size = Mathf.Max(size, local.magnitude);
            }
            var headIndices = new int[headTris.Count];
            for (int i = 0; i < headTris.Count; i++)
            {
                headIndices[i] = map[headTris[i]];
            }
            Head = ModelMeshes.Build($"UltraAttempts {Label} head", headVerts, headNormals, headIndices);
            HeadSize = Mathf.Max(size, 0.02f);
            HeadReport = $"head split from {Label} at '{head.name}' bone ({by}), {triangles} triangles";
        }

        // Each vertex mostly weighted to the head bone or one under it (in the order the poses have them).
        // False if the weights can't be read.
        private static bool WeightedToHead(SkinnedMeshRenderer[] bodies, Transform head, int[] counts, bool[] onHead)
        {
            if (counts == null || counts.Length != bodies.Length)
            {
                return false;
            }
            var under = new HashSet<Transform>(head.GetComponentsInChildren<Transform>(true));
            bool any = false;
            for (int i = 0, at = 0; i < bodies.Length; at += counts[i], i++)
            {
                Mesh mesh = bodies[i].sharedMesh;
                Mesh copy = null;
                BoneWeight[] weights = null;
                try
                {
                    if (mesh != null && !mesh.isReadable)
                    {
                        // (the game's meshes can't be read from scripts: a copy read back from the GPU can)
                        copy = ModelMeshes.ReadableCopy(mesh, ModelMeshes.RestBindposes(bodies[i]));
                        mesh = copy;
                    }
                    weights = mesh != null ? mesh.boneWeights : null;
                }
                finally
                {
                    if (copy != null)
                    {
                        Object.Destroy(copy);
                    }
                }
                if (weights == null || weights.Length != counts[i] || at + counts[i] > onHead.Length)
                {
                    return false;
                }
                Transform[] bones = bodies[i].bones;
                var isHead = new bool[bones.Length];
                for (int b = 0; b < bones.Length; b++)
                {
                    isHead[b] = bones[b] != null && under.Contains(bones[b]);
                }
                for (int v = 0; v < weights.Length; v++)
                {
                    BoneWeight w = weights[v];
                    float sum = On(isHead, w.boneIndex0, w.weight0) + On(isHead, w.boneIndex1, w.weight1) + On(isHead, w.boneIndex2, w.weight2) + On(isHead, w.boneIndex3, w.weight3);
                    onHead[at + v] = sum >= 0.5f;
                    any |= onHead[at + v];
                }
            }
            return any;
        }

        private static float On(bool[] isHead, int bone, float weight) => bone >= 0 && bone < isHead.Length && isHead[bone] ? weight : 0f;

        private void PoseTemplate(Animator animator, PoseKind kind, float phase)
        {
            if (animator != null && animator.isActiveAndEnabled)
            {
                Driver.Pose(animator, kind, phase);
                animator.Update(0f);
            }
        }

        // The crowd's mesh index for a pose (run frames by phase, 0..1 through the cycle).
        public static int PoseIndex(PoseKind kind, float phase)
        {
            switch (kind)
            {
                case PoseKind.Run:
                    int frame = Mathf.FloorToInt(phase * RunFrames) % RunFrames;
                    return 1 + (frame < 0 ? frame + RunFrames : frame);
                case PoseKind.Jump:
                    return RunFrames + 1;
                case PoseKind.Fall:
                    return RunFrames + 2;
                case PoseKind.Slide:
                    return RunFrames + 3;
                case PoseKind.Dash:
                    return RunFrames + 4;
                default:
                    return 0;
            }
        }

        private static void PoseOf(int index, out PoseKind kind, out float phase)
        {
            phase = 0.3f;
            if (index == 0)
            {
                kind = PoseKind.Idle;
                phase = 0f;
            }
            else if (index <= RunFrames)
            {
                kind = PoseKind.Run;
                phase = (index - 1) / (float)RunFrames;
            }
            else
            {
                kind = (PoseKind)(index - RunFrames + 1);  // Jump, Fall, Slide, Dash
            }
        }

        // The body as it's posed now, in the template holder's space (which is the world's: it sits at the origin).
        private bool Snapshot(SkinnedMeshRenderer[] bodies, Matrix4x4[][] rest, List<Vector3> verts, List<Vector3> normals, List<int> tris, List<int> counts)
        {
            if (mode == BakeMode.Unknown)
            {
                mode = PickMode(bodies, rest);
            }
            if (mode == BakeMode.Bones)
            {
                ModelMeshes.BoneBoxes(bodies, boneHeight, verts, normals, tris);
                counts.Add(verts.Count);
                return verts.Count > 0;
            }
            for (int i = 0; i < bodies.Length; i++)
            {
                if (!ModelMeshes.TryBake(bodies[i], scratch))
                {
                    return false;
                }
                int before = verts.Count;
                ModelMeshes.Append(scratch, ModelMeshes.BakedToWorld(bodies[i].transform, bakedInOwnSpace[i]), verts, normals, tris);
                counts.Add(verts.Count - before);
            }
            return true;
        }

        private BakeMode PickMode(SkinnedMeshRenderer[] bodies, Matrix4x4[][] rest)
        {
            bakedInOwnSpace = new bool[bodies.Length];
            if (AllBake(bodies))
            {
                return BakeMode.Direct;
            }
            // The game's meshes aren't readable from scripts: read them back from the GPU, then bake those.
            for (int i = 0; i < bodies.Length; i++)
            {
                Mesh copy = ModelMeshes.ReadableCopy(bodies[i].sharedMesh, rest[i]);
                if (copy == null)
                {
                    break;
                }
                owned.Add(copy);
                bodies[i].sharedMesh = copy;
            }
            if (AllBake(bodies))
            {
                return BakeMode.Copied;
            }
            float low = float.MaxValue, high = float.MinValue;
            foreach (Transform b in bodies.SelectMany(s => s.bones).Where(b => b != null))
            {
                low = Mathf.Min(low, b.position.y);
                high = Mathf.Max(high, b.position.y);
            }
            boneHeight = high > low ? (high - low) * 1.1f : 1f;
            return BakeMode.Bones;
        }

        private bool AllBake(SkinnedMeshRenderer[] bodies)
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                if (!ModelMeshes.TryBake(bodies[i], scratch))
                {
                    return false;
                }
                bakedInOwnSpace[i] = ModelMeshes.BakesInOwnSpace(bodies[i], scratch);
            }
            return true;
        }

        // Models made in Blender can face any way; V1's toes say which way is forward. Snapped to quarter turns.
        private static float FacingCorrection(Transform model)
        {
            Transform toeL = Puppet.Find(model, "toe.L", "Toe.L", "LeftToeBase");
            Transform toeR = Puppet.Find(model, "toe.R", "Toe.R", "RightToeBase");
            Transform footL = Puppet.Find(model, "foot.L", "Foot.L", "LeftFoot");
            Transform footR = Puppet.Find(model, "foot.R", "Foot.R", "RightFoot");
            if (toeL == null || toeR == null || footL == null || footR == null)
            {
                return 0f;
            }
            Vector3 toes = (toeL.position + toeR.position - footL.position - footR.position) * 0.5f;
            toes.y = 0f;
            if (toes.sqrMagnitude < 1e-8f)
            {
                return 0f;
            }
            float angle = Vector3.SignedAngle(toes, Vector3.forward, Vector3.up);
            return Mathf.Round(angle / 90f) * 90f % 360f;
        }

        private static string PathFrom(Transform root, Transform t)
        {
            var steps = new List<string>();
            for (; t != null && t != root; t = t.parent)
            {
                steps.Add(t.name);
            }
            steps.Reverse();
            return string.Join("/", steps);
        }

        // An animated copy for one ghost at a time, under parent (inactive until a ghost takes it).
        public LivePuppet NewPuppet(Transform parent, Material ghostMaterial)
        {
            var holder = new GameObject("UltraAttempts ghost " + Label) { layer = layer };
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject model = Object.Instantiate(template, holder.transform, false);
            model.name = template.name;
            model.SetActive(true);
            SkinnedMeshRenderer[] skinned = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer[] body = bodyIndex.Select(i => skinned[i]).ToArray();
            Transform hand = handPath != null ? model.transform.Find(handPath) : null;
            Animator animator = model.GetComponent<Animator>();
            if (animator != null)
            {
                animator.fireEvents = false;
            }
            return new LivePuppet(holder, animator, body, hand, LiveBakes, ghostMaterial);
        }

        // Where a body renderer's baked vertices are, and how big one of their units is in the world.
        public Matrix4x4 BakedToWorld(int body, Transform t) => ModelMeshes.BakedToWorld(t, bakedInOwnSpace[body]);

        public float BakedUnit(int body, Transform t)
        {
            if (!bakedInOwnSpace[body])
            {
                return 1f;
            }
            Vector3 s = t.lossyScale;
            return Mathf.Max(1e-4f, (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f);
        }

        public Color32[] Shading(int body) => shading != null && body < shading.Length ? shading[body] : null;

        public Color32[] White(int body) => white != null && body < white.Length ? white[body] : null;

        public void Dispose()
        {
            if (bakeHolder != null)
            {
                Object.Destroy(bakeHolder);
            }
            if (Poses != null)
            {
                foreach (Mesh mesh in Poses)
                {
                    Object.Destroy(mesh);
                }
            }
            if (Head != null)
            {
                Object.Destroy(Head);
            }
            foreach (Mesh mesh in owned)
            {
                Object.Destroy(mesh);
            }
            owned.Clear();
            if (scratch != null)
            {
                Object.Destroy(scratch);
            }
        }
    }

    // An animated ghost: a clone of the model's template, given to one ghost at a time (the followed one,
    // the winner, the nearest), driven by its recording every frame.
    internal sealed class LivePuppet
    {
        private static readonly MaterialPropertyBlock Empty = new MaterialPropertyBlock();
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public readonly GameObject Holder;
        public readonly Animator Animator;
        public readonly SkinnedMeshRenderer[] Body;
        public readonly Transform Hand;
        public readonly Mesh[] Live;        // the body baked each frame (when the model can be baked)
        private readonly Material[][] originals;
        private readonly Material[][] ghosted;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private int look = -1;              // what the renderers are set up as: 0 see-through, 1 the real model
        private Color lastGlow = Color.clear;

        public Ghost Ghost;
        public bool Fresh;                  // just given to a ghost (or seeked): jump straight to its state

        // Animation state, between frames.
        public Vector3 Facing;
        public bool WasAirborne;
        public bool WasDashing;
        public float LastRise;
        public int LastSample;
        public float LegsFB;
        public float LegsLR;

        // This frame, for the pass after the animators.
        public bool Shown;
        public bool Featured;
        public bool Hurt;
        public bool Weapons;
        public Color Color;
        public float Alpha;
        public float Size;
        public Ghost.Pose Pose;
        public GhostMotion Motion;
        public Vector3 Bottom;
        public Quaternion Rotation;
        public float Height;
        public int PoseIndex;

        public LivePuppet(GameObject holder, Animator animator, SkinnedMeshRenderer[] body, Transform hand, bool bakes, Material ghostMaterial)
        {
            Holder = holder;
            Animator = animator;
            Body = body;
            Hand = hand;
            originals = body.Select(r => r.sharedMaterials).ToArray();
            ghosted = originals.Select(m => Enumerable.Repeat(ghostMaterial, Mathf.Max(1, m.Length)).ToArray()).ToArray();
            if (bakes)
            {
                Live = body.Select(r =>
                {
                    var mesh = new Mesh { name = "UltraAttempts live " + r.name, hideFlags = HideFlags.HideAndDontSave };
                    mesh.MarkDynamic();
                    return mesh;
                }).ToArray();
            }
        }

        public void Take(Ghost g)
        {
            Ghost = g;
            Fresh = true;
            Holder.SetActive(true);
        }

        public void Release()
        {
            Ghost = null;
            Shown = false;
            if (Holder != null)
            {
                Holder.SetActive(false);
            }
        }

        // The real model (its own materials) or a see-through one in the ghost's colour. With baked copies,
        // the see-through one is drawn from those (shaded), so the renderers themselves stay hidden.
        public void SetLook(bool real, bool bakes)
        {
            int wanted = real ? 1 : 0;
            if (wanted == look)
            {
                return;
            }
            look = wanted;
            lastGlow = Color.clear;
            for (int i = 0; i < Body.Length; i++)
            {
                Body[i].sharedMaterials = real ? originals[i] : ghosted[i];
                Body[i].forceRenderingOff = !real && bakes;
                for (int m = 0; m < Body[i].sharedMaterials.Length; m++)
                {
                    Body[i].SetPropertyBlock(Empty, m);
                }
            }
        }

        // See-through and flat in one colour (when there are no baked copies to shade).
        public void Tint(Color c)
        {
            block.Clear();
            block.SetColor(ColorId, c);
            for (int i = 0; i < Body.Length; i++)
            {
                for (int m = 0; m < ghosted[i].Length; m++)
                {
                    Body[i].SetPropertyBlock(block, m);
                }
            }
        }

        // V1's wings (its materials after the first, as PlatformerMovement lights them) glow in the ghost's colour.
        public void Glow(Color c)
        {
            if (c == lastGlow)
            {
                return;
            }
            lastGlow = c;
            for (int i = 0; i < Body.Length; i++)
            {
                for (int m = 1; m < originals[i].Length; m++)
                {
                    Material mat = originals[i][m];
                    if (mat == null || !mat.HasProperty(UKShaderProperties.EmissiveColor))
                    {
                        continue;
                    }
                    block.Clear();
                    block.SetColor(UKShaderProperties.EmissiveColor, c);
                    block.SetFloat(UKShaderProperties.EmissiveIntensity, 1.2f);
                    Body[i].SetPropertyBlock(block, m);
                }
            }
        }

        public void Dispose()
        {
            if (Holder != null)
            {
                Object.Destroy(Holder);
            }
            if (Live != null)
            {
                foreach (Mesh mesh in Live)
                {
                    Object.Destroy(mesh);
                }
            }
        }
    }

    // Plays a model's animator from a ghost's recording. Each controller has its own parameters.
    internal abstract class AnimDriver
    {
        protected readonly HashSet<string> bools = new HashSet<string>();
        protected readonly HashSet<string> floats = new HashSet<string>();
        protected readonly HashSet<string> triggers = new HashSet<string>();

        protected AnimDriver(Animator a)
        {
            if (a == null || a.runtimeAnimatorController == null)
            {
                return;
            }
            foreach (AnimatorControllerParameter p in a.parameters)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        bools.Add(p.name);
                        break;
                    case AnimatorControllerParameterType.Float:
                        floats.Add(p.name);
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        triggers.Add(p.name);
                        break;
                }
            }
        }

        public abstract string Name { get; }

        // Whether the body turns to where it runs (else to where it looks, the legs running any way).
        public virtual bool FacesHeading => true;

        // Run cycles a second at animator speed 1.
        public virtual float RunCycles => 1f;

        public string Parameters => string.Join(", ", bools.Concat(floats).Concat(triggers));

        // A pose to bake (or to start a ghost in): parameters, layer weights and the state at a phase.
        public abstract bool Pose(Animator a, PoseKind kind, float phase);

        // Straight into a ghost's state, without blending from whatever the copy did before.
        public virtual void Snap(Animator a, LivePuppet p, PoseKind kind, float phase) => Pose(a, kind, phase);

        // Every frame for an animated ghost.
        public abstract void Drive(Animator a, LivePuppet p, in GhostMotion m, in GhostEvents e, float dt);

        public static AnimDriver For(Animator a)
        {
            if (a != null && a.runtimeAnimatorController != null && a.isActiveAndEnabled)
            {
                var v1 = new V1Driver(a);
                if (v1.Fits(a))
                {
                    return v1;
                }
                var v2 = new V2Driver(a);
                if (v2.Fits(a))
                {
                    return v2;
                }
                ModelMeshes.LogOnce($"animator {a.runtimeAnimatorController.name} on {a.name}: not one the ghosts know (parameters: {v2.Parameters}; {a.layerCount} layers); the model holds still");
            }
            return new StillDriver();
        }

        protected void Set(Animator a, string name, bool value)
        {
            if (bools.Contains(name))
            {
                a.SetBool(name, value);
            }
        }

        protected void Set(Animator a, string name, float value)
        {
            if (floats.Contains(name))
            {
                a.SetFloat(name, value);
            }
        }

        protected void Trigger(Animator a, string name)
        {
            if (triggers.Contains(name))
            {
                a.SetTrigger(name);
            }
        }

        // A state's hash by its name, or its "Layer.Name" path (0: the layer has no such state).
        private static int StateHash(Animator a, int layer, string state)
        {
            if (layer >= a.layerCount)
            {
                return 0;
            }
            int hash = Animator.StringToHash(state);
            if (a.HasState(layer, hash))
            {
                return hash;
            }
            hash = Animator.StringToHash(a.GetLayerName(layer) + "." + state);
            return a.HasState(layer, hash) ? hash : 0;
        }

        protected static bool Has(Animator a, int layer, string state) => StateHash(a, layer, state) != 0;

        protected static bool Play(Animator a, int layer, string state, float phase)
        {
            int hash = StateHash(a, layer, state);
            if (hash == 0)
            {
                return false;
            }
            a.Play(hash, layer, phase);
            return true;
        }
    }

    // V1Platformer.controller (the platformer secret level's V1): bools Running, InAir, Sliding; Jump and
    // Dash played straight, the way PlatformerMovement does.
    internal sealed class V1Driver : AnimDriver
    {
        public V1Driver(Animator a) : base(a)
        {
        }

        public override string Name => "V1Platformer";

        public bool Fits(Animator a) => bools.Contains("Running") && Has(a, 0, "Running") && Has(a, 0, "Idle");

        public override bool Pose(Animator a, PoseKind kind, float phase)
        {
            Set(a, "Running", kind == PoseKind.Run);
            Set(a, "InAir", kind == PoseKind.Jump || kind == PoseKind.Fall || kind == PoseKind.Dash);
            Set(a, "Sliding", kind == PoseKind.Slide);
            Set(a, "Spinning", false);
            switch (kind)
            {
                case PoseKind.Run:
                    return Play(a, 0, "Running", phase);
                case PoseKind.Jump:
                    return Play(a, 0, "Jump", 0.2f);
                case PoseKind.Fall:
                    return Play(a, 0, "Falling", phase);
                case PoseKind.Slide:
                    return Play(a, 0, "Sliding", phase);
                case PoseKind.Dash:
                    return Play(a, 0, "Dash", 0.3f);
                default:
                    return Play(a, 0, "Idle", phase);
            }
        }

        public override void Drive(Animator a, LivePuppet p, in GhostMotion m, in GhostEvents e, float dt)
        {
            Set(a, "Running", m.Running);
            Set(a, "InAir", m.Airborne && !m.Sliding);
            Set(a, "Sliding", m.Sliding);
            if (e.Dashed)
            {
                Play(a, 0, "Dash", 0f);
            }
            else if (e.Jumped)
            {
                Play(a, 0, "Jump", 0f);
            }
        }
    }

    // V2's controllers, driven as V2.cs drives them. V2.controller: Standing/Slide on the base layer, the
    // legs on LegsFB (RunF, RunB, Jump, InAir) and LegsLR (RunL, RunR, ...) weighted by how far the way it
    // runs is from the way it faces; bools InAir, Sliding, RunningLeft, RunningBack, trigger Jump.
    // V2Blend.controller instead blends the run by VelX/VelY and has Dodging, Shoot and Punch.
    internal sealed class V2Driver : AnimDriver
    {
        private readonly bool legs;
        private readonly bool blend;

        public V2Driver(Animator a) : base(a)
        {
            legs = a != null && Has(a, 1, "RunF");
            blend = floats.Contains("VelX") && floats.Contains("VelY");
        }

        public override string Name => blend ? "V2Blend" : "V2";

        public override bool FacesHeading => false;

        public override float RunCycles => blend ? 1.5f : 2.25f;  // (RunF: 0.67 s at state speed 1.5)

        public bool Fits(Animator a) => bools.Contains("InAir") && (Has(a, 0, "Standing") || Has(a, 0, "Grounded"));

        public override bool Pose(Animator a, PoseKind kind, float phase)
        {
            bool air = kind == PoseKind.Jump || kind == PoseKind.Fall || kind == PoseKind.Dash;
            Set(a, "InAir", air);
            Set(a, "Sliding", kind == PoseKind.Slide);
            Set(a, "Dodging", kind == PoseKind.Dash);
            Set(a, "RunningLeft", false);
            Set(a, "RunningBack", false);
            Set(a, "VelX", 0f);
            Set(a, "VelY", kind == PoseKind.Run || kind == PoseKind.Dash ? 1f : 0f);
            if (legs)
            {
                a.SetLayerWeight(1, kind == PoseKind.Idle || kind == PoseKind.Slide ? 0f : 1f);
                if (a.layerCount > 2)
                {
                    a.SetLayerWeight(2, 0f);
                }
            }
            if (kind == PoseKind.Slide)
            {
                return Play(a, 0, "Slide", phase);
            }
            if (blend)
            {
                switch (kind)
                {
                    case PoseKind.Jump:
                        return Play(a, 0, "Jump", 0.5f);
                    case PoseKind.Fall:
                        return Play(a, 0, "InAir", phase);
                    case PoseKind.Dash:
                        return Play(a, 0, "Dodging", phase) || Play(a, 0, "InAir", phase);
                    default:
                        return Play(a, 0, "Grounded", phase);
                }
            }
            bool stands = Play(a, 0, "Standing", 0f);
            if (!legs)
            {
                return stands && kind == PoseKind.Idle;
            }
            switch (kind)
            {
                case PoseKind.Run:
                    return Play(a, 1, "RunF", phase);
                case PoseKind.Jump:
                    return Play(a, 1, "Jump", 0.5f);
                case PoseKind.Fall:
                case PoseKind.Dash:
                    return Play(a, 1, "InAir", phase);
                default:
                    return stands;
            }
        }

        public override void Snap(Animator a, LivePuppet p, PoseKind kind, float phase)
        {
            Pose(a, kind, phase);
            p.LegsFB = kind == PoseKind.Idle || kind == PoseKind.Slide ? 0f : 1f;
            p.LegsLR = 0f;
        }

        public override void Drive(Animator a, LivePuppet p, in GhostMotion m, in GhostEvents e, float dt)
        {
            bool air = m.Airborne || m.Dashing;
            Set(a, "InAir", air);
            Set(a, "Sliding", m.Sliding);
            Set(a, "Dodging", m.Dashing);
            if (e.Jumped || e.Dashed)
            {
                Trigger(a, "Jump");
            }
            if (e.Shot)
            {
                Trigger(a, "Shoot");
            }
            if (e.Punched)
            {
                Trigger(a, "Punch");
            }

            bool running = m.Running;
            float angle = m.Heading == Vector3.zero ? 0f : Vector3.SignedAngle(p.Facing, m.Heading, m.Up);
            float off = Mathf.Abs(angle);
            Set(a, "RunningLeft", running && angle < 0f);
            Set(a, "RunningBack", running && off > 90f);
            if (blend)
            {
                float k = running ? Mathf.Clamp01(m.Speed / 8f) : 0f;
                Set(a, "VelX", Mathf.Sin(angle * Mathf.Deg2Rad) * k);
                Set(a, "VelY", Mathf.Cos(angle * Mathf.Deg2Rad) * k);
            }
            if (legs)
            {
                float ease = 1f - Mathf.Exp(-dt * 12f);
                p.LegsFB = Mathf.Lerp(p.LegsFB, running || air ? 1f : 0f, ease);
                p.LegsLR = Mathf.Lerp(p.LegsLR, running ? (off <= 90f ? off / 90f : Mathf.Abs(180f - off) / 90f) : 0f, ease);
                a.SetLayerWeight(1, p.LegsFB);
                if (a.layerCount > 2)
                {
                    a.SetLayerWeight(2, p.LegsLR);
                }
            }
        }
    }

    // No controller it knows: the model holds still.
    internal sealed class StillDriver : AnimDriver
    {
        public StillDriver() : base(null)
        {
        }

        public override string Name => "none";

        public override bool Pose(Animator a, PoseKind kind, float phase) => kind == PoseKind.Idle;

        public override void Drive(Animator a, LivePuppet p, in GhostMotion m, in GhostEvents e, float dt)
        {
        }
    }

    // Calls back after this frame's animators have moved (the animated ghosts' baked copies need that pose).
    internal sealed class GhostFrameHook : MonoBehaviour
    {
        public Action Late;

        private void LateUpdate()
        {
            Late?.Invoke();
        }
    }

    // Mesh work for the models: snapshots of skinned meshes, reading the game's (GPU-only) meshes back,
    // and the shaded, see-through-ready meshes the crowd is drawn with.
    internal static class ModelMeshes
    {
        private const MeshUpdateFlags Quiet = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;

        private static readonly HashSet<string> logged = new HashSet<string>();
        private static readonly List<Vector3> vertexList = new List<Vector3>();
        private static readonly List<Vector3> normalList = new List<Vector3>();
        private static readonly List<int> indexList = new List<int>();

        public static void LogOnce(string message)
        {
            if (logged.Add(message))
            {
                Plugin.Log.LogInfo(message);
            }
        }

        public static bool TryBake(SkinnedMeshRenderer smr, Mesh into)
        {
            try
            {
                smr.BakeMesh(into, true);
                return into.vertexCount > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // BakeMesh's vertices are relative to the renderer's Transform; whether its scale is taken out too
        // is measured, against the mesh's own size, rather than trusted.
        public static bool BakesInOwnSpace(SkinnedMeshRenderer smr, Mesh baked)
        {
            Vector3 s = smr.transform.lossyScale;
            float scale = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            float own = smr.sharedMesh != null ? smr.sharedMesh.bounds.size.magnitude : 0f;
            baked.RecalculateBounds();
            float size = baked.bounds.size.magnitude;
            if (own < 1e-6f || size < 1e-6f || scale < 1e-6f || Mathf.Abs(Mathf.Log(scale)) < 0.1f)
            {
                return true;
            }
            float ratio = size / own;
            return Mathf.Abs(Mathf.Log(ratio)) <= Mathf.Abs(Mathf.Log(ratio / scale));
        }

        public static Matrix4x4 BakedToWorld(Transform t, bool ownSpace) =>
            ownSpace ? t.localToWorldMatrix : Matrix4x4.TRS(t.position, t.rotation, Vector3.one);

        // A skinned part as it's posed now (null-free): baked if possible, else read back from the GPU and
        // baked, else its mesh as it is (the pose it was modelled in).
        public static Mesh Snapshot(SkinnedMeshRenderer smr, List<Mesh> owned, out Matrix4x4 toWorld)
        {
            var baked = new Mesh { name = smr.name + " (baked)", hideFlags = HideFlags.HideAndDontSave };
            owned.Add(baked);
            if (TryBake(smr, baked))
            {
                toWorld = BakedToWorld(smr.transform, BakesInOwnSpace(smr, baked));
                return baked;
            }
            Mesh copy = ReadableCopy(smr.sharedMesh, RestBindposes(smr));
            if (copy != null)
            {
                owned.Add(copy);
                smr.sharedMesh = copy;
                if (TryBake(smr, baked))
                {
                    toWorld = BakedToWorld(smr.transform, BakesInOwnSpace(smr, baked));
                    return baked;
                }
            }
            toWorld = smr.transform.localToWorldMatrix;
            return smr.sharedMesh;
        }

        // Bind poses from the skeleton as it is now (a fresh copy of a prefab: the pose it was modelled in).
        public static Matrix4x4[] RestBindposes(SkinnedMeshRenderer smr)
        {
            Transform[] bones = smr.bones;
            var result = new Matrix4x4[bones.Length];
            Matrix4x4 own = smr.transform.localToWorldMatrix;
            for (int i = 0; i < bones.Length; i++)
            {
                result[i] = bones[i] != null ? bones[i].worldToLocalMatrix * own : Matrix4x4.identity;
            }
            return result;
        }

        // A readable copy of a mesh whose data only lives on the GPU (the game's aren't Read/Write enabled):
        // every vertex stream and the indices, read back. Null if they can't be.
        public static Mesh ReadableCopy(Mesh source, Matrix4x4[] restBindposes)
        {
            if (source == null)
            {
                return null;
            }
            var copy = new Mesh { name = source.name + " (readable)", hideFlags = HideFlags.HideAndDontSave };
            try
            {
                copy.SetVertexBufferParams(source.vertexCount, source.GetVertexAttributes());
                for (int stream = 0; stream < source.vertexBufferCount; stream++)
                {
                    using (GraphicsBuffer buffer = source.GetVertexBuffer(stream))
                    {
                        var data = new byte[buffer.count * buffer.stride];
                        buffer.GetData(data);
                        copy.SetVertexBufferData(data, 0, 0, data.Length, stream, Quiet);
                    }
                }
                using (GraphicsBuffer indices = source.GetIndexBuffer())
                {
                    copy.SetIndexBufferParams(indices.count, source.indexFormat);
                    if (source.indexFormat == IndexFormat.UInt16)
                    {
                        var data = new ushort[indices.count];
                        indices.GetData(data);
                        copy.SetIndexBufferData(data, 0, 0, data.Length, Quiet);
                    }
                    else
                    {
                        var data = new uint[indices.count];
                        indices.GetData(data);
                        copy.SetIndexBufferData(data, 0, 0, data.Length, Quiet);
                    }
                }
                copy.subMeshCount = source.subMeshCount;
                for (int i = 0; i < source.subMeshCount; i++)
                {
                    copy.SetSubMesh(i, source.GetSubMesh(i), Quiet);
                }
                Matrix4x4[] bindposes = null;
                try
                {
                    bindposes = source.bindposes;
                }
                catch (Exception)
                {
                    // (not readable either: the skeleton's rest pose stands in)
                }
                if (bindposes != null && bindposes.Length > 0)
                {
                    copy.bindposes = bindposes;
                }
                else if (restBindposes != null && restBindposes.Length > 0)
                {
                    copy.bindposes = restBindposes;
                }
                copy.bounds = source.bounds;
                if (copy.vertexCount == 0)
                {
                    throw new InvalidOperationException("no vertices came back");
                }
                return copy;
            }
            catch (Exception e)
            {
                LogOnce($"couldn't read {source.name} back from the GPU: {e.GetBaseException().Message}");
                Object.Destroy(copy);
                return null;
            }
        }

        // Adds a (readable) mesh's triangles, moved by m.
        public static void Append(Mesh mesh, Matrix4x4 m, List<Vector3> verts, List<Vector3> normals, List<int> tris)
        {
            int start = verts.Count;
            mesh.GetVertices(vertexList);
            mesh.GetNormals(normalList);
            bool hasNormals = normalList.Count == vertexList.Count;
            for (int i = 0; i < vertexList.Count; i++)
            {
                verts.Add(m.MultiplyPoint3x4(vertexList[i]));
                normals.Add(hasNormals ? m.MultiplyVector(normalList[i]).normalized : Vector3.up);
            }
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                {
                    continue;
                }
                mesh.GetTriangles(indexList, s);
                foreach (int index in indexList)
                {
                    tris.Add(start + index);
                }
            }
        }

        // One mesh, one submesh, white shading baked into its vertex colours (the ghost material is unlit
        // and tints by _Color); moved so floor is at 0 and scaled so height is 1.
        public static Mesh Build(string name, IList<Vector3> verts, IList<Vector3> normals, IList<int> tris, float floor = 0f, float height = 1f)
        {
            var v = new Vector3[verts.Count];
            var c = new Color32[verts.Count];
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = new Vector3(verts[i].x, verts[i].y - floor, verts[i].z) / height;
                c[i] = Meshes.Shade(normals[i]);
            }
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            if (v.Length > 65000)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetTriangles(tris as int[] ?? tris.ToArray(), 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // The crowd model when the body can't be baked: a box along every bone (not the fingers), as thick
        // as that part of V1 is, so it still moves like it.
        public static void BoneBoxes(SkinnedMeshRenderer[] bodies, float height, List<Vector3> verts, List<Vector3> normals, List<int> tris)
        {
            var bones = new HashSet<Transform>(bodies.SelectMany(s => s.bones).Where(b => b != null));
            foreach (Transform b in bones)
            {
                foreach (Transform child in b)
                {
                    string name = child.name.ToLowerInvariant();
                    bool bone = bones.Contains(child) || name.EndsWith("_end");
                    if (!bone || name.StartsWith("f_") || name.StartsWith("thumb") || name.Contains("finger") || name.Contains("heel"))
                    {
                        continue;
                    }
                    Thickness(b.name.ToLowerInvariant(), out float width, out float depth);
                    Box(b.position, child.position, width * height, depth * height, verts, normals, tris);
                }
            }
        }

        private static void Thickness(string bone, out float width, out float depth)
        {
            if (bone.Contains("wing"))
            {
                width = 0.045f;
                depth = 0.012f;
                return;
            }
            if (bone == "spine.006" || bone.Contains("head"))
            {
                width = depth = 0.12f;
            }
            else if (bone == "spine.004" || bone == "spine.005" || bone.Contains("neck"))
            {
                width = depth = 0.055f;
            }
            else if (bone.StartsWith("spine") || bone.Contains("hips") || bone.Contains("chest"))
            {
                width = 0.13f;
                depth = 0.09f;
            }
            else if (bone.Contains("thigh"))
            {
                width = depth = 0.065f;
            }
            else if (bone.Contains("arm") || bone.Contains("shoulder") || bone.Contains("shin"))
            {
                width = depth = 0.05f;
            }
            else
            {
                width = depth = 0.04f;
            }
        }

        private static void Box(Vector3 a, Vector3 b, float width, float depth, List<Vector3> verts, List<Vector3> normals, List<int> tris)
        {
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-4f)
            {
                return;
            }
            Vector3 along = axis / length;
            Vector3 side = Vector3.Cross(along, Vector3.forward);
            if (side.sqrMagnitude < 1e-4f)
            {
                side = Vector3.Cross(along, Vector3.right);
            }
            side = side.normalized * (width * 0.5f);
            Vector3 front = Vector3.Cross(side, along).normalized * (depth * 0.5f);
            Vector3 middle = (a + b) * 0.5f;
            Vector3[] c =
            {
                a - side - front, a + side - front, a + side + front, a - side + front,
                b - side - front, b + side - front, b + side + front, b - side + front,
            };
            int[,] faces = { { 0, 1, 2, 3 }, { 4, 7, 6, 5 }, { 0, 4, 5, 1 }, { 1, 5, 6, 2 }, { 2, 6, 7, 3 }, { 3, 7, 4, 0 } };
            for (int f = 0; f < 6; f++)
            {
                Vector3 p0 = c[faces[f, 0]], p1 = c[faces[f, 1]], p2 = c[faces[f, 2]], p3 = c[faces[f, 3]];
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                if (Vector3.Dot(n, (p0 + p2) * 0.5f - middle) < 0f)
                {
                    // (clockwise seen from outside, Unity's front faces, is where this cross product points out)
                    (p1, p3) = (p3, p1);
                    n = -n;
                }
                int start = verts.Count;
                verts.Add(p0);
                verts.Add(p1);
                verts.Add(p2);
                verts.Add(p3);
                for (int k = 0; k < 4; k++)
                {
                    normals.Add(n);
                }
                tris.Add(start);
                tris.Add(start + 1);
                tris.Add(start + 2);
                tris.Add(start);
                tris.Add(start + 2);
                tris.Add(start + 3);
            }
        }
    }
}
