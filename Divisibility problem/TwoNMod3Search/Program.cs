// Program.cs — Search for n with (n+1) | 2^n + 3
//
// See README.md for usage and design notes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace TwoNMod3Search;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length < 2 ? 2 : 0;
        }

        if (!long.TryParse(args[0], out long startN) || startN < 1)
        {
            Console.Error.WriteLine("error: startN must be a positive integer.");
            return 2;
        }
        if (!long.TryParse(args[1], out long endN) || endN < startN)
        {
            Console.Error.WriteLine("error: endN must be >= startN.");
            return 2;
        }
        // m = n + 1 must fit in a 63-bit unsigned for our Montgomery code (m < 2^63).
        if (endN >= (1L << 62))
        {
            Console.Error.WriteLine("error: endN too large; supported up to 2^62 - 1.");
            return 2;
        }

        int cores;
        int defaultCores = Math.Max(1, Environment.ProcessorCount - 2);
        if (args.Length >= 3)
        {
            if (!int.TryParse(args[2], out cores) || cores < 1)
            {
                Console.Error.WriteLine("error: cores must be a positive integer.");
                return 2;
            }
            cores = Math.Min(cores, Environment.ProcessorCount);
        }
        else
        {
            cores = defaultCores;
        }

        return new Engine(startN, endN, cores).Run();
    }

    static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: TwoNMod3Search <startN> <endN> [cores]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Searches integers n in [startN, endN] for which");
        Console.Error.WriteLine("    2^n  ==  -3  (mod n+1)");
        Console.Error.WriteLine("equivalently  (n+1) | 2^n + 3.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Arguments:");
        Console.Error.WriteLine("  startN   inclusive lower bound (>= 1)");
        Console.Error.WriteLine("  endN     inclusive upper bound (< 2^62)");
        Console.Error.WriteLine("  cores    optional thread count (default: ProcessorCount - 2)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Controls during run:");
        Console.Error.WriteLine("  Enter    pause / resume");
        Console.Error.WriteLine("  Ctrl+C   stop and report progress");
    }
}

/// <summary>
/// Coordinates the search: chunked work distribution, per-thread progress tracking,
/// pause / resume, cancellation, and result reporting.
/// </summary>
public sealed class Engine
{
    readonly long _startN;
    readonly long _endN;
    readonly int _cores;

    readonly CancellationTokenSource _cts = new();
    readonly ManualResetEventSlim _runEvent = new(initialState: true);
    readonly object _outputLock = new();

    /// <summary>Next chunk's starting n. Advanced atomically by workers.</summary>
    long _nextChunkStart;

    /// <summary>Per-thread last-checked n (atomic 64-bit reads/writes).</summary>
    readonly long[] _threadLastN;

    /// <summary>Per-thread number of n values examined (for rate reporting).</summary>
    readonly long[] _threadCount;

    // Wheel mod 2310 = 2 * 3 * 5 * 7 * 11.
    // The primes 5, 7, 11 are inadmissible (a prime p is inadmissible iff any
    // m with p | m fails the congruence; see paper §5). 2 and 3 are excluded
    // by Proposition 4. Therefore m must be coprime to 2310.
    const int WheelMod = 2310;
    readonly int[] _wheelResidues;
    readonly int[] _wheelDeltas;
    readonly int _wheelLen;

    // Each chunk covers ChunkN values of n. Multiple of WheelMod for clean
    // wheel alignment; large enough to amortise atomic chunk-fetch overhead;
    // small enough that pause/cancel feel responsive (~30 ms typical).
    const long ChunkN = 256L * WheelMod;

    /// <summary>
    /// Additional per-prime filter table for primes 13..97. Each entry contains
    /// the order d of 2 mod p, the discrete log k of -3 in &lt;2&gt; if it
    /// exists, and an "admissible" flag (-3 is in &lt;2&gt; AND the parity /
    /// mod-3 conditions on k hold). For each candidate m and each entry:
    ///
    ///   if p | m and !admissible      : reject (no valid m can have this p as factor)
    ///   if p | m and  admissible      : require (m-1) mod d == k
    ///   if p does not divide m        : no constraint
    /// </summary>
    readonly SmallPrime[] _smallPrimes;

