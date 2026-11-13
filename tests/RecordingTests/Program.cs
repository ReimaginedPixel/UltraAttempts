using System;
using System.Collections.Generic;
using System.IO;
using UltraAttempts.Recording;

// Checks the recording format end to end: an attempt survives encoding exactly, runs continue and
// complete as they should, a crash's half-written block is ignored and cut off, old runs are pruned,
// and how small a realistic attempt is.
internal static class Program
{
    private static int failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok)
        {
            failures++;
        }
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "UltraAttemptsTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Codec();
            Compression();
            Runs(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
        Console.WriteLine(failures == 0 ? "all passed" : failures + " failed");
        return failures == 0 ? 0 : 1;
    }

    // A V1-ish run: moving fast, turning, jumping, firing; plus the extremes of every channel.
    private static RawAttempt Sample(int number, int samples, int seed, AttemptEnd end = AttemptEnd.Death)
    {
        var random = new Random(seed);
        var a = new RawAttempt
        {
            Number = number,
            Start = AttemptStart.LevelStart,
            End = end,
            StartedUtcTicks = 638_000_000_000_000_000L + seed,
            SampleRate = 30,
            LevelTimeStart = 1.5f,
            LevelTimeEnd = 1.5f + samples / 30f,
            Cheats = seed % 2 == 0,
            MajorAssists = seed % 3 == 0,
            WeaponsRecorded = true,
        };
        double x = 0, y = 0, z = 0, yaw = 0, pitch = 0;
        for (int i = 0; i < samples; i++)
        {
            yaw += (random.NextDouble() - 0.5) * 0.3;
            pitch = Math.Clamp(pitch + (random.NextDouble() - 0.5) * 0.1, -1.4, 1.4);
            double speed = 0.3 + random.NextDouble() * 0.6;  // ~10-30 m/s at 30 Hz
            x += Math.Sin(yaw) * speed;
            z += Math.Cos(yaw) * speed;
            y += (random.NextDouble() - 0.5) * 0.2;
            double cy = Math.Cos(yaw * 0.5), sy = Math.Sin(yaw * 0.5), cp = Math.Cos(pitch * 0.5), sp = Math.Sin(pitch * 0.5);
            double qx = cy * sp, qy = sy * cp, qz = -sy * sp, qw = cy * cp;
            if (qw < 0)
            {
                qx = -qx; qy = -qy; qz = -qz; qw = -qw;
            }
            a.Add(
                (int)Math.Round(x * 100), (int)Math.Round(y * 100), (int)Math.Round(z * 100),
                0, 100, (short)random.Next(-3, 4),
                (short)Math.Round(qx * 32767), (short)Math.Round(qy * 32767), (short)Math.Round(qz * 32767), (short)Math.Round(qw * 32767),
                (byte)random.Next(0, 256), (byte)Math.Max(0, 100 - i / 10));
        }
        if (samples > 3)
        {
            // Extremes: far-off positions, a teleport, every rotation component at its limit.
            a.PosX[1] = -20_000_000;
            a.PosY[2] = 20_000_000;
            a.RotX[3] = short.MinValue;
            a.RotW[3] = short.MaxValue;
            a.EyeZ[2] = short.MinValue;
        }
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Weapon, A = 1, B = 0, C = a.AddWeaponName("PIERCER REVOLVER") });
        a.Events.Add(new AttemptEvent { Sample = Math.Min(5, samples - 1), Kind = EventKind.Weapon, A = 2, B = 2, C = a.AddWeaponName("SAWED-ON ÜBER SHOTGUN") });
        a.Events.Add(new AttemptEvent { Sample = Math.Min(7, samples - 1), Kind = EventKind.Up, A = 0, B = -127, C = 0 });
        a.Events.Add(new AttemptEvent { Sample = samples - 1, Kind = EventKind.Checkpoint });
        return a;
    }

    private static bool Same(RawAttempt a, RawAttempt b)
    {
        if (a.Number != b.Number || a.Start != b.Start || a.End != b.End || a.StartedUtcTicks != b.StartedUtcTicks || a.SampleRate != b.SampleRate
            || a.LevelTimeStart != b.LevelTimeStart || a.LevelTimeEnd != b.LevelTimeEnd || a.Cheats != b.Cheats || a.MajorAssists != b.MajorAssists
            || a.WeaponsRecorded != b.WeaponsRecorded || a.Count != b.Count || a.Events.Count != b.Events.Count || a.WeaponNames.Count != b.WeaponNames.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a.PosX[i] != b.PosX[i] || a.PosY[i] != b.PosY[i] || a.PosZ[i] != b.PosZ[i]
                || a.EyeX[i] != b.EyeX[i] || a.EyeY[i] != b.EyeY[i] || a.EyeZ[i] != b.EyeZ[i]
                || a.RotX[i] != b.RotX[i] || a.RotY[i] != b.RotY[i] || a.RotZ[i] != b.RotZ[i] || a.RotW[i] != b.RotW[i]
                || a.Flags[i] != b.Flags[i] || a.Hp[i] != b.Hp[i])
            {
                return false;
            }
        }
        for (int i = 0; i < a.Events.Count; i++)
        {
            AttemptEvent e = a.Events[i], f = b.Events[i];
            if (e.Sample != f.Sample || e.Kind != f.Kind || e.A != f.A || e.B != f.B || e.C != f.C)
            {
                return false;
            }
        }
        for (int i = 0; i < a.WeaponNames.Count; i++)
        {
            if (a.WeaponNames[i] != b.WeaponNames[i])
            {
                return false;
            }
        }
        return true;
    }

    private static void Codec()
    {
        Console.WriteLine("codec");
        foreach (int n in new[] { 2, 7, 1800 })
        {
            RawAttempt a = Sample(42, n, n);
            byte[] bytes = AttemptCodec.Encode(a);
            RawAttempt b = AttemptCodec.Decode(bytes, 0, bytes.Length);
            Check(Same(a, b), $"{n} samples come back exactly");
        }
        RawAttempt live = Sample(3, 50, 9);
        RawAttempt clone = live.Clone(AttemptEnd.InProgress);
        Check(clone.End == AttemptEnd.InProgress && clone.PosX.Length == 50 && clone.PosX[10] == live.PosX[10], "an attempt in progress clones trimmed");
        bool threw = false;
        try
        {
            byte[] bytes = AttemptCodec.Encode(Sample(1, 100, 1));
            AttemptCodec.Decode(bytes, 0, bytes.Length / 2);
        }
        catch (Exception)
        {
            threw = true;
        }
        Check(threw, "a cut-short attempt is refused, not misread");
    }

    // Moving like V1 does: the mouse turns smoothly, speed builds and drops, jumps arc, dashes burst.
    private static RawAttempt Smooth(int samples, int seed)
    {
        var random = new Random(seed);
        var a = new RawAttempt { SampleRate = 30, WeaponsRecorded = true };
        double x = 0, y = 0, z = 0, vy = 0, yaw = 0, yawRate = 0, pitch = 0, pitchRate = 0, speed = 0.5;
        for (int i = 0; i < samples; i++)
        {
            yawRate = Math.Clamp(yawRate + (random.NextDouble() - 0.5) * 0.02, -0.12, 0.12);
            pitchRate = Math.Clamp(pitchRate * 0.9 + (random.NextDouble() - 0.5) * 0.01, -0.05, 0.05);
            yaw += yawRate;
            pitch = Math.Clamp(pitch + pitchRate, -1.2, 1.2);
            speed = Math.Clamp(speed + (random.NextDouble() - 0.5) * 0.04, 0.2, 0.9);
            bool dash = i % 90 < 6;
            double step = dash ? 2.0 : speed;
            x += Math.Sin(yaw) * step;
            z += Math.Cos(yaw) * step;
            if (y <= 0 && i % 45 == 0)
            {
                vy = 0.55;  // jump
            }
            vy -= 0.04;
            y = Math.Max(0, y + vy);
            double cy = Math.Cos(yaw * 0.5), sy = Math.Sin(yaw * 0.5), cp = Math.Cos(pitch * 0.5), sp = Math.Sin(pitch * 0.5);
            double qx = cy * sp, qy = sy * cp, qz = -sy * sp, qw = cy * cp;
            if (qw < 0)
            {
                qx = -qx; qy = -qy; qz = -qz; qw = -qw;
            }
            byte flags = (byte)((y <= 0 ? 1 : 0) | (dash ? 4 : 0) | (i % 60 < 20 ? 8 : 0) | (i % 25 == 0 ? 32 : 0));
            a.Add(
                (int)Math.Round(x * 100), (int)Math.Round(y * 100), (int)Math.Round(z * 100),
                0, 100, 0,
                (short)Math.Round(qx * 32767), (short)Math.Round(qy * 32767), (short)Math.Round(qz * 32767), (short)Math.Round(qw * 32767),
                flags, (byte)(100 - (i / 300) * 10));
        }
        return a;
    }

    private static void Compression()
    {
        Console.WriteLine("size");
        const int plainSample = 12 + 6 + 8 + 2;  // positions, eyes, rotation, flags + HP as plain structs
        RawAttempt smooth = Smooth(30 * 60, 123);
        RawAttempt back = AttemptCodec.Decode(AttemptCodec.Encode(smooth), 0, AttemptCodec.Encode(smooth).Length);
        Check(Same(smooth, back), "realistic movement comes back exactly");
        int raw = smooth.Count * plainSample;
        int packed = AttemptCodec.Encode(smooth).Length;
        Console.WriteLine($"        a minute of non-stop V1 movement at 30 Hz: {packed / 1024.0:0.0} KB ({raw / 1024.0:0.0} KB as plain structs, {packed / 60.0:0} bytes/s)");
        Check(packed * 4 < raw, "at least 4x smaller than plain samples");

        RawAttempt noise = Sample(1, 30 * 60, 123);
        int noisy = AttemptCodec.Encode(noise).Length;
        Console.WriteLine($"        worst case, pure noise every sample: {noisy / 1024.0:0.0} KB ({noisy / 60.0:0} bytes/s)");
        Check(noisy < noise.Count * plainSample, "even pure noise is smaller than plain samples");

        var still = new RawAttempt { SampleRate = 30 };
        for (int i = 0; i < 30 * 60; i++)
        {
            still.Add(1000, 200, -300, 0, 100, 0, 0, 0, 0, 32767, 1, 100);
        }
        int idle = AttemptCodec.Encode(still).Length;
        Console.WriteLine($"        a minute standing still: {idle} bytes");
        Check(idle < 1024, "standing still costs almost nothing");
    }

    private static LevelInfo Level(string root, string scene) => new LevelInfo
    {
        Key = scene + "|3",
        Scene = scene,
        Name = "TEST LEVEL",
        Difficulty = 3,
        Folder = SessionStore.LevelFolder(root, scene, "Violent"),
    };

    private static void Runs(string root)
    {
        Console.WriteLine("runs");
        var log = new List<string>();
        var store = new SessionStore(root, log.Add) { KeepRunsPerLevel = 2 };
        LevelInfo level = Level(root, "Level 1-1");

        for (int i = 0; i < 4; i++)
        {
            int number = store.BeginAttempt(level);
            Check(number == i + 1, $"attempt {i + 1} is numbered {number}");
            store.SubmitAttempt(level, Sample(0, 60 + i, i), finishesRun: false);
        }
        store.Flush();
        string first = store.CurrentRunPath(level.Key, out int attempts);
        Check(attempts == 4 && File.Exists(first), "four attempts in one run file");

        // Relaunch: an unfinished run carries on, numbering included.
        var again = new SessionStore(root, log.Add) { KeepRunsPerLevel = 2 };
        Check(again.BeginAttempt(level) == 5, "after a relaunch the unfinished run continues at attempt 5");

        // A crash mid-write: half a block on the end.
        using (var stream = new FileStream(first, FileMode.Append))
        {
            stream.Write(new byte[] { SessionFile.BlockAttempt, 200, 0, 0, 0, 1, 2, 3 }, 0, 8);
        }
        SessionInfo scanned = SessionFile.Scan(first);
        Check(scanned.Attempts == 4 && !scanned.Complete, "a half-written block is ignored when reading");
        var afterCrash = new SessionStore(root, log.Add) { KeepRunsPerLevel = 2 };
        Check(afterCrash.BeginAttempt(level) == 5, "...and the run still continues at attempt 5");
        afterCrash.SubmitAttempt(level, Sample(0, 90, 50, AttemptEnd.Finish), finishesRun: true);
        afterCrash.Flush();
        Check(new FileInfo(first).Length == SessionFile.Scan(first).ValidLength, "the half block was cut off before appending");

        SessionData data = SessionFile.Load(first, log.Add);
        Check(data.Info.Complete && data.Attempts.Count == 5, "the winning attempt completes the run (5 attempts)");
        Check(data.Attempts[4].End == AttemptEnd.Finish && data.Attempts[4].Number == 5, "attempt 5 is the finish");
        Check(data.Info.Header.Scene == "Level 1-1" && data.Info.Header.Difficulty == 3, "the header remembers the level");

        Check(afterCrash.BeginAttempt(level) == 1, "after a win the next attempt starts a new run at 1");
        string second = null;
        for (int run = 0; run < 3; run++)
        {
            afterCrash.BeginAttempt(level);
            afterCrash.SubmitAttempt(level, Sample(0, 40, 100 + run, AttemptEnd.Finish), finishesRun: true);
            afterCrash.Flush();
            second = second ?? afterCrash.CurrentRunPath(level.Key, out _);
            System.Threading.Thread.Sleep(1100);  // (run files are named by the second)
        }
        List<string> files = SessionStore.RunFiles(level.Folder);
        Check(files.Count == 2, $"only the newest 2 finished runs are kept ({files.Count} left)");

        var fresh = new SessionStore(root, log.Add) { ContinueAcrossVisits = false };
        LevelInfo other = Level(root, "Level 1-2");
        fresh.BeginAttempt(other);
        fresh.SubmitAttempt(other, Sample(0, 30, 7), finishesRun: false);
        fresh.Flush();
        fresh.ForgetRun(other.Key);
        Check(fresh.BeginAttempt(other) == 1, "with runs not carrying over, a new visit starts at attempt 1");
        Check(SessionStore.Sanitize("Level: 1/1?") == "Level_ 1_1_", "level names become safe folder names");
        foreach (string line in log)
        {
            Console.WriteLine("        log: " + line);
        }
    }
}
