// Program.cs — Search the congruence  2^n ≡ a (mod n+1)  for an integer a, or for a
// whole range of integers a, applying the reductions of the accompanying paper
// "On the congruence 2^n ≡ a (mod n+1)".
//
// With m = n+1 the condition is  m | 2^(m-1) - a   (m ≥ 2).
//
// Reductions implemented (paper §9):
//   R1/R2  prime members & non-emptiness   (Prop 2.2, Cor 2.3)
//   R3     S_0 = {2^t : t ≥ 1}             (Prop 3.1)
//   R4     a = 2^j : infinite family m=cp  (Thm 3.2)
//   R5     S_{-1} = ∅                       (Thm 3.5)
//   §4     local admissibility sieve        (wheel + small-prime table; two-prime pre-filter)
//   §5     two-prime reduction              (Cor 5.2 / Rmk 5.3, optional --two-prime)
//   §8     single-pass over m for a range of a (one residue 2^(m-1) mod m per m)
//
// See README.md for full usage and design notes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace TwoNMod3Search;

public sealed class Options
{
    public long StartN;
    public long EndN;
    public long StartA;
    public long EndA;
    public long[] ShiftList;     // explicit set of shifts; when non-null, overrides [StartA, EndA]
    public int Cores = Math.Max(1, Environment.ProcessorCount - 2);
    public bool TwoPrime;        // also run the §5 two-prime factoring search
    public int MaxPrime = 70;    // largest smaller-prime p tried in the two-prime search
    public bool ForceSearch;     // sweep even the decided shifts (-1, 0, 2^j)
    public long MaxResults = 1_000_000; // cap on solutions printed/listed per shift
    public bool Spill = true;    // auto-spill the result buffer to disk (bounds memory)
    public long SpillBytes = 8L * 1024 * 1024; // flush the buffer once it reaches this size
}

public static class Program
{
    public static int Main(string[] args)
    {
        var positional = new List<long>();
        var opt = new Options();
        bool coresSet = false;
        List<long> shiftList = null;

        for (int i = 0; i < args.Length; i++)
        {
            string t = args[i];
            if (long.TryParse(t, out long v)) { positional.Add(v); continue; }
            if (t.IndexOf(',') >= 0)   // an explicit list of shifts, e.g. "-3,5,9,17"
            {
                if (shiftList != null) { Console.Error.WriteLine("error: give only one shift list."); return 2; }
                shiftList = new List<long>();
                foreach (var part in t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!long.TryParse(part, out long av))
                    { Console.Error.WriteLine($"error: '{part}' in the shift list is not an integer."); return 2; }
                    shiftList.Add(av);
                }
                if (shiftList.Count == 0) { Console.Error.WriteLine("error: empty shift list."); return 2; }
                continue;
            }
            switch (t)
            {
                case "-h": case "--help": PrintUsage(); return 0;
                case "-c":
                case "--cores":
                    if (++i >= args.Length || !int.TryParse(args[i], out opt.Cores) || opt.Cores < 1)
                    { Console.Error.WriteLine("error: --cores needs a positive integer."); return 2; }
                    coresSet = true; break;
                case "-2": case "--two-prime": opt.TwoPrime = true; break;
                case "--max-prime":
                    if (++i >= args.Length || !int.TryParse(args[i], out opt.MaxPrime) || opt.MaxPrime < 3)
                    { Console.Error.WriteLine("error: --max-prime needs an integer >= 3."); return 2; }
                    break;
                case "--max-results":
                    if (++i >= args.Length || !long.TryParse(args[i], out opt.MaxResults) || opt.MaxResults < 1)
                    { Console.Error.WriteLine("error: --max-results needs a positive integer."); return 2; }
                    break;
                case "--force-search": opt.ForceSearch = true; break;
                case "--no-spill": opt.Spill = false; break;
                case "--spill-mb":
                    if (++i >= args.Length || !long.TryParse(args[i], out long mb) || mb < 1)
                    { Console.Error.WriteLine("error: --spill-mb needs a positive integer (megabytes)."); return 2; }
                    opt.SpillBytes = mb * 1024 * 1024; break;
                default: Console.Error.WriteLine($"error: unknown option '{t}'."); PrintUsage(); return 2;
            }
        }

        if (shiftList != null)
        {
            if (positional.Count != 2)
            {
                Console.Error.WriteLine("error: with a shift list, give exactly: <startN> <endN> <a1,a2,...>");
                return 2;
            }
            opt.StartN = positional[0];
            opt.EndN = positional[1];
            shiftList.Sort();
            var distinct = new List<long>();
            foreach (var a in shiftList) if (distinct.Count == 0 || distinct[^1] != a) distinct.Add(a);
            opt.ShiftList = distinct.ToArray();
            opt.StartA = opt.ShiftList[0];
            opt.EndA = opt.ShiftList[^1];
        }
        else
        {
            if (positional.Count < 3 || positional.Count > 4)
            {
                PrintUsage();
                return positional.Count == 0 ? 0 : 2;
            }
            opt.StartN = positional[0];
            opt.EndN = positional[1];
            opt.StartA = positional[2];
            opt.EndA = positional.Count == 4 ? positional[3] : positional[2];
            if (opt.EndA < opt.StartA) { Console.Error.WriteLine("error: enda must be >= starta."); return 2; }
        }

        if (opt.StartN < 1) { Console.Error.WriteLine("error: startN must be >= 1."); return 2; }
        if (opt.EndN < opt.StartN) { Console.Error.WriteLine("error: endN must be >= startN."); return 2; }
        if (opt.EndN >= (1L << 62)) { Console.Error.WriteLine("error: endN too large; supported up to 2^62 - 1."); return 2; }
        _ = coresSet; // explicit --cores is honored as given (the machine may have more cores than reported)