    readonly struct SmallPrime
    {
        public readonly uint P;
        public readonly uint D;
        public readonly uint K;
        public readonly bool Admissible;
        public SmallPrime(uint p, uint d, uint k, bool adm) { P = p; D = d; K = k; Admissible = adm; }
    }

    readonly string _resultsPath;

    public Engine(long startN, long endN, int cores)
    {
        _startN = startN;
        _endN = endN;
        _cores = cores;
        _nextChunkStart = startN;
        _threadLastN = new long[cores];
        _threadCount = new long[cores];
        Array.Fill(_threadLastN, -1L);

        // Build wheel: residues r in [0, 2310) with gcd(r, 2310) = 1.
        var residues = new List<int>(480);
        for (int r = 1; r < WheelMod; r++)
        {
            if (r % 2 == 0) continue;
            if (r % 3 == 0) continue;
            if (r % 5 == 0) continue;
            if (r % 7 == 0) continue;
            if (r % 11 == 0) continue;
            residues.Add(r);
        }
        _wheelResidues = residues.ToArray();
        _wheelLen = _wheelResidues.Length; // 480
        _wheelDeltas = new int[_wheelLen];
        for (int i = 0; i < _wheelLen; i++)
        {
            int next = (i + 1 < _wheelLen) ? _wheelResidues[i + 1] : _wheelResidues[0] + WheelMod;
            _wheelDeltas[i] = next - _wheelResidues[i];
        }

        // Build small-prime filter table.
        int[] basePrimes = { 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97 };
        var sp = new List<SmallPrime>();
        foreach (int p in basePrimes)
        {
            uint d = OrderModP(2u, (uint)p);
            int k = DiscreteLog(2u, (uint)((p - 3) % p), (uint)p, d);
            bool admissible;
            if (k < 0)
            {
                admissible = false; // -3 is not a power of 2 mod p
            }
            else
            {
                bool parityOk = (d % 2u != 0u) || ((uint)k % 2u == 0u);
                bool mod3Ok = (d % 3u != 0u) || ((uint)k % 3u != 2u);
                admissible = parityOk && mod3Ok;
            }
            sp.Add(new SmallPrime((uint)p, d, (uint)Math.Max(k, 0), admissible));
        }
        _smallPrimes = sp.ToArray();

        _resultsPath = Path.Combine(Environment.CurrentDirectory, "results.txt");
    }

    static uint OrderModP(uint a, uint p)
    {
        ulong x = 1, d = 0;
        do { x = x * a % p; d++; } while (x != 1);
        return (uint)d;
    }

    static int DiscreteLog(uint g, uint target, uint p, uint d)
    {
        ulong x = 1;
        for (uint i = 0; i < d; i++)
        {
            if (x == target) return (int)i;
            x = x * g % p;
        }
        return -1;
    }

