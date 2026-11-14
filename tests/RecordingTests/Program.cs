using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UltraAttempts.Recording;

// Checks the recording format end to end: an attempt (with its enemies and doors) survives encoding
// exactly, attempts recorded by 1.0 still read, damaged data is refused, runs continue and complete as
// they should (a run begun with 1.0 carries on with new attempts), a crash's half-written block is
// ignored and cut off, old runs are pruned, and how small a realistic attempt is.
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

    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return Real(args[0]);
        }
        string root = Path.Combine(Path.GetTempPath(), "UltraAttemptsTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Codec();
            OldAttempts();
            Damage();
            Compression();
            Runs(root);
            OldRun(root);
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

    // The attempt count (counting on across wins, reset back to 1, a 1.0 level's count) and export/import.
    private static void Counting(string root, List<string> log)
    {
        Console.WriteLine("counting, export, import");
        LevelInfo level = Level(root, "Level 5-3");
        var store = new SessionStore(root, log.Add);
        for (int i = 0; i < 3; i++)
        {
            store.BeginAttempt(level);
            store.SubmitAttempt(level, Sample(0, 30, 300 + i), finishesRun: i == 2);
        }
        store.Flush();
        Check(store.AttemptCount(level.Folder) == 3 && File.ReadAllText(Path.Combine(level.Folder, SessionStore.CountFile)) == "3", "the count is kept in the level's folder");
        Check(new SessionStore(root, log.Add).BeginAttempt(level) == 4, "a relaunch after a win counts on at attempt 4");

        // A level from 1.0: runs but no count file: it starts from every recorded attempt.
        File.Delete(Path.Combine(level.Folder, SessionStore.CountFile));
        Check(new SessionStore(root, log.Add).AttemptCount(level.Folder) == 3, "a level from 1.0 counts its recorded attempts");

        // Reset mid-run: back to 1, and that run is closed (not continued, not a win).
        var playing = new SessionStore(root, log.Add);
        System.Threading.Thread.Sleep(1100);
        playing.BeginAttempt(level);
        playing.SubmitAttempt(level, Sample(0, 30, 310), finishesRun: false);
        playing.Flush();
        string unfinished = playing.CurrentRunPath(level.Key, out _);
        System.Threading.Thread.Sleep(1100);
        playing.ResetCount(level.Folder);
        Check(playing.BeginAttempt(level) == 1 && playing.CurrentRunPath(level.Key, out _) != unfinished, "reset: back to attempt 1, in a new run");
        playing.Flush();
        SessionInfo closed = SessionFile.Scan(unfinished);
        Check(closed.Closed && !closed.Complete && closed.Attempts == 1, "the run that was going is closed, not won, its attempt kept");
        Check(new SessionStore(root, log.Add).BeginAttempt(level) == 1, "after a relaunch the closed run isn't continued, and the count is still reset");
        playing.SubmitAttempt(level, Sample(0, 30, 311), finishesRun: false);
        playing.Flush();
        Check(playing.AttemptCount(level.Folder) == 1, "the next attempt is attempt 1");

        // Export, lose everything, import it back.
        string zip = playing.Export(level.Folder);
        Check(File.Exists(zip) && Path.GetDirectoryName(zip) == Path.Combine(root, SessionStore.ExportsFolder), "a level exports to a zip in Exports");
        string[] before = SessionStore.RunFiles(level.Folder).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length).ToArray();
        string import = Path.Combine(root, SessionStore.ImportFolder);
        Directory.CreateDirectory(import);
        File.Copy(zip, Path.Combine(import, "backup.zip"));
        foreach (string f in SessionStore.RunFiles(level.Folder))
        {
            File.Delete(f);
        }
        var fresh = new SessionStore(root, log.Add);
        ImportResult back = fresh.Import();
        string[] after = SessionStore.RunFiles(level.Folder).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length).ToArray();
        Check(back.Zips == 1 && back.Runs == before.Length && before.SequenceEqual(after), $"importing it brings back all {before.Length} runs exactly");
        Check(File.Exists(Path.Combine(import, "Done", "backup.zip")) && !File.Exists(Path.Combine(import, "backup.zip")), "an imported zip moves to Import\\Done");
        File.Copy(zip, Path.Combine(import, "again.zip"));
        ImportResult twice = fresh.Import();
        Check(twice.Runs == 0 && twice.Skipped == before.Length, "importing the same runs again adds nothing");

        // A zip that tries to write outside the recordings folder, or somewhere odd in it.
        using (var archive = new ZipArchive(new FileStream(Path.Combine(import, "evil.zip"), FileMode.CreateNew), ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "../../outside.ukattempts", "loose.ukattempts", "Import/x/run.ukattempts", "a/b/c/deep.ukattempts" })
            {
                using (var w = new StreamWriter(archive.CreateEntry(name).Open()))
                {
                    w.Write("nope");
                }
            }
        }
        ImportResult evil = fresh.Import();
        Check(evil.Runs == 0 && evil.Skipped == 4 && !File.Exists(Path.Combine(root, "..", "..", "outside.ukattempts")) && !File.Exists(Path.Combine(root, "loose.ukattempts")), "entries outside <level>/<difficulty>/ are refused");

        List<LevelSummary> levels = fresh.Levels();
        LevelSummary summary = levels.FirstOrDefault(l => l.Folder == level.Folder);
        Check(summary != null && summary.Attempts == 1 && summary.Runs == before.Length && summary.Wins == 1, $"the level list: attempt {summary?.Attempts}, {summary?.Runs} runs, {summary?.Wins} win");
        Check(levels.All(l => l.Level != SessionStore.ExportsFolder && l.Level != SessionStore.ImportFolder), "Exports and Import aren't levels");
    }

    // dotnet run -c Release -- <folder>: reads every run in a recordings folder (a copy of yours) with
    // this version: every attempt must decode. Also how shaky the recorded eyes are: the eyes' offset from
    // the body should only change smoothly (sliding down, standing up), so big sample-to-sample changes
    // in it are screen shake.
    private static int Real(string folder)
    {
        string[] files = Directory.GetFiles(folder, "*" + SessionFile.Extension, SearchOption.AllDirectories);
        Console.WriteLine($"{files.Length} runs in {folder}");
        int attempts = 0, damaged = 0;
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var errors = new List<string>();
            SessionData run;
            try
            {
                run = SessionFile.Load(file, errors.Add);
            }
            catch (Exception e)
            {
                Check(false, $"{Path.GetRelativePath(folder, file)}: {e.Message}");
                continue;
            }
            damaged += errors.Count;
            attempts += run.Attempts.Count;
            double seconds = run.Attempts.Sum(a => a.Duration);
            long samples = 0, shaky = 0;
            double jitter = 0;
            foreach (RawAttempt a in run.Attempts)
            {
                for (int i = 2; i < a.Count; i++)
                {
                    // second difference of the eye offset, in cm
                    double dx = a.EyeX[i] - 2 * a.EyeX[i - 1] + a.EyeX[i - 2];
                    double dy = a.EyeY[i] - 2 * a.EyeY[i - 1] + a.EyeY[i - 2];
                    double dz = a.EyeZ[i] - 2 * a.EyeZ[i - 1] + a.EyeZ[i - 2];
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    jitter += d;
                    samples++;
                    if (d > 10)
                    {
                        shaky++;
                    }
                }
            }
            string versionInfo = run.Attempts.Count > 0 ? $"{run.Attempts[0].SampleRate} Hz" : "-";
            Console.WriteLine($"  {Path.GetRelativePath(folder, file)}: {run.Attempts.Count} attempts{(run.Info.Complete ? ", won" : "")}, {seconds:0.0} s, {versionInfo}, " +
                $"{new FileInfo(file).Length / Math.Max(seconds, 0.1):0} bytes/s, eye jitter {(samples > 0 ? jitter / samples : 0):0.0} cm/sample, {(samples > 0 ? 100.0 * shaky / samples : 0):0.0}% shaken (>10 cm)" +
                $"{(run.Attempts.Any(a => a.Enemies.Count > 0) ? $", {run.Attempts.Sum(a => a.Enemies.Count)} enemy tracks" : "")}");
            foreach (string error in errors)
            {
                Console.WriteLine("        " + error);
            }
        }
        Check(damaged == 0, $"all {attempts} attempts decode ({damaged} damaged)");
        Console.WriteLine(failures == 0 ? "all passed" : failures + " failed");
        return failures == 0 ? 0 : 1;
    }

    // A V1-ish run: moving fast, turning, jumping, firing; plus the extremes of every channel. With
    // world: enemies and doors too, also at their extremes.
    private static RawAttempt Sample(int number, int samples, int seed, AttemptEnd end = AttemptEnd.Death, bool world = true)
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
        if (world)
        {
            ExtremeWorld(a, random);
        }
        return a;
    }

    private static readonly string[] EnemyNames = { "Zombie", "Filth", "Stray", "Ünïcödé Mindflayer", "", "Drone", new string('W', 300) };

    // Enemies and doors with anything in them: any position, rotation, hash, time and health.
    private static void ExtremeWorld(RawAttempt a, Random random)
    {
        a.EnemyStride = 3;
        int doors = random.Next(1, 8);
        for (int d = 0; d < doors; d++)
        {
            int index = a.AddDoorPath($"Level#0/Room {d}#0/Ünïcödé Door#{d % 3}");
            a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Door, A = index, B = d % 2 });
        }
        for (int k = 0; k < 4; k++)
        {
            a.Events.Add(new AttemptEvent { Sample = random.Next(0, a.Count), Kind = EventKind.Door, A = random.Next(0, doors), B = k % 2 });
        }
        a.Events = a.Events.OrderBy(e => e.Sample).ToList();  // (stable: sample 0's weapon stays first)

        int enemies = random.Next(0, 9);
        int id = random.Next(0, 3);
        for (int k = 0; k < enemies; k++)
        {
            int samples = k == 0 ? 0 : k == 1 ? 1 : random.Next(2, 40);
            var e = new EnemyTrack
            {
                Id = id,
                Type = random.Next(-1, 45),
                Name = EnemyNames[random.Next(EnemyNames.Length)],
                Scale = k == 2 ? -0f : (float)(random.NextDouble() * 4),
                Start = random.Next(0, Math.Max(1, a.Count / 3)) * a.EnemyStride,
                Died = random.Next(2) == 0,
            };
            id += random.Next(1, 5);
            for (int i = 0; i < samples; i++)
            {
                e.Add(
                    random.Next(-100_000_000, 100_000_001), random.Next(-1_000_000, 1_000_000), random.Next(-100_000_000, 100_000_001),
                    (short)random.Next(short.MinValue, short.MaxValue + 1), (short)random.Next(short.MinValue, short.MaxValue + 1),
                    (short)random.Next(short.MinValue, short.MaxValue + 1), (short)random.Next(0, short.MaxValue + 1),
                    random.Next(int.MinValue, int.MaxValue), (ushort)random.Next(0, 65536), (byte)random.Next(0, 256));
            }
            if (samples > 3)
            {
                e.Anim[0] = int.MinValue;
                e.Anim[1] = int.MaxValue;
                e.AnimTime[2] = 65535;
                e.AnimTime[3] = 0;
                e.Health[2] = 255;
                e.Health[3] = 0;
                e.PosX[1] = -100_000_000;
                e.PosX[2] = 100_000_000;
            }
            a.Enemies.Add(e);
        }
    }

    private static bool Same(RawAttempt a, RawAttempt b)
    {
        if (a.Number != b.Number || a.Start != b.Start || a.End != b.End || a.StartedUtcTicks != b.StartedUtcTicks || a.SampleRate != b.SampleRate
            || a.LevelTimeStart != b.LevelTimeStart || a.LevelTimeEnd != b.LevelTimeEnd || a.Cheats != b.Cheats || a.MajorAssists != b.MajorAssists
            || a.WeaponsRecorded != b.WeaponsRecorded || a.Count != b.Count || a.Events.Count != b.Events.Count || a.WeaponNames.Count != b.WeaponNames.Count
            || a.EnemyStride != b.EnemyStride || a.Enemies.Count != b.Enemies.Count || !a.DoorPaths.SequenceEqual(b.DoorPaths))
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
        for (int i = 0; i < a.Enemies.Count; i++)
        {
            if (!Same(a.Enemies[i], b.Enemies[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Same(EnemyTrack a, EnemyTrack b)
    {
        if (a.Id != b.Id || a.Type != b.Type || a.Name != b.Name || BitConverter.SingleToInt32Bits(a.Scale) != BitConverter.SingleToInt32Bits(b.Scale)
            || a.Start != b.Start || a.Died != b.Died || a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a.PosX[i] != b.PosX[i] || a.PosY[i] != b.PosY[i] || a.PosZ[i] != b.PosZ[i]
                || a.RotX[i] != b.RotX[i] || a.RotY[i] != b.RotY[i] || a.RotZ[i] != b.RotZ[i] || a.RotW[i] != b.RotW[i]
                || a.Anim[i] != b.Anim[i] || a.AnimTime[i] != b.AnimTime[i] || a.Health[i] != b.Health[i])
            {
                return false;
            }
        }
        return true;
    }

    private static RawAttempt RoundTrip(RawAttempt a, bool world = true)
    {
        byte[] bytes = AttemptCodec.Encode(a);
        return AttemptCodec.Decode(bytes, 0, bytes.Length, world);
    }

    private static void Codec()
    {
        Console.WriteLine("codec");
        foreach (int n in new[] { 2, 7, 1800 })
        {
            RawAttempt a = Sample(42, n, n, world: false);
            Check(Same(a, RoundTrip(a)), $"{n} samples come back exactly");
        }
        int enemies = 0, doors = 0, enemySamples = 0;
        bool exact = true;
        for (int seed = 0; seed < 200; seed++)
        {
            RawAttempt a = Sample(seed, 2 + seed * 7 % 300, seed);
            exact &= Same(a, RoundTrip(a));
            enemies += a.Enemies.Count;
            doors += a.DoorPaths.Count;
            enemySamples += a.Enemies.Sum(e => e.Count);
        }
        Check(exact, $"200 attempts with their worlds come back exactly ({enemies} enemies, {enemySamples} enemy samples, {doors} doors)");

        RawAttempt crowd = Smooth(60 * 60, 5, 60);
        Crowd(crowd, 12, 5);
        Check(Same(crowd, RoundTrip(crowd)), "a minute with 12 enemies about comes back exactly");
        RawAttempt bare = RoundTrip(crowd, world: false);
        Check(bare.Count == crowd.Count && bare.Enemies.Count == 0 && bare.DoorPaths.Count == 0 && bare.PosX[100] == crowd.PosX[100],
            "decoding without the world leaves out the enemies and doors (and nothing else)");

        RawAttempt live = Sample(3, 50, 9);
        live.Enemies.Add(new EnemyTrack { Id = 99, Name = "Filth" });
        live.Enemies[^1].Add(1, 2, 3, 0, 0, 0, 32767, 5, 6, 255);
        RawAttempt clone = live.Clone(AttemptEnd.InProgress);
        clone.Enemies[^1].PosX[0] = 77;
        Check(clone.End == AttemptEnd.InProgress && clone.PosX.Length == 50 && clone.PosX[10] == live.PosX[10], "an attempt in progress clones trimmed");
        Check(clone.Enemies.Count == live.Enemies.Count && live.Enemies[^1].PosX[0] == 1, "...with its own copy of the enemies");

        bool threw = false;
        try
        {
            byte[] bytes = AttemptCodec.Encode(Sample(1, 100, 1, world: false));
            AttemptCodec.Decode(bytes, 0, bytes.Length / 2);
        }
        catch (Exception)
        {
            threw = true;
        }
        Check(threw, "a cut-short attempt is refused, not misread");
    }

    // An attempt recorded by UltraAttempts 1.0: format 1, written by its own encoder (commit bf1d01f)
    // from OldAttempt() below.
    private const string Version1Attempt =
        "Y2RnZGRYcHbiqQ93OOQYGDwcGRgyHNmYBAI8XYOcXYMUglzD/H3CXIME/IGEh6tjiEKwh3+Ie6ifjQIjAwgqgklG0ngpYJAMBklgkAgG" +
        "CWAQDwZxYBALBjFgEA0GF/gjmJmZ5ZhV5YCkKpABAcQJfGEWY4CAo4zHGClgvtezgYrQGaxhYmRkZCMTMzIxMZOLtounkO/qA7sZ+Sjw" +
        "NCsQy8mSRTy4wsxJvsUsDIxMDAwijCxMTNxMDH8ZGfiYgaIA";

    private static RawAttempt OldAttempt()
    {
        var a = new RawAttempt
        {
            Number = 7,
            Start = AttemptStart.Checkpoint,
            End = AttemptEnd.Death,
            StartedUtcTicks = 638_650_000_000_000_000L,
            SampleRate = 30,
            LevelTimeStart = 12.5f,
            LevelTimeEnd = 14.5f,
            Cheats = false,
            MajorAssists = true,
            WeaponsRecorded = true,
        };
        for (int i = 0; i < 60; i++)
        {
            a.Add(
                1000 + i * 45 - (i * i) % 17, 250 + (i % 9) * 11, -3000 + i * 30,
                0, (short)(150 - (i % 4)), (short)(i % 3 - 1),
                (short)(i * 50 - 1500), (short)(12000 + i * 7), (short)(-(i % 5) * 3), (short)(30000 - i * 5),
                (byte)((i % 2) | (i % 7 == 0 ? 32 : 0)), (byte)(100 - i / 6));
        }
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Weapon, A = 1, B = 0, C = a.AddWeaponName("PIERCER REVOLVER") });
        a.Events.Add(new AttemptEvent { Sample = 20, Kind = EventKind.Weapon, A = 2, B = 1, C = a.AddWeaponName("OVERHEAT SHOTGUN") });
        a.Events.Add(new AttemptEvent { Sample = 31, Kind = EventKind.Up, A = 0, B = -127, C = 0 });
        a.Events.Add(new AttemptEvent { Sample = 45, Kind = EventKind.Checkpoint });
        return a;
    }

    private static void OldAttempts()
    {
        Console.WriteLine("1.0 attempts");
        byte[] bytes = Convert.FromBase64String(Version1Attempt);
        Check(Inflate(bytes)[0] == 1, "the embedded attempt is format 1");
        RawAttempt a = AttemptCodec.Decode(bytes, 0, bytes.Length);
        Check(Same(OldAttempt(), a), "an attempt recorded by 1.0 reads back exactly");
        Check(a.Enemies.Count == 0 && a.DoorPaths.Count == 0, "...with no enemies or doors");
        byte[] again = AttemptCodec.Encode(a);
        Check(Inflate(again)[0] == AttemptCodec.Version && Same(a, AttemptCodec.Decode(again, 0, again.Length)), $"saved again it's format {AttemptCodec.Version}, and the same");
    }

    // Damaged attempts are refused (and quickly: a count too big for the data isn't allocated first).
    private static void Damage()
    {
        Console.WriteLine("damage");
        RawAttempt a = null;
        for (int seed = 0; a == null || a.Enemies.Sum(e => e.Count) < 20; seed++)
        {
            a = Sample(5, 120, seed);  // (one with a few enemies)
        }
        byte[] raw = Inflate(AttemptCodec.Encode(a));
        int refused = 0, tries = 0;
        for (int cut = raw.Length - 1; cut > 0; cut -= Math.Max(1, raw.Length / 97))
        {
            tries++;
            try
            {
                byte[] shortened = Deflate(raw.Take(cut).ToArray());
                AttemptCodec.Decode(shortened, 0, shortened.Length);
            }
            catch (EndOfStreamException)
            {
                refused++;
            }
            catch (InvalidDataException)
            {
                refused++;
            }
        }
        Check(refused == tries, $"an attempt cut anywhere (in V1's part or the world's) is refused ({refused} of {tries})");

        raw[0] = AttemptCodec.Version + 1;
        bool newer = false;
        try
        {
            byte[] future = Deflate(raw);
            AttemptCodec.Decode(future, 0, future.Length);
        }
        catch (InvalidDataException e)
        {
            newer = e.Message.Contains("newer");
        }
        Check(newer, "an attempt from a newer UltraAttempts is refused as newer");

        // Version 2, no samples, no events, no doors; then a count of enemies (or an enemy's samples)
        // far beyond what the data could hold.
        var head = new List<byte> { 2, 1, 0, 1 };
        head.AddRange(new byte[8 + 1 + 4 + 4]);
        head[12] = 60;
        head.AddRange(new byte[] { 0, 0, 0, 0, 2, 0 });
        var hugeCount = new List<byte>(head);
        hugeCount.AddRange(new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 });
        var hugeTrack = new List<byte>(head);
        hugeTrack.AddRange(new byte[] { 1, 1, (byte)'x', 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 });
        hugeTrack.AddRange(new byte[64]);
        bool both = true;
        var clock = Stopwatch.StartNew();
        foreach (List<byte> damaged in new[] { hugeCount, hugeTrack })
        {
            try
            {
                byte[] bytes = Deflate(damaged.ToArray());
                AttemptCodec.Decode(bytes, 0, bytes.Length);
                both = false;
            }
            catch (EndOfStreamException)
            {
            }
        }
        Check(both && clock.ElapsedMilliseconds < 1000, "a count too big for the data is refused before anything that size is made");
    }

    // Moving like V1 does: the mouse turns smoothly, speed builds and drops, jumps arc, dashes burst
    // (at 30 Hz exactly the same as 1.0's test; faster rates move the same per second).
    private static RawAttempt Smooth(int samples, int seed, int rate = 30)
    {
        var random = new Random(seed);
        var a = new RawAttempt { SampleRate = rate, WeaponsRecorded = true };
        double s = 30.0 / rate;
        double x = 0, y = 0, z = 0, vy = 0, yaw = 0, yawRate = 0, pitch = 0, pitchRate = 0, speed = 0.5;
        for (int i = 0; i < samples; i++)
        {
            yawRate = Math.Clamp(yawRate + (random.NextDouble() - 0.5) * 0.02 * s, -0.12, 0.12);
            pitchRate = Math.Clamp(pitchRate * Math.Pow(0.9, s) + (random.NextDouble() - 0.5) * 0.01 * s, -0.05, 0.05);
            yaw += yawRate * s;
            pitch = Math.Clamp(pitch + pitchRate * s, -1.2, 1.2);
            speed = Math.Clamp(speed + (random.NextDouble() - 0.5) * 0.04 * s, 0.2, 0.9);
            bool dash = i % (3 * rate) < rate / 5;
            double step = (dash ? 2.0 : speed) * s;
            x += Math.Sin(yaw) * step;
            z += Math.Cos(yaw) * step;
            if (y <= 0 && i % (rate * 3 / 2) == 0)
            {
                vy = 0.55;  // jump
            }
            vy -= 0.04 * s;
            y = Math.Max(0, y + vy * s);
            double cy = Math.Cos(yaw * 0.5), sy = Math.Sin(yaw * 0.5), cp = Math.Cos(pitch * 0.5), sp = Math.Sin(pitch * 0.5);
            double qx = cy * sp, qy = sy * cp, qz = -sy * sp, qw = cy * cp;
            if (qw < 0)
            {
                qx = -qx; qy = -qy; qz = -qz; qw = -qw;
            }
            byte flags = (byte)((y <= 0 ? 1 : 0) | (dash ? 4 : 0) | (i % (2 * rate) < 2 * rate / 3 ? 8 : 0) | (i % (rate * 5 / 6) == 0 ? 32 : 0));
            a.Add(
                (int)Math.Round(x * 100), (int)Math.Round(y * 100), (int)Math.Round(z * 100),
                0, 100, 0,
                (short)Math.Round(qx * 32767), (short)Math.Round(qy * 32767), (short)Math.Round(qz * 32767), (short)Math.Round(qw * 32767),
                flags, (byte)(100 - (i / (10 * rate)) * 10));
        }
        return a;
    }

    // Enemies the way the recorder sees them: walking about and turning smoothly to where they go (some
    // flying), looping a walk and now and then attacking (standing still for it), losing health, a third
    // of them dying, half there from the start and half showing up later; rotations in the recorder's
    // steps of 16 (step: 1 for exact ones). Plus 30 doors, a few opening and closing.
    private static void Crowd(RawAttempt a, int count, int seed, int step = 16)
    {
        var random = new Random(seed);
        a.EnemyStride = Math.Max(1, a.SampleRate / 30);
        double dt = (double)a.EnemyStride / a.SampleRate;
        int total = (a.Count - 1) / a.EnemyStride + 1;
        int walk = unchecked((int)0x9E3779B1), attack = 0x2545F491;
        for (int id = 0; id < count; id++)
        {
            int from = id < count / 2 ? 0 : random.Next(0, total / 2);
            bool died = id % 3 == 2;
            int to = died ? from + random.Next((total - from) / 3, total - from) : total;
            bool flies = id % 5 == 4;
            var e = new EnemyTrack
            {
                Id = id,
                Type = flies ? 1 : random.Next(2, 40),
                Name = flies ? "Drone" : EnemyNames[random.Next(3)],
                Scale = 1f,
                Start = from * a.EnemyStride,
                Died = died,
            };
            double x = random.Next(-50, 50), y = flies ? 8 : 0, z = random.Next(-50, 50);
            double heading = random.NextDouble() * Math.PI * 2, turn = 0, speed = 4 + random.NextDouble() * 8, health = 1;
            int anim = walk;
            double animTime = random.NextDouble();
            for (int k = from; k < to; k++)
            {
                if (anim == walk && random.Next(120) == 0)
                {
                    anim = attack;
                    animTime = 0;
                }
                else if (anim == attack && animTime >= 1.3)
                {
                    anim = walk;
                    animTime = 0;
                }
                animTime += dt / (anim == walk ? 0.8 : 1.1);
                turn = Math.Clamp(turn * 0.95 + (random.NextDouble() - 0.5) * 0.15, -2, 2);
                heading += turn * dt;
                double moving = anim == walk ? speed * dt : 0;
                x += Math.Sin(heading) * moving;
                z += Math.Cos(heading) * moving;
                if (flies)
                {
                    y = 8 + Math.Sin(k * dt * 1.7) * 2;
                }
                if (random.Next(45) == 0)
                {
                    health = Math.Max(0, health - random.NextDouble() * 0.3);
                }
                double qy = Math.Sin(heading * 0.5), qw = Math.Cos(heading * 0.5);
                if (qw < 0)
                {
                    qy = -qy;
                    qw = -qw;
                }
                double time = anim == walk ? animTime - Math.Floor(animTime) : Math.Min(1, animTime);
                e.Add(
                    (int)Math.Round(x * 100), (int)Math.Round(y * 100), (int)Math.Round(z * 100),
                    0, Coarse(qy, step), 0, Coarse(qw, step),
                    anim, (ushort)Math.Round(time * 65535), (byte)Math.Round(health * 255));
            }
            a.Enemies.Add(e);
        }
        for (int d = 0; d < 30; d++)
        {
            a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Door, A = a.AddDoorPath($"Main#0/Room {d / 3}#1/Doors#0/Door#{d % 3}"), B = d % 4 == 0 ? 1 : 0 });
        }
        for (int c = 0; c < 8; c++)
        {
            a.Events.Add(new AttemptEvent { Sample = a.Count * (c + 1) / 10, Kind = EventKind.Door, A = c * 3, B = (c + 1) % 2 });
        }
        a.Events = a.Events.OrderBy(e => e.Sample).ToList();
    }

    private static short Coarse(double q, int step) => (short)(Math.Clamp(Math.Round(q * 32767 / step), short.MinValue / step, short.MaxValue / step) * step);

    private static void Compression()
    {
        Console.WriteLine("size");
        const int plainSample = 12 + 6 + 8 + 2;  // positions, eyes, rotation, flags + HP as plain structs
        const int plainEnemySample = 12 + 8 + 4 + 2 + 1;  // position, rotation, animation, its time, health
        RawAttempt smooth = Smooth(30 * 60, 123);
        Check(Same(smooth, RoundTrip(smooth)), "realistic movement comes back exactly");
        int raw = smooth.Count * plainSample;
        int packed = AttemptCodec.Encode(smooth).Length;
        Console.WriteLine($"        a minute of non-stop V1 movement at 30 Hz: {packed / 1024.0:0.0} KB ({raw / 1024.0:0.0} KB as plain structs, {packed / 60.0:0} bytes/s)");
        Check(packed * 4 < raw, "at least 4x smaller than plain samples");

        RawAttempt fast = Smooth(60 * 60, 123, 60);
        int packed60 = AttemptCodec.Encode(fast).Length;
        Console.WriteLine($"        the same at 60 Hz: {packed60 / 1024.0:0.0} KB ({packed60 / 60.0:0} bytes/s)");
        Check(packed60 * 4 < fast.Count * plainSample, "at 60 Hz too, at least 4x smaller than plain samples");

        Crowd(fast, 10, 77);
        int enemySamples = fast.Enemies.Sum(e => e.Count);
        int crowded = AttemptCodec.Encode(fast).Length;
        int world = crowded - packed60;
        Console.WriteLine($"        ...with 10 enemies about and 30 doors: {crowded / 1024.0:0.0} KB; the world is {world / 1024.0:0.0} KB of it "
            + $"({enemySamples} enemy samples at {fast.SampleRate / fast.EnemyStride} Hz, {world / 60.0:0} bytes/s, {enemySamples * plainEnemySample / 1024.0:0.0} KB as plain structs)");
        Check(world * 4 < enemySamples * plainEnemySample, "the enemies are at least 4x smaller than plain samples");
        Check(world < packed60 * 2, "ten enemies cost less than twice V1 itself");

        RawAttempt exact = Smooth(60 * 60, 123, 60);
        Crowd(exact, 10, 77, step: 1);
        int full = AttemptCodec.Encode(exact).Length;
        Console.WriteLine($"        (with exact enemy rotations instead of the recorder's steps of 16, the world would be {(full - packed60) / 1024.0:0.0} KB)");
        Check(crowded < full, "enemy rotations in steps pack smaller");

        RawAttempt noise = Sample(1, 30 * 60, 123, world: false);
        int noisy = AttemptCodec.Encode(noise).Length;
        Console.WriteLine($"        worst case, pure noise every sample: {noisy / 1024.0:0.0} KB ({noisy / 60.0:0} bytes/s)");
        Check(noisy < noise.Count * plainSample, "even pure noise is smaller than plain samples");

        var still = new RawAttempt { SampleRate = 60 };
        for (int i = 0; i < 60 * 60; i++)
        {
            still.Add(1000, 200, -300, 0, 100, 0, 0, 0, 0, 32767, 1, 100);
        }
        var idle = new EnemyTrack { Name = "Filth", Type = 3 };
        for (int k = 0; k < 30 * 60; k++)
        {
            idle.Add(500, 0, 700, 0, 12000, 0, 30000, 12345, (ushort)(k * 1000), 255);
        }
        still.Enemies.Add(idle);
        int standing = AttemptCodec.Encode(still).Length;
        Console.WriteLine($"        a minute standing still at 60 Hz, next to an enemy idling: {standing} bytes");
        Check(standing < 1024, "standing still costs almost nothing");
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

        int afterWin = afterCrash.BeginAttempt(level);
        string newRun = afterCrash.CurrentRunPath(level.Key, out int inNewRun);
        Check(afterWin == 6 && newRun != first && inNewRun == 0, $"after a win attempts count on (attempt {afterWin}) in a new run");
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
        string firstVisit = fresh.CurrentRunPath(other.Key, out _);
        fresh.ForgetRun(other.Key);
        System.Threading.Thread.Sleep(1100);
        Check(fresh.BeginAttempt(other) == 2 && fresh.CurrentRunPath(other.Key, out _) != firstVisit, "with runs not carrying over, a new visit starts a new run (the count goes on: 2)");
        Counting(root, log);
        Check(SessionStore.Sanitize("Level: 1/1?") == "Level_ 1_1_", "level names become safe folder names");
        foreach (string line in log)
        {
            Console.WriteLine("        log: " + line);
        }
    }

    // A run begun with 1.0 and left unfinished (days ago, say): the next launch carries it on, with new
    // attempts (and their worlds) in the same file after the old ones.
    private static void OldRun(string root)
    {
        Console.WriteLine("a run begun with 1.0");
        var log = new List<string>();
        LevelInfo level = Level(root, "Level 4-2");
        Directory.CreateDirectory(level.Folder);
        string path = Path.Combine(level.Folder, "2026-11-01 20-00-00" + SessionFile.Extension);
        byte[] old = Convert.FromBase64String(Version1Attempt);
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            SessionFile.WriteHeader(stream, new SessionHeader { CreatedUtcTicks = 638_650_000_000_000_000L, Scene = level.Scene, LevelName = level.Name, Difficulty = level.Difficulty });
            for (int i = 0; i < 3; i++)
            {
                SessionFile.WriteBlock(stream, SessionFile.BlockAttempt, old);
            }
        }

        var store = new SessionStore(root, log.Add);
        Check(store.BeginAttempt(level) == 4, "a 1.0 run of 3 attempts carries on at attempt 4");
        RawAttempt died = Smooth(60 * 20, 9, 60);
        Crowd(died, 6, 9);
        store.SubmitAttempt(level, died, finishesRun: false);
        Check(store.BeginAttempt(level) == 5, "...then 5");
        RawAttempt won = Smooth(60 * 30, 10, 60);
        Crowd(won, 8, 10);
        won.End = AttemptEnd.Finish;
        store.SubmitAttempt(level, won, finishesRun: true);
        store.Flush();

        Check(SessionStore.RunFiles(level.Folder).Count == 1, "...in the same file");
        SessionData data = SessionFile.Load(path, log.Add);
        Check(data.Info.Complete && data.Attempts.Count == 5, "the win completes it (5 attempts)");
        Check(data.Attempts.Take(3).All(a => Same(OldAttempt(), a)), "the 1.0 attempts read back exactly, with no world");
        Check(Same(died, data.Attempts[3]) && Same(won, data.Attempts[4]) && data.Attempts[4].Number == 5, "the new ones read back exactly, enemies and doors included");
        SessionData light = SessionFile.Load(path, log.Add, world: false);
        Check(light.Attempts.Count == 5 && light.Attempts[4].Enemies.Count == 0 && light.Attempts[4].Count == won.Count, "loading without the world leaves the enemies out");
        Check(log.Count == 0, "nothing was skipped or complained about");
        foreach (string line in log)
        {
            Console.WriteLine("        log: " + line);
        }
    }

    private static byte[] Inflate(byte[] data)
    {
        using (var input = new MemoryStream(data))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using (var output = new MemoryStream())
        {
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
    }
}