        return new Engine(opt).Run();
    }

    static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: TwoNMod3Search <startN> <endN> <starta> [enda] [options]");
        Console.Error.WriteLine("   or: TwoNMod3Search <startN> <endN> <a1,a2,a3,...> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Searches n in [startN, endN] for which  2^n == a (mod n+1),");
        Console.Error.WriteLine("i.e. (n+1) | 2^n - a, for each shift a.  The shifts may be given as");
        Console.Error.WriteLine("an interval (starta..enda; omit enda for a single shift) OR as an");
        Console.Error.WriteLine("explicit comma-separated list with no spaces, e.g.  -3,5,9,17 .");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Positional:");
        Console.Error.WriteLine("  startN   inclusive lower bound on n (>= 1)");
        Console.Error.WriteLine("  endN     inclusive upper bound on n (< 2^62)");
        Console.Error.WriteLine("  starta   first shift a (any integer; may be negative)");
        Console.Error.WriteLine("  enda     last shift a (optional; default = starta)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  -c, --cores N      thread count (default: ProcessorCount - 2)");
        Console.Error.WriteLine("  -2, --two-prime    also run the two-prime factoring search (paper Cor 5.2)");
        Console.Error.WriteLine("      --max-prime P  largest smaller prime tried in two-prime search (default 70)");
        Console.Error.WriteLine("      --max-results N  cap on solutions listed per shift (default 1e6)");
        Console.Error.WriteLine("      --force-search   sweep even decided shifts (-1, 0, powers of two)");
        Console.Error.WriteLine("      --no-spill       keep all results in memory, write once at the end");
        Console.Error.WriteLine("                       (faster, but memory grows with the number of solutions)");
        Console.Error.WriteLine("      --spill-mb N     result-buffer size before auto-spill to disk (default 8)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Decided shifts are reported from theory and NOT swept by default:");
        Console.Error.WriteLine("  a = -1     : S_a = empty                       (R5)");
        Console.Error.WriteLine("  a = 0      : S_a = { 2^t : t >= 1 }            (R3)");
        Console.Error.WriteLine("  a = 2^j    : infinite, family m = (j+1)*p      (R4)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  TwoNMod3Search 1 1000000000000000 -3                # the (n+1)|2^n+3 problem");
        Console.Error.WriteLine("  TwoNMod3Search 1 2000000 -20 20                     # paper's table, one sweep");
        Console.Error.WriteLine("  TwoNMod3Search 1 2000000 -3,5,9,17                  # only these four shifts");
        Console.Error.WriteLine("  TwoNMod3Search 1 100 -3 -3 --two-prime --max-prime 70   # find big -3 solutions");
        Console.Error.WriteLine();
        Console.Error.WriteLine("During a sweep: <Enter> pauses/resumes, Ctrl+C stops and reports progress.");
    }
}

public sealed class Engine
{
    readonly Options _o;
    readonly string _resultsPath;
    ResultSink _sink;

    // ---- shift set (interval [_aLo,_aHi] when _shifts is null, else the explicit list) ----
    long[] _shifts;            // sorted distinct list, or null for an interval
    long _aLo, _aHi;
    int _shiftCount;           // number of shifts (clamped to int.MaxValue for display)
    byte[] _foundFlag;         // per-shift "found >= 1 result" flag; null when not tracked
    const int TrackCap = 4_000_000; // largest shift set tracked individually for the no-result summary

    // ---- threading / progress (shared by both sweep modes) ----
    CancellationTokenSource _cts;
    ManualResetEventSlim _runEvent;
    readonly object _outputLock = new();
    long _nextChunkStart;
    long[] _threadLastN;
    long[] _threadCount;
    long _chunkN;
    long _solutionsFound;

    // ---- per-shift wheel + small-prime table (single-shift sweep only) ----
    long _a;
    int _wheelMod;
    int[] _wheelResidues;
    int[] _wheelDeltas;
    int _wheelLen;
    SmallPrime[] _smallPrimes;
    const int TCAP = 8;

    readonly struct SmallPrime
    {
        public readonly uint P, D, K;
        public readonly bool Admissible;
        public SmallPrime(uint p, uint d, uint k, bool adm) { P = p; D = d; K = k; Admissible = adm; }
    }

    public Engine(Options o)
    {
        _o = o;
        _resultsPath = Path.Combine(Environment.CurrentDirectory, "results.txt");
    }

    public int Run()
    {
        _sink = new ResultSink(_resultsPath, _o.Spill, _o.SpillBytes);
        _shifts = _o.ShiftList; // null => interval mode
        _aLo = _o.StartA; _aHi = _o.EndA;
        if (_shifts != null) _shiftCount = _shifts.Length;
        else { long w = _aHi - _aLo + 1; _shiftCount = w > int.MaxValue ? int.MaxValue : (int)w; }

        bool single = _shifts != null ? _shifts.Length == 1 : _aLo == _aHi;
        long theShift = _shifts != null ? _shifts[0] : _aLo;

        try
        {
            Console.WriteLine($"n range : [{_o.StartN}, {_o.EndN}]   ({_o.EndN - _o.StartN + 1} values of n)");
            string noSpillNote = _o.Spill ? "" : "   (--no-spill: buffered in memory until the end)";
            if (single)
            {
                Console.WriteLine($"shift   : a = {theShift}");
                Console.WriteLine($"output  : {_resultsPath}{noSpillNote}");
                Console.WriteLine();
                RunSingle(theShift);
            }
            else
            {
                if (_shifts != null)
                    Console.WriteLine($"shifts  : explicit list of {_shifts.Length} values: {FormatList(_shifts)}");
                else
                    Console.WriteLine($"shifts  : a in [{_aLo}, {_aHi}]   ({_aHi - _aLo + 1} shifts)");
                Console.WriteLine($"output  : {_resultsPath}{noSpillNote}");
                Console.WriteLine();
                RunRange();
            }
        }
        finally
        {
            _sink.Flush(); // guarantees results are persisted on normal end and on Ctrl+C
        }
        return 0;
    }

    static string FormatList(long[] xs)
    {
        if (xs.Length <= 30) return string.Join(", ", xs);
        var head = string.Join(", ", xs[..15]);
        var tail = string.Join(", ", xs[^5..]);
        return $"{head}, ... , {tail}";
    }

    // =====================================================================
    //  Single shift
    // =====================================================================

    void RunSingle(long a)
    {
        var cls = Nt.Classify(a);
        ReportClassification(a, cls);

        bool decided = cls.Kind != Regime.Open;
        if (decided && !_o.ForceSearch)
        {
            switch (cls.Kind)
            {
                case Regime.Empty:
                    Console.WriteLine("No solutions exist for any n (R5).");
                    break;
                case Regime.Zero:
                    ListPowersOfTwo();
                    break;
                case Regime.PowerOfTwo:
                    ListPowerOfTwoFamily(cls.J);
                    break;
            }
        }
        else
        {
            if (decided)
                Console.WriteLine("[--force-search] sweeping the full n-range despite the shift being decided.\n");
            _a = a;
            BuildWheel(a);
            BuildSmallPrimeTable(a);
            _chunkN = (long)_wheelMod * Math.Max(1, 500_000 / _wheelMod);
            Console.WriteLine($"sweep   : wheel mod {_wheelMod}, {_wheelLen} residues " +
                              $"({100.0 * _wheelLen / _wheelMod:F1}% of integers examined), {_o.Cores} cores");
            ParallelSweep(PerAChunk);

            if (Interlocked.Read(ref _solutionsFound) == 0)
            {
                Console.WriteLine("\n---- shifts with no value found in this n-range ----");
                if (Nt.KnownNonEmpty(a))
                    Console.WriteLine($"  non-empty: a = {a} has a solution by reduction (R2/decided), but none in [{_o.StartN}, {_o.EndN}].");
                else if (a == -1)
                    Console.WriteLine($"  none found: a = {a} (provably empty, R5).");
                else
                    Console.WriteLine($"  none found: a = {a} — no solution found here and non-emptiness is open (a-1 = ±2^k).");
            }
        }

        if (_o.TwoPrime) TwoPrimeSearch(a);
    }

