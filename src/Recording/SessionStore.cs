using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace UltraAttempts.Recording
{
    // A level as the recordings know it: which folder its runs go in, and V1's size there.
    public sealed class LevelInfo
    {
        public string Key = "";
        public string Folder = "";
        public string Scene = "";
        public string Name = "";
        public int Difficulty;
        public float CapsuleHeight = 3.5f;
        public float CapsuleRadius = 0.5f;
        public float CapsuleCenterY;
    }

    // A level in the recordings folder, for the menu.
    public sealed class LevelSummary
    {
        public string Folder = "";
        public string Level = "";        // its folder's name ("Level 4-2")
        public string Difficulty = "";
        public string Name = "";         // the level's own name, from its runs
        public string Scene = "";        // its scene, from its runs (to load it)
        public int Attempts;             // the count ("ATTEMPT 37")
        public int Recorded;             // attempts in its runs on disk
        public int Runs;
        public int Wins;
        public DateTime Last;
    }

    public sealed class ImportResult
    {
        public int Zips;
        public int Runs;
        public int Skipped;
        public int Failed;
    }

    // The run (session) a level's attempts are going into right now.
    internal sealed class SessionHandle
    {
        public string Path;
        public LevelInfo Level;
        public int Attempts;
        public bool Complete;
        public bool FileExists;
    }

    // The recordings folder: one folder per level and difficulty, one file per run. Files are written
    // on a background task (one at a time, in order), so an attempt ending never hitches the game.
    public sealed class SessionStore
    {
        private readonly string root;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private readonly Dictionary<string, SessionHandle> handles = new Dictionary<string, SessionHandle>();
        private readonly Dictionary<string, int> totals = new Dictionary<string, int>();   // attempt counts, by level folder
        private readonly HashSet<string> fresh = new HashSet<string>();                    // folders reset this launch: no continuing old runs
        private Task writer = Task.CompletedTask;

        public bool ContinueAcrossVisits = true;
        public int KeepRunsPerLevel = 30;

        public SessionStore(string root, Action<string> log)
        {
            this.root = root;
            this.log = log;
        }

        public string Root => root;

        public static string LevelFolder(string root, string levelId, string difficultyName)
        {
            return Path.Combine(root, Sanitize(levelId), Sanitize(difficultyName));
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Unknown";
            }
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).TrimEnd('.', ' ');
        }

        // An attempt is starting: its number at the level. Attempts count up for good, across wins (like
        // Geometry Dash) until the count is reset; the run it goes into (since the last win) continues the
        // newest unfinished run on disk, or starts a new one.
        public int BeginAttempt(LevelInfo level)
        {
            lock (gate)
            {
                Handle(level);
                return Total(level.Folder) + 1;
            }
        }

        // An attempt has ended: write it into the run. finishesRun: the level was beaten, so the run
        // is complete and the next attempt starts a new one (its number still counts on).
        public void SubmitAttempt(LevelInfo level, RawAttempt attempt, bool finishesRun)
        {
            SessionHandle handle;
            lock (gate)
            {
                handle = Handle(level);
                handle.Attempts++;
                attempt.Number = Total(level.Folder) + 1;
                totals[level.Folder] = attempt.Number;
                if (finishesRun)
                {
                    handle.Complete = true;
                }
            }
            Enqueue(() =>
            {
                byte[] payload = AttemptCodec.Encode(attempt);
                byte[] complete = finishesRun ? SessionFile.CompletePayload(attempt.Number, attempt.LevelTimeEnd, DateTime.UtcNow.Ticks) : null;
                lock (gate)
                {
                    Append(handle, SessionFile.BlockAttempt, payload);
                    if (complete != null)
                    {
                        Append(handle, SessionFile.BlockComplete, complete);
                    }
                    WriteCount(level.Folder, totals[level.Folder]);
                }
                if (finishesRun)
                {
                    Prune(level.Folder, handle.Path);
                }
            });
        }

        // How many attempts the level (its folder) has counted so far.
        public int AttemptCount(string folder)
        {
            lock (gate)
            {
                return Total(folder);
            }
        }

        // Back to attempt 1 at a level: the count goes to 0, and the run in progress ends (without a win)
        // so the next attempt starts a new one. Its recordings stay.
        public void ResetCount(string folder)
        {
            var closing = new List<SessionHandle>();
            lock (gate)
            {
                totals[folder] = 0;
                fresh.Add(folder);
                foreach (string key in handles.Keys.ToList())
                {
                    SessionHandle h = handles[key];
                    if (h.Level.Folder != folder)
                    {
                        continue;
                    }
                    if (!h.Complete && h.FileExists)
                    {
                        closing.Add(h);
                    }
                    handles[key] = new SessionHandle { Level = h.Level, Path = NewRunPath(folder) };
                }
            }
            Enqueue(() =>
            {
                byte[] closed = SessionFile.ClosedPayload(DateTime.UtcNow.Ticks);
                lock (gate)
                {
                    if (closing.Count == 0)
                    {
                        // Not played this time: close the newest run on disk if it's unfinished.
                        List<string> files = RunFiles(folder);
                        if (files.Count > 0)
                        {
                            SessionInfo info = SessionFile.Scan(files[files.Count - 1]);
                            if (!info.Complete && !info.Closed)
                            {
                                closing.Add(new SessionHandle { Path = files[files.Count - 1], FileExists = true });
                            }
                        }
                    }
                    foreach (SessionHandle h in closing)
                    {
                        Append(h, SessionFile.BlockClosed, closed);
                    }
                    WriteCount(folder, totals[folder]);
                }
            });
        }

        public const string CountFile = "attempts.count";

        // The attempt count, from the level's count file; a level from 1.0 (no file yet) starts from
        // every attempt its runs have.
        private int Total(string folder)
        {
            if (totals.TryGetValue(folder, out int total))
            {
                return total;
            }
            total = 0;
            string path = Path.Combine(folder, CountFile);
            try
            {
                if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int saved))
                {
                    totals[folder] = Math.Max(0, saved);
                    return totals[folder];
                }
                foreach (string file in RunFiles(folder))
                {
                    try
                    {
                        total += SessionFile.Scan(file).Attempts;
                    }
                    catch
                    {
                        // (unreadable: doesn't count)
                    }
                }
            }
            catch (Exception e)
            {
                log?.Invoke("couldn't read the attempt count: " + e.Message);
            }
            totals[folder] = total;
            return total;
        }

        private void WriteCount(string folder, int count)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, CountFile), count.ToString());
        }

        // A new visit to the level when runs don't carry over between visits: forget its run.
        public void ForgetRun(string levelKey)
        {
            lock (gate)
            {
                handles.Remove(levelKey);
            }
        }

        // The file the level's current run is in, and how many attempts it has (0 if it's new).
        public string CurrentRunPath(string levelKey, out int attempts)
        {
            lock (gate)
            {
                attempts = 0;
                if (!handles.TryGetValue(levelKey, out SessionHandle handle))
                {
                    return null;
                }
                attempts = handle.Attempts;
                return handle.Path;
            }
        }

        // Waits for every pending write (call off the main thread, or before quitting).
        public void Flush(int timeoutMs = 10000)
        {
            Task pending;
            lock (gate)
            {
                pending = writer;
            }
            try
            {
                pending.Wait(timeoutMs);
            }
            catch (Exception e)
            {
                log?.Invoke("writing a recording failed: " + e.GetBaseException().Message);
            }
        }

        // The level's run files, oldest first.
        public static List<string> RunFiles(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return new List<string>();
            }
            var files = Directory.GetFiles(folder, "*" + SessionFile.Extension).ToList();
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        private SessionHandle Handle(LevelInfo level)
        {
            if (handles.TryGetValue(level.Key, out SessionHandle handle) && !handle.Complete)
            {
                return handle;
            }
            handle = null;
            if (ContinueAcrossVisits && !handles.ContainsKey(level.Key) && !fresh.Contains(level.Folder))
            {
                handle = Unfinished(level);
            }
            if (handle == null)
            {
                handle = new SessionHandle { Level = level, Path = NewRunPath(level.Folder) };
            }
            handles[level.Key] = handle;
            return handle;
        }

        // The newest run on disk, if it was never finished: you left mid-level and came back.
        private SessionHandle Unfinished(LevelInfo level)
        {
            List<string> files = RunFiles(level.Folder);
            for (int i = files.Count - 1; i >= 0; i--)
            {
                try
                {
                    SessionInfo info = SessionFile.Scan(files[i]);
                    if (info.Complete || info.Closed)
                    {
                        return null;
                    }
                    var fileLength = new FileInfo(files[i]).Length;
                    if (fileLength > info.ValidLength)
                    {
                        // Half a block from a crash: cut it off before appending after it.
                        using (var stream = new FileStream(files[i], FileMode.Open, FileAccess.Write, FileShare.Read))
                        {
                            stream.SetLength(info.ValidLength);
                        }
                    }
                    return new SessionHandle { Level = level, Path = files[i], Attempts = info.Attempts, FileExists = true };
                }
                catch (Exception e)
                {
                    log?.Invoke($"ignoring unreadable run {Path.GetFileName(files[i])}: {e.Message}");
                }
            }
            return null;
        }

        private static string NewRunPath(string folder)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            string path = Path.Combine(folder, stamp + SessionFile.Extension);
            for (int i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(folder, $"{stamp} ({i}){SessionFile.Extension}");
            }
            return path;
        }

        private void Append(SessionHandle handle, byte type, byte[] payload)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(handle.Path));
            using (var stream = new FileStream(handle.Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                if (!handle.FileExists || stream.Length == 0)
                {
                    LevelInfo level = handle.Level;
                    SessionFile.WriteHeader(stream, new SessionHeader
                    {
                        CreatedUtcTicks = DateTime.UtcNow.Ticks,
                        Scene = level.Scene,
                        LevelName = level.Name,
                        Difficulty = level.Difficulty,
                        CapsuleHeight = level.CapsuleHeight,
                        CapsuleRadius = level.CapsuleRadius,
                        CapsuleCenterY = level.CapsuleCenterY,
                    });
                    handle.FileExists = true;
                }
                SessionFile.WriteBlock(stream, type, payload);
            }
        }

        // Keeps the newest KeepRunsPerLevel finished runs of a level (0: keep everything).
        private void Prune(string folder, string keep)
        {
            if (KeepRunsPerLevel <= 0)
            {
                return;
            }
            try
            {
                var finished = new List<string>();
                foreach (string file in RunFiles(folder))
                {
                    try
                    {
                        if (SessionFile.Scan(file).Complete)
                        {
                            finished.Add(file);
                        }
                    }
                    catch
                    {
                        // (not ours to delete)
                    }
                }
                for (int i = 0; i < finished.Count - KeepRunsPerLevel; i++)
                {
                    if (finished[i] != keep)
                    {
                        File.Delete(finished[i]);
                    }
                }
            }
            catch (Exception e)
            {
                log?.Invoke("couldn't tidy old runs: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- every level, export, import

        public const string ExportsFolder = "Exports";
        public const string ImportFolder = "Import";

        // Every level with recordings: its attempt count, runs and wins (off the main thread: it reads
        // every run's block list).
        public List<LevelSummary> Levels()
        {
            var levels = new List<LevelSummary>();
            if (!Directory.Exists(root))
            {
                return levels;
            }
            foreach (string levelDir in Directory.GetDirectories(root))
            {
                string levelName = Path.GetFileName(levelDir);
                if (levelName == ExportsFolder || levelName == ImportFolder)
                {
                    continue;
                }
                foreach (string folder in Directory.GetDirectories(levelDir))
                {
                    var summary = new LevelSummary { Folder = folder, Level = levelName, Difficulty = Path.GetFileName(folder), Name = levelName };
                    foreach (string file in RunFiles(folder))
                    {
                        try
                        {
                            SessionInfo info = SessionFile.Scan(file);
                            summary.Runs++;
                            summary.Recorded += info.Attempts;
                            if (info.Complete)
                            {
                                summary.Wins++;
                            }
                            if (!string.IsNullOrEmpty(info.Header.LevelName))
                            {
                                summary.Name = info.Header.LevelName;
                            }
                            if (!string.IsNullOrEmpty(info.Header.Scene))
                            {
                                summary.Scene = info.Header.Scene;
                            }
                            summary.Last = File.GetLastWriteTime(file);
                        }
                        catch
                        {
                            // (unreadable: left out)
                        }
                    }
                    if (summary.Runs == 0 && !File.Exists(Path.Combine(folder, CountFile)))
                    {
                        continue;
                    }
                    summary.Attempts = AttemptCount(folder);
                    levels.Add(summary);
                }
            }
            levels.Sort((a, b) => b.Last.CompareTo(a.Last));
            return levels;
        }

        // One level's runs (folder), or every level's (null), into a zip in Exports. Returns its path.
        public string Export(string folder)
        {
            Flush();
            string exports = Path.Combine(root, ExportsFolder);
            Directory.CreateDirectory(exports);
            string name = folder == null ? "Every level" : Path.GetFileName(Path.GetDirectoryName(folder)) + " " + Path.GetFileName(folder);
            string path = Path.Combine(exports, Sanitize(name + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")) + ".zip");
            var files = new List<string>();
            if (folder != null)
            {
                files.AddRange(RunFiles(folder));
            }
            else
            {
                foreach (LevelSummary level in Levels())
                {
                    files.AddRange(RunFiles(level.Folder));
                }
            }
            using (var zip = new ZipArchive(new FileStream(path, FileMode.CreateNew, FileAccess.Write), ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    // (runs are deflated already: stored as they are)
                    ZipArchiveEntry entry = zip.CreateEntry(Relative(file), CompressionLevel.NoCompression);
                    using (Stream to = entry.Open())
                    using (var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        from.CopyTo(to);
                    }
                }
            }
            log?.Invoke($"exported {files.Count} runs to {path}");
            return path;
        }

        // Every zip in Import: its runs join the levels' folders (a run already there is skipped; a
        // different one with the same name is kept beside it). Done zips move to Import\Done.
        public ImportResult Import()
        {
            Flush();
            var result = new ImportResult();
            string import = Path.Combine(root, ImportFolder);
            Directory.CreateDirectory(import);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string zipPath in Directory.GetFiles(import, "*.zip"))
            {
                try
                {
                    using (ZipArchive zip = ZipFile(zipPath))
                    {
                        foreach (ZipArchiveEntry entry in zip.Entries)
                        {
                            if (!entry.FullName.EndsWith(SessionFile.Extension, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            // Only <level>/<difficulty>/<run> inside the recordings folder (never outside it,
                            // never into Import or Exports).
                            string[] steps = entry.FullName.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                            if (steps.Length != 3 || steps[0] == ImportFolder || steps[0] == ExportsFolder || !target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Skipped++;
                                continue;
                            }
                            byte[] bytes;
                            using (Stream from = entry.Open())
                            using (var ms = new MemoryStream())
                            {
                                from.CopyTo(ms);
                                bytes = ms.ToArray();
                            }
                            if (File.Exists(target))
                            {
                                if (File.ReadAllBytes(target).SequenceEqual(bytes))
                                {
                                    result.Skipped++;
                                    continue;
                                }
                                string stem = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target));
                                target = stem + " (imported)" + SessionFile.Extension;
                                for (int i = 2; File.Exists(target); i++)
                                {
                                    target = stem + $" (imported {i})" + SessionFile.Extension;
                                }
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.WriteAllBytes(target, bytes);
                            result.Runs++;
                            lock (gate)
                            {
                                string folder = Path.GetDirectoryName(target);
                                if (!File.Exists(Path.Combine(folder, CountFile)))
                                {
                                    totals.Remove(folder);  // (counted from its runs: count again)
                                }
                            }
                        }
                    }
                    string done = Path.Combine(import, "Done");
                    Directory.CreateDirectory(done);
                    string moved = Path.Combine(done, Path.GetFileName(zipPath));
                    for (int i = 2; File.Exists(moved); i++)
                    {
                        moved = Path.Combine(done, Path.GetFileNameWithoutExtension(zipPath) + $" ({i}).zip");
                    }
                    File.Move(zipPath, moved);
                    result.Zips++;
                }
                catch (Exception e)
                {
                    result.Failed++;
                    log?.Invoke($"couldn't import {Path.GetFileName(zipPath)}: {e.Message}");
                }
            }
            return result;
        }

        private static ZipArchive ZipFile(string path) => new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read);

        private string Relative(string file)
        {
            string full = Path.GetFullPath(file);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return (full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full.Substring(fullRoot.Length) : Path.GetFileName(file)).Replace(Path.DirectorySeparatorChar, '/');
        }

        private void Enqueue(Action job)
        {
            lock (gate)
            {
                writer = writer.ContinueWith(_ =>
                {
                    try
                    {
                        job();
                    }
                    catch (Exception e)
                    {
                        log?.Invoke("writing a recording failed: " + e);
                    }
                }, TaskScheduler.Default);
            }
        }
    }
}
