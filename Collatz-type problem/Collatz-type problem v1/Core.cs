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
            int w = 0;
            for (long j = 1; j <= m; j++)
            {
                int eOdd = PG((int)(2 * (j - 1)));
                for (int z = 0; z < eOdd; z++) ng[w++] = 0;
                if (j < m) ng[w++] = 1 + PG((int)(2 * j - 1));
            }
            int newT;
            if (ss == 2) { newT = bodyTz; }
            else
            {
                ng[w++] = bodyTz + 1;
                ng[w++] = 0;
                for (long r = 1; r < ss - 2; r++) { ng[w++] = 2; ng[w++] = 0; }
                newT = 1;
            }
            Gaps = ng; T = newT;
            return true;
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

        public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }
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
        public int KeepBitsUpToLength = 4096; // keep full string for display below this
        public string DiskWorkDir;

        public List<StepInfo> Run(CancellationToken ct, IProgress<int> progress = null)
        {
            var outp = new List<StepInfo>();
            var ge = GapEngine.FromSeed(Seed);
            if (ge == null) // nu = 0 : immediate halt
            {
                outp.Add(new StepInfo { N = 0, S = 0, Nu = 0, NuEven = true, Length = Seed.Length, Halted = true });
                return outp;
            }

            // choose engine
            EngineKind eng = Engine;
            if (eng == EngineKind.Auto) eng = EngineKind.GapMemory;

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
                    string nxt = BitEngine.Step(L, out bool halted, out long s, out long nu);
                    info.S = s; info.Halted = halted;
                    outp.Add(info);
                    progress?.Report(n);
                    if (halted) break;
                    L = nxt;
                }
                return outp;
            }

            if (eng == EngineKind.GapDisk)
            {
                using (var de = new DiskGapEngine(ge, DiskWorkDir ?? Path.Combine(Path.GetTempPath(), "BinaryRewriteStudio")))
                {
                    for (int n = 0; n <= MaxSteps; n++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var info = new StepInfo { N = n, Nu = de.Nu, NuEven = (de.Nu % 2 == 0) };
                        info.Length = de.ComputeLength();
                        if (ComputeOmega) info.Omega = de.ComputeOmega();
                        bool ok = de.Step(out long s, out bool _);
                        info.S = s; info.Halted = !ok;
                        outp.Add(info);
                        progress?.Report(n);
                        if (!ok) break;
                    }
                }
                return outp;
            }

            // in-memory gap engine (default)
            for (int n = 0; n <= MaxSteps; n++)
            {
                ct.ThrowIfCancellationRequested();
                var info = new StepInfo { N = n, Nu = ge.Nu, NuEven = (ge.Nu % 2 == 0) };
                info.Length = ge.Length;
                if (ComputeOmega) info.Omega = ge.Omega();
                if (info.Length <= KeepBitsUpToLength) info.Bits = Materialize(ge);
                if (ComputeValue && info.Length <= Value.MaxBitsForExact && Value.TryBits(Materialize(ge), out var v))
                { info.Value = v; info.HasValue = true; }
                bool ok = ge.Step(out long s, out bool _);
                info.S = s; info.Halted = !ok;
                outp.Add(info);
                progress?.Report(n);
                if (!ok) break;
            }
            return outp;
        }

        public Decomposition DecomposeAt(int targetN, CancellationToken ct)
        {
            var ge = GapEngine.FromSeed(Seed);
            if (ge == null) return null;
            for (int n = 0; n < targetN; n++)
            {
                ct.ThrowIfCancellationRequested();
                if (!ge.Step(out _, out _)) return null;
            }
            return ge.Decompose(targetN);
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
    public sealed class SurveyResult
    {
        public int LengthFrom, LengthTo;
        public long TotalSeeds;
        public long Halting, NonHalting;
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
    }

    public static class SeedSurvey
    {
        /// <param name="lenFrom">smallest seed length (seeds begin with 1)</param>
        /// <param name="lenTo">largest seed length</param>
        /// <param name="maxSteps">cap on simulated steps per seed</param>
        /// <param name="maxNu">cap on nu; a seed exceeding it is treated as presumed non-halting</param>
        public static SurveyResult Run(int lenFrom, int lenTo, int maxSteps, long maxNu,
                                       CancellationToken ct, IProgress<double> progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new SurveyResult { LengthFrom = lenFrom, LengthTo = lenTo };
            var sync = new object();

            long totalWork = 0;
            for (int len = lenFrom; len <= lenTo; len++) totalWork += 1L << (len - 1);
            long done = 0;

            for (int len = lenFrom; len <= lenTo; len++)
            {
                long count = 1L << (len - 1);    // seeds of this length begin with 1
                long haltThisLen = 0;
                var local = new SurveyResult();

                Parallel.For(0L, count,
                    new ParallelOptions { CancellationToken = ct },
                    () => new SurveyResult(),
                    (idx, state, acc) =>
                    {
                        // build seed string: leading 1 then (len-1) bits of idx
                        var sb = new StringBuilder(len);
                        sb.Append('1');
                        for (int b = len - 2; b >= 0; b--) sb.Append(((idx >> b) & 1) == 1 ? '1' : '0');
                        Classify(sb.ToString(), maxSteps, maxNu, acc);

                        long d = Interlocked.Increment(ref done);
                        if ((d & 0xFFFF) == 0) progress?.Report((double)d / totalWork);
                        return acc;
                    },
                    acc => { lock (sync) { Merge(res, acc); haltThisLen += acc.Halting; } });

                lock (sync)
                {
                    res.TotalByLength[len] = count;
                    res.HaltByLength[len] = haltThisLen;
                }
            }

            res.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        private static void Classify(string seed, int maxSteps, long maxNu, SurveyResult acc)
        {
            acc.TotalSeeds++;
            var ge = GapEngine.FromSeed(seed);
            if (ge == null) // nu = 0 -> immediate halt, counter 0
            {
                acc.TotalSeeds--; // all-zero strings aren't valid seeds (don't begin with 1) - skip
                acc.TotalSeeds++; // keep count consistent; treat as step-0 halt s=0
                acc.Halting++; acc.HaltStep0++; acc.HaltCounter0++;
                return;
            }

            long prevEven = long.MinValue, prevOdd = long.MinValue; // s_{n-2} by parity of index
            bool violated = false;
            int graze = 0;
            var sHist = new List<long>(Math.Min(maxSteps, 64));

            for (int n = 0; n <= maxSteps; n++)
            {
                if (ge.Nu > maxNu) { acc.NonHalting++; goto post; }
                bool ok = ge.Step(out long s, out bool _);
                sHist.Add(s);

                if (!ok)
                {
                    acc.Halting++;
                    if (n == 0) { acc.HaltStep0++; }
                    else if (n == 1) { acc.HaltStep1++; }
                    if (n > acc.MaxHaltStep) acc.MaxHaltStep = n;
                    if (s == 0) acc.HaltCounter0++; else if (s == 1) acc.HaltCounter1++;
                    if (n >= 2)
                    {
                        acc.FirstHaltN2Plus++;
                        long sN2 = sHist[n - 2];
                        if (sN2 > 5) acc.FirstHaltViolatingBound++;
                    }
                    return;
                }

                if (n >= 1 && s == 2) graze++;
                // two-step monotonicity check s_n >= s_{n-2}
                if (n >= 2 && sHist[n] < sHist[n - 2]) violated = true;
            }
            acc.NonHalting++; // ran out of steps without halting -> presumed non-halting
        post:
            if (violated) acc.MonotonicityViolators++;
            if (graze >= 1) { acc.Grazers++; if (graze > acc.MaxGrazeMultiplicity) acc.MaxGrazeMultiplicity = graze; }
        }

        private static void Merge(SurveyResult a, SurveyResult b)
        {
            a.TotalSeeds += b.TotalSeeds;
            a.Halting += b.Halting; a.NonHalting += b.NonHalting;
            a.HaltStep0 += b.HaltStep0; a.HaltStep1 += b.HaltStep1;
            a.MaxHaltStep = Math.Max(a.MaxHaltStep, b.MaxHaltStep);
            a.HaltCounter0 += b.HaltCounter0; a.HaltCounter1 += b.HaltCounter1;
            a.FirstHaltN2Plus += b.FirstHaltN2Plus;
            a.FirstHaltViolatingBound += b.FirstHaltViolatingBound;
            a.MonotonicityViolators += b.MonotonicityViolators;
            a.Grazers += b.Grazers;
            a.MaxGrazeMultiplicity = Math.Max(a.MaxGrazeMultiplicity, b.MaxGrazeMultiplicity);
        }
    }
}