    void ReportClassification(long a, Classification cls)
    {
        string regime = cls.Kind switch
        {
            Regime.Empty => "empty (R5)",
            Regime.Zero => "soluble, infinite — S_0 = { 2^t : t >= 1 } (R3)",
            Regime.PowerOfTwo => $"soluble, infinite — a = 2^{cls.J}, family m = {cls.J + 1}*p (R4)",
            _ => "open"
        };
        Console.WriteLine($"regime  : {regime}");

        // R1/R2: prime members & non-emptiness.
        var (pm, infinitePrimes, full) = Nt.PrimeMembers(a);
        if (infinitePrimes)
            Console.WriteLine("members : every odd prime is a solution (a = 1, R1).");
        else if (pm.Count > 0)
        {
            string list = string.Join(", ", pm.ConvertAll(p => p.ToString()));
            string tail = full ? "" : ", ...";
            Console.WriteLine($"members : prime solutions (R1/R2): {list}{tail}   [each gives n = p-1]");
        }
        else if (cls.Kind == Regime.Open)
            Console.WriteLine("members : none — a is odd with a-1 = ±2^k, so no prime solution exists (the hard case).");

        if (cls.Kind == Regime.Open)
        {
            if (a % 2 == 0)
                Console.WriteLine("nonempty: yes — 2 is a solution since a is even (R2).");
            else if (pm.Count > 0)
                Console.WriteLine($"nonempty: yes — the prime {pm[0]} is a solution (R2).");
            else
                Console.WriteLine("nonempty: undecided by reductions; a single exhibited solution would settle it.");
        }
        Console.WriteLine();
    }

    void ListPowersOfTwo()
    {
        Console.WriteLine("Solutions are exactly the powers of two m = 2^t (n = 2^t - 1).");
        long mLo = _o.StartN + 1, mHi = _o.EndN + 1;
        int count = 0;
        for (int t = 1; t < 62; t++)
        {
            long m = 1L << t;
            if (m > mHi) break;
            if (m < mLo) continue;
            Console.WriteLine($"  n = {m - 1}, m = {m}  (= 2^{t})");
            AppendResult(m - 1, m, 0);
            if (++count >= _o.MaxResults) break;
        }
        Console.WriteLine(count == 0 ? "  (none in the requested n-range)" : $"  {count} solution(s) in range; infinitely many overall.");
    }

    void ListPowerOfTwoFamily(int j)
    {
        long c = j + 1;
        long cp = c; int s = 0; while ((cp & 1) == 0) { cp >>= 1; s++; }
        int e = cp > 1 ? (int)Nt.OrderMod(2u, (uint)cp) : 1;
        Console.WriteLine($"Guaranteed infinite family (R4): m = c*p with c = {c} = 2^{s}*{cp}, e = ord_{cp}(2) = {e};");
        Console.WriteLine($"  any prime p with p does-not-divide {2 * c} and p ≡ 1 (mod {e}) gives a solution m = {c}*p.");
        Console.WriteLine("  (This family proves infinitude; sporadic non-family solutions may also exist —");
        Console.WriteLine("   use --force-search to sweep the full n-range for all of them.)");

        long mLo = _o.StartN + 1, mHi = _o.EndN + 1;
        long pLo = (mLo + c - 1) / c;
        long pHi = mHi / c;
        if (pLo < 2) pLo = 2;
        Console.WriteLine($"  family members with m in [{mLo}, {mHi}]:");
        int count = 0;
        long scanned = 0;
        const long scanCap = 50_000_000;
        for (long p = pLo; p <= pHi; p++)
        {
            if (++scanned > scanCap) { Console.WriteLine("  ... (scan cap reached; infinitely many beyond)"); break; }
            if ((2 * c) % p == 0) continue;
            if ((p - 1) % e != 0) continue;
            if (!Nt.IsPrime((ulong)p)) continue;
            long m = c * p;
            Console.WriteLine($"  n = {m - 1}, m = {m}  (= {c}*{p})");
            AppendResult(m - 1, m, 1L << j);
            if (++count >= Math.Min(_o.MaxResults, 10_000)) { Console.WriteLine("  ... (list cap reached; infinitely many)"); break; }
        }
        if (count == 0) Console.WriteLine("  (no family members in range; the family is still infinite overall)");
    }

    void PerAChunk(int id, long nStart, long nEnd, CancellationToken token)
    {
        long mStart = nStart + 1, mEnd = nEnd + 1;
        long baseM = (mStart / _wheelMod) * _wheelMod;
        int rs = (int)(mStart - baseM);
        int idx = 0;
        while (idx < _wheelLen && _wheelResidues[idx] < rs) idx++;
        if (idx == _wheelLen) { baseM += _wheelMod; idx = 0; }
        long m = baseM + _wheelResidues[idx];

        long localCount = 0; int since = 0;
        while (m <= mEnd)
        {
            if ((since & 0x1FFF) == 0)
            {
                Interlocked.Exchange(ref _threadLastN[id], m - 1);
                Interlocked.Add(ref _threadCount[id], localCount); localCount = 0;
                if (token.IsCancellationRequested) return;
                _runEvent.Wait(token);
            }
            since++; localCount++;
            if (CheckCandidate((ulong)m)) ReportSolution(_a, m - 1, m);
            m += _wheelDeltas[idx];
            if (++idx == _wheelLen) idx = 0;
        }
        Interlocked.Exchange(ref _threadLastN[id], mEnd - 1);
        Interlocked.Add(ref _threadCount[id], localCount);
    }

    // =====================================================================
    //  Range of shifts  (Section-8 single-pass: one residue per m)
    // =====================================================================

