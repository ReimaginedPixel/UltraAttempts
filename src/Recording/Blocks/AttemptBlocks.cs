using System;
using System.Collections.Generic;

namespace UltraAttempts.Recording
{
    // Format 5's blocks: each feature's own part of an attempt, in a file of its own here (its data as a part of
    // RawAttempt, and how it's written and read). On disk: how many blocks, then each one's tag, length and bytes,
    // so a version that doesn't know a tag steps over it. The attempt's blocks (World false) are read with or
    // without the world; the world's only with it. Pure data, like the rest of the format (the tests build it).
    internal static class AttemptBlocks
    {
        public sealed class Block
        {
            public int Tag;
            public bool World;
            public Func<RawAttempt, bool> Has;                       // anything to write
            public Action<AttemptCodec.VarWriter, RawAttempt> Encode;
            public Action<AttemptCodec.VarReader, RawAttempt> Decode;
        }

        // Tags are forever: a new block takes a new one.
        public static readonly List<Block> All = new List<Block>
        {
            new Block { Tag = 1, World = false, Has = ArmsBlock.Has, Encode = ArmsBlock.Encode, Decode = ArmsBlock.Decode },
            new Block { Tag = 2, World = true, Has = SectionsBlock.Has, Encode = SectionsBlock.Encode, Decode = SectionsBlock.Decode },
            new Block { Tag = 3, World = true, Has = EffectsBlock.Has, Encode = EffectsBlock.Encode, Decode = EffectsBlock.Decode },
        };

        public static void Encode(AttemptCodec.VarWriter w, RawAttempt a, bool world)
        {
            var written = new List<(int tag, AttemptCodec.VarWriter bytes)>();
            foreach (Block b in All)
            {
                if (b.World == world && b.Has(a))
                {
                    var bytes = new AttemptCodec.VarWriter(256);
                    b.Encode(bytes, a);
                    written.Add((b.Tag, bytes));
                }
            }
            Write(w, written);
        }

        public static void Write(AttemptCodec.VarWriter w, List<(int tag, AttemptCodec.VarWriter bytes)> blocks)
        {
            w.UInt((uint)blocks.Count);
            foreach ((int tag, AttemptCodec.VarWriter bytes) in blocks)
            {
                w.UInt((uint)tag);
                w.UInt((uint)bytes.Length);
                w.Bytes(bytes.Buffer, bytes.Length);
            }
        }

        // Each block known (of this group) decoded from its own bytes; the rest stepped over.
        public static void Decode(AttemptCodec.VarReader r, RawAttempt a, bool world)
        {
            int count = r.Count(2);
            for (int i = 0; i < count; i++)
            {
                int tag = (int)r.UInt();
                int length = r.Count(1);
                AttemptCodec.VarReader bytes = r.Slice(length);
                Block b = All.Find(x => x.Tag == tag && x.World == world);
                b?.Decode(bytes, a);
            }
        }
    }
}
