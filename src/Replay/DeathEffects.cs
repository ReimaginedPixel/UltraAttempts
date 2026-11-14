using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraAttempts.Replay
{
    // How a ghost was the moment it died: what the death starts from.
    internal struct DeathStart
    {
        public Vector3 Bottom;      // its feet
        public Quaternion Rot;      // which way its body faced
        public Vector3 Up;
        public float Height;
        public int Pose;            // the crowd pose it was in
        public Vector3 Velocity;
        public Vector3 Eye;
        public Quaternion Look;
    }

    // Deaths (DeathAnimations): where an attempt dies, its head pops off and flies, up and on the way it was
    // going, spinning, bouncing off the level until it comes to rest, and fades after a couple of seconds; its
    // body falls over where it stood and fades; blood bursts from its neck and pools under it. The crystal
    // ghosts shatter instead (their head still flies). All of it is only drawn: nothing is made in the level
    // and nothing stays. Each death's flight is worked out once, the first time it's wanted (a raycast against
    // the level per step), and kept, so seeking and watching again show the same. A few are worked out a
    // frame (planned ahead, before they die) and a frame's blood is capped, so 30 attempts dying in one spot
    // stay cheap.
    internal sealed class DeathEffects
    {
        public const float Life = 3.2f;
        private const float KeyRate = 20f;          // flight keys a second
        private const int Keys = (int)(Life * KeyRate) + 2;
        private const int PlansPerFrame = 6;
        private const int MostKept = 600;
        private const int MostBlood = 1200;         // blood drawn in a frame, at most (the heads and bodies always are)
        private const float Gravity = 32f;
        private const float FallTime = 0.55f;
        private static readonly Color Blood = new Color(0.72f, 0.03f, 0.02f);
        private static readonly Color Pool = new Color(0.42f, 0.01f, 0.01f);

        private readonly Camera cam;
        private readonly int layer;
        private readonly Material material;
        private readonly Mesh cube, sphere, crystal;
        private readonly Func<Ghost, DeathStart> startOf;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private readonly int colorId = Shader.PropertyToID("_Color");
        private readonly int environment;
        private readonly Dictionary<Ghost, Plan> plans = new Dictionary<Ghost, Plan>();
        private readonly List<Ghost> dropping = new List<Ghost>();
        private readonly HashSet<Ghost> popped = new HashSet<Ghost>();
        private int planned, blood;
        private int made, played, rested;
        private float planSeconds, now;

        // One death worked out: the body's feet and the head's middle, KeyRate times a second.
        private sealed class Plan
        {
            public DeathStart S;
            public bool Model;              // planned for the model (else the crystal)
            public Vector3 Fall;            // which way the body falls (flat)
            public Vector3[] Body = new Vector3[Keys];
            public Vector3[] Head = new Vector3[Keys];
            public Quaternion[] HeadRot = new Quaternion[Keys];
            public float Landed = -1f;      // when the body hit the ground (-1: it didn't)
            public bool Ground;             // where the blood pools
            public Vector3 GroundAt;
            public Vector3 GroundNormal;
            public uint Seed;
            public bool Counted;
        }

        public DeathEffects(Camera cam, int layer, Material material, Mesh cube, Mesh sphere, Mesh crystal, Func<Ghost, DeathStart> startOf)
        {
            this.cam = cam;
            this.layer = layer;
            this.material = material;
            this.cube = cube;
            this.sphere = sphere;
            this.crystal = crystal;
            this.startOf = startOf;
            environment = LayerMaskDefaults.Get(LMD.Environment);
        }

        public string Summary => $"death animations: {played} played ({rested} heads came to rest), {popped.Count} as a plain pop (too many to work out that frame), worked out in {planSeconds * 1000f / Math.Max(1, made):0.0} ms each";

        public bool Any => played > 0 || popped.Count > 0;

        // ---------------------------------------------------------------- every frame

        // Before a frame's deaths (t: the replay's time).
        public void BeginFrame(float t)
        {
            now = t;
            planned = 0;
            blood = 0;
            Forget();
        }

        // One death, age seconds after it (false: not worked out yet and this frame's are used up: the caller
        // shows the plain pop). kit: the model (null: crystals).
        public bool Draw(Ghost g, float age, GhostModelKit kit, float alpha)
        {
            Plan plan = PlanFor(g, kit);
            if (plan == null)
            {
                popped.Add(g);
                return false;
            }
            if (!plan.Counted)
            {
                plan.Counted = true;
                played++;
            }
            if (age < 0f || age > Life)
            {
                return true;
            }
            Color color = g.Color;
            if (plan.Model && kit != null)
            {
                DrawModel(plan, age, kit, color, alpha);
            }
            else
            {
                DrawCrystal(plan, age, color, alpha);
            }
            return true;
        }

        // Works out deaths about to happen (before they're on screen).
        public void Ahead(Ghost g, GhostModelKit kit) => PlanFor(g, kit);

        // Where a death's head is, age seconds after (for the camera).
        public bool HeadAt(Ghost g, float age, out Vector3 at)
        {
            at = default;
            if (!plans.TryGetValue(g, out Plan plan))
            {
                return false;
            }
            Key(age, out int i, out float a);
            at = Vector3.Lerp(plan.Head[i], plan.Head[i + 1], a);
            return true;
        }

        private Plan PlanFor(Ghost g, GhostModelKit kit)
        {
            bool model = kit != null;
            if (plans.TryGetValue(g, out Plan plan) && plan.Model == model)
            {
                return plan;
            }
            if (planned >= PlansPerFrame)
            {
                return null;
            }
            planned++;
            float started = Time.realtimeSinceStartup;
            try
            {
                plan = Make(g, kit, model);
            }
            catch (Exception e)
            {
                ModelMeshes.LogOnce("working out a death animation failed (plain pops instead): " + e.Message);
                plan = null;
            }
            planSeconds += Time.realtimeSinceStartup - started;
            if (plan != null)
            {
                plans[g] = plan;
                made++;
            }
            return plan;
        }

        // (a long replay with thousands of deaths: the ones furthest from now go once there are many)
        private void Forget()
        {
            if (plans.Count <= MostKept)
            {
                return;
            }
            dropping.Clear();
            foreach (KeyValuePair<Ghost, Plan> p in plans)
            {
                dropping.Add(p.Key);
            }
            dropping.Sort((a, b) => Mathf.Abs(a.Duration - now).CompareTo(Mathf.Abs(b.Duration - now)));
            for (int i = MostKept / 2; i < dropping.Count; i++)
            {
                plans.Remove(dropping[i]);
            }
        }

        // ---------------------------------------------------------------- working one out

        private Plan Make(Ghost g, GhostModelKit kit, bool model)
        {
            DeathStart s = startOf(g);
            var plan = new Plan { S = s, Model = model, Seed = unchecked((uint)(g.Number * 7919 + g.Order * 104729) * 2654435761u) | 1u };
            Vector3 up = s.Up;
            Vector3 flat = Vector3.ProjectOnPlane(s.Velocity, up);
            Vector3 facing = s.Rot * Vector3.forward;
            // (moving: it falls on the way it was going; standing: backwards, a little to one side)
            plan.Fall = flat.magnitude > 2f ? flat.normalized : Quaternion.AngleAxis((Next(ref plan.Seed) - 0.5f) * 70f, up) * -facing;
            if (plan.Fall.sqrMagnitude < 0.5f)
            {
                plan.Fall = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            }

            Vector3 headStart;
            Quaternion headTurn;
            bool head = model && kit.Head != null;
            if (head)
            {
                headStart = s.Bottom + s.Rot * (kit.HeadAt[s.Pose] * s.Height);
                headTurn = s.Rot * kit.HeadTurn[s.Pose];
            }
            else
            {
                headStart = s.Eye;
                headTurn = s.Look;
            }

            // The body: on with what it had left (sliding to a stop on the floor), falling if it was in the air.
            Vector3 pos = s.Bottom;
            Vector3 vel = s.Velocity * 0.6f;
            bool grounded = Physics.Raycast(pos + up * 0.4f, -up, out RaycastHit floor, 0.9f, environment, QueryTriggerInteraction.Ignore);
            if (grounded)
            {
                pos = floor.point;
                vel = Vector3.ProjectOnPlane(vel, up) * 0.6f;
                plan.Landed = 0f;
            }
            float dt = 1f / KeyRate;
            for (int k = 0; k < Keys; k++)
            {
                plan.Body[k] = pos;
                if (vel.sqrMagnitude < 0.01f && grounded)
                {
                    continue;
                }
                if (!grounded)
                {
                    vel -= up * (Gravity * dt);
                }
                Vector3 step = vel * dt;
                float length = step.magnitude;
                if (length > 1e-4f && Physics.Raycast(pos + up * 0.3f, step / length, out RaycastHit hit, length + 0.05f, environment, QueryTriggerInteraction.Ignore))
                {
                    pos = hit.point - up * 0.3f + hit.normal * 0.05f;
                    if (Vector3.Dot(hit.normal, up) > 0.5f)
                    {
                        pos = hit.point;
                        grounded = true;
                        plan.Landed = plan.Landed < 0f ? k * dt : plan.Landed;
                        vel = Vector3.ProjectOnPlane(vel, up) * 0.3f;
                    }
                    else
                    {
                        vel = Vector3.ProjectOnPlane(vel, hit.normal) * 0.2f;
                    }
                }
                else
                {
                    pos += step;
                }
                if (grounded)
                {
                    vel *= Mathf.Exp(-dt * 6f);
                    if (vel.sqrMagnitude >= 0.01f && !Physics.Raycast(pos + up * 0.4f, -up, 0.9f, environment, QueryTriggerInteraction.Ignore))
                    {
                        grounded = false;  // (slid off an edge)
                    }
                }
            }

            // The head: off up and along, spinning, bouncing (losing most of its speed each time) until it rests.
            float radius = head ? Mathf.Max(0.08f, kit.HeadSize * s.Height * 0.8f) : 0.3f;
            Vector3 side = Vector3.Cross(up, plan.Fall);
            Vector3 hv = Vector3.ClampMagnitude(s.Velocity, 35f) * 0.75f + up * (8.5f + 3f * Next(ref plan.Seed))
                + plan.Fall * (1.5f + Next(ref plan.Seed)) + side * ((Next(ref plan.Seed) - 0.5f) * 4f);
            Vector3 axis = new Vector3(Next(ref plan.Seed) - 0.5f, Next(ref plan.Seed) - 0.5f, Next(ref plan.Seed) - 0.5f);
            axis = axis.sqrMagnitude > 1e-4f ? axis.normalized : side;
            float spin = 540f + 600f * Next(ref plan.Seed);
            Vector3 hp = headStart;
            Quaternion hr = headTurn;
            bool resting = false;
            for (int k = 0; k < Keys; k++)
            {
                plan.Head[k] = hp;
                plan.HeadRot[k] = hr;
                if (resting)
                {
                    continue;
                }
                hv -= up * (Gravity * dt);
                Vector3 step = hv * dt;
                float length = step.magnitude;
                if (length > 1e-4f && Physics.Raycast(hp, step / length, out RaycastHit hit, length + radius, environment, QueryTriggerInteraction.Ignore))
                {
                    hp = hit.point + hit.normal * radius;
                    Vector3 into = hit.normal * Vector3.Dot(hv, hit.normal);
                    hv = (hv - into) * 0.7f - into * 0.42f;
                    spin *= 0.55f;
                    if (Vector3.Dot(hit.normal, up) > 0.6f && hv.magnitude < 2.2f)
                    {
                        resting = true;
                        rested++;
                    }
                }
                else
                {
                    hp += step;
                }
                hr = Quaternion.AngleAxis(spin * dt, axis) * hr;
            }

            // Where the blood pools: under where the body ends up.
            Vector3 last = plan.Body[Keys - 1];
            if (Physics.Raycast(last + up * 0.5f, -up, out RaycastHit ground, 2f, environment, QueryTriggerInteraction.Ignore))
            {
                plan.Ground = true;
                plan.GroundAt = ground.point;
                plan.GroundNormal = ground.normal;
                if (plan.Landed < 0f)
                {
                    plan.Landed = Life;
                }
            }
            return plan;
        }

        // ---------------------------------------------------------------- drawing

        private static void Key(float age, out int i, out float a)
        {
            float f = Mathf.Clamp(age * KeyRate, 0f, Keys - 1.001f);
            i = (int)f;
            a = f - i;
        }

        // How the body is, age seconds in: on its feet's path, tipping over onto the way it falls (quicker and
        // quicker, as falling things do, with a little bounce), and slumping a little.
        private static Matrix4x4 BodyAt(Plan plan, float age, out Quaternion turn)
        {
            Key(age, out int i, out float a);
            Vector3 feet = Vector3.Lerp(plan.Body[i], plan.Body[i + 1], a);
            float k = Mathf.Clamp01(age / FallTime);
            float angle = 84f * k * k;
            if (age > FallTime && age < FallTime + 0.3f)
            {
                angle -= 7f * Mathf.Sin((age - FallTime) / 0.3f * Mathf.PI);
            }
            float slump = 1f - 0.1f * k;
            Vector3 axis = Vector3.Cross(plan.S.Up, plan.Fall);
            Quaternion tip = axis.sqrMagnitude > 1e-4f ? Quaternion.AngleAxis(angle, axis.normalized) : Quaternion.identity;
            turn = tip * plan.S.Rot;
            float h = plan.S.Height;
            return Matrix4x4.TRS(feet, turn, new Vector3(h * 1.03f, h * slump, h * 1.03f));
        }

        private static float Fade(float age, float from, float to) => age <= from ? 1f : age >= to ? 0f : 1f - (age - from) / (to - from);

        private void DrawModel(Plan plan, float age, GhostModelKit kit, Color color, float alpha)
        {
            Matrix4x4 body = BodyAt(plan, age, out Quaternion _);
            int pose = Mathf.Clamp(plan.S.Pose, 0, kit.Poses.Length - 1);
            float bodyAlpha = alpha * Fade(age, 1.6f, 2.4f);
            if (bodyAlpha > 0.005f)
            {
                Draw(kit.Poses[pose], body, new Color(color.r, color.g, color.b, bodyAlpha), kit.Headless != null && kit.Headless[pose] ? 1 : 0);
            }
            Key(age, out int i, out float a);
            float headAlpha = Mathf.Max(alpha, 0.6f) * Fade(age, 2.4f, Life);
            if (headAlpha > 0.005f && kit.Head != null)
            {
                Quaternion rot = Quaternion.Slerp(plan.HeadRot[i], plan.HeadRot[i + 1], a);
                float h = plan.S.Height;
                Draw(kit.Head, Matrix4x4.TRS(Vector3.Lerp(plan.Head[i], plan.Head[i + 1], a), rot, new Vector3(h, h, h)), new Color(color.r, color.g, color.b, headAlpha), 0);
            }
            // (no head split off: it falls over whole, bleeding from the top)
            Bleed(plan, age, kit.HeadAt != null ? kit.HeadAt[pose] : new Vector3(0f, 0.88f, 0f));
        }

        // Blood: a burst where the head came off, a spray from the neck while the body falls, a pool under it.
        private void Bleed(Plan plan, float age, Vector3 neckInPose)
        {
            if (blood > MostBlood)
            {
                return;
            }
            DeathStart s = plan.S;
            Vector3 up = s.Up;
            // (just under the head's middle: the neck)
            Vector3 neck = neckInPose - new Vector3(0f, 0.06f, 0f);
            Vector3 first = BodyAt(plan, 0f, out Quaternion _).MultiplyPoint3x4(neck);
            float size = s.Height / 3.3f;
            if (age < 0.4f)
            {
                float k = age / 0.4f;
                float ease = 1f - (1f - k) * (1f - k);
                Draw(sphere, Matrix4x4.TRS(first, Quaternion.identity, Vector3.one * (Mathf.Lerp(0.3f, 1.5f, ease) * size)), new Color(Blood.r, Blood.g, Blood.b, 0.6f * (1f - k)), 0);
                blood++;
            }
            uint seed = plan.Seed;
            if (age < 0.7f)
            {
                float k = age / 0.7f;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 dir = Spread(ref seed, up, 1.1f);
                    Vector3 at = first + dir * (6f * size * age) - up * (0.5f * Gravity * age * age * 0.5f);
                    Quaternion spin = Quaternion.LookRotation(dir) * Quaternion.Euler(age * 600f, age * 450f, 0f);
                    Draw(cube, Matrix4x4.TRS(at, spin, Vector3.one * (0.16f * size * (1f - k))), new Color(Blood.r, Blood.g, Blood.b, 0.95f * (1f - k)), 0);
                    blood++;
                }
            }
            // (drops leave the neck one after another while it falls, each up and out from where the neck was then)
            const int Drops = 16;
            const float Gap = 0.05f, DropLife = 0.6f;
            seed = (plan.Seed ^ 0x5bd1e995u) | 1u;  // (their own scatter: the same every frame, whatever's drawn before)
            for (int d = 0; d < Drops; d++)
            {
                float born = d * Gap;
                float since = age - born;
                Vector3 dir = Spread(ref seed, up, 0.45f);
                float speed = 4.5f + 2.5f * Next(ref seed);
                if (since < 0f || since > DropLife)
                {
                    continue;
                }
                Matrix4x4 then = BodyAt(plan, born, out Quaternion turned);
                Vector3 from = then.MultiplyPoint3x4(neck);
                Vector3 v = turned * Quaternion.Inverse(s.Rot) * dir * speed + s.Velocity * 0.25f;
                Vector3 at = from + v * since - up * (0.5f * Gravity * since * since);
                float k = since / DropLife;
                Draw(cube, Matrix4x4.TRS(at, Quaternion.LookRotation(v.sqrMagnitude > 1e-4f ? v : up), Vector3.one * (0.11f * size * (1f - 0.6f * k))), new Color(Blood.r, Blood.g, Blood.b, 0.9f * (1f - k)), 0);
                blood++;
            }
            if (plan.Ground && age > plan.Landed)
            {
                float k = Mathf.Clamp01((age - plan.Landed) / 1.4f);
                float r = 1.3f * size * (1f - (1f - k) * (1f - k));
                Vector3 n = plan.GroundNormal;
                Quaternion flat = Quaternion.FromToRotation(Vector3.up, n);
                Draw(sphere, Matrix4x4.TRS(plan.GroundAt + n * 0.03f, flat, new Vector3(r * 2f, 0.03f, r * 2f)), new Color(Pool.r, Pool.g, Pool.b, 0.75f * Fade(age, 2.2f, Life)), 0);
                blood++;
            }
        }

        // The crystal ghost shatters: shards of its body flying out, its head (and visor) off like V1's.
        private void DrawCrystal(Plan plan, float age, Color color, float alpha)
        {
            DeathStart s = plan.S;
            Vector3 up = s.Up;
            uint seed = plan.Seed;
            Vector3 middle = s.Bottom + up * (s.Height * 0.4f);
            if (age < 1.4f)
            {
                float k = age / 1.4f;
                for (int c = 0; c < 10; c++)
                {
                    Vector3 dir = Spread(ref seed, up, 1.2f);
                    float speed = 4f + 5f * Next(ref seed);
                    Vector3 at = middle + dir * (speed * age) + s.Velocity * (0.4f * age) - up * (0.5f * Gravity * age * age);
                    Quaternion spin = Quaternion.LookRotation(dir) * Quaternion.Euler(age * 500f, age * 380f, 0f);
                    float size = 0.25f + 0.2f * Next(ref seed);
                    Draw(crystal, Matrix4x4.TRS(at, spin, new Vector3(size, size * 1.6f, size)), new Color(color.r, color.g, color.b, alpha * (1f - k * k)), 0);
                }
            }
            Key(age, out int i, out float a);
            float headAlpha = Mathf.Max(alpha, 0.6f) * Fade(age, 2.4f, Life);
            if (headAlpha > 0.005f)
            {
                Vector3 at = Vector3.Lerp(plan.Head[i], plan.Head[i + 1], a);
                Quaternion rot = Quaternion.Slerp(plan.HeadRot[i], plan.HeadRot[i + 1], a);
                Draw(cube, Matrix4x4.TRS(at, rot, new Vector3(0.6f, 0.45f, 0.6f)), new Color(color.r * 0.8f, color.g * 0.8f, color.b * 0.8f, headAlpha), 0);
                Draw(cube, Matrix4x4.TRS(at + rot * new Vector3(0f, 0.03f, 0.31f), rot, new Vector3(0.48f, 0.12f, 0.03f)),
                    new Color(GhostRenderer.Visor.r, GhostRenderer.Visor.g, GhostRenderer.Visor.b, Mathf.Min(1f, headAlpha * 2f)), 0);
            }
        }

        private void Draw(Mesh mesh, Matrix4x4 matrix, Color color, int submesh)
        {
            if (mesh == null)
            {
                return;
            }
            block.SetColor(colorId, color);
            Graphics.DrawMesh(mesh, matrix, material, layer, cam, submesh, block, false, false, false);
        }

        // A direction mostly up (spread: how far out from it), the same for the same seed.
        private static Vector3 Spread(ref uint seed, Vector3 up, float spread)
        {
            var v = new Vector3((Next(ref seed) * 2f - 1f) * spread, 0f, (Next(ref seed) * 2f - 1f) * spread);
            Vector3 dir = Quaternion.FromToRotation(Vector3.up, up) * (v + Vector3.up * (0.6f + 0.6f * Next(ref seed)));
            return dir.sqrMagnitude > 1e-4f ? dir.normalized : up;
        }

        private static float Next(ref uint seed)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return seed / 4294967296f;
        }
    }
}