    void RunRange()
    {
        int empties = 0, zeros = 0, powers = 0, opens;
        var decidedNotes = new List<string>();

        if (_shifts != null)
        {
            // Explicit list: classify each listed shift.
            foreach (long a in _shifts)
            {
                switch (Nt.Classify(a).Kind)
                {
                    case Regime.Empty: empties++; decidedNotes.Add("  a = -1: empty, no solutions (R5)"); break;
                    case Regime.Zero: zeros++; decidedNotes.Add("  a = 0: S_0 = {2^t} (R3)"); break;
                    case Regime.PowerOfTwo:
                        powers++;
                        decidedNotes.Add($"  a = {a}: infinite, m = {BitOperations.TrailingZeroCount((ulong)a) + 1}*p (R4)");
                        break;
                }
            }
            opens = _shifts.Length - empties - zeros - powers;
        }
        else
        {
            // Interval: enumerate decided shifts directly (powers of two are sparse), so this
            // is O(#decided) ~ O(log enda) rather than O(range width).
            long lo = _aLo, hi = _aHi;
            if (-1 >= lo && -1 <= hi) { empties = 1; decidedNotes.Add("  a = -1: empty, no solutions (R5)"); }
            if (0 >= lo && 0 <= hi) { zeros = 1; decidedNotes.Add("  a = 0: S_0 = {2^t} (R3)"); }
            for (int j = 0; j < 62; j++)
            {
                long a = 1L << j;
                if (a > hi) break;
                if (a >= lo) { powers++; decidedNotes.Add($"  a = {a}: infinite, m = {j + 1}*p (R4)"); }
            }
            opens = (int)Math.Min((hi - lo + 1) - (empties + zeros + powers), int.MaxValue);
        }

        Console.WriteLine($"classification: {opens} open, {powers} power-of-two (infinite), {zeros} zero (infinite), {empties} empty");
        if (decidedNotes.Count > 0 && decidedNotes.Count <= 70)
        {
            Console.WriteLine("decided shifts (reported from theory, not swept):");
            foreach (var s in decidedNotes) Console.WriteLine(s);
        }

        if (_shiftCount <= 64)
        {
            Console.WriteLine("\nnon-emptiness by reduction (R1/R2):");
            foreach (long a in EnumerateShifts())
            {
                var cls = Nt.Classify(a);
                var (pm, inf, _) = Nt.PrimeMembers(a);
                string verdict;
                if (cls.Kind == Regime.Empty) verdict = "empty";
                else if (cls.Kind != Regime.Open) verdict = "infinite";
                else if (inf) verdict = "infinite (all odd primes)";
                else if (a % 2 == 0) verdict = "non-empty (2 in S_a)";
                else if (pm.Count > 0) verdict = $"non-empty ({pm[0]} in S_a)";
                else verdict = "undecided (a-1 = ±2^k)";
                Console.WriteLine($"  a = {a,4}: {verdict}");
            }
        }

        // Per-shift hit tracking for the no-result summary (bounded memory).
        if (_shiftCount <= TrackCap) _foundFlag = new byte[_shiftCount];
        else Console.WriteLine($"\nnote: {_shiftCount} shifts exceeds the {TrackCap} tracking cap; the per-shift no-result list will be summarised by count only.");

        Console.WriteLine($"\nsweep   : single-pass over m, one residue 2^(m-1) mod m per m, {_o.Cores} cores");
        if (!_o.ForceSearch)
            Console.WriteLine("          (decided shifts -1/0/2^j are not recorded by the sweep; see notes above)");
        Console.WriteLine();

        _chunkN = 500_000;
        ParallelSweep(_shifts != null ? SinglePassListChunk : SinglePassIntervalChunk);

        ReportNoResults();

        if (_o.TwoPrime)
            foreach (long a in EnumerateShifts()) TwoPrimeSearch(a);
    }

    IEnumerable<long> EnumerateShifts()
    {
        if (_shifts != null) { foreach (var a in _shifts) yield return a; }
        else for (long a = _aLo; a <= _aHi; a++) yield return a;
    }

    void SinglePassIntervalChunk(int id, long nStart, long nEnd, CancellationToken token)
    {
        long mStart = nStart + 1, mEnd = nEnd + 1;
        long localCount = 0; int since = 0;
        byte[] flag = _foundFlag;
        for (long m = mStart; m <= mEnd; m++)
        {
            if ((since & 0x1FFF) == 0)
            {
                Interlocked.Exchange(ref _threadLastN[id], m - 1);
                Interlocked.Add(ref _threadCount[id], localCount); localCount = 0;
                if (token.IsCancellationRequested) return;
                _runEvent.Wait(token);
            }
            since++; localCount++;

            ulong r = Nt.ResidueOf((ulong)m);                 // 2^(m-1) mod m, in [0, m)
            long mm = m;
            long rem = ((_aLo % mm) + mm) % mm;                // aLo mod m in [0,m)
            long delta = (((long)r - rem) % mm + mm) % mm;     // shift up to first a ≡ r (mod m)
            for (long a = _aLo + delta; a <= _aHi; a += mm)
            {
                if (!_o.ForceSearch && Nt.IsDecided(a)) continue;
                if (flag != null) flag[a - _aLo] = 1;          // race-safe: all writers store 1
                ReportSolution(a, m - 1, m);
            }
        }
        Interlocked.Exchange(ref _threadLastN[id], mEnd - 1);
        Interlocked.Add(ref _threadCount[id], localCount);
    }

    void SinglePassListChunk(int id, long nStart, long nEnd, CancellationToken token)
    {
        long mStart = nStart + 1, mEnd = nEnd + 1;
        long localCount = 0; int since = 0;
        long[] shifts = _shifts;
        byte[] flag = _foundFlag;
        for (long m = mStart; m <= mEnd; m++)
        {
            if ((since & 0x1FFF) == 0)
            {
                Interlocked.Exchange(ref _threadLastN[id], m - 1);
                Interlocked.Add(ref _threadCount[id], localCount); localCount = 0;
                if (token.IsCancellationRequested) return;
                _runEvent.Wait(token);
            }
            since++; localCount++;

            ulong r = Nt.ResidueOf((ulong)m);                  // 2^(m-1) mod m, in [0, m)
            long mm = m, rr = (long)r;
            for (int i = 0; i < shifts.Length; i++)            // test each listed shift: m in S_a iff a ≡ r (mod m)
            {
                long a = shifts[i];
                if (((a - rr) % mm) != 0) continue;
                if (!_o.ForceSearch && Nt.IsDecided(a)) continue;
                if (flag != null) flag[i] = 1;
                ReportSolution(a, m - 1, m);
            }
        }
        Interlocked.Exchange(ref _threadLastN[id], mEnd - 1);
        Interlocked.Add(ref _threadCount[id], localCount);
    }

    // =====================================================================
    //  No-result summary: which shifts had no value found by the sweep,
    //  split into "non-empty" (a solution exists by reduction, just outside
    //  the searched range) and "none found" (no solution found; for these
    //  shifts non-emptiness is open, or -1 which is provably empty).
    //  Both lists are ordered by the size of a (|a|, then signed value).
    // =====================================================================

