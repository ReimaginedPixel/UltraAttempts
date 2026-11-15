using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

namespace UltraAttempts.Recording
{
    // Which of the game's ways a sound came in by (for the log).
    internal enum SoundPath
    {
        Play = 0,       // AudioSourceExtensions.Play: the source's own clip
        OneShot = 1,    // AudioSourceExtensions.PlayOneShot
        Delayed = 2,    // AudioSourceExtensions.PlayDelayed (V1's footsteps)
        OnAwake = 3,    // a sound that plays by itself when it's spawned or switched on (explosions, V1's jumps and dashes,
                        // the whiplash, the railcannon firing), as the game's virtual audio picks it up
        Scan = 4,       // found playing by the recorder's own look round: a plain AudioSource.Play (water and the like), a
                        // pooled sound switched back on, anything the virtual audio never sees
    }

    // Recording.RecordSounds: the game's own sounds while an attempt is recorded, as that attempt heard them. Each
    // sound that starts: its clip (by name, found again later), when, where (V1's own sounds go with the ghost), how
    // loud, pitched and spread; when it stopped, if it didn't play out by itself (a loop, a sound cut off); and where
    // it went, if it moved while it played. Nearly every sound in ULTRAKILL goes through its Play / PlayOneShot /
    // PlayDelayed extensions, or plays by itself when it's spawned, which its virtual audio picks up: Patches hooks
    // those. The virtual audio takes a spawned sound on before Unity has started it, so those wait here until they're
    // playing (a frame or so), timed from when they were spawned. Whatever comes in another way is caught by a look at
    // every sound source every other frame, for ones that have started since (timed from how far into their clip they
    // are). Each sound counts once, whichever ways it comes in by. Not the music, nothing out of earshot (unless it's
    // heard everywhere or it's V1's own). Like the rest of the recorder it only reads, and it's cheap: a few reads a
    // sound, a look at the ones still playing once a frame, and one read of whether each source is playing every
    // other frame.
    internal sealed class SoundRecorder
    {
        private const float Range = 150f;            // (as far as ThingRecorder looks)
        private const int MaxSounds = 20000;         // an attempt (about half an hour of a busy fight)
        private const int MaxMoves = 20000;
        private const int MaxActive = 128;           // sounds still playing that are kept an eye on
        private const float MoveDistance = 0.75f;    // a playing sound's source moved this far: a move...
        private const int MoveEvery = 2;             // ...at most every other sample
        private const float JustStarted = 0.25f;     // a sound that plays by itself, seconds into its clip when it's picked up
        private const int MaxKinds = 8192;
        private const float Same = 0.05f;            // a sound starting this close to one already known on its source is that one

        // Sounds that should play by themselves, waiting for Unity to start them.
        private const int MaxPending = 256;
        private const float PendingFor = 0.5f;       // game seconds a spawned sound is given to start

        // The look round every sound source.
        private const int ScanEvery = 2;             // frames
        private const int RefreshEvery = 30;         // frames between finding every source again (new ones)
        private const int MaxWatched = 8192;
        private const float LateBy = 1f;             // found this far into its clip at most (else it's an old sound, just noticed)
        private const float ScanSlack = 0.1f;

        private enum Kind : byte
        {
            World,
            Player,
            Music,
        }

        private sealed class Active
        {
            public AudioSource Source;
            public AudioClip Clip;
            public int Index;          // in the attempt's Sounds
            public double Started;     // game time
            public float Length;       // game seconds it takes to play out (its clip at its pitch)
            public bool OneShot;
            public bool Loop;
            public bool Moves;         // where it is is kept up with
            public Vector3 Last;
            public int LastMove;
        }

        private sealed class Pending
        {
            public AudioSource Source;
            public int Sample;         // when it was spawned
            public double Time;
            public int Frame;
        }

        private struct Watched
        {
            public AudioSource Source;
            public int Id;
            public bool Playing;
            public double IdleAt;      // game time it was last seen not playing
            public int IdleSample;
        }

        private readonly List<Active> active = new List<Active>();
        private readonly Stack<Active> spare = new Stack<Active>();
        private readonly List<Pending> pending = new List<Pending>();
        private readonly Stack<Pending> sparePending = new Stack<Pending>();
        private readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private readonly Dictionary<AudioClip, int> clipIndex = new Dictionary<AudioClip, int>();
        // (each source's last sound known, by any way in: when it starts, in game time; nothing counts twice)
        private readonly Dictionary<int, double> lastStart = new Dictionary<int, double>();
        private Watched[] watched = new Watched[256];
        private int watchedCount;
        private readonly HashSet<int> watchedIds = new HashSet<int>();
        private int lastScan = -1000, lastRefresh = -1000;
        private double refreshedAt;
        private int refreshedSample;
        private bool scanning;

