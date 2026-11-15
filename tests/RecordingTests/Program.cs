using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UltraAttempts.Recording;

// Checks the recording format end to end: an attempt (with its enemies, doors, and everything else that
// moved or happened: things, looks, explosions, beams, breaks) survives encoding exactly, attempts recorded
// by 1.0 and 1.1 still read, damaged data is refused, runs continue and complete as they should (a run begun
// with 1.0 carries on with new attempts), an attempt reads again from where it is in its file, a crash's
// half-written block is ignored and cut off, old runs are pruned, and how small a realistic attempt is.
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
            Details();
            Blocks();
            Effects();
            Sections();
            Arms();
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
                $"{(run.Attempts.Any(a => a.Enemies.Count > 0) ? $", {run.Attempts.Sum(a => a.Enemies.Count)} enemy tracks" : "")}" +
                $"{(run.Attempts.Any(a => a.HasThings) ? $", {run.Attempts.Sum(a => a.Things.Count)} things, {run.Attempts.Sum(a => a.WorldEvents.Count)} explosions/beams/breaks" : "")}" +
                $"{(run.Attempts.Any(a => a.SoundsRecorded) ? $", {run.Attempts.Sum(a => a.Sounds.Count)} sounds ({run.Attempts.SelectMany(a => a.SoundClips).Select(c => c.Name).Distinct().Count()} clips)" : "")}" +
                $", {run.Attempts.Sum(a => Enumerable.Range(0, a.Count).Count(i => (a.Flags[i] & (byte)SampleFlags.Shot) != 0))} shots, {run.Attempts.Sum(a => Enumerable.Range(0, a.Count).Count(i => (a.Flags[i] & (byte)SampleFlags.Punch) != 0))} punch presses");
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
            ExtremeThings(a, random);
            ExtremeSounds(a, random);
        }
        return a;
    }

    private static readonly string[] ClipNames = { "Revolver Shot 1", "Ünïcödé Explosion", "", "gabriel_taunt_04", new string('S', 300) };

    // Sounds with anything in them (Recording.RecordSounds): out of order (delayed ones), any number in any field,
    // positions far off, moves of any of them.
    private static void ExtremeSounds(RawAttempt a, Random random)
    {
        a.SoundsRecorded = random.Next(4) != 0;
        if (!a.SoundsRecorded && random.Next(2) == 0)
        {
            return;
        }
        for (int c = random.Next(0, 5); c > 0; c--)
        {
            a.SoundClips.Add(new SoundClip { Name = ClipNames[random.Next(ClipNames.Length)], LengthMs = random.Next(0, int.MaxValue) });
        }
        if (a.SoundClips.Count == 0)
        {
            return;
        }
        int sample = 0;
        for (int s = random.Next(0, 30); s > 0; s--)
        {
            sample += random.Next(0, 4) == 0 ? -random.Next(0, 50) : random.Next(0, 40);
            a.Sounds.Add(new SoundEvent
            {
                Sample = random.Next(10) == 0 ? random.Next(int.MinValue, int.MaxValue) : sample,
                Clip = random.Next(a.SoundClips.Count),
                Flags = (SoundFlags)random.Next(0, 256),
                Volume = random.Next(3) == 0 ? random.Next(int.MinValue, int.MaxValue) : random.Next(0, 2000),
                Pitch = random.Next(3) == 0 ? random.Next(int.MinValue, int.MaxValue) : random.Next(-1000, 3000),
                Spatial = (byte)random.Next(256),
                Rolloff = (byte)random.Next(256),
                MinDistance = random.Next(int.MinValue, int.MaxValue),
                MaxDistance = random.Next(0, 100_000_000),
                X = random.Next(-100_000_000, 100_000_001),
                Y = random.Next(-100_000_000, 100_000_001),
                Z = random.Next(int.MinValue, int.MaxValue),
                Length = random.Next(-5, 1000),
            });
        }
        if (a.Sounds.Count > 2)
        {
            // (the far ends of every number, next to each other: the steps between them wrap round and back)
            a.Sounds[0] = new SoundEvent { Sample = int.MinValue, Clip = 0, X = int.MaxValue, Y = int.MinValue, Z = int.MaxValue, Volume = int.MaxValue, Pitch = int.MinValue, Length = int.MaxValue };
            a.Sounds[1] = new SoundEvent { Sample = int.MaxValue, Clip = a.SoundClips.Count - 1, X = int.MinValue, Y = int.MaxValue, Z = int.MinValue, MinDistance = int.MinValue, MaxDistance = int.MaxValue, Flags = (SoundFlags)255, Spatial = 255, Rolloff = 255 };
        }
        for (int m = random.Next(0, 20); m > 0 && a.Sounds.Count > 0; m--)
        {
            a.SoundMoves.Add(new SoundMove
            {
                Sound = random.Next(a.Sounds.Count),
                Sample = random.Next(int.MinValue, int.MaxValue),
                X = random.Next(int.MinValue, int.MaxValue),
                Y = random.Next(-100_000_000, 100_000_001),
                Z = random.Next(-100_000_000, 100_000_001),
            });
        }
    }

    // A minute of a fight's sounds, as the recorder makes them: V1's guns, punches and feet (on V1, heard
    // everywhere), enemies about and now and then an explosion (where they were, some of them moving), a slide's loop.
    private static void Noises(RawAttempt a, int seed)
    {
        var random = new Random(seed);
        a.SoundsRecorded = true;
        string[] names = { "Revolver Shot 1", "Revolver Shot 2", "Shotgun Shot", "Footstep 1", "Footstep 2", "Footstep 3", "Punch",
            "Filth Attack", "Stray Throw", "Explosion", "Gabriel Taunt 3", "Gabriel Swing", "Slide Loop", "Door Open", "Hurt" };
        foreach (string name in names)
        {
            a.SoundClips.Add(new SoundClip { Name = name, LengthMs = 200 + random.Next(2500) });
        }
        for (int sample = 0; sample < a.Count; sample += 1 + random.Next(10))
        {
            int clip = random.Next(names.Length);
            bool own = clip < 7 || clip == 12 || clip == 14;
            var e = new SoundEvent
            {
                Sample = sample,
                Clip = clip,
                Flags = (own ? SoundFlags.OnPlayer : 0) | (clip == 12 ? SoundFlags.Loop : 0) | (clip == 6 ? SoundFlags.OneShot : 0),
                Volume = own ? 550 : 1000,
                Pitch = clip < 2 ? 900 + random.Next(200) : 1000,
                Spatial = (byte)(own ? 0 : 255),
                Rolloff = 1,
                MinDistance = 500,
                MaxDistance = clip == 9 ? 20000 : 10000,
                Length = clip == 12 ? 30 + random.Next(60) : random.Next(10) == 0 ? 5 + random.Next(20) : 0,
            };
            if (!own)
            {
                e.X = a.PosX[sample] + random.Next(-3000, 3000);
                e.Y = a.PosY[sample] + random.Next(-300, 300);
                e.Z = a.PosZ[sample] + random.Next(-3000, 3000);
            }
            a.Sounds.Add(e);
            if (!own && random.Next(8) == 0)
            {
                for (int k = 1; k <= 3; k++)
                {
                    a.SoundMoves.Add(new SoundMove { Sound = a.Sounds.Count - 1, Sample = sample + k * 2, X = e.X + k * 150, Y = e.Y, Z = e.Z - k * 90 });
                }
            }
        }
    }

    private static readonly string[] MeshNames = { "Sphere", "Cube", "Rocket", "Ünïcödé Nail", "", "Gib_Head", new string('M', 400) };

    private static float AnyFloat(Random random)
    {
        switch (random.Next(8))
        {
            case 0: return float.NaN;
            case 1: return float.PositiveInfinity;
            case 2: return -0f;
            case 3: return float.MaxValue;
            case 4: return float.Epsilon;
            default: return (float)((random.NextDouble() - 0.5) * 1000);
        }
    }

    // Looks, things and events with anything in them (WorldDetail = Everything).
    private static void ExtremeThings(RawAttempt a, Random random)
    {
        int looks = random.Next(0, 5);
        for (int l = 0; l < looks; l++)
        {
            var look = new ThingLook
            {
                Name = EnemyNames[random.Next(EnemyNames.Length)],
                Color = (uint)random.Next() ^ ((uint)random.Next(2) << 31),
                CenterX = AnyFloat(random), CenterY = AnyFloat(random), CenterZ = AnyFloat(random),
                SizeX = AnyFloat(random), SizeY = AnyFloat(random), SizeZ = AnyFloat(random),
            };
            for (int p = random.Next(0, 4); p > 0; p--)
            {
                look.Parts.Add(new LookPart
                {
                    Mesh = MeshNames[random.Next(MeshNames.Length)],
                    Vertices = random.Next(0, int.MaxValue),
                    Materials = Enumerable.Range(0, random.Next(0, 4)).Select(_ => MeshNames[random.Next(MeshNames.Length)] + " mat").ToArray(),
                    PX = AnyFloat(random), PY = AnyFloat(random), PZ = AnyFloat(random),
                    RX = AnyFloat(random), RY = AnyFloat(random), RZ = AnyFloat(random), RW = AnyFloat(random),
                    SX = AnyFloat(random), SY = AnyFloat(random), SZ = AnyFloat(random),
                });
            }
            a.Looks.Add(look);
        }
        for (int p = random.Next(0, 4); p > 0; p--)
        {
            a.AddThingPath($"Level#0/Room#{p}/Ünïcödé Platform#{random.Next(9)}");
        }
        int id = random.Next(0, 5);
        for (int k = random.Next(0, 10); k > 0; k--)
        {
            var t = new ThingTrack
            {
                Id = id,
                Kind = (ThingKind)random.Next(0, 5),
                Look = random.Next(-1, a.Looks.Count),
                Path = random.Next(-1, a.ThingPaths.Count),
                ScaleX = AnyFloat(random), ScaleY = AnyFloat(random), ScaleZ = AnyFloat(random),
                Gone = random.Next(2) == 0,
            };
            id += random.Next(1, 4);
            int sample = random.Next(0, Math.Max(1, a.Count));
            for (int i = random.Next(0, 30); i > 0; i--)
            {
                sample += random.Next(0, 3) == 0 ? random.Next(1, 200) : a.EnemyStride;
                t.Add(sample,
                    random.Next(-100_000_000, 100_000_001), random.Next(-100_000_000, 100_000_001), random.Next(-100_000_000, 100_000_001),
                    (short)random.Next(short.MinValue, short.MaxValue + 1), (short)random.Next(short.MinValue, short.MaxValue + 1),
                    (short)random.Next(short.MinValue, short.MaxValue + 1), (short)random.Next(0, short.MaxValue + 1),
                    (ushort)random.Next(0, 65536));
            }
            t.End = sample + random.Next(0, 50);
            a.Things.Add(t);
        }
        int at = 0;
        for (int e = random.Next(0, 12); e > 0; e--)
        {
            at += random.Next(0, 40);
            a.WorldEvents.Add(new WorldEvent
            {
                Sample = at,
                Kind = (WorldEventKind)random.Next(1, 4),
                X = random.Next(-100_000_000, 100_000_001), Y = random.Next(-100_000_000, 100_000_001), Z = random.Next(-100_000_000, 100_000_001),
                ToX = random.Next(-100_000_000, 100_000_001), ToY = random.Next(-100_000_000, 100_000_001), ToZ = random.Next(-100_000_000, 100_000_001),
                Size = random.Next(int.MinValue, int.MaxValue),
                Life = random.Next(0, 100_000),
                Color = (uint)random.Next() * 2u + (uint)random.Next(2),
                Look = random.Next(-1, a.Looks.Count),
                Path = random.Next(-1, a.ThingPaths.Count),
                RotX = (short)random.Next(short.MinValue, short.MaxValue + 1), RotY = short.MinValue, RotZ = short.MaxValue, RotW = (short)random.Next(0, 3),
            });
        }
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
        if (a.Looks.Count != b.Looks.Count || !a.ThingPaths.SequenceEqual(b.ThingPaths) || a.Things.Count != b.Things.Count || a.WorldEvents.Count != b.WorldEvents.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Looks.Count; i++)
        {
            if (!Same(a.Looks[i], b.Looks[i]))
            {
                return false;
            }
        }
        for (int i = 0; i < a.Things.Count; i++)
        {
            if (!Same(a.Things[i], b.Things[i]))
            {
                return false;
            }
        }
        for (int i = 0; i < a.WorldEvents.Count; i++)
        {
            WorldEvent e = a.WorldEvents[i], f = b.WorldEvents[i];
            if (e.Sample != f.Sample || e.Kind != f.Kind || e.X != f.X || e.Y != f.Y || e.Z != f.Z || e.ToX != f.ToX || e.ToY != f.ToY || e.ToZ != f.ToZ
                || e.Size != f.Size || e.Life != f.Life || e.Color != f.Color || e.Look != f.Look || e.Path != f.Path
                || e.RotX != f.RotX || e.RotY != f.RotY || e.RotZ != f.RotZ || e.RotW != f.RotW)
            {
                return false;
            }
        }
        if (a.SoundsRecorded != b.SoundsRecorded || a.SoundClips.Count != b.SoundClips.Count || a.Sounds.Count != b.Sounds.Count || a.SoundMoves.Count != b.SoundMoves.Count)
        {
            return false;
        }
        for (int i = 0; i < a.SoundClips.Count; i++)
        {
            if (a.SoundClips[i].Name != b.SoundClips[i].Name || a.SoundClips[i].LengthMs != b.SoundClips[i].LengthMs)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Sounds.Count; i++)
        {
            SoundEvent e = a.Sounds[i], f = b.Sounds[i];
            if (e.Sample != f.Sample || e.Clip != f.Clip || e.Flags != f.Flags || e.Volume != f.Volume || e.Pitch != f.Pitch || e.Spatial != f.Spatial
                || e.Rolloff != f.Rolloff || e.MinDistance != f.MinDistance || e.MaxDistance != f.MaxDistance || e.X != f.X || e.Y != f.Y || e.Z != f.Z
                || e.Length != f.Length)
            {
                return false;
            }
        }
        for (int i = 0; i < a.SoundMoves.Count; i++)
        {
            SoundMove m = a.SoundMoves[i], n = b.SoundMoves[i];
            if (m.Sound != n.Sound || m.Sample != n.Sample || m.X != n.X || m.Y != n.Y || m.Z != n.Z)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static bool Same(ThingLook a, ThingLook b)
    {
        if (a.Name != b.Name || a.Color != b.Color || !Bits(a.CenterX, b.CenterX) || !Bits(a.CenterY, b.CenterY) || !Bits(a.CenterZ, b.CenterZ)
            || !Bits(a.SizeX, b.SizeX) || !Bits(a.SizeY, b.SizeY) || !Bits(a.SizeZ, b.SizeZ) || a.Parts.Count != b.Parts.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Parts.Count; i++)
        {
            LookPart p = a.Parts[i], q = b.Parts[i];
            if (p.Mesh != q.Mesh || p.Vertices != q.Vertices || !p.Materials.SequenceEqual(q.Materials)
                || !Bits(p.PX, q.PX) || !Bits(p.PY, q.PY) || !Bits(p.PZ, q.PZ) || !Bits(p.RX, q.RX) || !Bits(p.RY, q.RY) || !Bits(p.RZ, q.RZ) || !Bits(p.RW, q.RW)
                || !Bits(p.SX, q.SX) || !Bits(p.SY, q.SY) || !Bits(p.SZ, q.SZ))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Same(ThingTrack a, ThingTrack b)
    {
        if (a.Id != b.Id || a.Kind != b.Kind || a.Look != b.Look || a.Path != b.Path || !Bits(a.ScaleX, b.ScaleX) || !Bits(a.ScaleY, b.ScaleY) || !Bits(a.ScaleZ, b.ScaleZ)
            || a.End != b.End || a.Gone != b.Gone || a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a.Sample[i] != b.Sample[i] || a.PosX[i] != b.PosX[i] || a.PosY[i] != b.PosY[i] || a.PosZ[i] != b.PosZ[i]
                || a.RotX[i] != b.RotX[i] || a.RotY[i] != b.RotY[i] || a.RotZ[i] != b.RotZ[i] || a.RotW[i] != b.RotW[i] || a.Scale[i] != b.Scale[i])
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
        int enemies = 0, doors = 0, enemySamples = 0, things = 0, keys = 0, looks = 0, happenings = 0, sounds = 0, clips = 0, moves = 0, recorded = 0;
        bool exact = true;
        for (int seed = 0; seed < 200; seed++)
        {
            RawAttempt a = Sample(seed, 2 + seed * 7 % 300, seed);
            exact &= Same(a, RoundTrip(a));
            enemies += a.Enemies.Count;
            doors += a.DoorPaths.Count;
            enemySamples += a.Enemies.Sum(e => e.Count);
            things += a.Things.Count;
            keys += a.Things.Sum(t => t.Count);
            looks += a.Looks.Count;
            happenings += a.WorldEvents.Count;
            sounds += a.Sounds.Count;
            clips += a.SoundClips.Count;
            moves += a.SoundMoves.Count;
            recorded += a.SoundsRecorded ? 1 : 0;
        }
        Check(exact, $"200 attempts with their worlds come back exactly ({enemies} enemies, {enemySamples} enemy samples, {doors} doors, "
            + $"{things} things, {keys} thing keys, {looks} looks, {happenings} explosions, beams and breaks; NaNs and extremes included)");
        Check(exact && sounds > 500 && moves > 100 && recorded > 100 && recorded < 200,
            $"...and their sounds ({recorded} recorded with sounds, {sounds} sounds of {clips} clips, {moves} moves; out of order, wrapping round, any number in any field)");

        RawAttempt crowd = Smooth(60 * 60, 5, 60);
        Crowd(crowd, 12, 5);
        Battle(crowd, 5);
        Noises(crowd, 5);
        Check(Same(crowd, RoundTrip(crowd)), $"a minute with 12 enemies about, a battle's worth of things and {crowd.Sounds.Count} sounds comes back exactly");
        RawAttempt bare = RoundTrip(crowd, world: false);
        Check(bare.Count == crowd.Count && bare.Enemies.Count == 0 && bare.DoorPaths.Count == 0 && bare.Things.Count == 0 && bare.Looks.Count == 0
            && bare.WorldEvents.Count == 0 && bare.PosX[100] == crowd.PosX[100],
            "decoding without the world leaves out the enemies, doors and things (and nothing else)");
        Check(bare.SoundsRecorded && bare.Sounds.Count == crowd.Sounds.Count && bare.SoundMoves.Count == crowd.SoundMoves.Count && bare.SoundClips.Count == crowd.SoundClips.Count
            && bare.Sounds[50].X == crowd.Sounds[50].X && bare.Sounds[^1].Length == crowd.Sounds[^1].Length,
            "...but keeps the sounds (a replay keeps every attempt's, not only their worlds')");

        RawAttempt live = Sample(3, 50, 9);
        live.Enemies.Add(new EnemyTrack { Id = 99, Name = "Filth" });
        live.Enemies[^1].Add(1, 2, 3, 0, 0, 0, 32767, 5, 6, 255);
        live.Things.Add(new ThingTrack { Id = 5 });
        live.Things[^1].Add(4, 1, 2, 3, 0, 0, 0, 32767, 1000);
        live.SourcePath = "somewhere";
        live.SoundsRecorded = true;
        live.SoundClips.Add(new SoundClip { Name = "Revolver Shot 1", LengthMs = 812 });
        live.Sounds.Add(new SoundEvent { Sample = 3, Clip = live.SoundClips.Count - 1, Volume = 550 });
        live.SoundMoves.Add(new SoundMove { Sound = live.Sounds.Count - 1, Sample = 5, X = 1 });
        RawAttempt clone = live.Clone(AttemptEnd.InProgress);
        clone.Enemies[^1].PosX[0] = 77;
        clone.Things[^1].PosX[0] = 77;
        clone.Things.Add(new ThingTrack());
        int liveSounds = live.Sounds.Count, liveMoves = live.SoundMoves.Count, liveClips = live.SoundClips.Count;
        live.Sounds.Add(new SoundEvent());     // (the recorder carries on after the snapshot)
        live.SoundMoves.Add(new SoundMove());
        live.SoundClips.Add(new SoundClip());
        Check(clone.End == AttemptEnd.InProgress && clone.PosX.Length == 50 && clone.PosX[10] == live.PosX[10], "an attempt in progress clones trimmed");
        Check(clone.Enemies.Count == live.Enemies.Count && live.Enemies[^1].PosX[0] == 1, "...with its own copy of the enemies");
        Check(clone.Things.Count == live.Things.Count + 1 && live.Things[^1].PosX[0] == 1 && clone.SourcePath == null, "...and of the things (and no file of its own)");
        Check(clone.SoundsRecorded && clone.Sounds.Count == liveSounds && clone.SoundMoves.Count == liveMoves && clone.SoundClips.Count == liveClips
            && clone.Sounds[^1].Volume == 550, "...and of the sounds");

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

    // What newer recordings keep in fields older ones left at 0 (no format change): how an explosion grows (in
    // its To, which explosions didn't use) with its look, and what a door event says about the door (its C).
    private static void Details()
    {
        Console.WriteLine("explosions and doors");
        RawAttempt a = Sample(7, 40, 7, world: false);
        a.Looks.Add(new ThingLook { Name = "Explosion Big", Color = 0xFF8020C0u, Parts = { new LookPart { Mesh = "Sphere", Vertices = 515, Materials = new[] { "Explosion" }, RW = 1f, SX = 1f, SY = 1f, SZ = 1f } } });
        var grows = new WorldEvent { Sample = 3, Kind = WorldEventKind.Explosion, X = -12_345_678, Y = 789, Z = 42, Size = 5500, Life = 280, Color = 0xFF8020C0u, Look = 0, Path = -1, RotY = 23170, RotW = 23170 };
        ExplosionGrowth.Put(ref grows, 1.25f, 37.5f);
        var old = new WorldEvent { Sample = 4, Kind = WorldEventKind.Explosion, X = 5, Y = 6, Z = 7, ToX = 5, ToY = 6, ToZ = 7, Size = 1000, Life = 100, Color = 0xFFFFFFFFu, Look = -1, Path = -1, RotW = 32767 };
        var odd = new WorldEvent { Sample = 5, Kind = WorldEventKind.Explosion, X = 100_000_000, Y = -100_000_000, Z = 0, Size = 1, Life = 20, Look = 0, Path = -1, RotW = 32767 };
        ExplosionGrowth.Put(ref odd, float.NaN, 1e12f);
        a.WorldEvents.Add(grows);
        a.WorldEvents.Add(old);
        a.WorldEvents.Add(odd);
        int door = a.AddDoorPath("Level#0/2 - Arena#0/Big Door#0");
        int other = a.AddDoorPath("Level#0/2 - Arena#0/Door#1");
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Door, A = door, B = 0, C = (int)(DoorDetail.Recorded | DoorDetail.AsFound) });
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Door, A = other, B = 1, C = 0 });
        a.Events.Add(new AttemptEvent { Sample = 20, Kind = EventKind.Door, A = door, B = 1, C = (int)(DoorDetail.Recorded | DoorDetail.Reversed) });
        RawAttempt b = RoundTrip(a);
        Check(Same(a, b), "an explosion with its look and how it grows, and doors with what's known about them, come back exactly");
        WorldEvent e = b.WorldEvents[0];
        Check(Math.Abs(ExplosionGrowth.StartScale(e) - 1.25f) < 1e-6f && Math.Abs(ExplosionGrowth.PerSecond(e) - 37.5f) < 1e-6f && e.Look == 0 && e.Size == 5500 && e.Life == 280
            && e.Color == 0xFF8020C0u && e.RotY == 23170 && e.ToZ == e.Z, "...the explosion's scale as it went off, how fast it grows, its look, size, time, colour and turn");
        Check(ExplosionGrowth.StartScale(b.WorldEvents[1]) == 0f && ExplosionGrowth.PerSecond(b.WorldEvents[1]) == 0f && b.WorldEvents[1].Look == -1,
            "...one recorded before then says it doesn't know how it grows (the replay draws a sphere)");
        Check(ExplosionGrowth.StartScale(b.WorldEvents[2]) == 0f && ExplosionGrowth.PerSecond(b.WorldEvents[2]) == 1000f, "...nonsense (NaN, huge) is kept in bounds");
        List<AttemptEvent> doors = b.Events.Where(d => d.Kind == EventKind.Door).ToList();
        Check(doors.Count == 3 && (DoorDetail)doors[0].C == (DoorDetail.Recorded | DoorDetail.AsFound) && (DoorDetail)doors[1].C == DoorDetail.None
            && (DoorDetail)doors[2].C == (DoorDetail.Recorded | DoorDetail.Reversed) && doors[2].B == 1 && doors[2].Sample == 20,
            "doors: found shut at the start, one from an older recording (nothing known), opened from the other side");
    }

    // Format 5's blocks: one a later version adds (a tag this one doesn't know), in the attempt's group and the
    // world's, is stepped over; everything around it still reads, with or without the world.
    private static void Blocks()
    {
        Console.WriteLine("blocks");
        RawAttempt a = Sample(11, 300, 11);
        var future = new[]
        {
            new AttemptBlocks.Block { Tag = 9001, World = false, Has = _ => true, Encode = (w, _) => { w.UInt(123456); w.String("from a later version"); w.Long(-1); }, Decode = (r, _) => throw new InvalidDataException("not this one's") },
            new AttemptBlocks.Block { Tag = 9002, World = true, Has = _ => true, Encode = (w, _) => { for (int i = 0; i < 1000; i++) w.Int(-i); }, Decode = (r, _) => throw new InvalidDataException("not this one's") },
        };
        AttemptBlocks.All.AddRange(future);
        byte[] bytes;
        try
        {
            bytes = AttemptCodec.Encode(a);
        }
        finally
        {
            foreach (AttemptBlocks.Block b in future)
            {
                AttemptBlocks.All.Remove(b);
            }
        }
        Check(Same(a, AttemptCodec.Decode(bytes, 0, bytes.Length)), "an attempt with blocks this version doesn't know reads exactly, stepping over them");
        RawAttempt bare = AttemptCodec.Decode(bytes, 0, bytes.Length, world: false);
        Check(bare.Count == a.Count && bare.Things.Count == 0 && bare.Sounds.Count == a.Sounds.Count, "...and without its world");
        Check(AttemptBlocks.All.Select(b => b.Tag).Distinct().Count() == AttemptBlocks.All.Count, "every block has a tag of its own");
    }

    private static readonly string[] PrefabNames = { "MuzzleFlash", "Ünïcödé SlideScrape", "", "Explosion Big", "Revolver Beam", new string('P', 300) };

    // Effects (format 5's block 3) with anything in them: any frame, time, scale (NaN and all), tint and key; prefabs
    // with names and paths of any length; links to the attempt's own looks and beams.
    private static void ExtremeEffects(RawAttempt a, Random random)
    {
        for (int p = random.Next(1, 6); p > 0; p--)
        {
            a.AddEffectPrefab(PrefabNames[random.Next(PrefabNames.Length)], random.Next(3) == 0 ? $"Level#0/Ünïcödé Room#{p}/Effect#{random.Next(9)}" : "", random.Next(0, 1000));
        }
        int start = 0;
        for (int k = random.Next(0, 40); k > 0; k--)
        {
            start += random.Next(0, 3) == 0 ? random.Next(-5, 400) : random.Next(0, 3);
            float scale = AnyFloat(random);
            var e = new EffectTrack
            {
                Prefab = random.Next(a.EffectPrefabs.Count),
                Frame = (EffectFrame)random.Next(0, 4),
                Start = start,
                Stop = random.Next(3) == 0 ? -1 : start + random.Next(-10, 300),
                End = random.Next(3) == 0 ? -1 : random.Next(int.MinValue, int.MaxValue),
                ScaleX = scale,
                ScaleY = random.Next(2) == 0 ? scale : AnyFloat(random),
                ScaleZ = random.Next(2) == 0 ? scale : AnyFloat(random),
                Tint = random.Next(2) == 0 ? 0u : (uint)random.Next() * 2u + (uint)random.Next(2),
            };
            int sample = start;
            for (int i = random.Next(0, 25); i > 0; i--)
            {
                e.Add(sample,
                    random.Next(-100_000_000, 100_000_001), random.Next(-100_000_000, 100_000_001), random.Next(int.MinValue, int.MaxValue),
                    (short)random.Next(short.MinValue, short.MaxValue + 1), (short)random.Next(short.MinValue, short.MaxValue + 1),
                    (short)random.Next(short.MinValue, short.MaxValue + 1), (short)random.Next(0, short.MaxValue + 1),
                    (ushort)random.Next(0, 65536));
                sample += random.Next(0, 3) == 0 ? random.Next(1, 200) : 2;
            }
            a.Effects.Add(e);
        }
        for (int i = 0; i < a.Looks.Count; i++)
        {
            if (random.Next(2) == 0)
            {
                a.LookPrefabs.Add(new EffectLink { Index = i, Prefab = random.Next(a.EffectPrefabs.Count) });
            }
        }
        for (int i = 0; i < a.WorldEvents.Count; i++)
        {
            if (a.WorldEvents[i].Kind == WorldEventKind.Beam && random.Next(3) != 0)
            {
                a.BeamPrefabs.Add(new EffectLink { Index = i, Prefab = random.Next(a.EffectPrefabs.Count) });
            }
        }
    }

    private static bool SameEffects(RawAttempt a, RawAttempt b)
    {
        if (a.EffectPrefabs.Count != b.EffectPrefabs.Count || a.Effects.Count != b.Effects.Count
            || !a.LookPrefabs.SequenceEqual(b.LookPrefabs) || !a.BeamPrefabs.SequenceEqual(b.BeamPrefabs))
        {
            return false;
        }
        for (int i = 0; i < a.EffectPrefabs.Count; i++)
        {
            EffectPrefab p = a.EffectPrefabs[i], q = b.EffectPrefabs[i];
            if (p.Name != q.Name || p.Path != q.Path || p.Parts != q.Parts)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Effects.Count; i++)
        {
            EffectTrack e = a.Effects[i], f = b.Effects[i];
            if (e.Prefab != f.Prefab || e.Frame != f.Frame || e.Start != f.Start || e.Stop != f.Stop || e.End != f.End || e.Tint != f.Tint
                || !Bits(e.ScaleX, f.ScaleX) || !Bits(e.ScaleY, f.ScaleY) || !Bits(e.ScaleZ, f.ScaleZ) || e.Count != f.Count)
            {
                return false;
            }
            for (int k = 0; k < e.Count; k++)
            {
                if (e.Sample[k] != f.Sample[k] || e.PosX[k] != f.PosX[k] || e.PosY[k] != f.PosY[k] || e.PosZ[k] != f.PosZ[k] || e.RotX[k] != f.RotX[k]
                    || e.RotY[k] != f.RotY[k] || e.RotZ[k] != f.RotZ[k] || e.RotW[k] != f.RotW[k] || e.Scale[k] != f.Scale[k])
                {
                    return false;
                }
            }
        }
        return true;
    }

    // The game's own effects (block 3): they come back exactly, only with the world, cloned for an attempt in
    // progress, trimmed at its end, refused when they point at nothing; and what a minute of them costs.
    private static void Effects()
    {
        Console.WriteLine("effects");
        bool exact = true;
        int effects = 0, keys = 0, links = 0;
        for (int seed = 0; seed < 150; seed++)
        {
            RawAttempt a = Sample(seed, 2 + seed * 11 % 250, seed);
            ExtremeEffects(a, new Random(seed));
            RawAttempt b = RoundTrip(a);
            exact &= Same(a, b) && SameEffects(a, b);
            effects += a.Effects.Count;
            keys += a.Effects.Sum(e => e.Count);
            links += a.LookPrefabs.Count + a.BeamPrefabs.Count;
        }
        Check(exact && effects > 1000 && links > 50, $"150 attempts' effects come back exactly ({effects} effects, {keys} keys, {links} links to looks and beams; NaNs and extremes included)");

        RawAttempt some = Sample(5, 120, 5);
        ExtremeEffects(some, new Random(77));
        RawAttempt bare = RoundTrip(some, world: false);
        Check(bare.Effects.Count == 0 && bare.EffectPrefabs.Count == 0 && bare.LookPrefabs.Count == 0 && bare.Count == some.Count,
            "they're the world's: read without it, an attempt has none");

        var only = Sample(6, 60, 6, world: false);
        int flash = only.AddEffectPrefab("MuzzleFlash", "", 4);
        var track = new EffectTrack { Prefab = flash, Frame = EffectFrame.Camera, Start = 10, Stop = 12, End = 14 };
        track.Add(10, 420, -300, 900, 0, 0, 0, 32767, 1000);
        only.Effects.Add(track);
        RawAttempt back = RoundTrip(only);
        Check(only.HasThings && SameEffects(only, back) && back.Effects[0].Frame == EffectFrame.Camera && back.Effects[0].PosZ[0] == 900,
            "an attempt with only effects keeps its world for them");

        RawAttempt clone = some.Clone(AttemptEnd.InProgress);
        clone.Effects[0].PosX[0] = 12345;
        clone.Effects.Add(new EffectTrack());
        clone.LookPrefabs.Add(new EffectLink());
        Check(some.Effects[0].PosX[0] != 12345 && clone.Effects.Count == some.Effects.Count + 1 && clone.LookPrefabs.Count == some.LookPrefabs.Count + 1,
            "an attempt in progress clones with its own copy of the effects");

        var ending = new RawAttempt();
        int dust = ending.AddEffectPrefab("Dust", "", 2);
        var late = new EffectTrack { Prefab = dust, Start = 95, Stop = 120, End = 130 };
        late.Add(95, 0, 0, 0, 0, 0, 0, 32767, 1000);
        late.Add(110, 1, 0, 0, 0, 0, 0, 32767, 1000);
        var early = new EffectTrack { Prefab = dust, Start = 5, Stop = 50, End = 60 };
        ending.Effects.Add(late);
        ending.Effects.Add(early);
        EffectsBlock.Trim(ending, 100);
        Check(late.Stop == -1 && late.End == -1 && late.Sample[1] == 100 && early.Stop == 50 && early.End == 60,
            "an attempt's end: what stopped or went after its last sample hadn't yet, and no key is after it");

        bool refused = true;
        foreach (Action<RawAttempt> spoil in new Action<RawAttempt>[]
        {
            x => x.LookPrefabs.Add(new EffectLink { Index = x.Looks.Count, Prefab = 0 }),
            x => x.BeamPrefabs.Add(new EffectLink { Index = 0, Prefab = x.EffectPrefabs.Count }),
            x => x.Effects.Add(new EffectTrack { Prefab = x.EffectPrefabs.Count }),
        })
        {
            RawAttempt bad = Sample(8, 40, 8, world: false);
            bad.AddEffectPrefab("Spark", "", 1);
            bad.WorldEvents.Add(new WorldEvent { Kind = WorldEventKind.Beam, Look = -1, Path = -1, RotW = 32767 });
            spoil(bad);
            byte[] bytes = AttemptCodec.Encode(bad);
            try
            {
                AttemptCodec.Decode(bytes, 0, bytes.Length);
                refused = false;
            }
            catch (Exception)
            {
            }
        }
        Check(refused, "an effect or link pointing at a prefab, look or beam that isn't there is refused");

        // A minute of a fight's effects: 300 muzzle flashes on the camera (where the last one was), 40 slides with
        // their scrape keyed along, 200 hits about the level that stay put.
        RawAttempt minute = Smooth(60 * 60, 3, 60);
        var random = new Random(3);
        int muzzle = minute.AddEffectPrefab("MuzzleFlash", "", 6), scrape = minute.AddEffectPrefab("SlideScrape", "", 3), hit = minute.AddEffectPrefab("HitSpark", "", 2);
        for (int i = 0; i < 540; i++)
        {
            int at = i * 6 + random.Next(0, 3);
            int kind = i % 27 < 15 ? muzzle : i % 27 < 17 ? scrape : hit;
            var e = new EffectTrack { Prefab = kind, Start = at, End = at + (kind == muzzle ? 6 : kind == scrape ? 60 : 30), Stop = kind == scrape ? at + 40 : -1 };
            if (kind == muzzle)
            {
                e.Frame = EffectFrame.Camera;
                e.Add(at, 412, -287, 934, 0, 0, 0, 32767, 1000);
            }
            else if (kind == scrape)
            {
                for (int k = 0; k < 20; k++)
                {
                    e.Add(at + k * 2, minute.PosX[at + k * 2], minute.PosY[at + k * 2] - 150, minute.PosZ[at + k * 2], 0, 23170, 0, 23170, 1000);
                }
            }
            else
            {
                e.Add(at, minute.PosX[at] + random.Next(-2000, 2000), minute.PosY[at] + random.Next(-300, 300), minute.PosZ[at] + random.Next(-2000, 2000),
                    (short)random.Next(-32767, 32768), (short)random.Next(-32767, 32768), (short)random.Next(-32767, 32768), (short)random.Next(0, 32768), 1000);
            }
            minute.Effects.Add(e);
        }
        int without = AttemptCodec.Encode(Smooth(60 * 60, 3, 60)).Length;
        int with = AttemptCodec.Encode(minute).Length;
        Check(SameEffects(minute, RoundTrip(minute)) && with - without < 12_000,
            $"a minute of a fight's effects ({minute.Effects.Count} effects, {minute.Effects.Sum(e => e.Count)} keys) costs {(with - without) / 1024.0:0.0} KB");
    }

    // Block 2: which parts of the level were switched on. The paths (sharing most of their beginnings, odd names among
    // them), each part's state at the start and every change after come back exactly, but not without the world; an
    // attempt in progress copies them; an attempt without any writes no block; nonsense is refused.
    private static void Sections()
    {
        Console.WriteLine("sections");
        RawAttempt a = Sample(13, 400, 13);
        var random = new Random(13);
        for (int room = 0; room < 40; room++)
        {
            for (int part = 0; part < 50; part++)
            {
                a.SectionPaths.Add($"Level#0/{room} - Hall#0/Geometry#{part / 25}/Rock ({part})#0");
            }
        }
        a.SectionPaths.Add("Ünïcode ✓ #name#0/a|b#2/x#y#10");
        a.SectionPaths.Add("");
        a.SectionPaths.Add("Level#0");
        for (int i = 0; i < a.SectionPaths.Count; i++)
        {
            a.SectionChanges.Add(new SectionChange { Sample = 0, Section = i, On = random.Next(3) > 0 });
        }
        int sample = 0;
        for (int k = 0; k < 300; k++)
        {
            sample += random.Next(0, 4) == 0 ? 0 : random.Next(1, 30);
            a.SectionChanges.Add(new SectionChange { Sample = sample, Section = random.Next(a.SectionPaths.Count), On = random.Next(2) == 0 });
        }
        a.SectionChanges.Add(new SectionChange { Sample = 2, Section = 0, On = false });  // (out of order, as a damaged recording might be)

        RawAttempt b = RoundTrip(a);
        bool same = Same(a, b) && b.SectionPaths.SequenceEqual(a.SectionPaths) && b.SectionChanges.Count == a.SectionChanges.Count;
        for (int i = 0; same && i < a.SectionChanges.Count; i++)
        {
            same = a.SectionChanges[i].Sample == b.SectionChanges[i].Sample && a.SectionChanges[i].Section == b.SectionChanges[i].Section && a.SectionChanges[i].On == b.SectionChanges[i].On;
        }
        Check(same, $"{a.SectionPaths.Count} parts of the level and {a.SectionChanges.Count} times they were found or switched come back exactly");
        RawAttempt again = RoundTrip(a);
        Check(again.SectionPaths.SequenceEqual(a.SectionPaths) && ReferenceEquals(again.SectionPaths[1234], b.SectionPaths[1234]) && ReferenceEquals(again.SectionPaths[^1], b.SectionPaths[^1]),
            "...and read again, the same paths are the same strings (a thousand attempts at a level don't make a thousand of each)");
        RawAttempt bare = RoundTrip(a, world: false);
        Check(bare.SectionPaths.Count == 0 && bare.SectionChanges.Count == 0 && bare.Count == a.Count, "...but not without the world (it's the world's block)");

        RawAttempt without = a.Clone(a.End);
        without.SectionPaths.Clear();
        without.SectionChanges.Clear();
        int cost = AttemptCodec.Encode(a).Length - AttemptCodec.Encode(without).Length;
        Check(cost > 0 && cost < 6000, $"...in {cost} bytes deflated");
        RawAttempt none = RoundTrip(without);
        Check(none.SectionPaths.Count == 0 && Same(without, none), "an attempt without them writes no block, and reads back without them");

        RawAttempt live = a.Clone(AttemptEnd.InProgress);
        a.SectionPaths.Add("Level#0/later#0");   // (the recorder carries on after the snapshot)
        a.SectionChanges.Add(new SectionChange { Sample = 399, Section = a.SectionPaths.Count - 1, On = true });
        Check(live.SectionPaths.Count == a.SectionPaths.Count - 1 && live.SectionChanges.Count == a.SectionChanges.Count - 1, "an attempt in progress clones its own lists of them");

        Check(Refused(w => { w.UInt(1); w.UInt(0); w.String("a"); w.UInt(1); w.Int(0); w.Int(1 << 1); }), "a change to a part that isn't there is refused");
        Check(Refused(w => { w.UInt(2); w.UInt(0); w.String("ab"); w.UInt(3); w.String("c"); w.UInt(0); }), "a path starting with more of the one before than it has is refused");
        Check(Refused(w => { w.UInt(1); w.UInt(0); w.String("a"); w.UInt(1); w.Int(-5); w.Int(0); }), "a change before the attempt started is refused");
        Check(Refused(w => { w.UInt(1000); w.UInt(0); }), "more parts than the block could hold are refused");
    }

    private static bool Refused(Action<AttemptCodec.VarWriter> write)
    {
        var w = new AttemptCodec.VarWriter(64);
        write(w);
        try
        {
            SectionsBlock.Decode(new AttemptCodec.VarReader(w.Buffer, 0, w.Length), new RawAttempt());
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
        catch (EndOfStreamException)
        {
            return true;
        }
    }

    // Block 1, V1's arms: which arm it had and when it switched, its animators' states (any hash, any time, every
    // flag, both parts), and the whiplash's path (out of order is never written, but far off, standing still, thrown
    // fast, and every phase). Exact, with or without the world; cloned on its own; an attempt without them says so.
    private static void Arms()
    {
        Console.WriteLine("arms");
        var random = new Random(51);
        RawAttempt a = Sample(12, 600, 12);
        a.ArmsRecorded = true;
        a.ArmSwitches.Add(new ArmSwitch { Sample = 0, Arm = ArmKind.Feedbacker });
        a.ArmSwitches.Add(new ArmSwitch { Sample = 120, Arm = ArmKind.Knuckleblaster });
        a.ArmSwitches.Add(new ArmSwitch { Sample = 121, Arm = ArmKind.None });
        a.ArmSwitches.Add(new ArmSwitch { Sample = 599, Arm = (ArmKind)200 });
        int sample = 0;
        for (int i = 0; i < 300; i++)
        {
            sample += random.Next(0, 4);
            a.ArmMoves.Add(new ArmMove
            {
                Sample = sample,
                Part = (ArmPart)(i % 2),
                Flags = (ArmMoveFlags)random.Next(0, 16),
                State = i == 0 ? int.MinValue : i == 1 ? int.MaxValue : i == 2 ? 0 : random.Next(int.MinValue, int.MaxValue),
                Time = (ushort)(i == 3 ? ushort.MaxValue : random.Next(0, 65536)),
            });
        }
        // Thrown fast, caught on something far off and still, pulled in, coming back; again after a gap.
        int x = 1_000_000, y = -20_000_000, z = 5;
        for (int k = 30; k < 200; k++)
        {
            if (k == 90)
            {
                k = 140;
            }
            var phase = k < 60 ? HookPhase.Throwing : k < 70 ? HookPhase.Caught : k < 80 ? HookPhase.Pulling : HookPhase.Returning;
            if (phase == HookPhase.Throwing)
            {
                x += 250;
                z -= 33;
            }
            a.Hook.Add(k, phase, x, y + random.Next(-3, 4), z);
        }
        RawAttempt b = RoundTrip(a);
        RawAttempt bare = RoundTrip(a, world: false);
        Check(Same(a, b) && SameArms(a, b), $"the arms come back exactly ({a.ArmSwitches.Count} switches, {a.ArmMoves.Count} animations, {a.Hook.Count} samples with the whiplash out)");
        Check(SameArms(a, bare), "...without the world too (every ghost may show its arm)");

        RawAttempt clone = a.Clone(AttemptEnd.InProgress);
        a.ArmMoves.Add(new ArmMove { Sample = 700 });
        a.Hook.Add(700, HookPhase.Throwing, 1, 2, 3);
        a.Hook.X[0] = 77;
        Check(clone.ArmsRecorded && clone.ArmMoves.Count == 300 && clone.Hook.Count == b.Hook.Count && clone.Hook.X[0] == b.Hook.X[0], "...and clone on their own");

        RawAttempt none = Sample(13, 50, 13);
        RawAttempt noneBack = RoundTrip(none);
        Check(!noneBack.ArmsRecorded && noneBack.ArmSwitches.Count == 0 && noneBack.ArmMoves.Count == 0 && noneBack.Hook.Count == 0, "an attempt recorded without its arms says so");
        RawAttempt empty = Sample(14, 50, 14);
        empty.ArmsRecorded = true;
        Check(RoundTrip(empty).ArmsRecorded, "...and one with them but no punches says that");
    }

    private static bool SameArms(RawAttempt a, RawAttempt b)
    {
        if (a.ArmsRecorded != b.ArmsRecorded || a.ArmSwitches.Count != b.ArmSwitches.Count || a.ArmMoves.Count != b.ArmMoves.Count || a.Hook.Count != b.Hook.Count)
        {
            return false;
        }
        for (int i = 0; i < a.ArmSwitches.Count; i++)
        {
            if (a.ArmSwitches[i].Sample != b.ArmSwitches[i].Sample || a.ArmSwitches[i].Arm != b.ArmSwitches[i].Arm)
            {
                return false;
            }
        }
        for (int i = 0; i < a.ArmMoves.Count; i++)
        {
            ArmMove p = a.ArmMoves[i], q = b.ArmMoves[i];
            if (p.Sample != q.Sample || p.Part != q.Part || p.Flags != q.Flags || p.State != q.State || p.Time != q.Time)
            {
                return false;
            }
        }
        for (int i = 0; i < a.Hook.Count; i++)
        {
            if (a.Hook.Sample[i] != b.Hook.Sample[i] || a.Hook.Phase[i] != b.Hook.Phase[i] || a.Hook.X[i] != b.Hook.X[i] || a.Hook.Y[i] != b.Hook.Y[i] || a.Hook.Z[i] != b.Hook.Z[i])
            {
                return false;
            }
        }
        return true;
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

    // An attempt recorded by 1.1 (enemies and doors): format 2, written by its own encoder (commit 0941ac7)
    // from OldAttemptV2() below.
    private const string Version2Attempt =
        "Y+JhYGR4sEnvN9NdDhsGhgAHBoYVDixMgr6OQd7Bvo5+CkGuYf4+Ya5BQs7+Qa4Krl6uziEKwR7+Ie6hfhWJDEDNIKQIJB1gDBBygDFAIo5wNWBlSFIO" +
        "CO1gZQgpsGkwKZAyuBTQ0hQoSIaCJChIhIIEKIiHgjgoiIWCGCiIhoIoKIiEgggoCIeCMCh4zq7DwiIqJwolgIBOnA+MfAwwMJNxFjAUBpBzgAcYFy" +
        "MJnGBkZGQZCDRQHu6XARYFAwEu9DCKDYzNDFwMDOoa9McbnjCzDoyHWRkYmVgYGFgYmIAEEBuxMDExaDAC+UxM8j6pZak5ygb6hgq6Cm6ZRcUlCkH5" +
        "+blAAZf8/CJlA1WYvBFQ3iMxJwcqUwyknTLTFcCqDJlY3TJzSjJYg0uKEiuZGdiAljbYMzBosAgAU/YBexEGeRYpkEscRBjFLvATmeQu8FcQkN9CUb65" +
        "AQwTfGADJ1bh+fISxJlPSCHRBlETHADGP1UUEm0Q9UDDF2Z5KqnEpWDuzCNzeFCF0PyJ4BKhFo+SHeLUhBQZR4Lmf8ysxAFCCnHIAwA=";

    private static RawAttempt OldAttemptV2()
    {
        var a = new RawAttempt
        {
            Number = 12,
            Start = AttemptStart.LevelStart,
            End = AttemptEnd.Death,
            StartedUtcTicks = 638_670_000_000_000_000L,
            SampleRate = 60,
            LevelTimeStart = 3.25f,
            LevelTimeEnd = 5.25f,
            Cheats = false,
            MajorAssists = false,
            WeaponsRecorded = true,
            EnemyStride = 2,
        };
        for (int i = 0; i < 120; i++)
        {
            a.Add(
                -500 + i * 21 + (i * i) % 13, 120 + (i % 11) * 7, 800 - i * 17,
                0, (short)(100 - (i % 3)), 0,
                (short)(i * 30 - 1800), (short)(9000 + i * 11), (short)((i % 4) * 5), (short)(31000 - i * 3),
                (byte)((i % 3 == 0 ? 1 : 0) | (i % 9 == 0 ? 32 : 0) | (i % 13 == 0 ? 64 : 0)), (byte)(100 - i / 8));
        }
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Weapon, A = 1, B = 2, C = a.AddWeaponName("MARKSMAN REVOLVER") });
        int d0 = a.AddDoorPath("Level#0/1 - First Room#0/Door#0");
        int d1 = a.AddDoorPath("Level#0/2 - Hall#0/Doors#0/Big Door#1");
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Door, A = d0, B = 1 });
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Door, A = d1, B = 0 });
        a.Events.Add(new AttemptEvent { Sample = 50, Kind = EventKind.Door, A = d1, B = 1 });
        a.Events.Add(new AttemptEvent { Sample = 90, Kind = EventKind.Weapon, A = 2, B = 0, C = a.AddWeaponName("CORE EJECT SHOTGUN") });
        for (int k = 0; k < 3; k++)
        {
            var e = new EnemyTrack { Id = k * 2, Type = 3 + k * 5, Name = k == 1 ? "Stray" : "Filth", Scale = 1f + k * 0.5f, Start = k * 10, Died = k == 2 };
            for (int s = 0; s < 40 - k * 9; s++)
            {
                e.Add(1000 + s * 30 * (k + 1), k * 300, -2000 + s * 12, 0, (short)(s * 160), 0, (short)(32000 - s * 16), unchecked((int)0x9E3779B1) + (s / 15), (ushort)(s * 1500), (byte)(255 - s * 3));
            }
            a.Enemies.Add(e);
        }
        return a;
    }

    // An attempt recorded by 1.2 (everything that moved or happened, no sounds): format 3, written by its own
    // encoder (commit 860bb34) from OldAttemptV3() below.
    private const string Version3Attempt =
        "YxZlZGFQmHFaW/Quhw0Dg5ITA4OWEwujkLN/kKuCq5erc4hCsId/iHuoX4UiAwMDIxgrQGkGBnqIpcBAMgwkwUAiDCTAQDwMxMFALAzEwEA0DETBQCQMRMBA" +
        "eHj4AnllFhZBFiAAE/Rj3mARAnqbgaG2js7UGiY9MGPEgA+MIMBBf2KgPLxd3GhgLG6ohaRp+gMOBnGJAUAz3jAzD4yHmRmARToDAwi7sTAwMTAxSvmklqXm" +
        "KBvomyjoKjgWpeYlAtku+flFygaM7O6JSUWZqTmMbBZADQ32HIxGL9gDSLWzg49kZx5nlCNZD+lgAh0ioeELszqpelZ9uPyFH4s4E36xFSK0hv+YOUkFLGxB" +
        "+cnZqSWcEMo3sYTFPSe/XA5bqgvISSxJyy/KVTZkZPjf2PgHHAZnz5yxBWK7WTNnwrA9I8MaNiZGJK832KGFuj2QcIBhRmYmJnCxChKG4ApGESYWUAuiwQEq" +
        "5FDGwGQDyhfoYAvjCcY12IKeYQEbNtEX7MlYxSkH//5jS7Ag0QvYkgtQkLmKcYfBAjaR7Qbz2YR/hH5h/n+w8R9ELVCbGtN0wZQ/TC/YGTZYf2C8wfIfCOCS" +
        "csz9zKuZfnD1y1xnaefbIc4AlgUG4wMhhh//mAE=";

    private static RawAttempt OldAttemptV3()
    {
        var a = new RawAttempt
        {
            Number = 21,
            Start = AttemptStart.Checkpoint,
            End = AttemptEnd.Finish,
            StartedUtcTicks = 638_690_000_000_000_000L,
            SampleRate = 60,
            LevelTimeStart = 40.5f,
            LevelTimeEnd = 42.5f,
            Cheats = false,
            MajorAssists = false,
            WeaponsRecorded = true,
            EnemyStride = 2,
        };
        for (int i = 0; i < 120; i++)
        {
            a.Add(
                2000 - i * 19 + (i * i) % 11, 300 + (i % 7) * 9, 150 + i * 23,
                0, (short)(120 - (i % 5)), 0,
                (short)(i * 25 - 1500), (short)(8000 + i * 9), (short)((i % 3) * 4), (short)(31500 - i * 2),
                (byte)((i % 4 == 0 ? 1 : 0) | (i % 10 == 0 ? 32 : 0)), (byte)(100 - i / 9));
        }
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Weapon, A = 2, B = 0, C = a.AddWeaponName("CORE EJECT SHOTGUN") });
        int d0 = a.AddDoorPath("Level#0/4 - Arena#0/Door#0");
        a.Events.Add(new AttemptEvent { Sample = 0, Kind = EventKind.Door, A = d0, B = 0 });
        a.Events.Add(new AttemptEvent { Sample = 70, Kind = EventKind.Door, A = d0, B = 1 });
        var e = new EnemyTrack { Id = 3, Type = 28, Name = "Gabriel", Scale = 1f, Start = 4, Died = true };
        for (int s = 0; s < 50; s++)
        {
            e.Add(500 + s * 40, 900, -100 + s * 15, 0, (short)(s * 200), 0, (short)(32000 - s * 20), unchecked((int)0x7F4A7C15) + (s / 20), (ushort)(s * 1300), (byte)(255 - s * 5));
        }
        a.Enemies.Add(e);
        a.Looks.Add(new ThingLook
        {
            Name = "Rocket",
            Color = 0xFF8040FFu,
            CenterX = 0f, CenterY = 0.1f, CenterZ = 0.4f,
            SizeX = 0.3f, SizeY = 0.3f, SizeZ = 1.2f,
            Parts = { new LookPart { Mesh = "Rocket", Vertices = 812, Materials = new[] { "RocketMat", "Glow" }, PZ = 0.25f, SX = 2f, SY = 2f, SZ = 2f } },
        });
        int platform = a.AddThingPath("Level#0/4 - Arena#0/Platform#1");
        var rocket = new ThingTrack { Id = 1, Kind = ThingKind.Projectile, Look = 0, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f, End = 60, Gone = true };
        for (int k = 0; k < 20; k++)
        {
            rocket.Add(30 + k * 2, 100 + k * 150, 400, -50 + k * 3, 0, 0, 0, 32767, 1000);
        }
        a.Things.Add(rocket);
        var mover = new ThingTrack { Id = 2, Kind = ThingKind.Mover, Path = platform, ScaleX = 4f, ScaleY = 1f, ScaleZ = 4f, End = 119 };
        mover.Add(0, 0, 0, 0, 0, 0, 0, 32767, 1000);
        mover.Add(90, 0, 500, 0, 0, 0, 0, 32767, 1000);
        a.Things.Add(mover);
        a.WorldEvents.Add(new WorldEvent { Sample = 61, Kind = WorldEventKind.Explosion, X = 3100, Y = 400, Z = 10, Size = 5500, Life = 250, Color = 0xFFC060FFu, Look = -1, Path = -1, RotW = 32767 });
        a.WorldEvents.Add(new WorldEvent { Sample = 80, Kind = WorldEventKind.Beam, X = 2000, Y = 450, Z = 200, ToX = 2500, ToY = 450, ToZ = 4000, Size = 120, Life = 300, Color = 0xFFFFFFFFu, Look = -1, Path = -1, RotW = 32767 });
        a.WorldEvents.Add(new WorldEvent { Sample = 95, Kind = WorldEventKind.Break, X = 1800, Y = 300, Z = 900, Size = 1500, Color = 0xFFFFFFFFu, Look = 0, Path = platform, RotY = 1200, RotW = 32700 });
        return a;
    }

    private static void OldAttempts()
    {
        Console.WriteLine("1.0, 1.1 and 1.2 attempts");
        byte[] bytes = Convert.FromBase64String(Version1Attempt);
        Check(Inflate(bytes)[0] == 1, "the embedded attempt is format 1");
        RawAttempt a = AttemptCodec.Decode(bytes, 0, bytes.Length);
        Check(Same(OldAttempt(), a), "an attempt recorded by 1.0 reads back exactly");
        Check(a.Enemies.Count == 0 && a.DoorPaths.Count == 0 && a.Things.Count == 0, "...with no enemies, doors or things");
        byte[] again = AttemptCodec.Encode(a);
        Check(Inflate(again)[0] == AttemptCodec.Version && Same(a, AttemptCodec.Decode(again, 0, again.Length)), $"saved again it's format {AttemptCodec.Version}, and the same");

        bytes = Convert.FromBase64String(Version2Attempt);
        Check(Inflate(bytes)[0] == 2, "the embedded 1.1 attempt is format 2");
        RawAttempt b = AttemptCodec.Decode(bytes, 0, bytes.Length);
        Check(Same(OldAttemptV2(), b) && b.Enemies.Count == 3 && b.DoorPaths.Count == 2, "an attempt recorded by 1.1 reads back exactly, enemies and doors included");
        Check(b.Things.Count == 0 && b.Looks.Count == 0 && b.WorldEvents.Count == 0, "...with no things");
        RawAttempt light = AttemptCodec.Decode(bytes, 0, bytes.Length, world: false);
        Check(light.Count == 120 && light.Enemies.Count == 0 && light.Events.Count == b.Events.Count, "...and without its world when asked");
        again = AttemptCodec.Encode(b);
        Check(Inflate(again)[0] == AttemptCodec.Version && Same(b, AttemptCodec.Decode(again, 0, again.Length)), $"saved again it's format {AttemptCodec.Version}, and the same");

        bytes = Convert.FromBase64String(Version3Attempt);
        Check(Inflate(bytes)[0] == 3, "the embedded 1.2 attempt is format 3");
        RawAttempt c = AttemptCodec.Decode(bytes, 0, bytes.Length);
        Check(Same(OldAttemptV3(), c) && c.Enemies.Count == 1 && c.Things.Count == 2 && c.Looks.Count == 1 && c.WorldEvents.Count == 3,
            "an attempt recorded by 1.2 reads back exactly, its things, looks, explosions, beams and breaks included");
        Check(!c.SoundsRecorded && c.Sounds.Count == 0 && c.SoundClips.Count == 0 && c.SoundMoves.Count == 0, "...with no sounds (and known not to have been recording them)");
        again = AttemptCodec.Encode(c);
        Check(Inflate(again)[0] == AttemptCodec.Version && Same(c, AttemptCodec.Decode(again, 0, again.Length)), $"saved again it's format {AttemptCodec.Version}, and the same");
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
        // Version 3 the same, with no enemies: then far too many names, looks, things, a thing's keys, events.
        var head3 = new List<byte>(head);
        head3[0] = 3;
        head3.AddRange(new byte[] { 0, 0 });
        var tooMany = new List<List<byte>> { hugeCount, hugeTrack };
        foreach (byte[] tail in new[]
        {
            new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            new byte[] { 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            new byte[] { 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            new byte[] { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
        })
        {
            var damaged = new List<byte>(head3);
            damaged.AddRange(tail);
            damaged.AddRange(new byte[64]);
            tooMany.Add(damaged);
        }
        // Version 4: the sounds come right after the events (no samples, no events): far too many clips, sounds, moves.
        var head4 = head.Take(head.Count - 2).ToList();
        head4[0] = 4;
        foreach (byte[] tail in new[]
        {
            new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            new byte[] { 1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            new byte[] { 1, 1, 1, (byte)'x', 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            new byte[] { 1, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
        })
        {
            var damaged = new List<byte>(head4);
            damaged.AddRange(tail);
            damaged.AddRange(new byte[64]);
            tooMany.Add(damaged);
        }
        bool both = true;
        var clock = Stopwatch.StartNew();
        foreach (List<byte> damaged in tooMany)
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
        Check(both && clock.ElapsedMilliseconds < 1000, $"a count too big for the data is refused before anything that size is made ({tooMany.Count} kinds, versions 3's and 4's included)");

        // A sound of a clip that isn't there, and a sound's move for a sound that isn't there.
        RawAttempt badClip = Sample(5, 60, 3, world: false);
        badClip.SoundClips.Add(new SoundClip { Name = "Shot", LengthMs = 500 });
        badClip.Sounds.Add(new SoundEvent { Sample = 4, Clip = 1 });
        RawAttempt badMove = Sample(5, 60, 3, world: false);
        badMove.SoundClips.Add(new SoundClip { Name = "Shot", LengthMs = 500 });
        badMove.Sounds.Add(new SoundEvent { Sample = 4, Clip = 0 });
        badMove.SoundMoves.Add(new SoundMove { Sound = 1, Sample = 6 });
        int refusedSounds = 0;
        foreach (RawAttempt wrong in new[] { badClip, badMove })
        {
            try
            {
                byte[] bytes = AttemptCodec.Encode(wrong);
                AttemptCodec.Decode(bytes, 0, bytes.Length, world: false);
            }
            catch (InvalidDataException)
            {
                refusedSounds++;
            }
        }
        Check(refusedSounds == 2, "a sound with a clip that isn't there, or a move of a sound that isn't there, is refused");

        // A thing pointing at a look or a path that isn't there.
        RawAttempt bad = Sample(5, 60, 3, world: false);
        bad.Looks.Add(new ThingLook());
        bad.Things.Add(new ThingTrack { Look = 1 });
        bool badLook = false;
        try
        {
            byte[] bytes = AttemptCodec.Encode(bad);
            AttemptCodec.Decode(bytes, 0, bytes.Length);
        }
        catch (InvalidDataException)
        {
            badLook = true;
        }
        Check(badLook, "a thing with a look that isn't there is refused");
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

    // Everything else, the way the recorder sees it: projectiles flying (a key every world step), shotgun
    // pellets in bursts, rockets, nails that stick in walls (no keys once they stop), gibs falling and resting,
    // a platform going up and down now and then (a level object, by path), explosions, beams and a crate breaking.
    private static void Battle(RawAttempt a, int seed)
    {
        var random = new Random(seed);
        int stride = Math.Max(1, a.EnemyStride);
        double dt = (double)stride / a.SampleRate;
        int ticks = (a.Count - 1) / stride;
        string[] kinds = { "Projectile", "Pellet", "Rocket", "Nail", "Gib", "Coin" };
        foreach (string kind in kinds)
        {
            a.Looks.Add(new ThingLook
            {
                Name = kind,
                Color = 0xFF8020FFu,
                SizeX = 0.4f, SizeY = 0.4f, SizeZ = 1.2f,
                Parts = { new LookPart { Mesh = kind + " mesh", Vertices = 24 * kind.Length, Materials = new[] { kind + " material", "Glow" }, RW = 1f, SX = 1f, SY = 1f, SZ = 1f } },
            });
        }
        // (explosions as they're recorded now: their own mesh and material, and how they grow)
        int explosionLook = a.Looks.Count;
        a.Looks.Add(new ThingLook { Name = "Explosion", Color = 0xFFA040FFu, SizeX = 1f, SizeY = 1f, SizeZ = 1f, Parts = { new LookPart { Mesh = "Sphere", Vertices = 515, Materials = new[] { "Explosion" }, RW = 1f, SX = 1f, SY = 1f, SZ = 1f } } });
        a.Looks.Add(new ThingLook { Name = "Platform", Parts = { new LookPart { Mesh = "Cube", Vertices = 24, Materials = new[] { "Metal" }, SX = 8f, SY = 0.5f, SZ = 8f, RW = 1f } } });
        int platformPath = a.AddThingPath("Level#0/3 - Shaft#0/Elevator#0");
        int id = 0;
        for (int tick = 0; tick < ticks; tick++)
        {
            int spawns = tick % 6 == 0 ? (random.Next(4) == 0 ? 8 : 1) : 0;  // (a shot every few ticks, now and then a shotgun burst)
            for (int s = 0; s < spawns; s++)
            {
                int look = spawns == 8 ? 1 : random.Next(kinds.Length);
                var t = new ThingTrack { Id = id++, Kind = look == 4 ? ThingKind.Debris : ThingKind.Projectile, Look = look, Gone = true };
                double x = random.Next(-30, 30), y = 2, z = random.Next(-30, 30);
                double vx = (random.NextDouble() - 0.5) * 80, vy = random.NextDouble() * 10, vz = (random.NextDouble() - 0.5) * 80;
                bool falls = look == 4 || look == 5;
                int life = random.Next(8, 90), rest = look == 3 || look == 4 ? random.Next(30, 300) : 0;
                double yaw = Math.Atan2(vx, vz);
                for (int k = 0; k < life && tick + k <= ticks; k++)
                {
                    if (falls)
                    {
                        vy -= 30 * dt;
                    }
                    x += vx * dt;
                    y = Math.Max(0, y + vy * dt);
                    z += vz * dt;
                    double qy = Math.Sin(yaw * 0.5), qw = Math.Cos(yaw * 0.5);
                    t.Add((tick + k) * stride, (int)Math.Round(x * 100), (int)Math.Round(y * 100), (int)Math.Round(z * 100),
                        0, Coarse(qy, 16), 0, Coarse(Math.Abs(qw), 16), 1000);
                }
                t.End = Math.Min(a.Count - 1, (tick + life + rest) * stride);
                a.Things.Add(t);
            }
            if (tick % 40 == 0)
            {
                var explosion = new WorldEvent
                {
                    Sample = tick * stride, Kind = WorldEventKind.Explosion, X = random.Next(-3000, 3000), Y = 200, Z = random.Next(-3000, 3000),
                    Size = 5500, Life = 250, Color = 0xFFA040FFu, Look = explosionLook, Path = -1, RotW = 32767,
                };
                ExplosionGrowth.Put(ref explosion, 1f, 2.5f * (1 + random.Next(3)));
                a.WorldEvents.Add(explosion);
            }
            if (tick % 9 == 0)
            {
                int x = random.Next(-3000, 3000), z = random.Next(-3000, 3000);
                a.WorldEvents.Add(new WorldEvent
                {
                    Sample = tick * stride, Kind = WorldEventKind.Beam, X = x, Y = 250, Z = z, ToX = x + random.Next(-6000, 6000), ToY = 100, ToZ = z + random.Next(-6000, 6000),
                    Size = 500, Life = 330, Color = 0xFFFFC0FFu, Look = -1, Path = -1,
                });
            }
        }
        a.WorldEvents.Add(new WorldEvent { Sample = a.Count / 2, Kind = WorldEventKind.Break, X = 500, Y = 50, Z = -700, Size = 1500, Color = 0x806040FFu, Look = 0, Path = platformPath, RotW = 32767 });

        // The platform: up for 3 s, wait 5 s, down for 3 s, wait 5 s...
        var platform = new ThingTrack { Id = id, Kind = ThingKind.Mover, Look = a.Looks.Count - 1, Path = platformPath, End = a.Count - 1 };
        for (int tick = 0; tick < ticks; tick++)
        {
            double cycle = tick * dt % 16;
            if (cycle < 3 || (cycle >= 8 && cycle < 11) || tick == 0)
            {
                double height = cycle < 8 ? cycle / 3 * 12 : 12 - (cycle - 8) / 3 * 12;
                platform.Add(tick * stride, 4000, (int)Math.Round(Math.Clamp(height, 0, 12) * 100), -1500, 0, 0, 0, 32767, 1000);
            }
        }
        a.Things.Add(platform);
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
        int crowded = AttemptCodec.Encode(fast).Length;  // (the things' empty tables cost a few bytes)
        int world = crowded - packed60;
        Console.WriteLine($"        ...with 10 enemies about and 30 doors: {crowded / 1024.0:0.0} KB; the world is {world / 1024.0:0.0} KB of it "
            + $"({enemySamples} enemy samples at {fast.SampleRate / fast.EnemyStride} Hz, {world / 60.0:0} bytes/s, {enemySamples * plainEnemySample / 1024.0:0.0} KB as plain structs)");
        Check(world * 4 < enemySamples * plainEnemySample, "the enemies are at least 4x smaller than plain samples");
        Check(world < packed60 * 2, "ten enemies cost less than twice V1 itself");

        int crowdedBytes = crowded;
        Battle(fast, 77);
        int keys = fast.Things.Sum(t => t.Count);
        int battle = AttemptCodec.Encode(fast).Length;
        Console.WriteLine($"        ...and a battle (WorldDetail = Everything): {battle / 1024.0:0.0} KB; {fast.Things.Count} things ({keys} keys), {fast.WorldEvents.Count} explosions and beams "
            + $"add {(battle - crowdedBytes) / 1024.0:0.0} KB ({(battle - crowdedBytes) / 60.0:0} bytes/s, {keys * 26 / 1024.0:0.0} KB as plain structs)");
        Check(battle - crowdedBytes < keys * 26 / 4, "the things are at least 4x smaller than plain keys");
        Check(Same(fast, RoundTrip(fast)), "...and come back exactly");

        Noises(fast, 77);
        int heard = AttemptCodec.Encode(fast).Length;
        const int plainSound = 4 + 4 + 1 + 4 + 4 + 1 + 1 + 4 + 4 + 12 + 4;  // every field of a sound as a plain struct
        Console.WriteLine($"        ...and its sounds (RecordSounds): {fast.Sounds.Count} sounds ({fast.Sounds.Count / 60.0:0} a second) and {fast.SoundMoves.Count} moves "
            + $"add {(heard - battle) / 1024.0:0.0} KB ({(heard - battle) / (double)fast.Sounds.Count:0.0} bytes a sound, {fast.Sounds.Count * plainSound / 1024.0:0.0} KB as plain structs)");
        Check(heard - battle < fast.Sounds.Count * plainSound / 5, "the sounds are at least 5x smaller than plain structs");
        Check(Same(fast, RoundTrip(fast)), "...and come back exactly");

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
        bool reread = data.Attempts.All(a => a.SourcePath == first && Same(a, SessionFile.ReadAttemptAt(a.SourcePath, a.SourceOffset, a.SourceLength)));
        RawAttempt light = SessionFile.ReadAttemptAt(first, data.Attempts[2].SourceOffset, data.Attempts[2].SourceLength, world: false);
        Check(reread && light.Things.Count == 0 && light.Count == data.Attempts[2].Count, "each attempt reads again from where it is in the file (a replay's worlds, later)");
        Check(SessionFile.ReadAttemptAt(first, data.Attempts[2].SourceOffset + 1, data.Attempts[2].SourceLength) == null
            && SessionFile.ReadAttemptAt(first, data.Attempts[2].SourceOffset, data.Attempts[2].SourceLength + 1) == null, "...and not from anywhere else");
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