    void ReportNoResults()
    {
        var nonEmpty = new List<long>();
        var noneFound = new List<long>();
        long eligible = 0;

        if (_foundFlag != null)
        {
            if (_shifts != null)
            {
                for (int i = 0; i < _shifts.Length; i++)
                {
                    long a = _shifts[i];
                    if (!_o.ForceSearch && Nt.IsDecided(a)) continue; // reported analytically, not swept
                    eligible++;
                    if (_foundFlag[i] != 0) continue;
                    (Nt.KnownNonEmpty(a) ? nonEmpty : noneFound).Add(a);
                }
            }
            else
            {
                for (long a = _aLo; a <= _aHi; a++)
                {
                    if (!_o.ForceSearch && Nt.IsDecided(a)) continue;
                    eligible++;
                    if (_foundFlag[a - _aLo] != 0) continue;
                    (Nt.KnownNonEmpty(a) ? nonEmpty : noneFound).Add(a);
                }
            }
        }

        var bySize = Comparer<long>.Create((x, y) =>
        {
            int c = Math.Abs(x).CompareTo(Math.Abs(y));
            return c != 0 ? c : x.CompareTo(y);
        });
        nonEmpty.Sort(bySize);
        noneFound.Sort(bySize);

        Console.WriteLine("\n---- shifts with no value found in this n-range ----");
        if (_foundFlag == null)
        {
            Console.WriteLine($"  (per-shift breakdown suppressed: {_shiftCount} shifts exceeds the {TrackCap} cap)");
            return;
        }
        if (nonEmpty.Count == 0 && noneFound.Count == 0)
        {
            Console.WriteLine($"  none — every swept shift produced at least one solution ({eligible} shift(s) swept).");
            return;
        }
        Console.WriteLine($"  (of {eligible} swept shift(s); ordered by |a|)");

        Console.WriteLine($"  non-empty — a solution exists (R2/decided) but lies outside [{_o.StartN}, {_o.EndN}]: {nonEmpty.Count}");
        if (nonEmpty.Count > 0) Console.WriteLine("    " + FormatShiftList(nonEmpty));

        Console.WriteLine($"  none found — no solution found here; non-emptiness is open: {noneFound.Count}");
        if (noneFound.Count > 0) Console.WriteLine("    " + FormatShiftList(noneFound, annotateMinusOne: true));
    }

    static string FormatShiftList(List<long> xs, bool annotateMinusOne = false)
    {
        const int cap = 60;
        var labels = new List<string>(Math.Min(xs.Count, cap));
        for (int i = 0; i < xs.Count && i < cap; i++)
            labels.Add(annotateMinusOne && xs[i] == -1 ? "-1 (empty, R5)" : xs[i].ToString());
        string s = string.Join(", ", labels);
        if (xs.Count > cap) s += $", ... (+{xs.Count - cap} more)";
        return s;
    }

    // =====================================================================
    //  Two-prime search (paper §5, Cor 5.2 / Rmk 5.3)
    // =====================================================================

    void TwoPrimeSearch(long a)
    {
        if (a == 0) return; // S_0 has no two-distinct-prime members
        Console.WriteLine($"\n[two-prime] a = {a}: factoring N_p = 2^(p-1) - a for admissible odd primes p <= {_o.MaxPrime}");
        int found = 0;
        for (int p = 3; p <= _o.MaxPrime; p += 2)
        {
            if (!Nt.IsPrime((ulong)p)) continue;            // O(1) memory; no O(maxPrime) sieve
            if (a % p == 0) continue;                       // Lemma 2.1: p does not divide a
            uint d = Nt.OrderMod(2u, (uint)p);
            int target = (int)(((a % p) + p) % p);
            int k = Nt.DiscreteLog(2u, (uint)target, (uint)p, d);
            if (k < 0) continue;                            // a not in <2> (mod p)
            if ((a & 1L) == 1L && p >= 5 && !Nt.AdmissibleOddA(a, p, (int)d, k)) continue; // §4 sieve

            BigInteger N = BigInteger.Pow(2, p - 1) - a;
            if (N <= 1) continue;

            var (factors, complete) = Nt.Factor(N);
            int residue = (int)(((k + 1) % (int)d + (int)d) % (int)d);
            var seen = new HashSet<BigInteger>();
            foreach (var q in factors)
            {
                if (q <= p || !seen.Add(q)) continue;
                if ((int)(q % d) != residue) continue;
                BigInteger m = (BigInteger)p * q;
                BigInteger lhs = BigInteger.ModPow(2, m - 1, m);
                BigInteger rhs = ((new BigInteger(a) % m) + m) % m;
                if (lhs != rhs) continue;                   // exact verification
                BigInteger n = m - 1;
                lock (_outputLock)
                {
                    Console.WriteLine($"  *** SOLUTION  a = {a}, n = {n}, m = {m} = {p} * {q} ***");
                    AppendResultBig(n, m, a);
                }
                found++;
            }
            if (!complete)
                Console.WriteLine($"  (N_{p} = 2^{p - 1} - {a} not fully factored within budget; some solutions may be missed)");
        }
        Console.WriteLine(found == 0
            ? $"[two-prime] a = {a}: no two-prime solutions found with smaller prime <= {_o.MaxPrime}."
            : $"[two-prime] a = {a}: {found} two-prime solution(s) found.");
    }

    // =====================================================================
    //  Per-shift filters (single-shift sweep)
    // =====================================================================

    void BuildWheel(long a)
    {
        int v2a = BitOperations.TrailingZeroCount((ulong)a);
        int t = Math.Max(2, Math.Min(v2a + 1, TCAP));
        int W = (1 << t) * 1155; // 1155 = 3*5*7*11
        _wheelMod = W;

        int[] oddPrimes = { 3, 5, 7, 11 };
        int[] ord = new int[oddPrimes.Length];
        int[] target = new int[oddPrimes.Length];
        for (int i = 0; i < oddPrimes.Length; i++)
        {
            ord[i] = (int)Nt.OrderMod(2u, (uint)oddPrimes[i]);
            target[i] = (int)(((a % oddPrimes[i]) + oddPrimes[i]) % oddPrimes[i]);
        }

        int lowMask = (1 << t) - 1;
        var residues = new List<int>();
        for (int r = 0; r < W; r++)
        {
            int low = r & lowMask;
            bool ok2 = low != 0 ? BitOperations.TrailingZeroCount(low) <= v2a : t <= v2a;
            if (!ok2) continue;
            bool ok = true;
            for (int i = 0; i < oddPrimes.Length; i++)
            {
                int p = oddPrimes[i];
                if (r % p == 0)
                {
                    int dd = ord[i];
                    int e = ((r - 1) % dd + dd) % dd;
                    if (Nt.PowModSmall(2, e, p) != target[i]) { ok = false; break; }
                }
            }
            if (ok) residues.Add(r);
        }

        if (residues.Count == 0) residues.Add(1); // pathological; keep iteration valid
        _wheelResidues = residues.ToArray();
        _wheelLen = _wheelResidues.Length;
        _wheelDeltas = new int[_wheelLen];
        for (int i = 0; i < _wheelLen; i++)
        {
            int next = (i + 1 < _wheelLen) ? _wheelResidues[i + 1] : _wheelResidues[0] + W;
            _wheelDeltas[i] = next - _wheelResidues[i];
        }
    }

