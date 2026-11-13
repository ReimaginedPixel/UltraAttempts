using System;
using System.Collections.Generic;
using System.IO;
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

        // An attempt is starting: its number in the level's current run (continuing the newest
        // unfinished run on disk, or starting a new one).
        public int BeginAttempt(LevelInfo level)
        {
            lock (gate)
            {
                return Handle(level).Attempts + 1;
            }
        }

        // An attempt has ended: write it into the run. finishesRun: the level was beaten, so the run
        // is complete and the next attempt starts a new one.
        public void SubmitAttempt(LevelInfo level, RawAttempt attempt, bool finishesRun)
        {
            SessionHandle handle;
            lock (gate)
            {
                handle = Handle(level);
                handle.Attempts++;
                attempt.Number = handle.Attempts;
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
                }
                if (finishesRun)
                {
                    Prune(level.Folder, handle.Path);
                }
            });
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
            if (ContinueAcrossVisits && !handles.ContainsKey(level.Key))
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
                    if (info.Complete)
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
