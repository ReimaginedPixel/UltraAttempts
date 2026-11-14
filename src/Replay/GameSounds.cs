using System;
using System.Collections.Generic;
using UltraAttempts.Recording;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraAttempts.Replay
{
    // One attempt's sounds, ready to play (from what Recording.RecordSounds recorded): seconds and metres, by when
    // they start. Built off the main thread (only plain maths here); Bank is filled in on the main thread once the
    // replay has found the clips again.
    internal sealed class GhostSounds
    {
        public struct Cue
        {
            public float Start;          // seconds into the attempt
            public float Stop;           // when it was stopped (infinity: it played out by itself)
            public int Clip;             // into Clips (-1: none)
            public SoundFlags Flags;
            public float Volume;
            public float Pitch;
            public float Spatial;
            public float MinDistance;
            public float MaxDistance;
            public AudioRolloffMode Rolloff;
            public Vector3 Pos;          // where it started (OnPlayer: at the ghost instead)
            public int FirstMove;        // its moves: MoveTime / MovePos [FirstMove, FirstMove + Moves)
            public int Moves;

            public bool Loop => (Flags & SoundFlags.Loop) != 0;

            public bool OnPlayer => (Flags & SoundFlags.OnPlayer) != 0;
        }

        public bool Recorded;                                   // false: recorded before sounds were (or with RecordSounds off)
        public SoundClip[] Clips = Array.Empty<SoundClip>();
        public Cue[] Cues = Array.Empty<Cue>();
        public int[] Loops = Array.Empty<int>();                // the cues that loop
        public float[] MoveTime = Array.Empty<float>();
        public Vector3[] MovePos = Array.Empty<Vector3>();
        public int[] Bank;                                      // each clip's place in the replay's clips (-1: not found)

        public static GhostSounds FromRaw(RawAttempt raw, int rate)
        {
            var s = new GhostSounds { Recorded = raw.SoundsRecorded };
            if (raw.SoundClips != null)
            {
                s.Clips = raw.SoundClips.ToArray();
            }
            if (raw.Sounds == null || raw.Sounds.Count == 0)
            {
                return s;
            }
            float perSample = 1f / Math.Max(1, rate);
            int n = raw.Sounds.Count;

            // (by when they start; the same moment keeps its order)
            var order = new int[n];
            var keys = new long[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                keys[i] = ((long)Math.Max(0, raw.Sounds[i].Sample) << 24) | (uint)Math.Min(i, 0xFFFFFF);
            }
            Array.Sort(keys, order);

            // Each sound's moves together, in time order.
            var counts = new int[n];
            List<SoundMove> moves = raw.SoundMoves ?? new List<SoundMove>();
            foreach (SoundMove m in moves)
            {
                if (m.Sound >= 0 && m.Sound < n)
                {
                    counts[m.Sound]++;
                }
            }
            var first = new int[n];
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                first[i] = total;
                total += counts[i];
            }
            s.MoveTime = new float[total];
            s.MovePos = new Vector3[total];
            var fill = (int[])first.Clone();
            foreach (SoundMove m in moves)
            {
                if (m.Sound >= 0 && m.Sound < n)
                {
                    int k = fill[m.Sound]++;
                    s.MoveTime[k] = Math.Max(0, m.Sample) * perSample;
                    s.MovePos[k] = new Vector3(m.X * 0.01f, m.Y * 0.01f, m.Z * 0.01f);
                }
            }
            for (int i = 0; i < n; i++)
            {
                SortMoves(s, first[i], counts[i]);
            }

            s.Cues = new Cue[n];
            var loops = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int j = order[i];
                SoundEvent e = raw.Sounds[j];
                float min = Sane(e.MinDistance * 0.01f, 0.01f, 1e5f, 1f);
                var c = new Cue
                {
                    Start = Math.Max(0, e.Sample) * perSample,
                    Stop = e.Length > 0 ? (Math.Max(0, e.Sample) + (float)e.Length) * perSample : float.PositiveInfinity,
                    Clip = e.Clip >= 0 && e.Clip < s.Clips.Length ? e.Clip : -1,
                    Flags = e.Flags,
                    Volume = Sane(e.Volume / 1000f, 0f, 10f, 1f),
                    Pitch = Sane(e.Pitch / 1000f, -3f, 3f, 1f),
                    Spatial = e.Spatial / 255f,
                    MinDistance = min,
                    MaxDistance = Sane(e.MaxDistance * 0.01f, min + 0.01f, 1e6f, 500f),
                    Rolloff = e.Rolloff <= (byte)AudioRolloffMode.Custom ? (AudioRolloffMode)e.Rolloff : AudioRolloffMode.Logarithmic,
                    Pos = new Vector3(e.X * 0.01f, e.Y * 0.01f, e.Z * 0.01f),
                    FirstMove = first[j],
                    Moves = counts[j],
                };
                s.Cues[i] = c;
                if (c.Loop)
                {
                    loops.Add(i);
                }
            }
            s.Loops = loops.ToArray();
            return s;
        }

        private static float Sane(float v, float min, float max, float otherwise) => float.IsNaN(v) || float.IsInfinity(v) ? otherwise : Math.Min(max, Math.Max(min, v));

        // (they come in time order from the recorder; this only makes sure)
        private static void SortMoves(GhostSounds s, int from, int count)
        {
            for (int i = from + 1; i < from + count; i++)
            {
                float t = s.MoveTime[i];
                Vector3 p = s.MovePos[i];
                int j = i - 1;
                for (; j >= from && s.MoveTime[j] > t; j--)
                {
                    s.MoveTime[j + 1] = s.MoveTime[j];
                    s.MovePos[j + 1] = s.MovePos[j];
                }
                s.MoveTime[j + 1] = t;
                s.MovePos[j + 1] = p;
            }
        }

        // The first cue starting after `time`.
        public int FirstAfter(float time)
        {
            int lo = 0, hi = Cues.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Cues[mid].Start <= time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        // Where a cue's sound is t seconds into the attempt (it moved with whatever was playing it).
        public Vector3 PosAt(in Cue c, float t)
        {
            if (c.Moves == 0 || t <= c.Start)
            {
                return c.Pos;
            }
            int lo = c.FirstMove, hi = c.FirstMove + c.Moves;   // (the first move after t)
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (MoveTime[mid] <= t)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            Vector3 from = lo > c.FirstMove ? MovePos[lo - 1] : c.Pos;
            float fromT = lo > c.FirstMove ? MoveTime[lo - 1] : c.Start;
            if (lo >= c.FirstMove + c.Moves)
            {
                return from;
            }
            float span = MoveTime[lo] - fromT;
            return span > 1e-4f ? Vector3.Lerp(from, MovePos[lo], (t - fromT) / span) : MovePos[lo];
        }
    }

    // The game's own sounds in a replay, as each attempt heard them: its guns and punches, explosions, enemies and
    // their voice lines, doors. The clips are ULTRAKILL's own, found again by name among the ones loaded. The attempt
    // followed (nobody followed: the winner) plays in full; every other attempt at Replay.OtherAttemptsVolume, the
    // nearest first while there are voices free, and the same clip from many attempts at once starts once, louder
    // (not 28 gunshots on top of each other at the start). Played outside ULTRAKILL's frozen mixer (as ReplaySounds
    // is), on the replay's clock: they pause with it, pitch up and down with its speed, and a seek or a cut stops them
    // (no burst of everything skipped over; loops going at the new moment carry on from there).
    internal sealed class GameSounds
    {
        private const int MaxVoices = 24;          // (Unity mixes 32 at once by default: room left for the pops and chimes)
        private const float Together = 0.05f;      // the same clip from attempts this close together starts once
        private const float LongestStep = 1f;      // replay time that went on further than this in a frame (a hitch): its sounds are skipped
        private const float FollowedScore = -1f;

        private sealed class Voice
        {
            public AudioSource Source;
            public Transform T;
            public bool Busy;
            public Ghost Ghost;
            public int Cue;                 // in Ghost.Sounds.Cues (-1: an old recording's shot)
            public int Clip;                // in the bank
            public bool Followed;
            public float Score;             // lower: more wanted (the followed attempt's, then the nearest)
            public float Loudest;           // the loudest copy, as recorded times its attempt's share
            public int Copies;              // how many attempts' copies of it this is
            public float Pitch;
            public float Start;             // replay time its cue started at
            public float Stop;              // replay time it stops at (infinity: once it's played out)
            public bool Loop;
            public bool Attached;           // at its ghost
            public Vector3 Pos;             // (not attached, no moves)
            public float PlayedAt;          // real time it was started (a clip still loading isn't playing yet)
        }

        private struct Candidate
        {
            public Ghost Ghost;
            public int Cue;
            public int Clip;
            public float Time;
            public float Offset;            // seconds into it (a loop carrying on after a cut)
            public float Volume;
            public float Pitch;
            public float Score;
            public bool Followed;
            public bool Attached;
            public bool Loop;
            public float Stop;
            public Vector3 Pos;
            public float Spatial;
            public float MinDistance, MaxDistance;
            public AudioRolloffMode Rolloff;
        }

        private readonly GameObject holder;
        private readonly Voice[] voices = new Voice[MaxVoices];
        private readonly List<AudioClip> bank = new List<AudioClip>();
        private readonly Dictionary<string, int> bankByName = new Dictionary<string, int>();
        private readonly Dictionary<AudioClip, int> bankByClip = new Dictionary<AudioClip, int>();
        private readonly Dictionary<string, int[]> fireClips = new Dictionary<string, int[]>();
        private readonly List<Candidate> candidates = new List<Candidate>();
        private readonly Comparison<Candidate> byScore = (a, b) => a.Score.CompareTo(b.Score);

        private Ghost lastShown;
        private float lastTo = -1f;
        private bool resume;
        private bool paused;
        private bool silenced;
        private float sfx = 1f;
        private Transform listener;
        private bool listenerOnCamera = true;
        private Camera camera;

        // For the log.
        private int playedFollowed, playedOthers, merged, noVoice, stolen, missing, resumed, oldShots, cuts;
        private int clipsWanted, clipsFound, clipsByName, attemptsWith, attemptsWithout, weaponsFound;
        private float prepareMs;
        private bool prepared;
        private readonly List<string> notFound = new List<string>();

        public GameSounds(Transform root, Camera camera)
        {
            this.camera = camera;
            holder = new GameObject("UltraAttempts game sounds");
            holder.transform.SetParent(root, false);
            for (int i = 0; i < voices.Length; i++)
            {
                var go = new GameObject("voice " + i);
                go.transform.SetParent(holder.transform, false);
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.ignoreListenerPause = true;
                source.outputAudioMixerGroup = null;
                source.dopplerLevel = 0f;
                source.spread = 0f;
                voices[i] = new Voice { Source = source, T = go.transform };
            }
        }

        // ---------------------------------------------------------------- once, when the attempts have loaded

        // Finds every attempt's clips again among the game's loaded ones (by name; the length tells same-named ones
        // apart), the game's own sound effects volume, and where the game hears from.
        public void Prepare(List<Ghost> ghosts)
        {
            float started = Time.realtimeSinceStartup;
            var mixers = Game.Get<AudioMixerController>();
            var prefs = Game.Get<PrefsManager>();
            sfx = mixers != null ? mixers.sfxVolume : prefs != null ? prefs.GetFloat("sfxVolume", 1f) : 1f;
            sfx = float.IsNaN(sfx) ? 1f : Mathf.Clamp01(sfx);

            AudioListener heard = Object.FindObjectOfType<AudioListener>();
            listener = heard != null ? heard.transform : null;
            Transform cam = camera != null ? camera.transform : null;
            listenerOnCamera = listener == null || cam == null || listener == cam || listener.IsChildOf(cam);

            var byName = new Dictionary<string, List<AudioClip>>();
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (clip == null || string.IsNullOrEmpty(clip.name))
                {
                    continue;
                }
                if (!byName.TryGetValue(clip.name, out List<AudioClip> named))
                {
                    byName[clip.name] = named = new List<AudioClip>(1);
                }
                named.Add(clip);
            }
            foreach (Ghost g in ghosts)
            {
                GhostSounds s = g.Sounds;
                if (s == null)
                {
                    continue;
                }
                if (!s.Recorded)
                {
                    attemptsWithout++;
                    continue;
                }
                attemptsWith++;
                s.Bank = new int[s.Clips.Length];
                for (int i = 0; i < s.Clips.Length; i++)
                {
                    SoundClip wanted = s.Clips[i];
                    string key = wanted.Name + "|" + wanted.LengthMs;
                    if (!bankByName.TryGetValue(key, out int index))
                    {
                        AudioClip found = Resolve(byName, wanted);
                        index = found != null ? Add(found) : -1;
                        bankByName[key] = index;
                        clipsWanted++;
                        clipsFound += found != null ? 1 : 0;
                        if (found == null && notFound.Count < 6)
                        {
                            notFound.Add($"\"{wanted.Name}\"");
                        }
                    }
                    s.Bank[i] = index;
                }
            }
            if (attemptsWithout > 0)
            {
                FindFireClips();
            }
            foreach (AudioClip clip in bank)
            {
                if (clip.loadState == AudioDataLoadState.Unloaded)
                {
                    clip.LoadAudioData();
                }
            }
            prepareMs = (Time.realtimeSinceStartup - started) * 1000f;
            prepared = true;
        }

        // The same name, and the length nearest the one recorded (only one that's about as long: else it's another sound).
        private AudioClip Resolve(Dictionary<string, List<AudioClip>> byName, SoundClip wanted)
        {
            if (wanted.Name == null || !byName.TryGetValue(wanted.Name, out List<AudioClip> named))
            {
                return null;
            }
            AudioClip best = null;
            float bestOff = float.MaxValue;
            foreach (AudioClip clip in named)
            {
                float off = Mathf.Abs(clip.length * 1000f - wanted.LengthMs);
                if (off < bestOff)
                {
                    best = clip;
                    bestOff = off;
                }
            }
            if (bestOff <= 2f)
            {
                return best;
            }
            if (bestOff <= Mathf.Max(50f, wanted.LengthMs * 0.2f))
            {
                clipsByName++;
                return best;
            }
            return null;
        }

        private int Add(AudioClip clip)
        {
            if (!bankByClip.TryGetValue(clip, out int index))
            {
                index = bank.Count;
                bank.Add(clip);
                bankByClip[clip] = index;
            }
            return index;
        }

        // Attempts recorded before sounds were: their shots, with the game's own fire sound for each weapon (from your
        // loadout's guns, by the name the attempt recorded).
        private void FindFireClips()
        {
            GunControl guns = Game.Get<GunControl>();
            if (guns == null || guns.allWeapons == null)
            {
                return;
            }
            foreach (GameObject weapon in guns.allWeapons)
            {
                if (weapon == null)
                {
                    continue;
                }
                var icon = weapon.GetComponent<WeaponIcon>();
                WeaponDescriptor descriptor = icon != null ? icon.weaponDescriptor : null;
                string name = descriptor != null && !string.IsNullOrEmpty(descriptor.weaponName) ? descriptor.weaponName : weapon.name.Replace("(Clone)", "").Trim();
                if (fireClips.ContainsKey(name))
                {
                    continue;
                }
                var clips = new List<AudioClip>();
                try
                {
                    Revolver revolver = weapon.GetComponent<Revolver>();
                    if (revolver != null && revolver.gunShots != null)
                    {
                        clips.AddRange(revolver.gunShots);
                    }
                    Shotgun shotgun = weapon.GetComponent<Shotgun>();
                    if (shotgun != null)
                    {
                        clips.Add(shotgun.shootSound);
                    }
                    Nailgun nailgun = weapon.GetComponent<Nailgun>();
                    if (nailgun != null && nailgun.muzzleFlash != null)
                    {
                        clips.Add(nailgun.muzzleFlash.GetComponentInChildren<AudioSource>(true)?.clip);
                    }
                    Railcannon rail = weapon.GetComponent<Railcannon>();
                    if (rail != null && rail.fireSound != null)
                    {
                        clips.Add(rail.fireSound.GetComponentInChildren<AudioSource>(true)?.clip);
                    }
                    if (weapon.GetComponent<RocketLauncher>() != null)
                    {
                        clips.Add(weapon.GetComponent<AudioSource>()?.clip);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"finding {name}'s fire sound: {e.Message}");
                }
                clips.RemoveAll(c => c == null);
                if (clips.Count > 0)
                {
                    fireClips[name] = clips.ConvertAll(Add).ToArray();
                    weaponsFound++;
                }
            }
        }

        // ---------------------------------------------------------------- every frame

        // from, to: the replay's time last frame and now. shown: the attempt followed (nobody: the winner). paused: the
        // replay is (or a menu is open over it). camPos, camRot: where the camera is this frame.
        public void Update(List<Ghost> ghosts, Ghost shown, float from, float to, float speed, bool paused, Vector3 camPos, Quaternion camRot, bool throughEyes)
        {
            // (a seek or a start-over moved the clock between frames, the pause included; a cut changed whose sounds
            // are heard in full)
            if (shown != lastShown || (lastTo >= 0f && from != lastTo))
            {
                if (lastShown != null || lastTo >= 0f)
                {
                    cuts++;
                }
                StopAll();
                resume = true;
                lastShown = shown;
            }
            lastTo = to;
            if (paused != this.paused)
            {
                this.paused = paused;
                foreach (Voice v in voices)
                {
                    if (v.Busy)
                    {
                        if (paused)
                        {
                            v.Source.Pause();
                        }
                        else
                        {
                            v.Source.UnPause();
                        }
                    }
                }
            }
            if (paused)
            {
                return;
            }
            bool silent = !Settings.Sounds.Value || Settings.Volume.Value <= 0f;
            if (silent)
            {
                if (!silenced)
                {
                    StopAll();
                    silenced = true;
                }
                return;
            }
            if (silenced)
            {
                silenced = false;
                resume = true;
            }

            candidates.Clear();
            if (resume)
            {
                resume = false;
                Carrying(ghosts, shown, to, camPos);
            }
            if (to > from && to - from <= LongestStep)
            {
                Starting(ghosts, shown, from, to, camPos);
            }
            if (candidates.Count > 0)
            {
                candidates.Sort(byScore);
                foreach (Candidate c in candidates)
                {
                    Play(c);
                }
            }
            Follow(shown, to, speed, camPos, camRot, throughEyes);
        }

        // Every sound starting between from and to, from every attempt that's heard.
        private void Starting(List<Ghost> ghosts, Ghost shown, float from, float to, Vector3 camPos)
        {
            float others = Settings.OtherAttemptsVolume.Value;
            foreach (Ghost g in ghosts)
            {
                GhostSounds s = g.Sounds;
                bool followed = g == shown;
                if (s == null || (!followed && others <= 0f))
                {
                    continue;
                }
                float share = followed ? 1f : others;
                if (!s.Recorded)
                {
                    OldShots(g, from, to, share, followed, camPos);
                    continue;
                }
                if (s.Bank == null)
                {
                    continue;
                }
                // (from the very start: the sounds at its first sample too)
                for (int i = from <= 0f ? 0 : s.FirstAfter(from); i < s.Cues.Length && s.Cues[i].Start <= to; i++)
                {
                    Add(g, s, i, s.Cues[i].Start, 0f, share, followed, camPos);
                }
            }
        }

        // After a cut: the loops going at this moment carry on from where they'd be.
        private void Carrying(List<Ghost> ghosts, Ghost shown, float t, Vector3 camPos)
        {
            float others = Settings.OtherAttemptsVolume.Value;
            foreach (Ghost g in ghosts)
            {
                GhostSounds s = g.Sounds;
                bool followed = g == shown;
                if (s == null || s.Bank == null || s.Loops.Length == 0 || (!followed && others <= 0f))
                {
                    continue;
                }
                foreach (int i in s.Loops)
                {
                    GhostSounds.Cue c = s.Cues[i];
                    float stop = float.IsInfinity(c.Stop) ? g.Duration : c.Stop;
                    if (c.Start < t && t < stop)
                    {
                        Add(g, s, i, c.Start, t - c.Start, followed ? 1f : others, followed, camPos);
                        resumed++;
                    }
                }
            }
        }

        private void Add(Ghost g, GhostSounds s, int i, float time, float offset, float share, bool followed, Vector3 camPos)
        {
            GhostSounds.Cue c = s.Cues[i];
            int clip = c.Clip >= 0 && c.Clip < s.Bank.Length ? s.Bank[c.Clip] : -1;
            if (clip < 0)
            {
                missing++;
                return;
            }
            if (Mathf.Abs(c.Pitch) < 0.05f || c.Volume <= 0f)
            {
                return;  // (held still, or silent, when it started: it'd only take up a voice)
            }
            Vector3 at = c.OnPlayer ? g.PoseAt(Mathf.Min(time + offset, g.Duration)).Eye : s.PosAt(c, time + offset);
            bool others3D = !followed && c.OnPlayer && c.Spatial < 0.5f;
            candidates.Add(new Candidate
            {
                Ghost = g,
                Cue = i,
                Clip = clip,
                Time = time,
                Offset = offset,
                Volume = c.Volume * share,
                Pitch = c.Pitch,
                Score = followed ? FollowedScore : (at - camPos).sqrMagnitude,
                Followed = followed,
                Attached = c.OnPlayer,
                Loop = c.Loop,
                Stop = c.Loop && float.IsInfinity(c.Stop) ? g.Duration : c.Stop,
                Pos = at,
                // (another attempt's own gun, heard everywhere in the game: here it comes from that ghost)
                Spatial = others3D ? 1f : c.Spatial,
                MinDistance = others3D ? 5f : c.MinDistance,
                MaxDistance = others3D ? 150f : c.MaxDistance,
                Rolloff = others3D ? AudioRolloffMode.Logarithmic : c.Rolloff,
            });
        }

        // An old recording's shots (it has no sounds of its own): its weapon's fire sound at the ghost.
        private void OldShots(Ghost g, float from, float to, float share, bool followed, Vector3 camPos)
        {
            if (fireClips.Count == 0)
            {
                return;
            }
            int a = from <= 0f ? 0 : Mathf.FloorToInt(from * g.Rate) + 1;
            int b = Mathf.Min(Mathf.FloorToInt(to * g.Rate), g.Count - 1);
            for (int k = Mathf.Max(0, a); k <= b; k++)
            {
                if ((g.Flags[k] & (byte)SampleFlags.Shot) == 0)
                {
                    continue;
                }
                Ghost.WeaponChange w = g.WeaponAt(k);
                string name = g.WeaponName(w);
                if (name == null || !fireClips.TryGetValue(name, out int[] clips))
                {
                    continue;
                }
                Vector3 at = g.Body[k] + g.Eye[k];
                candidates.Add(new Candidate
                {
                    Ghost = g,
                    Cue = -1,
                    Clip = clips[k % clips.Length],
                    Time = k / (float)g.Rate,
                    Volume = 0.55f * share,
                    Pitch = 1f,
                    Score = followed ? FollowedScore : (at - camPos).sqrMagnitude,
                    Followed = followed,
                    Attached = true,
                    Stop = float.PositiveInfinity,
                    Pos = at,
                    Spatial = followed ? 0f : 1f,
                    MinDistance = 5f,
                    MaxDistance = 150f,
                    Rolloff = AudioRolloffMode.Logarithmic,
                });
                oldShots++;
            }
        }

        private void Play(in Candidate c)
        {
            // The same clip from another attempt (or this one) just now, or the same loop going already: that one,
            // louder, instead of another on top.
            foreach (Voice v in voices)
            {
                if (v.Busy && v.Clip == c.Clip && ((c.Loop && v.Loop) || (c.Offset <= 0f && Mathf.Abs(v.Start - c.Time) <= Together)))
                {
                    v.Copies++;
                    v.Loudest = Mathf.Max(v.Loudest, c.Volume);
                    if (c.Followed && !v.Followed)
                    {
                        // (the followed attempt's own copy: it's heard from where that one is, and stops when it does)
                        Configure(v, c);
                        v.Start = c.Time;
                        v.Stop = c.Stop;
                        v.Pitch = c.Pitch;
                    }
                    v.Score = Mathf.Min(v.Score, c.Score);
                    merged++;
                    return;
                }
            }
            Voice free = null;
            foreach (Voice v in voices)
            {
                if (!v.Busy)
                {
                    free = v;
                    break;
                }
            }
            if (free == null)
            {
                // (all taken: the furthest of the other attempts' gives way to a nearer one, or to the followed one's)
                Voice worst = null;
                foreach (Voice v in voices)
                {
                    if (worst == null || v.Score > worst.Score)
                    {
                        worst = v;
                    }
                }
                if (worst == null || worst.Score <= c.Score)
                {
                    noVoice++;
                    return;
                }
                worst.Source.Stop();
                worst.Busy = false;
                free = worst;
                stolen++;
            }
            AudioClip clip = bank[c.Clip];
            if (clip == null)
            {
                missing++;
                return;
            }
            Voice voice = free;
            voice.Busy = true;
            voice.Clip = c.Clip;
            voice.Copies = 1;
            voice.Loudest = c.Volume;
            voice.Start = c.Time;
            voice.Pitch = c.Pitch;
            voice.Stop = c.Stop;
            voice.Loop = c.Loop;
            voice.PlayedAt = Time.unscaledTime;
            Configure(voice, c);
            AudioSource source = voice.Source;
            source.clip = clip;
            source.loop = c.Loop;
            source.volume = 0f;  // (set properly below, once it knows how many copies it is)
            source.Play();
            if (c.Offset > 0f && clip.length > 0.05f && clip.loadState == AudioDataLoadState.Loaded)
            {
                try
                {
                    source.time = (c.Offset * Mathf.Abs(c.Pitch)) % clip.length;
                }
                catch (Exception)
                {
                    // (some clips can't be started part way: from the top then)
                }
            }
            if (c.Followed)
            {
                playedFollowed++;
            }
            else
            {
                playedOthers++;
            }
        }

        private void Configure(Voice v, in Candidate c)
        {
            v.Ghost = c.Ghost;
            v.Cue = c.Cue;
            v.Followed = c.Followed;
            v.Score = c.Score;
            v.Attached = c.Attached;
            v.Pos = c.Pos;
            AudioSource s = v.Source;
            s.spatialBlend = Mathf.Clamp01(c.Spatial);
            s.rolloffMode = c.Rolloff == AudioRolloffMode.Custom ? AudioRolloffMode.Linear : c.Rolloff;
            s.minDistance = c.MinDistance;
            s.maxDistance = Mathf.Max(c.MaxDistance, c.MinDistance + 0.01f);
            s.priority = c.Followed ? 16 : 96;  // (ahead of the frozen game's own, which are silent)
        }

        // The voices playing: stopped where their attempt stopped them, where their sound is now, pitched to the
        // replay's speed, as loud as their copies add up to.
        private void Follow(Ghost shown, float t, float speed, Vector3 camPos, Quaternion camRot, bool throughEyes)
        {
            float volume = Settings.Volume.Value * sfx;
            foreach (Voice v in voices)
            {
                if (!v.Busy)
                {
                    continue;
                }
                AudioSource source = v.Source;
                bool done = !source.isPlaying && !v.Loop && Time.unscaledTime - v.PlayedAt > 0.25f;
                if (t >= v.Stop || done || v.Ghost == null)
                {
                    source.Stop();
                    v.Busy = false;
                    v.Ghost = null;
                    continue;
                }
                Vector3 at;
                if (v.Attached)
                {
                    at = v.Ghost == shown && throughEyes ? camPos : v.Ghost.PoseAt(Mathf.Min(t, v.Ghost.Duration)).Eye;
                }
                else if (v.Cue >= 0 && v.Ghost.Sounds != null && v.Cue < v.Ghost.Sounds.Cues.Length)
                {
                    at = v.Ghost.Sounds.PosAt(v.Ghost.Sounds.Cues[v.Cue], t);
                }
                else
                {
                    at = v.Pos;
                }
                v.T.position = Heard(at, camPos, camRot);
                source.pitch = Mathf.Clamp(v.Pitch * speed, -3f, 3f);
                float louder = Mathf.Min(2f, 1f + 0.25f * Mathf.Log(Mathf.Max(1, v.Copies), 2f));
                source.volume = Mathf.Clamp01(v.Loudest * louder * volume);
            }
        }

        // Where to put a sound so it's heard from where the camera is (the game's listener is on the camera, as a
        // rule; if it isn't, the same place relative to wherever it is).
        private Vector3 Heard(Vector3 at, Vector3 camPos, Quaternion camRot)
        {
            if (listenerOnCamera || listener == null)
            {
                return at;
            }
            return listener.position + listener.rotation * (Quaternion.Inverse(camRot) * (at - camPos));
        }

        private void StopAll()
        {
            foreach (Voice v in voices)
            {
                if (v.Busy)
                {
                    v.Source.Stop();
                    v.Busy = false;
                    v.Ghost = null;
                }
            }
        }

        public void Dispose()
        {
            StopAll();
            if (holder != null)
            {
                Object.Destroy(holder);
            }
            if (!prepared)
            {
                return;  // (left while loading)
            }
            string clips = clipsWanted > 0
                ? $"{clipsFound} of {clipsWanted} clips found{(clipsByName > 0 ? $" ({clipsByName} by name, at another length)" : "")}"
                    + (clipsFound < clipsWanted ? $", {missing} sounds of the rest skipped (not loaded in this level: {string.Join(", ", notFound)}{(clipsWanted - clipsFound > notFound.Count ? ", ..." : "")})" : "")
                : "no clips recorded";
            string old = attemptsWithout > 0 ? $"; {attemptsWithout} recorded before sounds were: {oldShots} of their shots with the game's fire sounds ({weaponsFound} of your weapons')" : "";
            Plugin.Log.LogInfo($"replay sounds{(Settings.Sounds.Value ? "" : " (Replay.Sounds is off)")}: {playedFollowed + playedOthers} played ({playedFollowed} the followed attempt's, {playedOthers} the others'), {merged} joined the same sound from another attempt, "
                + $"{noVoice} left out with every voice busy ({stolen} cut for nearer ones), {resumed} loops carried on over {cuts} cuts; {clips}; {attemptsWith} attempts with sounds{old}; "
                + $"the game's sound effects volume {sfx:0.##}, the listener {(listenerOnCamera ? "on the camera" : "off the camera (sounds placed relative to it)")}, {prepareMs:0} ms to get ready");
        }
    }
}