    void BuildSmallPrimeTable(long a)
    {
        int[] basePrimes = { 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97 };
        var sp = new List<SmallPrime>();
        foreach (int p in basePrimes)
        {
            uint d = Nt.OrderMod(2u, (uint)p);
            uint tgt = (uint)(((a % p) + p) % p);
            int k = Nt.DiscreteLog(2u, tgt, (uint)p, d);
            sp.Add(new SmallPrime((uint)p, d, (uint)Math.Max(k, 0), k >= 0));
        }
        _smallPrimes = sp.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool CheckCandidate(ulong m)
    {
        var sp = _smallPrimes;
        for (int i = 0; i < sp.Length; i++)
        {
            uint p = sp[i].P;
            if (m % p == 0)
            {
                if (!sp[i].Admissible) return false;
                if ((m - 1) % sp[i].D != sp[i].K) return false;
            }
        }
        return Nt.ResidueOf(m) == Nt.ModU(_a, m);
    }

    // =====================================================================
    //  Threading skeleton shared by both sweep modes
    // =====================================================================

    void ParallelSweep(Action<int, long, long, CancellationToken> processChunk)
    {
        _cts = new CancellationTokenSource();
        _runEvent = new ManualResetEventSlim(true);
        _nextChunkStart = _o.StartN;
        _threadLastN = new long[_o.Cores];
        _threadCount = new long[_o.Cores];
        Array.Fill(_threadLastN, -1L);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (!_cts.IsCancellationRequested)
            {
                Console.WriteLine("\n[ctrl+c] stopping; waiting for workers to finish current chunks...");
                _cts.Cancel(); _runEvent.Set();
            }
        };

        var input = new Thread(InputLoop) { IsBackground = true, Name = "input" };
        input.Start();
        Console.WriteLine("press <Enter> to pause/resume, Ctrl+C to stop.\n");

        var sw = Stopwatch.StartNew();
        var reporter = new Thread(ReporterLoop) { IsBackground = true, Name = "reporter" };
        reporter.Start();

        var workers = new Task[_o.Cores];
        for (int i = 0; i < _o.Cores; i++)
        {
            int id = i;
            workers[i] = Task.Run(() => Worker(id, processChunk));
        }
        try { Task.WaitAll(workers); }
        catch (AggregateException ae)
        {
            foreach (var inner in ae.Flatten().InnerExceptions)
                if (inner is not OperationCanceledException) Console.Error.WriteLine(inner);
        }
        sw.Stop();
        _sink.Flush(); // persist this sweep's results before any subsequent phase

        Console.WriteLine("\n---- sweep summary ----");
        long total = 0, minLast = long.MaxValue;
        for (int i = 0; i < _o.Cores; i++)
        {
            long last = Interlocked.Read(ref _threadLastN[i]);
            long cnt = Interlocked.Read(ref _threadCount[i]);
            total += cnt;
            if (last >= 0 && last < minLast) minLast = last;
        }
        Console.WriteLine($"  examined : {total} values of n");
        Console.WriteLine($"  solutions: {Interlocked.Read(ref _solutionsFound)}");
        if (_o.Spill && _sink.Spills > 0)
            Console.WriteLine($"  spilled  : results flushed to disk {_sink.Spills} time(s) (memory bounded to ~{_o.SpillBytes / (1024 * 1024)} MB)");
        if (minLast != long.MaxValue)
            Console.WriteLine($"  contiguous lower bound on processed n : {minLast}");
        double secs = sw.Elapsed.TotalSeconds;
        if (secs > 0 && total > 0)
            Console.WriteLine($"  rate : {total / secs:N0} n/s ({total / secs / _o.Cores:N0} per core)");
        Console.WriteLine($"  elapsed : {secs:F2} s");
    }

    void Worker(int id, Action<int, long, long, CancellationToken> processChunk)
    {
        var token = _cts.Token;
        try
        {
            while (true)
            {
                _runEvent.Wait(token);
                if (token.IsCancellationRequested) break;
                long chunkStart = Interlocked.Add(ref _nextChunkStart, _chunkN) - _chunkN;
                if (chunkStart > _o.EndN) break;
                long chunkEnd = Math.Min(chunkStart + _chunkN - 1, _o.EndN);
                processChunk(id, chunkStart, chunkEnd, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    void InputLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            string line;
            try { line = Console.ReadLine(); } catch { return; }
            if (line is null) return;
            if (_runEvent.IsSet) { _runEvent.Reset(); Console.WriteLine("[paused] press <Enter> to resume."); }
            else { _runEvent.Set(); Console.WriteLine("[resumed]"); }
        }
    }

    void ReporterLoop()
    {
        long lastTotal = 0;
        var sw = Stopwatch.StartNew();
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { Task.Delay(5000, token).Wait(token); } catch { return; }
            if (!_runEvent.IsSet) continue;
            long total = 0, maxLast = -1;
            for (int i = 0; i < _o.Cores; i++)
            {
                total += Interlocked.Read(ref _threadCount[i]);
                long l = Interlocked.Read(ref _threadLastN[i]);
                if (l > maxLast) maxLast = l;
            }
            double dt = sw.Elapsed.TotalSeconds; sw.Restart();
            double rate = dt > 0 ? (total - lastTotal) / dt : 0; lastTotal = total;
            Console.WriteLine($"[progress] frontier n = {maxLast}, examined = {total:N0}, rate = {rate:N0} n/s");
        }
    }

    // =====================================================================
    //  Result output
    // =====================================================================

    void ReportSolution(long a, long n, long m)
    {
        long idx = Interlocked.Increment(ref _solutionsFound);
        lock (_outputLock)
        {
            if (idx <= _o.MaxResults)
                Console.WriteLine($"*** SOLUTION  a = {a}, n = {n}, m = {m} ***");
            else if (idx == _o.MaxResults + 1)
                Console.WriteLine($"... (more than {_o.MaxResults} solutions; further hits buffered to {Path.GetFileName(_resultsPath)} only)");
        }
        _sink.Add(n, m, a);
    }

    void AppendResult(long n, long m, long a) => _sink.Add(n, m, a);

    void AppendResultBig(BigInteger n, BigInteger m, long a) => _sink.Add(n, m, a);
}

// =========================================================================
//  Buffered result sink with optional automatic spill to disk
// =========================================================================
//
// Solutions are accumulated in an in-memory buffer and appended to results.txt.
//   - auto-spill ON (default): the buffer is flushed to disk whenever it reaches
//     SpillBytes, so resident memory stays bounded no matter how many solutions
//     are found (this also makes writing fast — appends are batched, not per-line).
//   - auto-spill OFF (--no-spill): the buffer is never flushed mid-run and is
//     written once at the end. Faster (a single write) but memory grows with the
//     number of solutions; intended for runs known to produce few of them.
// The buffer is always flushed at the end and on Ctrl+C, so no solutions are lost.

public sealed class ResultSink : IDisposable
{
    readonly string _path;
    readonly bool _autoSpill;
    readonly long _spillBytes;
    readonly object _lock = new();
    readonly System.Text.StringBuilder _buf = new();
    long _total;
    long _spills;

