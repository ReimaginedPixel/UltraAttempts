using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace UltraAttempts.Recording
{
    // Which of the game's ways a sound came in by (for the log).
    internal enum SoundPath
    {
        Play = 0,       // AudioSourceExtensions.Play: the source's own clip
        OneShot = 1,    // AudioSourceExtensions.PlayOneShot
        Delayed = 2,    // AudioSourceExtensions.PlayDelayed (V1's footsteps)
        OnAwake = 3,    // a sound that plays by itself when it's spawned or switched on (explosions, Gabriel's swings),
                        // as the game's virtual audio picks it up
    }

    // Recording.RecordSounds: the game's own sounds while an attempt is recorded, as that attempt heard them. Each
    // sound that starts: its clip (by name, found again later), when, where (V1's own sounds go with the ghost), how
    // loud, pitched and spread; when it stopped, if it didn't play out by itself (a loop, a sound cut off); and where
    // it went, if it moved while it played. Nearly every sound in ULTRAKILL goes through its Play / PlayOneShot /
    // PlayDelayed extensions, or plays by itself when it's spawned, which its virtual audio picks up: Patches hooks
    // those. Not the music, nothing out of earshot (unless it's heard everywhere or it's V1's own). Like the rest of
    // the recorder it only reads, and it's cheap: a few reads a sound, and a look at the ones still playing once a
    // frame.
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

        private readonly List<Active> active = new List<Active>();
        private readonly Stack<Active> spare = new Stack<Active>();
        private readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private readonly Dictionary<AudioClip, int> clipIndex = new Dictionary<AudioClip, int>();
        // (each source's last sound by Play: the game reports some twice, by two of its paths, in one frame)
        private readonly Dictionary<int, (int frame, AudioClip clip)> lastPlay = new Dictionary<int, (int, AudioClip)>();

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
        private AudioSource oneShotSource;   // (PlayOneShot registers its source with the virtual audio: not a sound of its own)
        private int oneShotFrame = -1;
        private static bool warnedFull, warnedMoves, warnedFailure;

        // What this attempt left out, how its sounds came in, and how they ended (for the log).
        private int musicSkipped, far, quiet, capped, cut, loops;
        private readonly int[] byPath = new int[4];

        public void EnterScene()
        {
            End(-1);
            kinds.Clear();
            lastPlay.Clear();
        }

        public void Begin(RawAttempt a, bool record)
        {
            End(-1);
            attempt = record ? a : null;
            a.SoundsRecorded = record;
            rate = Math.Max(1, a.SampleRate);
            failed = false;
            clipIndex.Clear();
            musicSkipped = far = quiet = capped = cut = loops = 0;
            Array.Clear(byPath, 0, byPath.Length);
            if (lastPlay.Count > MaxKinds)
            {
                lastPlay.Clear();
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
                int frame = Time.frameCount;
                if (path == SoundPath.OneShot)
                {
                    oneShotSource = source;
                    oneShotFrame = frame;
                }
                else
                {
                    lastPlay[source.GetInstanceID()] = (frame, clip);
                }
                if (clip == null || source == null || Game.Paused)
                {
                    return;  // (the pause menu's own clicks)
                }
                Refresh(frame);
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
                int start = sample + (delay > 0f ? Mathf.RoundToInt(Mathf.Min(delay, 60f) * rate) : 0);
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
                    a.Started = Time.timeAsDouble + Math.Max(0f, delay);
                    a.Length = speed > 0.01f ? clip.length / speed : float.MaxValue;
                    a.OneShot = oneShot;
                    a.Loop = loop;
                    a.Moves = !onPlayer && spatial > 0.01f;
                    a.Last = at;
                    a.LastMove = start;
                    active.Add(a);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // A sound that plays by itself (playOnAwake) being picked up by the game's virtual audio: when it's spawned or
        // switched on, or a frame later once it's playing. Only once, and only as it starts (not one that's been
        // going a while, and not the Play or PlayOneShot that's registering it right now).
        public void OnAwake(AudioSource source, int sample)
        {
            if (attempt == null || failed || source == null)
            {
                return;
            }
            try
            {
                if (!source.playOnAwake || !source.isPlaying || source.time > JustStarted)
                {
                    return;
                }
                int frame = Time.frameCount;
                AudioClip clip = source.clip;
                if (clip == null || (frame == oneShotFrame && source == oneShotSource))
                {
                    return;
                }
                if (lastPlay.TryGetValue(source.GetInstanceID(), out var last) && last.clip == clip && frame - last.frame <= 1)
                {
                    return;
                }
                OnStart(source, clip, 1f, 0f, SoundPath.OnAwake, sample);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Once a frame (after its samples): which of the sounds still playing have stopped, and where the moving ones
        // have got to.
        public void Poll(int sample)
        {
            if (attempt == null || failed || active.Count == 0)
            {
                return;
            }
            try
            {
                double now = Time.timeAsDouble;
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
            catch (Exception e)
            {
                Fail(e);
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
                + $"{byPath[(int)SoundPath.OnAwake]} playing by themselves when spawned), {loops} loops, {cut} cut short, {a.SoundMoves.Count} moves; "
                + $"left out: {musicSkipped} music, {far} out of earshot, {quiet} silent" + (capped > 0 ? $", {capped} over the limit" : "")
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
