using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BinaryRewrite
{
    // ===================================================================================
    //  The map F  (Definition 1 of the papers)
    //
    //  For a binary string L with k = nu(L) = number of 1's:
    //   Step 1 (pad): k even -> append "00",  s := 0 ;  k odd -> append "0001", s := -1.
    //   Step 2 (pair/fill/count): positions p_1<...<p_2m of the 1's, grouped in consecutive
    //           pairs. For each pair j: set p_{2j-1}..p_{2j}-1 to 1; set p_{2j} to 0; add
    //           (p_{2j} - p_{2j-1} - 1) to s.
    //   Step 3: if s < 2 HALT (F undefined); else F(L) = L^(2) . (0110)^(s-2).
    //
    //  Three independent engines are provided; all agree on every common output (validated
    //  against the papers and cross-checked against one another):
    //    * BitEngine        - literal char-array transcription, ground truth (small strings).
    //    * GapEngine        - in-memory gap-vector engine (the memory-frugal workhorse).
    //    * DiskGapEngine     - streaming gap-vector engine, bounded only by free disk space.
    //  plus the integer conjugate T of Collatz type (Proposition 27 / Theorem 29).
    // ===================================================================================

    public enum EngineKind
    {
        Auto,       // pick Bit for tiny n, Gap otherwise
        Bit,        // literal string engine (ground truth, small only)
        GapMemory,  // in-memory gap-vector engine
        GapDisk     // disk-streaming gap-vector engine (no limit but physical)
    }

    /// <summary>Everything known about one iterate L_n and the step that produced s_n.</summary>
    public sealed class StepInfo
    {
        public int N;                 // index n
        public long S;                // counter s_n computed during F(L_n)
        public long Nu;               // nu(L_n) = number of 1's
        public bool NuEven;           // parity of nu(L_n)
        public long Length;           // |L_n|
        public long Omega = -1;       // omega(L_n) = # odd-length 1-runs (-1 = not computed)
        public bool Halted;           // F(L_n) undefined (s < 2)
        public BigInteger Value;      // V(L_n) = int(L_n, 2)
        public bool HasValue;         // whether Value was materialized
        public string Bits;           // full string, only for small n (else null)

        public char ParityChar => NuEven ? 'E' : 'o';
    }

    /// <summary>Body/tail decomposition of an even-parity counter (Observation 24 / 26).</summary>
    public sealed class Decomposition
    {
        public int N;
        public long S;
        public SortedDictionary<int, long> Multiset = new SortedDictionary<int, long>();
        public long Count;     // number of contributing (non-zero) within-pair gaps
        public long Surplus;   // size surplus  =  S - Count
    }

    // -----------------------------------------------------------------------------------
    //  Bit engine - literal transcription of Definition 1. Ground truth while it fits.
    // -----------------------------------------------------------------------------------
    public static class BitEngine
    {
        /// <summary>One application of F. Returns next string, or null with halted=true.</summary>
        public static string Step(string L, out bool halted, out long s, out long nu)
        {
            long k = 0;
            for (int i = 0; i < L.Length; i++) if (L[i] == '1') k++;
            nu = k;

            char[] arr;
            if (k % 2 == 0) { arr = new char[L.Length + 2]; s = 0; }
            else { arr = new char[L.Length + 4]; s = -1; }
            for (int i = 0; i < arr.Length; i++) arr[i] = '0';
            for (int i = 0; i < L.Length; i++) arr[i] = L[i];
            if (k % 2 != 0) arr[L.Length + 3] = '1'; // the "0001" pad's final 1

            // positions of 1's
            var pos = new List<int>();
            for (int i = 0; i < arr.Length; i++) if (arr[i] == '1') pos.Add(i);

            int m = pos.Count / 2;
            for (int j = 0; j < m; j++)
            {
                int a = pos[2 * j], b = pos[2 * j + 1];
                for (int p = a; p < b; p++) arr[p] = '1';
                arr[b] = '0';
                s += (b - a - 1);
            }

            if (s < 2) { halted = true; return null; }
            halted = false;

            var sb = new StringBuilder(arr.Length + (int)(4 * (s - 2)));
            sb.Append(arr);
            for (long t = 0; t < s - 2; t++) sb.Append("0110");
            return sb.ToString();
        }
    }

    // -----------------------------------------------------------------------------------
    //  In-memory gap-vector engine. State = (gaps[], t) where gaps are the 0-counts
    //  between consecutive 1's and t is the trailing-zero count. nu = gaps.Length + 1.
    //
    //  The faithful-engine subtleties from Appendix A are handled explicitly:
    //    * additive seed s_init = -1 on the odd pad,
    //    * trailing-zero count t' = t+3 at the unique s = 2 even step (via body_tz),
    //    * the gap from the body's last 1 into the appended tail = (body trailing zeros)+1.
    // -----------------------------------------------------------------------------------
    public sealed class GapEngine
    {
        public int[] Gaps;   // gaps between consecutive 1's
        public int T;        // trailing-zero count

        public long Nu => (long)Gaps.Length + 1;

        public GapEngine(int[] gaps, int t) { Gaps = gaps; T = t; }

        /// <summary>Build a gap-engine state from a raw seed string (leading 0's are inert).</summary>
        public static GapEngine FromSeed(string seed)
        {
            int first = -1, last = -1, ones = 0;
            for (int i = 0; i < seed.Length; i++)
                if (seed[i] == '1') { if (first < 0) first = i; last = i; ones++; }
            if (ones == 0) return null; // nu = 0 : F undefined immediately

            var g = new List<int>(ones - 1);
            int prev = first;
            for (int i = first + 1; i <= last; i++)
            {
                if (seed[i] == '1') { g.Add(i - prev - 1); prev = i; }
            }
            int t = seed.Length - 1 - last;
            return new GapEngine(g.ToArray(), t);
        }

        private int PG(int k) => k < Gaps.Length ? Gaps[k] : (T + 3);

        /// <summary>Counter that F would compute on the current state (no rewrite). nuEven set.</summary>
        public long PeekCounter(out bool nuEven)
        {
            long nu = Nu;
            bool oddPad = (nu % 2 == 1);
            nuEven = !oddPad;
            long sinit = oddPad ? -1 : 0;
            long m = (oddPad ? nu + 1 : nu) / 2;
            long s = sinit;
            for (long j = 1; j <= m; j++) s += PG((int)(2 * (j - 1)));
            return s;
        }

        /// <summary>Decomposition of the (even-parity) counter into count + size-surplus.</summary>
        public Decomposition Decompose(int n)
        {
            long nu = Nu;
            bool oddPad = (nu % 2 == 1);
            long sinit = oddPad ? -1 : 0;
            long m = (oddPad ? nu + 1 : nu) / 2;
            var d = new Decomposition { N = n };
            long s = sinit, count = 0;
            for (long j = 1; j <= m; j++)
            {
                int v = PG((int)(2 * (j - 1)));
                s += v;
                if (v > 0) { count++; d.Multiset.TryGetValue(v, out long c); d.Multiset[v] = c + 1; }
            }
            d.S = s; d.Count = count; d.Surplus = s - count;
            return d;
        }

        public long Omega()
        {
            long omega = 0, run = 1;
            for (int i = 0; i < Gaps.Length; i++)
            {
                if (Gaps[i] == 0) run++;
                else { if ((run & 1) == 1) omega++; run = 1; }
            }
            if ((run & 1) == 1) omega++;
            return omega;
        }

        public long Length
        {
            get { long len = 1; for (int i = 0; i < Gaps.Length; i++) len += Gaps[i] + 1; return len + T; }
        }

        /// <summary>Advance one step in place. Returns false (and leaves state intact) on halt.</summary>
        public bool Step(out long s, out bool nuEven)
        {
            long nu = Nu;
            bool oddPad = (nu % 2 == 1);
            nuEven = !oddPad;
            long sinit = oddPad ? -1 : 0;
            long m = (oddPad ? nu + 1 : nu) / 2;

            long ss = sinit;
            for (long j = 1; j <= m; j++) ss += PG((int)(2 * (j - 1)));
            s = ss;
            if (ss < 2) return false;

            int bodyTz = oddPad ? 1 : (T + 3);

            // exact output length (closed form): sum(within-pair gaps) + (m-1) separators + tail
            long sumWithin = ss - sinit;
            long tailLen = ss > 2 ? 2 * (ss - 2) : 0;
            long newLenL = sumWithin + (m - 1) + tailLen;
            if (newLenL > 2_000_000_000L)
                throw new OutOfMemoryException(
                    "In-memory gap vector would exceed ~2e9 entries; switch to the disk engine.");
            int newLen = (int)newLenL;

            var ng = new int[newLen];
            // new int[] is zero-initialised by the CLR, so we DON'T need to write the
            // 'eOdd' zero blocks of each pair — we only write the separators and the tail.
            int pos = 0;
            for (long j = 1; j <= m; j++)
            {
                int eOdd = PG((int)(2 * (j - 1)));
                pos += eOdd;                 // skip the zero block (already zero)
                if (j < m)
                {
                    ng[pos] = 1 + PG((int)(2 * j - 1));
                    pos++;
                }
            }
            // pos now sits at the start of the tail.
            int newT;
            if (ss == 2) { newT = bodyTz; }
            else
            {
                ng[pos++] = bodyTz + 1;
                ng[pos++] = 0;
                for (long r = 1; r < ss - 2; r++) { ng[pos++] = 2; ng[pos++] = 0; }
                newT = 1;
            }
            Gaps = ng; T = newT;
            return true;
        }

        /// <summary>
        /// Step using up to <paramref name="cores"/> threads for the separator writes. Same
        /// arithmetic as <see cref="Step"/>; profitable when m (≈ ν/2) is in the millions.
        /// </summary>
        public bool ParallelStep(int cores, out long s, out bool nuEven)
        {
            long nu = Nu;
            bool oddPad = (nu % 2 == 1);
            nuEven = !oddPad;
            long sinit = oddPad ? -1 : 0;
            long m = (oddPad ? nu + 1 : nu) / 2;

            long ss = sinit;
            for (long j = 1; j <= m; j++) ss += PG((int)(2 * (j - 1)));
            s = ss;
            if (ss < 2) return false;
            if (cores < 2 || m < 50_000) return Step(out s, out nuEven); // overhead exceeds gain

            int bodyTz = oddPad ? 1 : (T + 3);
            long sumWithin = ss - sinit;
            long tailLen = ss > 2 ? 2 * (ss - 2) : 0;
            long newLenL = sumWithin + (m - 1) + tailLen;
            if (newLenL > 2_000_000_000L)
                throw new OutOfMemoryException(
                    "In-memory gap vector would exceed ~2e9 entries; switch to the disk engine.");
            int newLen = (int)newLenL;

            var ng = new int[newLen];

            // Sequential prefix-sum of block start positions (O(m); fast cache-friendly walk).
            // starts[j] is the start of block j+1 in ng (so starts[0]=0, starts[m] = newLen - tailLen).
            long[] starts = new long[m + 1];
            long acc = 0;
            for (long j = 0; j < m; j++)
            {
                int eOdd = PG((int)(2 * j));
                acc += eOdd + (j + 1 < m ? 1 : 0); // +1 for the separator after each non-last block
                starts[j + 1] = acc;
            }

            // Parallel: write separator at the end of each non-last block. Each j writes a
            // distinct index (starts[j-1] + eOdd[j-1]), so no synchronisation is required.
            Parallel.For(1L, m, new ParallelOptions { MaxDegreeOfParallelism = cores }, j =>
            {
                long eOdd = PG((int)(2 * (j - 1)));
                long sepIdx = starts[j - 1] + eOdd;
                ng[sepIdx] = 1 + PG((int)(2 * j - 1));
            });

            // Tail is small; sequential is fine.
            int pos = (int)starts[m];
            int newT;
            if (ss == 2) { newT = bodyTz; }
            else
            {
                ng[pos++] = bodyTz + 1;
                ng[pos++] = 0;
                for (long r = 1; r < ss - 2; r++) { ng[pos++] = 2; ng[pos++] = 0; }
                newT = 1;
            }
            Gaps = ng; T = newT;
            return true;
        }
    }

    // -----------------------------------------------------------------------------------
    //  Central scratch space for all disk-engine files. Everything lives under one
    //  session root so it can be wiped in a single call when the program closes.
    // -----------------------------------------------------------------------------------
    public static class DiskWorkspace
    {
        public static readonly string Root =
            Path.Combine(Path.GetTempPath(), "BinaryRewriteStudio", "session-" + Guid.NewGuid().ToString("N"));

        private static int _ctr;

        public static string NewEngineDir()
        {
            int id = Interlocked.Increment(ref _ctr);
            return Path.Combine(Root, "orbit-" + id);
        }

        /// <summary>Delete every file this session wrote to disk. Safe to call repeatedly.</summary>
        public static void Cleanup()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* best effort */ }
        }

        /// <summary>Approximate bytes currently used on disk by this session.</summary>
        public static long BytesUsed()
        {
            try
            {
                if (!Directory.Exists(Root)) return 0;
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
                return total;
            }
            catch { return 0; }
        }
    }

    // -----------------------------------------------------------------------------------
    //  Disk-streaming gap engine. Identical arithmetic to GapEngine, but the gap vector
    //  lives in a file of raw Int32's, so the only ceiling is free disk space. Each step
    //  reads the input file once to compute s and once more to write the successor file.
    //  This is the "calculate anything up to physical (SSD) limits" path.
    // -----------------------------------------------------------------------------------
    public sealed class DiskGapEngine : IDisposable
    {
        private string _path;          // current gap file (raw Int32, little-endian)
        private readonly string _dir;
        private int _gen;
        public long GapCount { get; private set; }
        public int T { get; private set; }
        public long Nu => GapCount + 1;

        private const int BUF = 1 << 20; // 1M ints per buffer

        public DiskGapEngine(GapEngine seed, string workDir)
        {
            _dir = workDir;
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "gaps_0.bin");
            using (var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var bw = new BinaryWriter(fs))
                foreach (int g in seed.Gaps) bw.Write(g);
            GapCount = seed.Gaps.LongLength;
            T = seed.T;
        }

        public long PeekCounter(out bool nuEven)
        {
            long nu = Nu;
            bool oddPad = (nu % 2 == 1);
            nuEven = !oddPad;
            long sinit = oddPad ? -1 : 0;
            long s = sinit;
            using (var br = OpenRead())
            {
                long k = 0;
                int[] buf = new int[BUF];
                while (true)
                {
                    int got = ReadInts(br, buf);
                    if (got == 0) break;
                    for (int i = 0; i < got; i++, k++)
                        if ((k & 1) == 0) s += buf[i];   // even-index gaps are within-pair
                }
            }
            if (oddPad) s += (T + 3); // the virtual padded gap at the final even index
            return s;
        }

        /// <summary>Advance one step on disk. Returns false on halt (file unchanged).</summary>
        public bool Step(out long s, out bool nuEven)
        {
            long nu = Nu;
            bool oddPad = (nu % 2 == 1);
            nuEven = !oddPad;
            long sinit = oddPad ? -1 : 0;
            long m = (oddPad ? nu + 1 : nu) / 2;

            s = PeekCounter(out _);
            if (s < 2) return false;

            int bodyTz = oddPad ? 1 : (T + 3);
            string outPath = Path.Combine(_dir, "gaps_" + (_gen + 1) + ".bin");

            using (var br = OpenRead())
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var bw = new BinaryWriter(new BufferedStream(fs, 1 << 20)))
            {
                long produced = 0;
                for (long j = 1; j <= m; j++)
                {
                    int eOdd = (j < m || !oddPad) ? ReadOne(br) : (T + 3); // last even read is virtual on odd pad
                    for (int z = 0; z < eOdd; z++) { bw.Write(0); produced++; }
                    if (j < m) { int eEven = ReadOne(br); bw.Write(1 + eEven); produced++; }
                }
                int newT;
                if (s == 2) { newT = bodyTz; }
                else
                {
                    bw.Write(bodyTz + 1); produced++;
                    bw.Write(0); produced++;
                    for (long r = 1; r < s - 2; r++) { bw.Write(2); bw.Write(0); produced += 2; }
                    newT = 1;
                }
                bw.Flush();
                GapCount = produced;
                T = newT;
            }

            string old = _path;
            _path = outPath;
            _gen++;
            try { File.Delete(old); } catch { /* keep going even if cleanup fails */ }
            return true;
        }

        /// <summary>Decomposition of the (even-parity) counter from streamed gaps.</summary>
        public Decomposition Decompose(int n)
        {
            long nu = Nu;
            bool oddPad = (nu % 2 == 1);
            long sinit = oddPad ? -1 : 0;
            var d = new Decomposition { N = n };
            long sSum = sinit, count = 0;
            using (var br = OpenRead())
            {
                long k = 0;
                int[] buf = new int[BUF];
                while (true)
                {
                    int got = ReadInts(br, buf);
                    if (got == 0) break;
                    for (int i = 0; i < got; i++, k++)
                    {
                        if ((k & 1) == 0)   // even index = within-pair gap
                        {
                            int v = buf[i];
                            sSum += v;
                            if (v > 0) { count++; d.Multiset.TryGetValue(v, out long c); d.Multiset[v] = c + 1; }
                        }
                    }
                }
            }
            if (oddPad)
            {
                int v = T + 3;
                sSum += v;
                if (v > 0) { count++; d.Multiset.TryGetValue(v, out long c); d.Multiset[v] = c + 1; }
            }
            d.S = sSum; d.Count = count; d.Surplus = sSum - count;
            return d;
        }

        public long ComputeOmega()
        {
            long omega = 0, run = 1;
            using (var br = OpenRead())
            {
                int[] buf = new int[BUF];
                while (true)
                {
                    int got = ReadInts(br, buf);
                    if (got == 0) break;
                    for (int i = 0; i < got; i++)
                    {
                        if (buf[i] == 0) run++;
                        else { if ((run & 1) == 1) omega++; run = 1; }
                    }
                }
            }
            if ((run & 1) == 1) omega++;
            return omega;
        }

        public long ComputeLength()
        {
            long len = 1;
            using (var br = OpenRead())
            {
                int[] buf = new int[BUF];
                while (true)
                {
                    int got = ReadInts(br, buf);
                    if (got == 0) break;
                    for (int i = 0; i < got; i++) len += buf[i] + 1;
                }
            }
            return len + T;
        }

        private BinaryReader OpenRead() =>
            new BinaryReader(new BufferedStream(
                new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20), 1 << 20));

        private static int ReadInts(BinaryReader br, int[] buf)
        {
            int n = 0;
            try { for (; n < buf.Length; n++) buf[n] = br.ReadInt32(); }
            catch (EndOfStreamException) { }
            return n;
        }

        private static int ReadOne(BinaryReader br) => br.ReadInt32();

        public void Dispose()
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }
    }

    // -----------------------------------------------------------------------------------
    //  Integer conjugate  T : Z+ -> Z+   (Proposition 27 / Theorem 29).
    //  N0 = V(L0); verified N0..N3 = 2, 998, 45868646, 213192976 for seed 10.
    // -----------------------------------------------------------------------------------
    public static class Conjugate
    {
        public static int PopCount(BigInteger x)
        {
            if (x.Sign < 0) x = -x;
            int c = 0;
            while (x > 0) { if (!(x & 1).IsZero) c++; x >>= 1; }
            return c;
        }

        // S(M): alternating bit-sum over set-bit positions in DESCENDING order, signs +,-,+,-...
        public static BigInteger AltBitSum(BigInteger M)
        {
            var bits = new List<int>();
            BigInteger x = M;
            int pos = 0;
            while (x > 0) { if (!(x & 1).IsZero) bits.Add(pos); x >>= 1; pos++; }
            bits.Reverse(); // descending
            BigInteger s = 0;
            for (int i = 0; i < bits.Count; i++)
            {
                BigInteger term = BigInteger.One << bits[i];
                s += (i % 2 == 0) ? term : -term;
            }
            return s;
        }

        /// <summary>T(N). Returns false on halt (s &lt; 2). Also reports s and S(M).</summary>
        public static bool T(BigInteger N, out BigInteger next, out long s, out BigInteger S)
        {
            BigInteger M; long sinit;
            if (PopCount(N) % 2 == 0) { M = 4 * N; sinit = 0; }
            else { M = 16 * N + 1; sinit = -1; }
            S = AltBitSum(M);
            s = sinit + PopCount(S) - PopCount(M) / 2;
            if (s < 2) { next = BigInteger.Zero; return false; }
            BigInteger p = BigInteger.Pow(16, (int)(s - 2));
            next = 2 * S * p + 6 * ((p - 1) / 15);
            return true;
        }
    }

    // -----------------------------------------------------------------------------------
    //  V(L) : binary value of a string. Only feasible for short strings; guarded.
    // -----------------------------------------------------------------------------------
    public static class Value
    {
        public const int MaxBitsForExact = 200_000; // ~60k decimal digits; above this we report length only

        public static bool TryBits(string bits, out BigInteger v)
        {
            v = BigInteger.Zero;
            if (bits.Length > MaxBitsForExact) return false;
            foreach (char c in bits) { v <<= 1; if (c == '1') v += 1; }
            return true;
        }

        // approximate decimal-digit count of a value with `lengthBits` significant bits
        public static long DecimalDigits(long lengthBits) =>
            (long)Math.Floor(lengthBits * 0.30102999566398114) + 1;
    }

    // -----------------------------------------------------------------------------------
    //  Trajectory runner. Drives whichever engine and yields one StepInfo per index.
    // -----------------------------------------------------------------------------------
    public sealed class TrajectoryRunner
    {
        public string Seed;
        public int MaxSteps = 30;
        public EngineKind Engine = EngineKind.Auto;
        public bool ComputeOmega = true;
        public bool ComputeValue = true;
        public bool AllowDiskFallback = true;       // in-memory exhaustion -> continue on disk
        public int KeepBitsUpToLength = 4096;       // keep full string for display below this
        public int Cores = 1;                       // separator-write parallelism inside each step

        /// <summary>true if the gap engine spilled to disk mid-trajectory.</summary>
        public bool SpilledToDisk { get; private set; }

        /// <summary>n at which the spill happened (-1 if it did not).</summary>
        public int SpillStep { get; private set; } = -1;

        public List<StepInfo> Run(CancellationToken ct, IProgress<int> progress = null)
        {
            SpilledToDisk = false; SpillStep = -1;
            var outp = new List<StepInfo>();
            var ge = GapEngine.FromSeed(Seed);
            if (ge == null) // nu = 0 : immediate halt
            {
                outp.Add(new StepInfo { N = 0, S = 0, Nu = 0, NuEven = true, Length = Seed.Length, Halted = true });
                return outp;
            }

            EngineKind eng = Engine == EngineKind.Auto ? EngineKind.GapMemory : Engine;

            // ----- Bit engine: literal transcription, ground truth -----
            if (eng == EngineKind.Bit)
            {
                string L = Normalize(Seed);
                for (int n = 0; n <= MaxSteps; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    var info = new StepInfo { N = n, Length = L.Length };
                    long nuTmp = 0; for (int i = 0; i < L.Length; i++) if (L[i] == '1') nuTmp++;
                    info.Nu = nuTmp; info.NuEven = (nuTmp % 2 == 0);
                    if (ComputeOmega) info.Omega = OmegaOf(L);
                    if (L.Length <= KeepBitsUpToLength) info.Bits = L;
                    if (ComputeValue && Value.TryBits(L, out var v)) { info.Value = v; info.HasValue = true; }
                    string nxt = BitEngine.Step(L, out bool halted, out long s, out long _);
                    info.S = s; info.Halted = halted;
                    outp.Add(info); progress?.Report(n);
                    if (halted) break;
                    L = nxt;
                }
                return outp;
            }

            // ----- Unified gap loop: starts on whichever the caller picked, can spill to disk -----
            DiskGapEngine de = null;
            bool onDisk = eng == EngineKind.GapDisk;
            if (onDisk) de = new DiskGapEngine(ge, DiskWorkspace.NewEngineDir());

            try
            {
                for (int n = 0; n <= MaxSteps; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    StepInfo info;
                    long s; bool ok;

                    if (!onDisk)
                    {
                        info = new StepInfo { N = n, Nu = ge.Nu, NuEven = (ge.Nu % 2 == 0), Length = ge.Length };
                        if (ComputeOmega) info.Omega = ge.Omega();
                        if (info.Length <= KeepBitsUpToLength) info.Bits = Materialize(ge);
                        if (ComputeValue && info.Length <= Value.MaxBitsForExact)
                        {
                            string mat = info.Bits ?? Materialize(ge);
                            if (Value.TryBits(mat, out var vv)) { info.Value = vv; info.HasValue = true; }
                        }
                        try
                        {
                            ok = Cores > 1 ? ge.ParallelStep(Cores, out s, out _) : ge.Step(out s, out _);
                        }
                        catch (Exception) when (AllowDiskFallback)
                        {
                            // ge.Step throws BEFORE mutating state, so ge is still valid -> hand off to disk.
                            de = new DiskGapEngine(ge, DiskWorkspace.NewEngineDir());
                            onDisk = true;
                            SpilledToDisk = true; SpillStep = n;
                            ok = de.Step(out s, out _);
                        }
                    }
                    else
                    {
                        info = new StepInfo { N = n, Nu = de.Nu, NuEven = (de.Nu % 2 == 0) };
                        info.Length = de.ComputeLength();
                        if (ComputeOmega) info.Omega = de.ComputeOmega();
                        ok = de.Step(out s, out _);
                    }

                    info.S = s; info.Halted = !ok;
                    outp.Add(info); progress?.Report(n);
                    if (!ok) break;
                }
            }
            finally
            {
                de?.Dispose();
            }
            return outp;
        }

        public Decomposition DecomposeAt(int targetN, CancellationToken ct, IProgress<int> progress = null)
        {
            SpilledToDisk = false; SpillStep = -1;
            var ge = GapEngine.FromSeed(Seed);
            if (ge == null) return null;

            DiskGapEngine de = null;
            bool onDisk = (Engine == EngineKind.GapDisk);
            if (onDisk) de = new DiskGapEngine(ge, DiskWorkspace.NewEngineDir());
            try
            {
                for (int n = 0; n < targetN; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    bool ok;
                    if (!onDisk)
                    {
                        try
                        {
                            ok = Cores > 1 ? ge.ParallelStep(Cores, out _, out _) : ge.Step(out _, out _);
                        }
                        catch (Exception) when (AllowDiskFallback)
                        {
                            de = new DiskGapEngine(ge, DiskWorkspace.NewEngineDir());
                            onDisk = true;
                            SpilledToDisk = true; SpillStep = n;
                            ok = de.Step(out _, out _);
                        }
                    }
                    else
                    {
                        ok = de.Step(out _, out _);
                    }
                    progress?.Report(n);
                    if (!ok) return null;
                }
                return onDisk ? de.Decompose(targetN) : ge.Decompose(targetN);
            }
            finally
            {
                de?.Dispose();
            }
        }

        public static string Normalize(string seed)
        {
            int first = -1;
            for (int i = 0; i < seed.Length; i++) if (seed[i] == '1') { first = i; break; }
            return first < 0 ? "" : seed.Substring(first);
        }

        private static long OmegaOf(string L)
        {
            long omega = 0, run = 0;
            for (int i = 0; i < L.Length; i++)
            {
                if (L[i] == '1') run++;
                else { if ((run & 1) == 1) omega++; run = 0; }
            }
            if ((run & 1) == 1) omega++;
            return omega;
        }

        private static string Materialize(GapEngine ge)
        {
            var sb = new StringBuilder();
            sb.Append('1');
            for (int i = 0; i < ge.Gaps.Length; i++) { sb.Append('0', ge.Gaps[i]); sb.Append('1'); }
            sb.Append('0', ge.T);
            return sb.ToString();
        }
    }

    // -----------------------------------------------------------------------------------
    //  Multicore seed survey. Runs every seed of a given length range forward (in parallel
    //  across seeds) and gathers the Table 2 / Table 3 statistics.
    // -----------------------------------------------------------------------------------
    /// <summary>Why a seed did not halt within the run. None = halted.</summary>
    public enum NonHaltingReason { None, StepCap, NuCap, ResourceLimit }

    /// <summary>Per-seed outcome, collected for the listing.</summary>
    public sealed class SeedOutcome
    {
        public string Seed;
        public int Length;                   // |L_0|, for filtering
        public bool Halted;
        public int HaltStep;
        public long HaltCounter;
        public NonHaltingReason Reason;      // meaningful when !Halted

        public string Outcome => Halted
            ? $"halt @ step {HaltStep}  (s={HaltCounter})"
            : Reason switch
            {
                NonHaltingReason.NuCap         => "non-halting (ν cap hit)",
                NonHaltingReason.ResourceLimit => "non-halting (memory limit)",
                NonHaltingReason.StepCap       => "non-halting (step cap)",
                _                              => "non-halting"
            };

        public override string ToString() => Seed + "   ->   " + Outcome;
    }

    public sealed class SurveyResult
    {
        public int LengthFrom, LengthTo;
        public long TotalSeeds;
        public List<SeedOutcome> Seeds = new List<SeedOutcome>(); // possibly partial when too many
        public bool SeedsComplete;                                // true if every seed is in Seeds
        public long TotalCollected;                               // number of seeds actually in Seeds

        public long Halting, NonHalting;
        public long NuCapped;        // hit the (finite) ν cap
        public long ResourceCapped;  // in-memory engine ran out (or disk also ran out)
        public long StepCapped;      // ran past maxSteps without halting

        public long HaltStep0, HaltStep1;
        public int MaxHaltStep;
        public long HaltCounter0, HaltCounter1;
        public long FirstHaltN2Plus;          // first halts with N >= 2
        public long FirstHaltViolatingBound;  // those violating s_{N-2} <= 5
        public long MonotonicityViolators;    // non-halting seeds with some s_n < s_{n-2}
        public long Grazers;                  // non-halting seeds with g(L0) >= 1  (some s_n == 2, n>=1)
        public int MaxGrazeMultiplicity;
        public Dictionary<int, long> HaltByLength = new Dictionary<int, long>();
        public Dictionary<int, long> TotalByLength = new Dictionary<int, long>();
        public double ElapsedSeconds;

        public double HaltFraction => TotalSeeds == 0 ? 0 : (double)Halting / TotalSeeds;

        public long NonHaltingOfLength(int len)
        {
            long t = TotalByLength.TryGetValue(len, out var tot) ? tot : 0;
            long h = HaltByLength.TryGetValue(len, out var hh) ? hh : 0;
            return t - h;
        }
    }

    /// <summary>Atomic-incrementable cell used to share a counter across parallel workers.</summary>
    internal sealed class CounterCell { public long Value; }

    public static class SeedSurvey
    {
        /// <param name="lenFrom">smallest seed length (seeds begin with 1)</param>
        /// <param name="lenTo">largest seed length</param>
        /// <param name="maxSteps">cap on simulated steps per seed</param>
        /// <param name="maxNu">cap on nu; pass long.MaxValue for unlimited</param>
        /// <param name="maxCores">degree of parallelism (1..Environment.ProcessorCount)</param>
        /// <param name="collectCap">max number of seeds to retain in the per-seed list (rest classified anyway)</param>
        /// <param name="engine">classifier engine: GapMemory or Bit</param>
        /// <param name="allowSpill">on in-memory exhaustion, continue that seed on disk (uses scratch space)</param>
        public static SurveyResult Run(int lenFrom, int lenTo, int maxSteps, long maxNu,
                                       int maxCores, long collectCap, EngineKind engine, bool allowSpill,
                                       CancellationToken ct, IProgress<double> progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new SurveyResult { LengthFrom = lenFrom, LengthTo = lenTo };
            var sync = new object();

            long totalWork = 0;
            for (int len = lenFrom; len <= lenTo; len++) totalWork += 1L << (len - 1);
            long done = 0;
            if (maxCores < 1) maxCores = 1;
            if (maxCores > Environment.ProcessorCount) maxCores = Environment.ProcessorCount;

            // Always collect up to collectCap seeds (smallest-length first, since we iterate len ascending).
            // A shared atomic counter caps the per-seed list across all threads.
            var collected = new CounterCell();

            for (int len = lenFrom; len <= lenTo; len++)
            {
                long count = 1L << (len - 1);    // seeds of this length begin with 1
                long haltThisLen = 0;
                int lenCapture = len;

                Parallel.For(0L, count,
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxCores },
                    () => new SurveyResult(),
                    (idx, state, acc) =>
                    {
                        var sb = new StringBuilder(lenCapture);
                        sb.Append('1');
                        for (int b = lenCapture - 2; b >= 0; b--) sb.Append(((idx >> b) & 1) == 1 ? '1' : '0');
                        string seed = sb.ToString();

                        // willCollect true while we still have room in the global cap
                        bool willCollect = Interlocked.Increment(ref collected.Value) <= collectCap;

                        if (engine == EngineKind.Bit) ClassifyBit(seed, lenCapture, maxSteps, maxNu, willCollect, acc);
                        else                          ClassifyGap(seed, lenCapture, maxSteps, maxNu, willCollect, allowSpill, acc);

                        long d = Interlocked.Increment(ref done);
                        if ((d & 0x3FFF) == 0) progress?.Report((double)d / totalWork);
                        return acc;
                    },
                    acc => { lock (sync) { Merge(res, acc); haltThisLen += acc.Halting; } });

                lock (sync)
                {
                    res.TotalByLength[lenCapture] = count;
                    res.HaltByLength[lenCapture] = haltThisLen;
                }
            }

            res.Seeds.Sort((a, b) =>
            {
                int c = a.Length.CompareTo(b.Length);
                return c != 0 ? c : string.CompareOrdinal(a.Seed, b.Seed);
            });
            res.TotalCollected = res.Seeds.Count;
            res.SeedsComplete = res.TotalCollected == res.TotalSeeds;
            res.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        // ---- gap-engine classification, optionally spilling to disk on memory pressure ----
        private static void ClassifyGap(string seed, int seedLen, int maxSteps, long maxNu,
                                        bool collect, bool allowSpill, SurveyResult acc)
        {
            acc.TotalSeeds++;
            var outcome = collect ? new SeedOutcome { Seed = seed, Length = seedLen } : null;

            var ge = GapEngine.FromSeed(seed);
            if (ge == null)
            {
                acc.Halting++; acc.HaltStep0++; acc.HaltCounter0++;
                if (outcome != null) { outcome.Halted = true; outcome.HaltStep = 0; outcome.HaltCounter = 0; acc.Seeds.Add(outcome); }
                return;
            }

            DiskGapEngine de = null;
            bool onDisk = false;
            bool violated = false;
            int graze = 0;
            var sHist = new List<long>(Math.Min(maxSteps, 64));

            try
            {
                for (int n = 0; n <= maxSteps; n++)
                {
                    long nuNow = onDisk ? de.Nu : ge.Nu;
                    if (nuNow > maxNu)
                    {
                        acc.NonHalting++; acc.NuCapped++;
                        if (outcome != null) outcome.Reason = NonHaltingReason.NuCap;
                        goto post;
                    }

                    long s; bool ok;
                    if (!onDisk)
                    {
                        try { ok = ge.Step(out s, out _); }
                        catch (Exception)
                        {
                            if (allowSpill)
                            {
                                try
                                {
                                    de = new DiskGapEngine(ge, DiskWorkspace.NewEngineDir());
                                    onDisk = true;
                                    ok = de.Step(out s, out _);
                                }
                                catch (Exception)
                                {
                                    acc.NonHalting++; acc.ResourceCapped++;
                                    if (outcome != null) outcome.Reason = NonHaltingReason.ResourceLimit;
                                    goto post;
                                }
                            }
                            else
                            {
                                acc.NonHalting++; acc.ResourceCapped++;
                                if (outcome != null) outcome.Reason = NonHaltingReason.ResourceLimit;
                                goto post;
                            }
                        }
                    }
                    else
                    {
                        try { ok = de.Step(out s, out _); }
                        catch (Exception)
                        {
                            acc.NonHalting++; acc.ResourceCapped++;
                            if (outcome != null) outcome.Reason = NonHaltingReason.ResourceLimit;
                            goto post;
                        }
                    }

                    sHist.Add(s);
                    if (!ok)
                    {
                        acc.Halting++;
                        if (n == 0) acc.HaltStep0++;
                        else if (n == 1) acc.HaltStep1++;
                        if (n > acc.MaxHaltStep) acc.MaxHaltStep = n;
                        if (s == 0) acc.HaltCounter0++; else if (s == 1) acc.HaltCounter1++;
                        if (n >= 2)
                        {
                            acc.FirstHaltN2Plus++;
                            long sN2 = sHist[n - 2];
                            if (sN2 > 5) acc.FirstHaltViolatingBound++;
                        }
                        if (outcome != null) { outcome.Halted = true; outcome.HaltStep = n; outcome.HaltCounter = s; acc.Seeds.Add(outcome); }
                        return;
                    }

                    if (n >= 1 && s == 2) graze++;
                    if (n >= 2 && sHist[n] < sHist[n - 2]) violated = true;
                }
                acc.NonHalting++; acc.StepCapped++;
                if (outcome != null) outcome.Reason = NonHaltingReason.StepCap;
            post:
                if (violated) acc.MonotonicityViolators++;
                if (graze >= 1) { acc.Grazers++; if (graze > acc.MaxGrazeMultiplicity) acc.MaxGrazeMultiplicity = graze; }
                if (outcome != null) acc.Seeds.Add(outcome);
            }
            finally { de?.Dispose(); }
        }

        // ---- bit-engine classification (no spill — strings cannot stream to disk) ----
        private static void ClassifyBit(string seed, int seedLen, int maxSteps, long maxNu, bool collect, SurveyResult acc)
        {
            acc.TotalSeeds++;
            var outcome = collect ? new SeedOutcome { Seed = seed, Length = seedLen } : null;

            string L = TrajectoryRunner.Normalize(seed);
            if (L.Length == 0)
            {
                acc.Halting++; acc.HaltStep0++; acc.HaltCounter0++;
                if (outcome != null) { outcome.Halted = true; outcome.HaltStep = 0; outcome.HaltCounter = 0; acc.Seeds.Add(outcome); }
                return;
            }

            bool violated = false;
            int graze = 0;
            var sHist = new List<long>(Math.Min(maxSteps, 64));

            for (int n = 0; n <= maxSteps; n++)
            {
                long nuL = 0; for (int i = 0; i < L.Length; i++) if (L[i] == '1') nuL++;
                if (nuL > maxNu)
                {
                    acc.NonHalting++; acc.NuCapped++;
                    if (outcome != null) outcome.Reason = NonHaltingReason.NuCap;
                    goto post;
                }

                string nxt; bool halted; long s;
                try { nxt = BitEngine.Step(L, out halted, out s, out long _); }
                catch (Exception)
                {
                    acc.NonHalting++; acc.ResourceCapped++;
                    if (outcome != null) outcome.Reason = NonHaltingReason.ResourceLimit;
                    goto post;
                }
                sHist.Add(s);

                if (halted)
                {
                    acc.Halting++;
                    if (n == 0) acc.HaltStep0++;
                    else if (n == 1) acc.HaltStep1++;
                    if (n > acc.MaxHaltStep) acc.MaxHaltStep = n;
                    if (s == 0) acc.HaltCounter0++; else if (s == 1) acc.HaltCounter1++;
                    if (n >= 2)
                    {
                        acc.FirstHaltN2Plus++;
                        long sN2 = sHist[n - 2];
                        if (sN2 > 5) acc.FirstHaltViolatingBound++;
                    }
                    if (outcome != null) { outcome.Halted = true; outcome.HaltStep = n; outcome.HaltCounter = s; acc.Seeds.Add(outcome); }
                    return;
                }

                if (n >= 1 && s == 2) graze++;
                if (n >= 2 && sHist[n] < sHist[n - 2]) violated = true;
                L = nxt;
            }
            acc.NonHalting++; acc.StepCapped++;
            if (outcome != null) outcome.Reason = NonHaltingReason.StepCap;
        post:
            if (violated) acc.MonotonicityViolators++;
            if (graze >= 1) { acc.Grazers++; if (graze > acc.MaxGrazeMultiplicity) acc.MaxGrazeMultiplicity = graze; }
            if (outcome != null) acc.Seeds.Add(outcome);
        }

        private static void Merge(SurveyResult a, SurveyResult b)
        {
            a.TotalSeeds += b.TotalSeeds;
            a.Halting += b.Halting; a.NonHalting += b.NonHalting;
            a.NuCapped += b.NuCapped; a.ResourceCapped += b.ResourceCapped; a.StepCapped += b.StepCapped;
            a.HaltStep0 += b.HaltStep0; a.HaltStep1 += b.HaltStep1;
            a.MaxHaltStep = Math.Max(a.MaxHaltStep, b.MaxHaltStep);
            a.HaltCounter0 += b.HaltCounter0; a.HaltCounter1 += b.HaltCounter1;
            a.FirstHaltN2Plus += b.FirstHaltN2Plus;
            a.FirstHaltViolatingBound += b.FirstHaltViolatingBound;
            a.MonotonicityViolators += b.MonotonicityViolators;
            a.Grazers += b.Grazers;
            a.MaxGrazeMultiplicity = Math.Max(a.MaxGrazeMultiplicity, b.MaxGrazeMultiplicity);
            if (b.Seeds.Count > 0) a.Seeds.AddRange(b.Seeds);
        }
    }
}
