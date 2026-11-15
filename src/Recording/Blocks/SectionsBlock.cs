namespace UltraAttempts.Recording
{
    // Block 2 (the world's): which parts of the level were switched on. Nothing yet.
    public sealed partial class RawAttempt
    {
        partial void CloneSections(RawAttempt copy)
        {
        }
    }

    internal static class SectionsBlock
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