        private RawAttempt attempt;
        private int rate = 60;
        private bool failed;
        private int refreshed = -1;
        private Transform player;
        private Transform camera;
        private Transform music;
        private AudioMixer musicMixer;
        private AudioMixerGroup musicGroup;
        private readonly AudioSource[] themes = new AudioSource[4];
        private static bool warnedFull, warnedMoves, warnedFailure, warnedScan;

        // What this attempt left out, how its sounds came in, and how they ended (for the log).
        private int musicSkipped, far, quiet, capped, cut, loops, neverPlayed;
        private readonly int[] byPath = new int[5];

        public void EnterScene()
        {
            End(-1);
            kinds.Clear();
            lastStart.Clear();
            ClearWatched();
        }

        public void Begin(RawAttempt a, bool record)
        {
            End(-1);
            attempt = record ? a : null;
            a.SoundsRecorded = record;
            rate = Math.Max(1, a.SampleRate);
            failed = false;
            clipIndex.Clear();
            musicSkipped = far = quiet = capped = cut = loops = neverPlayed = 0;
            Array.Clear(byPath, 0, byPath.Length);
            if (lastStart.Count > MaxKinds)
            {
                lastStart.Clear();
            }
            // (whatever's playing as it starts was started before it: only what starts from now on)
            ClearWatched();
            scanning = record;
            if (record)
            {
                FindSources(0, true);
            }
        }