    public ResultSink(string path, bool autoSpill, long spillBytes)
    {
        _path = path; _autoSpill = autoSpill; _spillBytes = spillBytes;
    }

    public long Total { get { lock (_lock) return _total; } }
    public long Spills { get { lock (_lock) return _spills; } }
    public bool AutoSpill => _autoSpill;

    public void Add(long n, long m, long a)
    {
        lock (_lock)
        {
            _buf.Append(n).Append('\t').Append(m).Append('\t').Append(a).Append('\n');
            _total++;
            if (_autoSpill && _buf.Length >= _spillBytes) FlushLocked();
        }
    }

    public void Add(BigInteger n, BigInteger m, long a)
    {
        lock (_lock)
        {
            _buf.Append(n).Append('\t').Append(m).Append('\t').Append(a).Append('\n');
            _total++;
            if (_autoSpill && _buf.Length >= _spillBytes) FlushLocked();
        }
    }

    public void Flush() { lock (_lock) FlushLocked(); }

    void FlushLocked()
    {
        if (_buf.Length == 0) return;
        try { File.AppendAllText(_path, _buf.ToString()); _spills++; }
        catch (Exception ex) { Console.Error.WriteLine($"warn: results write failed: {ex.Message}"); }
        _buf.Clear();
    }

    public void Dispose() => Flush();
}

// =========================================================================
//  Number theory
// =========================================================================

public enum Regime { Open, Empty, Zero, PowerOfTwo }

public readonly struct Classification
{
    public readonly Regime Kind;
    public readonly int J; // for PowerOfTwo: a = 2^J
    public Classification(Regime kind, int j = 0) { Kind = kind; J = j; }
}

public static class Nt
{
    // ---- classification (Theorem 1.1) ----
    public static Classification Classify(long a)
    {
        if (a == -1) return new Classification(Regime.Empty);
        if (a == 0) return new Classification(Regime.Zero);
        if (a > 0 && (a & (a - 1)) == 0)
            return new Classification(Regime.PowerOfTwo, BitOperations.TrailingZeroCount((ulong)a));
        return new Classification(Regime.Open);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDecided(long a)
        => a == -1 || a == 0 || (a > 0 && (a & (a - 1)) == 0);

    // True when S_a is provably non-empty by a reduction (no factoring needed):
    //   a even (2 in S_a, R2); a = 0 (R3); a = 2^j incl. 1 (R4);
    //   a odd, a != 1 with a-1 having an odd prime factor, i.e. |a-1| not a power of two (R2).
    // Returns false for a = -1 (provably empty) and for the odd a-1 = ±2^k shifts whose
    // non-emptiness is open.
    public static bool KnownNonEmpty(long a)
    {
        if ((a & 1L) == 0L) return true;                 // even
        if (a == 1) return true;                         // 2^0
        if (a > 0 && (a & (a - 1)) == 0) return true;    // power of two
        BigInteger am1 = BigInteger.Abs(new BigInteger(a) - 1);  // a odd => even, >= 2
        return !(am1 > 0 && (am1 & (am1 - 1)).IsZero);   // has an odd prime factor
    }

    // ---- prime members (Prop 2.2 / R1, R2) ----
    public static (List<long> members, bool infinite, bool complete) PrimeMembers(long a)
    {
        var list = new List<long>();
        if ((a & 1L) == 0L) list.Add(2);
        if (a == 1) return (list, true, true);     // every odd prime divides a-1 = 0
        bool complete = true;
        BigInteger am1 = BigInteger.Abs(new BigInteger(a) - 1);
        var (fac, ok) = Factor(am1);
        complete = ok;
        var seen = new HashSet<long>();
        foreach (var q in fac)
            if (q != 2 && q <= long.MaxValue && seen.Add((long)q)) list.Add((long)q);
        list.Sort();
        return (list, false, complete);
    }

    public static bool AdmissibleOddA(long a, int p, int d, int k)
    {
        if ((d & 1) == 0 && (k & 1) != 0) return false;             // (A) d even => k even
        int am3 = (int)(((a % 3) + 3) % 3);
        if (am3 != 1 && d % 3 == 0 && k % 3 == 2) return false;     // (B) 3|d => k != 2 (mod 3), active if a != 1 (mod 3)
        return true;
    }

    // ---- 2^(m-1) mod m, returned in [0,m); even m handled by CRT on the odd part ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ResidueOf(ulong m)
    {
        int s = BitOperations.TrailingZeroCount(m);
        if (s == 0) return PowMod2(m - 1, m);
        ulong u = m >> s;
        if (u == 1) return 0UL;                 // m a power of two
        ulong ru = PowMod2(m - 1, u);
        ulong mask = (1UL << s) - 1;
        ulong uinv = InvMod2Pow(u, s);
        ulong jm = (ru * uinv) & mask;
        ulong j = (mask + 1 - jm) & mask;
        return ru + u * j;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ModU(long a, ulong u)
    {
        long r = a % (long)u;
        if (r < 0) r += (long)u;
        return (ulong)r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong InvMod2Pow(ulong u, int s)
    {
        ulong x = 1;
        unchecked { for (int i = 0; i < 6; i++) x *= 2UL - u * x; } // inverse mod 2^64
        return s >= 64 ? x : (x & ((1UL << s) - 1));
    }

    // ---- Montgomery 2^e mod m, m odd, 1 < m < 2^63 ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong PowMod2(ulong e, ulong m)
    {
        ulong mInvNeg = NegInvMod64(m);
        ulong Rmod; unchecked { Rmod = (0UL - m) % m; }
        ulong oneMont = Rmod;
        ulong twoMont = 2UL * Rmod; if (twoMont >= m) twoMont -= m;
        ulong res = oneMont;
        int top = 63; while (top > 0 && (e >> top) == 0) top--;
        for (int i = top; i >= 0; i--)
        {
            res = MontMul(res, res, m, mInvNeg);
            if (((e >> i) & 1UL) != 0) res = MontMul(res, twoMont, m, mInvNeg);
        }
        return MontMul(res, 1UL, m, mInvNeg);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MontMul(ulong a, ulong b, ulong m, ulong mInvNeg)
    {
        ulong tHi = Math.BigMul(a, b, out ulong tLo);
        ulong q; unchecked { q = tLo * mInvNeg; }
        ulong qmHi = Math.BigMul(q, m, out ulong qmLo);
        ulong sumLo; unchecked { sumLo = tLo + qmLo; }
        ulong carry = sumLo < tLo ? 1UL : 0UL;
        ulong r; unchecked { r = tHi + qmHi + carry; }
        if (r >= m) r -= m;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong NegInvMod64(ulong m)
    {
        ulong x = m;
        unchecked
        {
            x *= 2UL - m * x; x *= 2UL - m * x; x *= 2UL - m * x; x *= 2UL - m * x; x *= 2UL - m * x;
            return 0UL - x;
        }
    }

    // ---- small modular helpers ----
    public static uint OrderMod(uint a, uint p)
    {
        ulong x = 1; uint d = 0;
        do { x = x * a % p; d++; } while (x != 1);
        return d;
    }

    public static int DiscreteLog(uint g, uint target, uint p, uint d)
    {
        ulong x = 1;
        for (uint i = 0; i < d; i++) { if (x == target) return (int)i; x = x * g % p; }
        return -1;
    }

    public static int PowModSmall(int b, int e, int m)
    {
        long r = 1, bb = b % m;
        while (e > 0) { if ((e & 1) != 0) r = r * bb % m; bb = bb * bb % m; e >>= 1; }
        return (int)r;
    }

    public static List<int> PrimesUpTo(int n)
    {
        var sieve = new bool[n + 1];
        var primes = new List<int>();
        for (int i = 2; i <= n; i++)
        {
            if (sieve[i]) continue;
            primes.Add(i);
            for (long j = (long)i * i; j <= n; j += i) sieve[j] = true;
        }
        return primes;
    }

    // ---- primality ----
    static readonly ulong[] MrBases = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MulMod(ulong a, ulong b, ulong m) => (ulong)((UInt128)a * b % m);

    static ulong PowModU(ulong b, ulong e, ulong m)
    {
        ulong r = 1 % m; b %= m;
        while (e > 0) { if ((e & 1) != 0) r = MulMod(r, b, m); b = MulMod(b, b, m); e >>= 1; }
        return r;
    }

    public static bool IsPrime(ulong n)
    {
        if (n < 2) return false;
        foreach (ulong p in MrBases) { if (n == p) return true; if (n % p == 0) return false; }
        ulong d = n - 1; int s = 0;
        while ((d & 1) == 0) { d >>= 1; s++; }
        foreach (ulong a in MrBases)
        {
            ulong x = PowModU(a, d, n);
            if (x == 1 || x == n - 1) continue;
            bool composite = true;
            for (int i = 1; i < s; i++) { x = MulMod(x, x, n); if (x == n - 1) { composite = false; break; } }
            if (composite) return false;
        }
        return true;
    }

    public static bool IsPrimeBig(BigInteger n)
    {
        if (n < 2) return false;
        if (n <= ulong.MaxValue) return IsPrime((ulong)n);
        foreach (ulong p in MrBases) if (n % p == 0) return false;
        BigInteger d = n - 1; int s = 0;
        while ((d & 1) == 0) { d >>= 1; s++; }
        foreach (ulong a in MrBases)
        {
            BigInteger x = BigInteger.ModPow(a, d, n);
            if (x == 1 || x == n - 1) continue;
            bool composite = true;
            for (int i = 1; i < s; i++) { x = x * x % n; if (x == n - 1) { composite = false; break; } }
            if (composite) return false;
        }
        return true;
    }

    // ---- factorization: trial division then Brent's rho ----
    static readonly int[] SmallTrial = PrimesUpTo(100_000).ToArray();

    public static (List<BigInteger> factors, bool complete) Factor(BigInteger n)
    {
        var factors = new List<BigInteger>();
        bool complete = true;
        if (n < 0) n = -n;
        if (n <= 1) return (factors, true);

        foreach (int p in SmallTrial)
        {
            if ((BigInteger)p * p > n) break;
            while (n % p == 0) { factors.Add(p); n /= p; }
        }
        if (n == 1) { factors.Sort(); return (factors, true); }

        var stack = new Stack<BigInteger>();
        stack.Push(n);
        while (stack.Count > 0)
        {
            BigInteger cur = stack.Pop();
            if (cur == 1) continue;
            if (IsPrimeBig(cur)) { factors.Add(cur); continue; }
            BigInteger f = PollardRho(cur);
            if (f == 0 || f == cur) { complete = false; factors.Add(cur); continue; }
            stack.Push(f);
            stack.Push(cur / f);
        }
        factors.Sort();
        return (factors, complete);
    }

    static BigInteger PollardRho(BigInteger n)
    {
        if (n % 2 == 0) return 2;
        if (n % 3 == 0) return 3;
        var rng = new Random(987654321);
        const long maxIters = 1_000_000_000;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            BigInteger c = (rng.NextInt64() & long.MaxValue) % (n - 1) + 1;
            BigInteger y = (rng.NextInt64() & long.MaxValue) % (n - 1) + 1;
            long m = 128;
            BigInteger g = 1, q = 1, x = 0, ys = 0;
            long r = 1, iters = 0;
            do
            {
                x = y;
                for (long i = 0; i < r; i++) y = (y * y + c) % n;
                long k = 0;
                while (k < r && g == 1)
                {
                    ys = y;
                    long lim = Math.Min(m, r - k);
                    for (long i = 0; i < lim; i++)
                    {
                        y = (y * y + c) % n;
                        q = q * BigInteger.Abs(x - y) % n;
                        if (++iters > maxIters) return 0;
                    }
                    g = BigInteger.GreatestCommonDivisor(q, n);
                    k += lim;
                }
                r *= 2;
            } while (g == 1);
            if (g == n)
            {
                do { ys = (ys * ys + c) % n; g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(x - ys), n); }
                while (g == 1);
            }
            if (g != n && g > 1) return g;
        }
        return 0;
    }
}