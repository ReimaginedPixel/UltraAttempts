using UnityEngine;

namespace UltraAttempts.Replay
{
    // A pop for each death and a chime for each finish, synthesized (no sound files). Played outside
    // ULTRAKILL's mixer, so the frozen game's silence doesn't silence them.
    internal sealed class ReplaySounds
    {
        private const int Rate = 44100;

        private readonly AudioSource[] voices = new AudioSource[8];
        private readonly AudioClip pop;
        private readonly AudioClip chime;
        private int next;
        private float lastPop = -1f;

        public ReplaySounds(GameObject root)
        {
            for (int i = 0; i < voices.Length; i++)
            {
                var source = root.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
                source.ignoreListenerPause = true;
                source.bypassReverbZones = true;
                voices[i] = source;
            }
            pop = Pop();
            chime = Chime();
        }

        public void Dispose()
        {
            Object.Destroy(pop);
            Object.Destroy(chime);
        }

        public void Death(int seed, int together)
        {
            if (!Settings.Sounds.Value || Time.unscaledTime - lastPop < 0.03f)
            {
                return;
            }
            lastPop = Time.unscaledTime;
            float pitch = 0.8f + (seed * 0.6180339f % 1f) * 0.45f;
            Play(pop, pitch, Settings.Volume.Value * Mathf.Min(1f, 0.55f + 0.1f * together));
        }

        public void Finish()
        {
            if (Settings.Sounds.Value)
            {
                Play(chime, 1f, Settings.Volume.Value);
            }
        }

        private void Play(AudioClip clip, float pitch, float volume)
        {
            AudioSource source = voices[next];
            next = (next + 1) % voices.Length;
            source.pitch = pitch;
            source.PlayOneShot(clip, volume);
        }

        // A short square-wave drop with a burst of noise: GD-ish, not a gunshot.
        private static AudioClip Pop()
        {
            int n = (int)(Rate * 0.18f);
            var data = new float[n];
            var random = new System.Random(7);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float freq = Mathf.Lerp(620f, 90f, Mathf.Sqrt(t / 0.18f));
                phase += freq / Rate;
                float square = (phase % 1.0) < 0.5 ? 1f : -1f;
                float noise = (float)random.NextDouble() * 2f - 1f;
                float env = Mathf.Exp(-t * 28f);
                data[i] = (square * 0.35f + noise * 0.4f * Mathf.Exp(-t * 60f)) * env;
            }
            var clip = AudioClip.Create("UltraAttempts pop", n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // Three rising notes.
        private static AudioClip Chime()
        {
            int n = (int)(Rate * 1.1f);
            var data = new float[n];
            float[] notes = { 659.25f, 830.61f, 987.77f, 1318.5f };
            for (int k = 0; k < notes.Length; k++)
            {
                int start = (int)(Rate * 0.09f * k);
                for (int i = start; i < n; i++)
                {
                    float t = (i - start) / (float)Rate;
                    float env = Mathf.Exp(-t * 4.5f) * Mathf.Min(1f, t * 200f);
                    data[i] += (Mathf.Sin(2f * Mathf.PI * notes[k] * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * notes[k] * t)) * env * 0.22f;
                }
            }
            var clip = AudioClip.Create("UltraAttempts chime", n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
