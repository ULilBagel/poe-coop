using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PoECoop.Net;

namespace PoECoop
{
    /// <summary>
    /// Opt-in timing and traffic diagnostics (--coop-perf FILE). Every call must be
    /// near-free while disabled. Section and counter names are dotted lower-case
    /// "module.what", for example "positions.capture", "vitals.apply",
    /// "positions.full", "positions.delta".
    ///
    /// While enabled, one JSON object per line is appended to FILE: a "header"
    /// record once, then one "window" record about every second. Section times are
    /// inclusive, so a section called inside another is counted in both. No Unity
    /// types in here.
    /// </summary>
    internal static class PerfStats
    {
        /// <summary>False unless diagnostics were requested on the command line.</summary>
        internal static bool Enabled;

        private const int FormatVersion = 1;
        private const int MaxNames = 512;
        private const int MaxFrameSamples = 4096;
        private const string OverflowName = "(overflow)";

        private sealed class Section
        {
            public string Name;
            public long Calls, Ticks, MaxTicks, FrameTicks, FrameMaxTicks;
            public long Frame;
        }

        private sealed class Counter
        {
            public string Name;
            public long Value;
        }

        private sealed class GaugeValue
        {
            public string Name;
            public double Last, Max, Sum;
            public long Samples;
        }

        // Sections, counters and gauges may be reported from transport threads.
        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, Section> s_sections = new Dictionary<string, Section>();
        private static readonly List<Section> s_sectionList = new List<Section>();
        private static readonly Dictionary<string, Counter> s_counters = new Dictionary<string, Counter>();
        private static readonly List<Counter> s_counterList = new List<Counter>();
        private static readonly Dictionary<string, GaugeValue> s_gauges = new Dictionary<string, GaugeValue>();
        private static readonly List<GaugeValue> s_gaugeList = new List<GaugeValue>();

        // Indexed by the message type byte.
        private static readonly long[] s_sentCount = new long[256];
        private static readonly long[] s_sentBytes = new long[256];
        private static readonly long[] s_receivedCount = new long[256];
        private static readonly long[] s_receivedBytes = new long[256];
        private static readonly long[] s_dispatchCount = new long[256];
        private static readonly long[] s_dispatchTicks = new long[256];
        private static readonly long[] s_dispatchMaxTicks = new long[256];
        private static readonly string[] s_messageNames = new string[256];

        private static readonly float[] s_frameMs = new float[MaxFrameSamples];
        private static readonly float[] s_updateMs = new float[MaxFrameSamples];
        private static int s_frameSamples, s_updateSamples;
        private static long s_frames, s_frameTicks, s_frameMaxTicks, s_over33, s_over50, s_over100;
        private static long s_updates, s_updateTicks, s_updateMaxTicks;
        private static long s_frameNumber;
        private static long s_lastFrameStart, s_updateStart;

        private static long s_heapAtWindowStart, s_heapAtFrameStart, s_heapAtUpdateStart;
        private static long s_allocatedBytes, s_updateAllocatedBytes;
        private static int s_gcAtWindowStart, s_gcAtFrameStart;
        private static long s_gcFrames, s_gcFrameMaxTicks;

        private static string s_role = "offline";
        private static int s_peers;
        private static bool s_inGame, s_loading;

        private static long s_startTicks, s_windowStart, s_sequence, s_lastFlushTicks;
        private static StreamWriter s_writer;
        private static readonly StringBuilder s_line = new StringBuilder(8192);
        private static char[] s_chars = new char[8192];
        private static Action<string> s_log;

        /// <summary>Returns a timestamp to hand to End, or 0 while disabled.</summary>
        internal static long Begin()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        /// <summary>Adds the time since Begin to the named section for this frame and reporting window.</summary>
        internal static void End(string section, long start)
        {
            if (!Enabled || start == 0L || section == null) return;
            long ticks = Stopwatch.GetTimestamp() - start;
            if (ticks < 0) ticks = 0;
            lock (s_lock)
            {
                Section entry;
                if (!s_sections.TryGetValue(section, out entry)) entry = AddSection(section);
                if (entry.Frame != s_frameNumber)
                {
                    if (entry.FrameTicks > entry.FrameMaxTicks) entry.FrameMaxTicks = entry.FrameTicks;
                    entry.FrameTicks = 0;
                    entry.Frame = s_frameNumber;
                }
                entry.Calls++;
                entry.Ticks += ticks;
                entry.FrameTicks += ticks;
                if (ticks > entry.MaxTicks) entry.MaxTicks = ticks;
            }
        }

