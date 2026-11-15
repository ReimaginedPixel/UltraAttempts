namespace UltraAttempts.Recording
{
    // Block 1 (the attempt's): V1's arms. Nothing yet.
    public sealed partial class RawAttempt
    {
        partial void CloneArms(RawAttempt copy)
        {
        }
    }

    internal static class ArmsBlock
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
