using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ShreddedBots
{
    /// <summary>
    /// ShreddedBots.log writer (v0.2.2: non-blocking).
    /// Callers only format a line and enqueue it (ConcurrentQueue, lock-free). A single background thread writes batches
    /// through one long-lived FileStream (64 KB buffer) and flushes every ~1 s, so the game thread never touches the disk
    /// (the game folder can be on a slow USB HDD). Before the async writer starts (loader, before the game loop) and
    /// after it stops, lines are written synchronously. Crash/exit paths call <see cref="FlushBlocking"/>.
    /// If more than MAX_QUEUE lines are pending, the oldest are dropped and a "[log] dropped N lines" note is written.
    /// Each batch seeks to end-of-file first, so the loader AppDomain's few synchronous lines never get overwritten.
    /// </summary>
    public static class Log
    {
        private const int MAX_QUEUE = 10000;
        private static readonly object _syncLock = new object();
        private static string _path;
        private static readonly ConcurrentQueue<string> _q = new ConcurrentQueue<string>();
        private static int _count, _dropped;
        private static Thread _thread;
        private static readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private static readonly ManualResetEvent _flushed = new ManualResetEvent(true);
        private static volatile bool _async, _stopping;
        private static int _flushRequests;

        public static string GameDir => AppDomain.CurrentDomain.BaseDirectory;
        private static string PathLog => _path ?? (_path = ChooseLogPath());

        private static string _dataDir;
        /// <summary>The ONE folder where ShreddedBots.log and ShreddedBots.ini live: the game folder if it is writable, otherwise Documents\ShreddedBots.</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir == null) { string p = PathLog; _dataDir = Path.GetDirectoryName(p); }
                return _dataDir;
            }
        }

        /// <summary>ShreddedBots.log next to the loader; falls back to Documents\ShreddedBots if the game folder is not writable.</summary>
        private static string ChooseLogPath()
        {
            string preferred = Path.Combine(GameDir, "ShreddedBots.log");
            try
            {
                string probe = Path.Combine(GameDir, "shreddedbots-write-test.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return preferred;
            }
            catch
            {
                try
                {
                    string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShreddedBots");
                    Directory.CreateDirectory(d);
                    return Path.Combine(d, "ShreddedBots.log");
                }
                catch { return preferred; }
            }
        }

        /// <summary>Rotation (ShreddedBots.log -> ShreddedBots.prev.log). Called once by the loader at startup, before the game loop.</summary>
        public static void Init(bool truncate)
        {
            lock (_syncLock)
            {
                try
                {
                    if (truncate && File.Exists(PathLog))
                    {
                        string old = Path.Combine(Path.GetDirectoryName(PathLog), "ShreddedBots.prev.log");
                        try { if (File.Exists(old)) File.Delete(old); File.Move(PathLog, old); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>Switch this AppDomain to the background writer. Called in the game AppDomain before Game.exe's Main runs.</summary>
        public static void StartAsync()
        {
            lock (_syncLock)
            {
                if (_async) return;
                _thread = new Thread(WriterLoop) { IsBackground = true, Name = "ShreddedBots log writer", Priority = ThreadPriority.BelowNormal };
                _async = true;
                _thread.Start();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
                AppDomain.CurrentDomain.DomainUnload += (s, e) => Shutdown();
            }
        }

        public static void Info(string msg) => Write("INFO ", msg);
        /// <summary>Per-event diagnostics. Compiled out entirely (call and arguments) unless the SB_DEBUG symbol is defined (it never is in this source release); at run time it only
        /// writes when [Debug] VerboseLog=true in ShreddedBots.ini (or the F7 debug toggle is on).</summary>
        [Conditional("SB_DEBUG")] public static void Verbose(string msg) { if (VerboseEnabled) Write("DEBUG", msg); }
        public static volatile bool VerboseEnabled;
        public static void Warn(string msg) => Write("WARN ", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        public static void Exception(string context, Exception ex)
        {
            var sb = new StringBuilder();
            Exception orig = ex;
            sb.Append(context).Append(Environment.NewLine);
            int depth = 0;
            while (ex != null && depth < 8)
            {
                sb.AppendFormat("  {0}: {1}{2}{3}{2}", ex.GetType().FullName, ex.Message, Environment.NewLine, ex.StackTrace);
                ex = ex.InnerException;
                depth++;
            }
            sb.Append(Diag.LoaderInfo(orig));
            Write("FATAL", sb.ToString());
            if (context != null && context.StartsWith("Unhandled", StringComparison.Ordinal)) FlushBlocking(2000);
        }

        private static string Format(string level, string msg) =>
            string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.Now, level, Redact(msg), Environment.NewLine);

        // Defence in depth: whatever any caller passes (exception text, URLs from the game), a Discord webhook URL / its id+token, a Steam
        // web API key style value or an HTTP basic-auth style secret never reaches the log file.
        private static readonly System.Text.RegularExpressions.Regex _secrets = new System.Text.RegularExpressions.Regex(
            @"(discord(app)?\.com/api/(v\d+/)?webhooks/)[^\s""'<>]+|(api[_-]?key|key|token|secret)(=|:)\s*[A-Za-z0-9_\-]{16,}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        public static string Redact(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            try { return _secrets.Replace(msg, m => m.Groups[1].Success ? m.Groups[1].Value + "[redacted]" : m.Groups[4].Value + m.Groups[5].Value + "[redacted]"); }
            catch { return msg; }
        }

        private static void Write(string level, string msg)
        {
            try
            {
                string line = Format(level, msg);
                if (!_async) { WriteSync(line); return; }
                if (_stopping) { FlushBlocking(500); WriteSync(line); return; }   // after shutdown flush: write directly
                _q.Enqueue(line);
                if (Interlocked.Increment(ref _count) > MAX_QUEUE && _q.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _count);
                    Interlocked.Increment(ref _dropped);
                }
                if (level == "FATAL" || level == "ERROR") _wake.Set();
            }
            catch { }
        }

        private static void WriteSync(string line)
        {
            lock (_syncLock)
            {
                try
                {
                    using (var fs = new FileStream(PathLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        byte[] b = Encoding.UTF8.GetBytes(line);
                        fs.Write(b, 0, b.Length);
                    }
                }
                catch { }
            }
        }

        /// <summary>Wait (up to timeoutMs) until everything queued so far is on disk. For crash/exit paths only, never per frame.</summary>
        public static void FlushBlocking(int timeoutMs)
        {
            if (!_async || _thread == null) return;
            try
            {
                if (Thread.CurrentThread == _thread) return;
                lock (_syncLock) { _flushed.Reset(); Interlocked.Increment(ref _flushRequests); }
                _wake.Set();
                _flushed.WaitOne(timeoutMs);
            }
            catch { }
        }

        /// <summary>Called by the loader (via DoCallBack into the game AppDomain) after Game.exe's Main returns.</summary>
        public static void FlushForExit() { FlushBlocking(3000); }

        private static void Shutdown()
        {
            if (!_async || _stopping) return;
            FlushBlocking(2000);
            _stopping = true;
        }

        private static void WriterLoop()
        {
            FileStream fs = null;
            var sb = new StringBuilder(16 * 1024);
            DateTime lastFlush = DateTime.UtcNow;
            bool dirty = false;
            while (true)
            {
                _wake.WaitOne(1000);
                try
                {
                    bool flushReq = Interlocked.Exchange(ref _flushRequests, 0) > 0;   // before draining: everything queued before the request gets written
                    bool stopping = _stopping;
                    int dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0) sb.Append(Format("WARN ", "[log] queue over " + MAX_QUEUE + " lines: dropped " + dropped + " oldest lines"));
                    while (_q.TryDequeue(out string line))
                    {
                        Interlocked.Decrement(ref _count);
                        sb.Append(line);
                        if (sb.Length > 48 * 1024) { WriteChunk(ref fs, sb); dirty = true; }
                    }
                    if (sb.Length > 0) { WriteChunk(ref fs, sb); dirty = true; }
                    if (dirty && (flushReq || stopping || (DateTime.UtcNow - lastFlush).TotalMilliseconds >= 1000))
                    {
                        fs?.Flush();   // to the OS (survives a process crash); no fsync
                        dirty = false; lastFlush = DateTime.UtcNow;
                    }
                    if (flushReq || stopping) _flushed.Set();
                }
                catch
                {
                    // disk hiccup / file locked: drop the handle and retry next round (lines in sb are kept)
                    try { fs?.Dispose(); } catch { }
                    fs = null;
                    if (sb.Length > 1024 * 1024) sb.Clear();
                    _flushed.Set();
                }
            }
        }

        private static void WriteChunk(ref FileStream fs, StringBuilder sb)
        {
            if (fs == null)
                fs = new FileStream(PathLog, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.None);
            fs.Seek(0, SeekOrigin.End);   // another writer (loader AppDomain) may have appended
            byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
            fs.Write(b, 0, b.Length);
            sb.Clear();
        }
    }
}