        /// <summary>Adds to a named counter for the current reporting window.</summary>
        internal static void Count(string counter, long amount = 1)
        {
            if (!Enabled || counter == null) return;
            lock (s_lock)
            {
                Counter entry;
                if (!s_counters.TryGetValue(counter, out entry)) entry = AddCounter(counter);
                entry.Value += amount;
            }
        }

        /// <summary>Records a sampled value; the report keeps the last and the high-water mark per window.</summary>
        internal static void Gauge(string name, double value)
        {
            if (!Enabled || name == null) return;
            lock (s_lock)
            {
                GaugeValue entry;
                if (!s_gauges.TryGetValue(name, out entry)) entry = AddGauge(name);
                if (entry.Samples == 0 || value > entry.Max) entry.Max = value;
                entry.Last = value;
                entry.Sum += value;
                entry.Samples++;
            }
        }

        /// <summary>One message handed to the transport for one connection. Call only while Enabled.</summary>
        internal static void NetSent(byte type, int bytes)
        {
            lock (s_lock)
            {
                s_sentCount[type]++;
                s_sentBytes[type] += bytes;
            }
        }

        /// <summary>One message taken from the transport. Call only while Enabled.</summary>
        internal static void NetReceived(byte type, int bytes)
        {
            lock (s_lock)
            {
                s_receivedCount[type]++;
                s_receivedBytes[type] += bytes;
            }
        }

        /// <summary>Time all game handlers took for one received message; start comes from Begin.</summary>
        internal static void NetDispatched(byte type, long start)
        {
            if (!Enabled || start == 0L) return;
            long ticks = Stopwatch.GetTimestamp() - start;
            if (ticks < 0) ticks = 0;
            lock (s_lock)
            {
                s_dispatchCount[type]++;
                s_dispatchTicks[type] += ticks;
                if (ticks > s_dispatchMaxTicks[type]) s_dispatchMaxTicks[type] = ticks;
            }
        }

        /// <summary>
        /// Enables diagnostics when the arguments hold "--coop-perf FILE". Each game
        /// copy needs its own FILE. Main thread, once, before anything is measured.
        /// </summary>
        internal static void Start(string[] args, string pluginVersion, Action<string> log)
        {
            if (Enabled || args == null) return;
            int flag = Array.IndexOf(args, "--coop-perf");
            if (flag < 0 || flag + 1 >= args.Length) return;
            string file = args[flag + 1];
            s_log = log;
            try
            {
                var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                s_writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16);
            }
            catch (Exception ex)
            {
                s_writer = null;
                if (log != null) log("--coop-perf: cannot write " + file + ": " + ex.Message);
                return;
            }

            long now = Stopwatch.GetTimestamp();
            s_startTicks = now;
            s_windowStart = now;
            s_lastFrameStart = 0;
            s_updateStart = 0;
            s_heapAtWindowStart = s_heapAtFrameStart = GC.GetTotalMemory(false);
            s_gcAtWindowStart = s_gcAtFrameStart = GC.CollectionCount(0);

            s_line.Length = 0;
            s_line.Append("{\"kind\":\"header\",\"v\":").Append(FormatVersion)
                .Append(",\"plugin\":"); AppendString(pluginVersion);
            s_line.Append(",\"protocol\":").Append(Protocol.Version)
                .Append(",\"started_utc\":"); AppendString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            s_line.Append(",\"tick_hz\":").Append(Stopwatch.Frequency)
                .Append(",\"high_resolution\":").Append(Stopwatch.IsHighResolution ? 1 : 0)
                .Append(",\"gc_max_generation\":").Append(GC.MaxGeneration)
                .Append(",\"window_ms\":1000}");
            if (!WriteLine()) return;
            Enabled = true;
            if (log != null) log("Performance diagnostics are written to " + file);
        }