    public int Run()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // we handle shutdown ourselves
            if (!_cts.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("[ctrl+c] stopping; waiting for workers to exit current chunks...");
                _cts.Cancel();
                _runEvent.Set(); // unblock any paused workers
            }
        };

        var inputThread = new Thread(InputLoop) { IsBackground = true, Name = "input" };
        inputThread.Start();

        Console.WriteLine($"range  : n in [{_startN}, {_endN}]   ({_endN - _startN + 1} values)");
        Console.WriteLine($"cores  : {_cores}");
        Console.WriteLine($"output : {_resultsPath}");
        Console.WriteLine("press <Enter> to pause/resume, Ctrl+C to stop.");
        Console.WriteLine();

        var totalSw = Stopwatch.StartNew();
        var reporter = new Thread(ReporterLoop) { IsBackground = true, Name = "reporter" };
        reporter.Start();

        var workers = new Task[_cores];
        for (int i = 0; i < _cores; i++)
        {
            int id = i;
            workers[i] = Task.Run(() => Worker(id));
        }

        try { Task.WaitAll(workers); }
        catch (AggregateException ae)
        {
            foreach (var inner in ae.Flatten().InnerExceptions)
                if (inner is not OperationCanceledException)
                    Console.Error.WriteLine(inner);
        }

        totalSw.Stop();

        // Final summary
        Console.WriteLine();
        Console.WriteLine("---- final summary ----");
        long totalCount = 0;
        long minLast = long.MaxValue;
        for (int i = 0; i < _cores; i++)
        {
            long last = Interlocked.Read(ref _threadLastN[i]);
            long cnt = Interlocked.Read(ref _threadCount[i]);
            totalCount += cnt;
            if (last >= 0 && last < minLast) minLast = last;
            Console.WriteLine($"  thread {i,2}: last n = {(last >= 0 ? last.ToString() : "-")}, examined = {cnt}");
        }
        Console.WriteLine($"  total examined : {totalCount}");
        if (minLast != long.MaxValue)
            Console.WriteLine($"  contiguous lower bound on processed n : {minLast}");
        double secs = totalSw.Elapsed.TotalSeconds;
        if (secs > 0 && totalCount > 0)
            Console.WriteLine($"  rate : {totalCount / secs:N0} n/s ({totalCount / secs / _cores:N0} per core)");
        Console.WriteLine($"  elapsed : {secs:F2} s");
        return 0;
    }

    void InputLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            string line;
            try { line = Console.ReadLine(); }
            catch { return; }
            if (line is null) return; // EOF (e.g. piped input)

            if (_runEvent.IsSet)
            {
                _runEvent.Reset();
                Console.WriteLine("[paused] press <Enter> to resume.");
            }
            else
            {
                _runEvent.Set();
                Console.WriteLine("[resumed]");
            }
        }
    }

    void ReporterLoop()
    {
        long lastTotal = 0;
        var sw = Stopwatch.StartNew();
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { Task.Delay(5000, token).Wait(token); }
            catch { return; }
            if (!_runEvent.IsSet) continue;

            long total = 0;
            long maxLast = -1;
            for (int i = 0; i < _cores; i++)
            {
                total += Interlocked.Read(ref _threadCount[i]);
                long lst = Interlocked.Read(ref _threadLastN[i]);
                if (lst > maxLast) maxLast = lst;
            }
            double dt = sw.Elapsed.TotalSeconds;
            sw.Restart();
            long delta = total - lastTotal;
            lastTotal = total;
            double rate = dt > 0 ? delta / dt : 0;
            Console.WriteLine(
                $"[progress] frontier n = {maxLast}, examined = {total:N0}, rate = {rate:N0} n/s");
        }
    }

    void Worker(int id)
    {
        var token = _cts.Token;
        try
        {
            while (true)
            {
                _runEvent.Wait(token);
                if (token.IsCancellationRequested) break;

                long chunkStart = Interlocked.Add(ref _nextChunkStart, ChunkN) - ChunkN;
                if (chunkStart > _endN) break;
                long chunkEnd = Math.Min(chunkStart + ChunkN - 1, _endN);

                ProcessChunk(id, chunkStart, chunkEnd, token);
            }
        }
        catch (OperationCanceledException)
        {
            // expected when paused -> cancelled
        }
    }

    void ProcessChunk(int id, long nStart, long nEnd, CancellationToken token)
    {
        // Iterate over m = n + 1 in [nStart + 1, nEnd + 1] using the wheel.
        long mStart = nStart + 1;
        long mEnd = nEnd + 1;

        // Position to first allowed residue >= mStart.
        long baseM = (mStart / WheelMod) * WheelMod;
        int rs = (int)(mStart - baseM);
        int idx = 0;
        while (idx < _wheelLen && _wheelResidues[idx] < rs) idx++;
        if (idx == _wheelLen) { baseM += WheelMod; idx = 0; }
        long m = baseM + _wheelResidues[idx];

        long localCount = 0;
        int sinceCheck = 0;

        while (m <= mEnd)
        {
            // Periodic pause / cancel / progress publish.
            if ((sinceCheck & 0x1FFF) == 0)
            {
                Interlocked.Exchange(ref _threadLastN[id], m - 1);
                Interlocked.Add(ref _threadCount[id], localCount);
                localCount = 0;
                if (token.IsCancellationRequested) return;
                _runEvent.Wait(token);
            }
            sinceCheck++;
            localCount++;

            if (CheckCandidate((ulong)m))
            {
                long n = m - 1;
                lock (_outputLock)
                {
                    Console.WriteLine();
                    Console.WriteLine($"*** SOLUTION  n = {n}, m = {m} ***");
                    Console.WriteLine();
                    try { File.AppendAllText(_resultsPath, $"{n}\t{m}\n"); }
                    catch (Exception ex) { Console.Error.WriteLine($"warn: writing results.txt failed: {ex.Message}"); }
                }
            }

            m += _wheelDeltas[idx];
            idx++;
            if (idx == _wheelLen) idx = 0;
        }

        // flush
        Interlocked.Exchange(ref _threadLastN[id], mEnd - 1);
        Interlocked.Add(ref _threadCount[id], localCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool CheckCandidate(ulong m)
    {
        // Small-prime filter. For each tabulated prime p:
        //   if p | m: must be admissible AND (m-1) mod d_p == k_p, else reject.
        // (If p does not divide m the entry contributes nothing.)
        var sp = _smallPrimes;
        for (int i = 0; i < sp.Length; i++)
        {
            uint p = sp[i].P;
            if (m % p == 0)
            {
                if (!sp[i].Admissible) return false;
                uint d = sp[i].D;
                uint k = sp[i].K;
                ulong e = (m - 1) % d;
                if (e != k) return false;
            }
        }

        // Full test: 2^(m-1) ≡ -3 (mod m)?
        ulong target = m - 3;
        return PowMod2(m - 1, m) == target;
    }

    // -------------------- 64-bit modular exponentiation (Montgomery) --------------------
    //
    // Computes 2^e mod m for odd m with 1 < m < 2^63.
    // Implementation: Montgomery multiplication with R = 2^64.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong PowMod2(ulong e, ulong m)
    {
        // m must be odd; we are guaranteed this by the wheel (m coprime to 2).
        ulong mInvNeg = NegInvMod64(m);

        // R mod m, where R = 2^64.  R - m·floor(R/m)  =  -m  mod m  =  (0-m) mod m.
        ulong Rmod;
        unchecked { Rmod = (0UL - m) % m; }

        // 1 in Montgomery form is R mod m.
        ulong oneMont = Rmod;

        // 2 in Montgomery form: (2 * R) mod m. Since Rmod < m < 2^63, 2*Rmod fits in ulong.
        ulong twoMont = 2UL * Rmod;
        if (twoMont >= m) twoMont -= m;

        ulong res = oneMont;

        // Find topmost bit of e.
        int top = 63;
        while (top > 0 && (e >> top) == 0) top--;

        for (int i = top; i >= 0; i--)
        {
            res = MontMul(res, res, m, mInvNeg);
            if (((e >> i) & 1UL) != 0)
                res = MontMul(res, twoMont, m, mInvNeg);
        }

        // Convert out of Montgomery form: MontMul(res, 1).
        return MontMul(res, 1UL, m, mInvNeg);
    }

    // Computes a · b · R^(-1) mod m, where R = 2^64.
    // Requires odd m with m < 2^63 and a, b < m.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MontMul(ulong a, ulong b, ulong m, ulong mInvNeg)
    {
        ulong tHi = Math.BigMul(a, b, out ulong tLo);

        ulong q;
        unchecked { q = tLo * mInvNeg; }

        ulong qmHi = Math.BigMul(q, m, out ulong qmLo);

        // The lower halves cancel (mod 2^64) by construction; we need the carry.
        ulong sumLo;
        unchecked { sumLo = tLo + qmLo; }
        ulong carry = (sumLo < tLo) ? 1UL : 0UL;

        ulong r;
        unchecked { r = tHi + qmHi + carry; }

        // The Montgomery output lies in [0, 2m); fold to [0, m).
        if (r >= m) r -= m;
        return r;
    }

    // Computes -m^(-1) mod 2^64, for odd m, via Newton-Hensel iteration.
    // Starting from x = m gives x ≡ m^(-1) (mod 8) (since m^2 ≡ 1 mod 8 for odd m).
    // Each iteration x ← x·(2 - m·x) doubles the precision: 8 → 64 → 512 → ... → 2^64.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong NegInvMod64(ulong m)
    {
        ulong x = m;
        unchecked
        {
            x *= 2UL - m * x; // accurate to 2^6
            x *= 2UL - m * x; // 2^12
            x *= 2UL - m * x; // 2^24
            x *= 2UL - m * x; // 2^48
            x *= 2UL - m * x; // 2^96 (i.e. exact in 64 bits)
            return 0UL - x;   // -m^(-1) mod 2^64
        }
    }
}