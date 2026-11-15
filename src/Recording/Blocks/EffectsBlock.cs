namespace UltraAttempts.Recording
{
    // Block 3 (the world's): the game's own effects. Nothing yet.
    public sealed partial class RawAttempt
    {
        partial void CloneEffects(RawAttempt copy)
        {
        }
    }

    internal static class EffectsBlock
    {
        public static bool Has(RawAttempt a) => false;

        public static void Encode(AttemptCodec.VarWriter w, RawAttempt a)
        {
        }

        public static void Decode(AttemptCodec.VarReader r, RawAttempt a)
        {
        }
    }
}