        /// <summary>Top of Plugin.Update. Closes the previous frame and, about once a second, writes a record.</summary>
        internal static void BeginFrame()
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            long heap = GC.GetTotalMemory(false);
            int collections = GC.CollectionCount(0);
            if (s_lastFrameStart != 0)
            {
                long ticks = now - s_lastFrameStart;
                s_frames++;
                s_frameTicks += ticks;
                if (ticks > s_frameMaxTicks) s_frameMaxTicks = ticks;
                double ms = ticks * 1000.0 / Stopwatch.Frequency;
                if (ms > 33.0) s_over33++;
                if (ms > 50.0) s_over50++;
                if (ms > 100.0) s_over100++;
                if (s_frameSamples < MaxFrameSamples) s_frameMs[s_frameSamples++] = (float)ms;
                if (collections != s_gcAtFrameStart)
                {
                    s_gcFrames++;
                    if (ticks > s_gcFrameMaxTicks) s_gcFrameMaxTicks = ticks;
                }
                else if (heap > s_heapAtFrameStart)
                {
                    s_allocatedBytes += heap - s_heapAtFrameStart;
                }
            }
            s_lastFrameStart = now;
            s_gcAtFrameStart = collections;
            s_heapAtFrameStart = heap;

            if (now - s_windowStart >= Stopwatch.Frequency)
            {
                Flush(now, heap, collections);
                // Keep the cost of reporting out of the mod's own numbers.
                s_heapAtFrameStart = GC.GetTotalMemory(false);
                s_gcAtFrameStart = GC.CollectionCount(0);
            }

            lock (s_lock) s_frameNumber++;
            s_heapAtUpdateStart = s_heapAtFrameStart;
            s_updateStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Bottom of Plugin.Update, with the state the next record is labelled with.</summary>
        internal static void EndFrame(string role, int peers, bool inGame, bool loading)
        {
            if (!Enabled || s_updateStart == 0) return;
            long ticks = Stopwatch.GetTimestamp() - s_updateStart;
            s_updateStart = 0;
            s_updates++;
            s_updateTicks += ticks;
            if (ticks > s_updateMaxTicks) s_updateMaxTicks = ticks;
            if (s_updateSamples < MaxFrameSamples)
                s_updateMs[s_updateSamples++] = (float)(ticks * 1000.0 / Stopwatch.Frequency);
            if (GC.CollectionCount(0) == s_gcAtFrameStart)
            {
                long heap = GC.GetTotalMemory(false);
                if (heap > s_heapAtUpdateStart) s_updateAllocatedBytes += heap - s_heapAtUpdateStart;
            }
            s_role = role ?? "offline";
            s_peers = peers;
            s_inGame = inGame;
            s_loading = loading;
        }

        /// <summary>Writes the last partial window and closes the file.</summary>
        internal static void Stop()
        {
            if (!Enabled) return;
            try { Flush(Stopwatch.GetTimestamp(), GC.GetTotalMemory(false), GC.CollectionCount(0)); }
            catch { }
            Enabled = false;
            Close();
        }

        private static Section AddSection(string name)
        {
            Section entry;
            if (s_sectionList.Count >= MaxNames)
            {
                if (s_sections.TryGetValue(OverflowName, out entry)) return entry;
                name = OverflowName;
            }
            entry = new Section { Name = name, Frame = s_frameNumber };
            s_sections[name] = entry;
            s_sectionList.Add(entry);
            return entry;
        }

        private static Counter AddCounter(string name)
        {
            Counter entry;
            if (s_counterList.Count >= MaxNames)
            {
                if (s_counters.TryGetValue(OverflowName, out entry)) return entry;
                name = OverflowName;
            }
            entry = new Counter { Name = name };
            s_counters[name] = entry;
            s_counterList.Add(entry);
            return entry;
        }

        private static GaugeValue AddGauge(string name)
        {
            GaugeValue entry;
            if (s_gaugeList.Count >= MaxNames)
            {
                if (s_gauges.TryGetValue(OverflowName, out entry)) return entry;
                name = OverflowName;
            }
            entry = new GaugeValue { Name = name };
            s_gauges[name] = entry;
            s_gaugeList.Add(entry);
            return entry;
        }