        // The attempt is over at lastSample (-1: it's gone without one): loops still going stop there, anything else
        // plays out.
        public void End(int lastSample)
        {
            if (attempt != null && lastSample >= 0 && !failed)
            {
                try
                {
                    foreach (Active a in active)
                    {
                        if (a.Loop && a.Index < attempt.Sounds.Count)
                        {
                            SoundEvent e = attempt.Sounds[a.Index];
                            e.Length = Math.Max(1, lastSample - e.Sample);
                            attempt.Sounds[a.Index] = e;
                            loops++;
                        }
                    }
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
            foreach (Active a in active)
            {
                a.Source = null;
                a.Clip = null;
                spare.Push(a);
            }
            active.Clear();
            foreach (Pending p in pending)
            {
                p.Source = null;
                sparePending.Push(p);
            }
            pending.Clear();
            attempt = null;
        }

        // A sound starting (from Patches, through the recorder): the source's clip, or PlayOneShot's.
        public void OnStart(AudioSource source, AudioClip clip, float volumeScale, float delay, SoundPath path, int sample)
        {
            if (attempt == null || failed || (object)source == null)
            {
                return;
            }
            try
            {
                double now = Time.timeAsDouble;
                double starts = now + Math.Max(0f, Mathf.Min(delay, 60f));
                int start = sample + (delay > 0f ? Mathf.RoundToInt(Mathf.Min(delay, 60f) * rate) : 0);
                Record(source, clip, volumeScale, start, starts, path);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // A sound that plays by itself (playOnAwake) being picked up by the game's virtual audio: as it's spawned or
        // switched on (before Unity has started it: it waits for that, a frame or so), or later once it's playing (only
        // as it starts, not one that's been going a while, and not the Play or PlayOneShot that's registering it).
        public void OnAwake(AudioSource source, int sample)
        {
            if (attempt == null || failed || source == null)
            {
                return;
            }
            try
            {
                if (!source.playOnAwake)
                {
                    return;
                }
                double now = Time.timeAsDouble;
                if (Known(source, now - Same))
                {
                    return;  // (the Play or PlayOneShot handing it over right now)
                }
                if (!source.isPlaying)
                {
                    Queue(source, sample, now);
                    return;
                }
                float elapsed = Elapsed(source);
                if (elapsed > JustStarted)
                {
                    return;
                }
                Caught(source, Math.Max(0, sample - Mathf.RoundToInt(elapsed * rate)), now - elapsed, SoundPath.OnAwake);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Once a frame (after its samples): the spawned sounds that have started since, a look round every source every
        // other frame, which of the sounds still playing have stopped, and where the moving ones have got to.
        public void Poll(int sample)
        {
            if (attempt == null || failed)
            {
                return;
            }
            try
            {
                double now = Time.timeAsDouble;
                if (pending.Count > 0)
                {
                    Started(now);
                }
                if (scanning)
                {
                    Scan(sample, now);
                }
                if (active.Count > 0)
                {
                    Follow(sample, now);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void Follow(int sample, double now)
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                Active a = active[i];
                if (now < a.Started)
                {
                    continue;  // (delayed: not started yet)
                }
                if (!a.Loop && now - a.Started >= a.Length + 0.1)
                {
                    Release(i);  // (played out)
                    continue;
                }
                AudioSource s = a.Source;
                if (s == null || !s.isActiveAndEnabled || (!a.OneShot && s.clip != a.Clip) || (!s.isPlaying && !s.IsPaused()))
                {
                    Stopped(i, sample);
                    continue;
                }
                if (a.Moves && sample - a.LastMove >= MoveEvery)
                {
                    Vector3 p = s.transform.position;
                    if ((p - a.Last).sqrMagnitude > MoveDistance * MoveDistance)
                    {
                        a.Last = p;
                        a.LastMove = sample;
                        if (attempt.SoundMoves.Count < MaxMoves)
                        {
                            attempt.SoundMoves.Add(new SoundMove { Sound = a.Index, Sample = sample, X = Recorder.Cm(p.x), Y = Recorder.Cm(p.y), Z = Recorder.Cm(p.z) });
                        }
                        else if (!warnedMoves)
                        {
                            warnedMoves = true;
                            Plugin.Log.LogInfo($"this attempt has recorded {MaxMoves} moves of sounds: the rest stay where they started (RecordSounds' limit)");
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- sounds that play by themselves

        private void Queue(AudioSource source, int sample, double now)
        {
            foreach (Pending p in pending)
            {
                if (ReferenceEquals(p.Source, source))
                {
                    return;
                }
            }
            if (pending.Count >= MaxPending)
            {
                return;
            }
            Pending q = sparePending.Count > 0 ? sparePending.Pop() : new Pending();
            q.Source = source;
            q.Sample = sample;
            q.Time = now;
            q.Frame = Time.frameCount;
            pending.Add(q);
        }

        // The ones waiting that have started (from when they were spawned), are gone, or never will.
        private void Started(double now)
        {
            int frame = Time.frameCount;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                AudioSource s = p.Source;
                bool done = true;
                if (s == null)
                {
                    // (destroyed before it played)
                }
                else if (s.isPlaying)
                {
                    Caught(s, p.Sample, p.Time, SoundPath.OnAwake);
                }
                else if (now - p.Time < PendingFor || frame - p.Frame <= 3)
                {
                    done = false;
                }
                else
                {
                    neverPlayed++;
                }
                if (done)
                {
                    int last = pending.Count - 1;
                    pending[i] = pending[last];
                    pending.RemoveAt(last);
                    p.Source = null;
                    sparePending.Push(p);
                }
            }
        }

        private bool IsPending(AudioSource source)
        {
            foreach (Pending p in pending)
            {
                if (ReferenceEquals(p.Source, source))
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- the look round

        // Every source playing now that wasn't at the last look (every ScanEvery frames); every RefreshEvery frames, the
        // sources there are now (the new ones are looked at from then on).
        private void Scan(int sample, double now)
        {
            int frame = Time.frameCount;
            if (frame - lastScan < ScanEvery)
            {
                return;
            }
            lastScan = frame;
            if (frame - lastRefresh >= RefreshEvery)
            {
                FindSources(sample, false);
            }
            for (int i = watchedCount - 1; i >= 0; i--)
            {
                ref Watched w = ref watched[i];
                AudioSource s = w.Source;
                if (s == null)
                {
                    watchedIds.Remove(w.Id);
                    watched[i] = watched[--watchedCount];
                    watched[watchedCount] = default;
                    continue;
                }
                bool playing = s.isPlaying;
                if (playing && !w.Playing)
                {
                    Found(s, w.IdleAt, w.IdleSample, sample, now);
                }
                if (!playing)
                {
                    w.IdleAt = now;
                    w.IdleSample = sample;
                }
                w.Playing = playing;
            }
        }

        // The sources there are now. first: as the attempt starts (whatever's playing then isn't one of its sounds);
        // else the new ones count as not playing at the last refresh, so they're looked at next.
        private void FindSources(int sample, bool first)
        {
            lastRefresh = Time.frameCount;
            AudioSource[] sources;
            try
            {
                sources = Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            }
            catch (Exception e)
            {
                scanning = false;
                if (!warnedScan)
                {
                    warnedScan = true;
                    Plugin.Log.LogWarning("the sounds' look round can't find the game's sound sources, so only the ones the game hands over are recorded: " + e.Message);
                }
                return;
            }
            double now = Time.timeAsDouble;
            foreach (AudioSource s in sources)
            {
                if (s == null || watchedCount >= MaxWatched)
                {
                    continue;
                }
                int id = s.GetInstanceID();
                if (!watchedIds.Add(id))
                {
                    continue;
                }
                if (watchedCount == watched.Length)
                {
                    Array.Resize(ref watched, watched.Length * 2);
                }
                watched[watchedCount++] = new Watched
                {
                    Source = s,
                    Id = id,
                    Playing = first && s.isPlaying,
                    IdleAt = first ? now : refreshedAt,
                    IdleSample = first ? sample : refreshedSample,
                };
            }
            refreshedAt = now;
            refreshedSample = sample;
        }

        private void ClearWatched()
        {
            Array.Clear(watched, 0, watchedCount);
            watchedCount = 0;
            watchedIds.Clear();
            lastScan = lastRefresh = -1000;
        }

        // A source found playing that wasn't (at idleAt): a sound that's started since, unless it's one known already.
        private void Found(AudioSource source, double idleAt, int idleSample, int sample, double now)
        {
            if (source.clip == null || IsPending(source) || Known(source, idleAt - Same))
            {
                return;
            }
            float elapsed = Elapsed(source);
            if (elapsed > LateBy || elapsed > now - idleAt + ScanSlack)
            {
                return;  // (it started before it was last seen: an old sound, only just noticed)
            }
            int start = Math.Max(Math.Max(0, idleSample), sample - Mathf.RoundToInt(elapsed * rate));
            Caught(source, start, Math.Max(idleAt, now - elapsed), SoundPath.Scan);
        }

        // ---------------------------------------------------------------- recording one

        // A sound found started (playing by itself, or by the look round): its own clip, unless it's one known already.
        private void Caught(AudioSource source, int start, double starts, SoundPath path)
        {
            AudioClip clip = source.clip;
            if (clip == null || Known(source, starts - Same))
            {
                return;
            }
            Record(source, clip, 1f, start, starts, path);
        }

        // Whether a sound's known already on this source that starts at or after since (game time).
        private bool Known(AudioSource source, double since) => lastStart.TryGetValue(source.GetInstanceID(), out double at) && at >= since;

        // How far into its clip a playing source is, in game seconds.
        private static float Elapsed(AudioSource source)
        {
            float pitch = Mathf.Abs(source.GetPitch());
            return pitch > 0.01f ? Mathf.Max(0f, source.time) / pitch : 0f;
        }

        // A sound starting at sample start (game time starts): kept, unless it's out of earshot, silent or the music.
        private void Record(AudioSource source, AudioClip clip, float volumeScale, int start, double starts, SoundPath path)
        {
            // (known from now on, kept or not: the look round mustn't take it again)
            int id = source.GetInstanceID();
            if (lastStart.Count > MaxKinds)
            {
                lastStart.Clear();
            }
            lastStart[id] = starts;
            if (clip == null || source == null || Game.Paused)
            {
                return;  // (the pause menu's own clicks)
            }
            Refresh(Time.frameCount);
            Kind kind = KindOf(source);
            if (kind == Kind.Music)
            {
                musicSkipped++;
                return;
            }
            float volume = source.volume * volumeScale;
            if (source.mute || volume <= 0.0005f)
            {
                quiet++;
                return;
            }
            float spatial = source.GetSpatialBlend();
            float max = source.maxDistance;
            AudioRolloffMode rolloff = source.rolloffMode;
            Vector3 at = source.transform.position;
            if (kind == Kind.World && spatial > 0.01f && player != null)
            {
                // (out of earshot: further than anything's recorded, or past where it fades to nothing)
                float d2 = (at - player.position).sqrMagnitude;
                if (d2 > Range * Range || (rolloff == AudioRolloffMode.Linear && d2 > max * max))
                {
                    far++;
                    return;
                }
            }
            if (attempt.Sounds.Count >= MaxSounds)
            {
                capped++;
                if (!warnedFull)
                {
                    warnedFull = true;
                    Plugin.Log.LogInfo($"this attempt has recorded {MaxSounds} sounds: no more for it (RecordSounds' limit)");
                }
                return;
            }
            float pitch = source.GetPitch();
            bool oneShot = path == SoundPath.OneShot;
            bool loop = !oneShot && source.loop;
            if (!oneShot)
            {
                EndPlaysOn(source, start);  // (Play starts a source over: what it was playing stops)
            }
            var flags = SoundFlags.None;
            if (loop)
            {
                flags |= SoundFlags.Loop;
            }
            if (kind == Kind.Player)
            {
                flags |= SoundFlags.OnPlayer;
            }
            if (oneShot)
            {
                flags |= SoundFlags.OneShot;
            }
            bool onPlayer = kind == Kind.Player;
            attempt.Sounds.Add(new SoundEvent
            {
                Sample = start,
                Clip = ClipIndex(clip),
                Flags = flags,
                Volume = Mathf.RoundToInt(Mathf.Clamp(volume, 0f, 100f) * 1000f),
                Pitch = Mathf.RoundToInt(Mathf.Clamp(pitch, -100f, 100f) * 1000f),
                Spatial = (byte)Mathf.RoundToInt(Mathf.Clamp01(spatial) * 255f),
                Rolloff = (byte)rolloff,
                MinDistance = Recorder.Cm(Mathf.Max(0f, source.minDistance)),
                MaxDistance = Recorder.Cm(Mathf.Max(0f, max)),
                X = onPlayer ? 0 : Recorder.Cm(at.x),
                Y = onPlayer ? 0 : Recorder.Cm(at.y),
                Z = onPlayer ? 0 : Recorder.Cm(at.z),
            });
            byPath[(int)path]++;

            if (active.Count < MaxActive)
            {
                Active a = spare.Count > 0 ? spare.Pop() : new Active();
                float speed = Mathf.Abs(pitch);
                a.Source = source;
                a.Clip = clip;
                a.Index = attempt.Sounds.Count - 1;
                a.Started = starts;
                a.Length = speed > 0.01f ? clip.length / speed : float.MaxValue;
                a.OneShot = oneShot;
                a.Loop = loop;
                a.Moves = !onPlayer && spatial > 0.01f;
                a.Last = at;
                a.LastMove = start;
                active.Add(a);
            }
        }

        // An attempt about to be saved, ending at lastSample: a sound or move after it (a delayed one) is at it instead.
        public static void Trim(RawAttempt a, int lastSample)
        {
            for (int i = 0; i < a.Sounds.Count; i++)
            {
                if (a.Sounds[i].Sample > lastSample)
                {
                    SoundEvent e = a.Sounds[i];
                    e.Sample = lastSample;
                    e.Length = e.Length > 0 ? 1 : 0;
                    a.Sounds[i] = e;
                }
            }
            for (int i = 0; i < a.SoundMoves.Count; i++)
            {
                if (a.SoundMoves[i].Sample > lastSample)
                {
                    SoundMove m = a.SoundMoves[i];
                    m.Sample = lastSample;
                    a.SoundMoves[i] = m;
                }
            }
        }

        // What an attempt's sounds were, for the log.
        public string Summary(RawAttempt a)
        {
            if (!a.SoundsRecorded)
            {
                return "not recorded (RecordSounds is off)";
            }
            int own = 0;
            foreach (SoundEvent e in a.Sounds)
            {
                own += (e.Flags & SoundFlags.OnPlayer) != 0 ? 1 : 0;
            }
            return $"{a.Sounds.Count} sounds of {a.SoundClips.Count} clips ({own} V1's own, {a.Sounds.Count - own} around it; "
                + $"{byPath[(int)SoundPath.Play]} played, {byPath[(int)SoundPath.OneShot]} one-shots, {byPath[(int)SoundPath.Delayed]} delayed, "
                + $"{byPath[(int)SoundPath.OnAwake]} playing by themselves when spawned, {byPath[(int)SoundPath.Scan]} caught by the look round"
                + (scanning ? $" of {watchedCount} sources" : " (off)") + $"), {loops} loops, {cut} cut short, {a.SoundMoves.Count} moves; "
                + $"left out: {musicSkipped} music, {far} out of earshot, {quiet} silent" + (neverPlayed > 0 ? $", {neverPlayed} spawned that never played" : "")
                + (capped > 0 ? $", {capped} over the limit" : "")
                + (failed ? "; FAILED partway (see the warning above)" : "");
        }

        // A source being played again: what it was playing (by Play) stops now.
        private void EndPlaysOn(AudioSource source, int sample)
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (!active[i].OneShot && ReferenceEquals(active[i].Source, source))
                {
                    Stopped(i, sample);
                }
            }
        }

        // It stopped at this sample: cut short (or a loop ended) unless it had about played out anyway.
        private void Stopped(int i, int sample)
        {
            Active a = active[i];
            if (a.Index < attempt.Sounds.Count)
            {
                double played = Time.timeAsDouble - a.Started;
                if (a.Loop || played < a.Length * 0.95 - 0.02)
                {
                    SoundEvent e = attempt.Sounds[a.Index];
                    e.Length = Math.Max(1, sample - e.Sample);
                    attempt.Sounds[a.Index] = e;
                    if (a.Loop)
                    {
                        loops++;
                    }
                    else
                    {
                        cut++;
                    }
                }
            }
            Release(i);
        }

        private void Release(int i)
        {
            Active a = active[i];
            int last = active.Count - 1;
            active[i] = active[last];
            active.RemoveAt(last);
            a.Source = null;
            a.Clip = null;
            spare.Push(a);
        }

        private int ClipIndex(AudioClip clip)
        {
            if (!clipIndex.TryGetValue(clip, out int index))
            {
                index = attempt.SoundClips.Count;
                attempt.SoundClips.Add(new SoundClip { Name = clip.name ?? "", LengthMs = Mathf.RoundToInt(Mathf.Max(0f, clip.length) * 1000f) });
                clipIndex[clip] = index;
            }
            return index;
        }

        // What's what this frame: V1, its camera, the music.
        private void Refresh(int frame)
        {
            if (frame == refreshed)
            {
                return;
            }
            refreshed = frame;
            NewMovement nm = Game.Player;
            player = nm != null ? nm.transform : null;
            CameraController cc = nm != null && nm.cc != null ? nm.cc : Game.Get<CameraController>();
            camera = cc != null ? cc.transform : null;
            MusicManager mm = Game.Get<MusicManager>();
            music = mm != null ? mm.transform : null;
            themes[0] = mm != null ? mm.battleTheme : null;
            themes[1] = mm != null ? mm.cleanTheme : null;
            themes[2] = mm != null ? mm.bossTheme : null;
            themes[3] = mm != null ? mm.targetTheme : null;
            AudioMixerController mixers = Game.Get<AudioMixerController>();
            musicMixer = mixers != null ? mixers.musicSound : null;
            musicGroup = mixers != null ? mixers.musicGroup : null;
        }

        // Music (skipped), V1's own (played at the ghost), or the world's. Worked out once per source.
        private Kind KindOf(AudioSource source)
        {
            int id = source.GetInstanceID();
            if (kinds.TryGetValue(id, out Kind kind))
            {
                return kind;
            }
            if (kinds.Count > MaxKinds)
            {
                kinds.Clear();
            }
            kind = Classify(source);
            kinds[id] = kind;
            return kind;
        }

        private Kind Classify(AudioSource source)
        {
            AudioMixerGroup group = source.outputAudioMixerGroup;
            if (group != null && (group == musicGroup || (musicMixer != null && group.audioMixer == musicMixer)))
            {
                return Kind.Music;
            }
            foreach (AudioSource theme in themes)
            {
                if (theme != null && theme == source)
                {
                    return Kind.Music;
                }
            }
            Transform t = source.transform;
            if (music != null && t.IsChildOf(music))
            {
                return Kind.Music;
            }
            if ((player != null && t.IsChildOf(player)) || (camera != null && t.IsChildOf(camera)))
            {
                return Kind.Player;
            }
            // V1's own sounds the game spawns on their own (a jump, a dash, a slide stopping, the whiplash thrown, the
            // railcannon firing): a copy made just now, at the top of the scene, heard everywhere rather than from
            // anywhere in particular.
            if (t.parent == null && source.GetSpatialBlend() < 0.01f && source.gameObject.name.EndsWith("(Clone)", StringComparison.Ordinal))
            {
                return Kind.Player;
            }
            return Kind.World;
        }

        // Something in the game didn't look as expected: the rest of this attempt goes without sounds (everything else
        // is still recorded).
        private void Fail(Exception e)
        {
            failed = true;
            if (!warnedFailure)
            {
                warnedFailure = true;
                Plugin.Log.LogWarning("recording the sounds failed, so this attempt has none from here on (it's still recorded): " + e);
            }
        }
    }
}