        private static void Flush(long now, long heap, int collections)
        {
            long flushStart = Stopwatch.GetTimestamp();
            StringBuilder sb = s_line;
            sb.Length = 0;
            sb.Append("{\"kind\":\"window\",\"seq\":").Append(s_sequence++);
            sb.Append(",\"t\":"); AppendMilliseconds(now - s_startTicks, 0.001);
            sb.Append(",\"dur_ms\":"); AppendMilliseconds(now - s_windowStart, 1.0);
            sb.Append(",\"role\":"); AppendString(s_role);
            sb.Append(",\"peers\":").Append(s_peers);
            sb.Append(",\"ingame\":").Append(s_inGame ? 1 : 0);
            sb.Append(",\"loading\":").Append(s_loading ? 1 : 0);

            sb.Append(",\"frames\":{\"n\":").Append(s_frames);
            sb.Append(",\"avg_ms\":"); AppendMilliseconds(s_frames > 0 ? s_frameTicks / s_frames : 0, 1.0);
            Array.Sort(s_frameMs, 0, s_frameSamples);
            sb.Append(",\"p50_ms\":"); AppendNumber(Percentile(s_frameMs, s_frameSamples, 0.50));
            sb.Append(",\"p95_ms\":"); AppendNumber(Percentile(s_frameMs, s_frameSamples, 0.95));
            sb.Append(",\"p99_ms\":"); AppendNumber(Percentile(s_frameMs, s_frameSamples, 0.99));
            sb.Append(",\"max_ms\":"); AppendMilliseconds(s_frameMaxTicks, 1.0);
            sb.Append(",\"over33\":").Append(s_over33);
            sb.Append(",\"over50\":").Append(s_over50);
            sb.Append(",\"over100\":").Append(s_over100).Append('}');

            sb.Append(",\"update\":{\"n\":").Append(s_updates);
            sb.Append(",\"total_ms\":"); AppendMilliseconds(s_updateTicks, 1.0);
            sb.Append(",\"avg_ms\":"); AppendMilliseconds(s_updates > 0 ? s_updateTicks / s_updates : 0, 1.0);
            Array.Sort(s_updateMs, 0, s_updateSamples);
            sb.Append(",\"p95_ms\":"); AppendNumber(Percentile(s_updateMs, s_updateSamples, 0.95));
            sb.Append(",\"p99_ms\":"); AppendNumber(Percentile(s_updateMs, s_updateSamples, 0.99));
            sb.Append(",\"max_ms\":"); AppendMilliseconds(s_updateMaxTicks, 1.0);
            sb.Append('}');

            sb.Append(",\"gc\":{\"collections\":").Append(collections - s_gcAtWindowStart);
            sb.Append(",\"gc_frames\":").Append(s_gcFrames);
            sb.Append(",\"gc_frame_max_ms\":"); AppendMilliseconds(s_gcFrameMaxTicks, 1.0);
            sb.Append(",\"heap_bytes\":").Append(heap);
            sb.Append(",\"heap_delta_bytes\":").Append(heap - s_heapAtWindowStart);
            sb.Append(",\"alloc_bytes\":").Append(s_allocatedBytes);
            sb.Append(",\"update_alloc_bytes\":").Append(s_updateAllocatedBytes).Append('}');

            lock (s_lock)
            {
                sb.Append(",\"sections\":{");
                bool first = true;
                for (int i = 0; i < s_sectionList.Count; i++)
                {
                    Section s = s_sectionList[i];
                    if (s.Calls == 0) continue;
                    if (s.FrameTicks > s.FrameMaxTicks) s.FrameMaxTicks = s.FrameTicks;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendString(s.Name);
                    sb.Append(":{\"n\":").Append(s.Calls);
                    sb.Append(",\"us\":").Append(Microseconds(s.Ticks));
                    sb.Append(",\"max_us\":").Append(Microseconds(s.MaxTicks));
                    sb.Append(",\"frame_max_us\":").Append(Microseconds(s.FrameMaxTicks)).Append('}');
                    s.Calls = s.Ticks = s.MaxTicks = s.FrameTicks = s.FrameMaxTicks = 0;
                }

                sb.Append("},\"counters\":{");
                first = true;
                for (int i = 0; i < s_counterList.Count; i++)
                {
                    Counter c = s_counterList[i];
                    if (c.Value == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendString(c.Name);
                    sb.Append(':').Append(c.Value);
                    c.Value = 0;
                }

                sb.Append("},\"gauges\":{");
                first = true;
                for (int i = 0; i < s_gaugeList.Count; i++)
                {
                    GaugeValue g = s_gaugeList[i];
                    if (g.Samples == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendString(g.Name);
                    sb.Append(":{\"n\":").Append(g.Samples);
                    sb.Append(",\"last\":"); AppendNumber(g.Last);
                    sb.Append(",\"max\":"); AppendNumber(g.Max);
                    sb.Append(",\"avg\":"); AppendNumber(g.Sum / g.Samples);
                    sb.Append('}');
                    g.Samples = 0;
                    g.Sum = g.Max = g.Last = 0;
                }

                sb.Append("},\"net\":{");
                first = true;
                for (int i = 0; i < 256; i++)
                {
                    if (s_sentCount[i] == 0 && s_receivedCount[i] == 0 && s_dispatchCount[i] == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendString(MessageName(i));
                    sb.Append(":{\"id\":").Append(i);
                    sb.Append(",\"sent\":").Append(s_sentCount[i]);
                    sb.Append(",\"sent_bytes\":").Append(s_sentBytes[i]);
                    sb.Append(",\"recv\":").Append(s_receivedCount[i]);
                    sb.Append(",\"recv_bytes\":").Append(s_receivedBytes[i]);
                    sb.Append(",\"dispatch\":").Append(s_dispatchCount[i]);
                    sb.Append(",\"dispatch_us\":").Append(Microseconds(s_dispatchTicks[i]));
                    sb.Append(",\"dispatch_max_us\":").Append(Microseconds(s_dispatchMaxTicks[i])).Append('}');
                    s_sentCount[i] = s_sentBytes[i] = s_receivedCount[i] = s_receivedBytes[i] = 0;
                    s_dispatchCount[i] = s_dispatchTicks[i] = s_dispatchMaxTicks[i] = 0;
                }
                sb.Append('}');
            }

            sb.Append(",\"flush_us\":").Append(Microseconds(s_lastFlushTicks)).Append('}');
            WriteLine();

            s_frameSamples = s_updateSamples = 0;
            s_frames = s_frameTicks = s_frameMaxTicks = s_over33 = s_over50 = s_over100 = 0;
            s_updates = s_updateTicks = s_updateMaxTicks = 0;
            s_allocatedBytes = s_updateAllocatedBytes = 0;
            s_gcFrames = s_gcFrameMaxTicks = 0;
            s_gcAtWindowStart = collections;
            s_heapAtWindowStart = heap;
            s_windowStart = now;
            s_lastFlushTicks = Stopwatch.GetTimestamp() - flushStart;
        }

        private static bool WriteLine()
        {
            try
            {
                s_line.Append('\n');
                int count = s_line.Length;
                if (s_chars.Length < count) s_chars = new char[Math.Max(count, s_chars.Length * 2)];
                s_line.CopyTo(0, s_chars, 0, count);
                s_writer.Write(s_chars, 0, count);
                s_writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Enabled = false;
                Close();
                if (s_log != null) s_log("--coop-perf stopped: " + ex.Message);
                return false;
            }
        }

        private static void Close()
        {
            StreamWriter writer = s_writer;
            s_writer = null;
            if (writer == null) return;
            try { writer.Dispose(); }
            catch { }
        }

        private static string MessageName(int id)
        {
            string name = s_messageNames[id];
            if (name != null) return name;
            // FirstGameMessage shares its value with SaveBegin.
            name = id == (int)MessageType.SaveBegin ? "SaveBegin" : Enum.GetName(typeof(MessageType), (MessageType)id);
            return s_messageNames[id] = name ?? "Type" + id.ToString(CultureInfo.InvariantCulture);
        }

        private static double Percentile(float[] sorted, int count, double fraction)
        {
            if (count <= 0) return 0.0;
            int index = (int)Math.Ceiling(fraction * count) - 1;
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;
            return sorted[index];
        }

        private static long Microseconds(long ticks)
        {
            return (long)(ticks * 1000000.0 / Stopwatch.Frequency);
        }

        private static void AppendMilliseconds(long ticks, double scale)
        {
            AppendNumber(ticks * 1000.0 / Stopwatch.Frequency * scale);
        }

        private static void AppendNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) s_line.Append("null");
            else s_line.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void AppendString(string value)
        {
            StringBuilder sb = s_line;
            sb.Append('"');
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                    else if (c < ' ') sb.Append(' ');
                    else sb.Append(c);
                }
            }
            sb.Append('"');
        }
    }
}
