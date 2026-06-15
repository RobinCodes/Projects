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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

    // ---- factorisation engine (used by the two-prime search) ----
    public bool UseFactorDb = true;       // consult factordb.com for large cofactors (verified locally)
    public int FactorDbTimeoutMs = 8000; // per-request HTTP timeout
    public int EcmBudgetMs = 20000;      // per-number wall-clock budget for the ECM fallback
    public bool FactorVerbose;            // log FactorDB / ECM activity

    // ---- two-prime search: selection, scheduling, effort ----
    public long TwoPrimeLo = 3;           // smaller-prime lower bound (range form)
    public long TwoPrimeHi = 70;          // smaller-prime upper bound (range form); kept == MaxPrime
    public long[] TwoPrimeList;           // explicit list of smaller primes, or null for [lo, hi]
    public string TwoPrimeMode = "after"; // "before" | "after" | "alongside" the sweep
    public bool TwoPrimeOnly;             // run ONLY the two-prime search (skip the sweep entirely)
    public int TwoPrimeCores = 1;         // cores given to two-prime when running alongside
    public int TwoPrimeEffortMs = -1;     // per-N_p ECM budget for two-prime; < 0 => use EcmBudgetMs

    // ---- wheel sieve tuning (single-shift sweep) ----
    public int WheelMax = 11;            // largest odd prime baked into the wheel modulus
    public bool AutoWheel;                // auto-pick the wheel modulus by a cost/benefit model
    public long WheelMemMb = 256;         // memory budget (MB) for the wheel residue table

    // ---- periodic status file ----
    public string StatusFile = "status.txt";
    public int StatusIntervalSec = 300;   // also written on pause/resume/finish/Ctrl+C
}

public static class Program
{
    public static int Main(string[] args)
    {
        // Hidden diagnostic mode: verify the factorisation engine (parser, rho, ECM)
        // without needing any positional arguments or network access.
        foreach (var s in args)
            if (s == "--selftest")
            {
                foreach (var v in args) if (v == "--factor-verbose") Factorizer.Verbose = true;
                return Factorizer.SelfTest();
            }

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
                case "-2":
                case "--two-prime":
                    opt.TwoPrime = true;
                    // optional inline selection: a comma list of primes, OR one/two prime bounds.
                    if (i + 1 < args.Length && TryParsePrimeList(args[i + 1], out long[] tpl))
                    { opt.TwoPrimeList = tpl; i++; }
                    else if (i + 1 < args.Length && long.TryParse(args[i + 1], out long lo1) && lo1 >= 2)
                    {
                        i++;
                        if (i + 1 < args.Length && long.TryParse(args[i + 1], out long hi1) && hi1 >= 2)
                        { i++; opt.TwoPrimeLo = Math.Min(lo1, hi1); opt.TwoPrimeHi = Math.Max(lo1, hi1); opt.TwoPrimeList = null; }
                        else { opt.TwoPrimeLo = 3; opt.TwoPrimeHi = lo1; opt.TwoPrimeList = null; }
                    }
                    break;
                case "--two-prime-only": opt.TwoPrime = true; opt.TwoPrimeOnly = true; break;
                case "--two-prime-mode":
                    if (++i >= args.Length) { Console.Error.WriteLine("error: --two-prime-mode needs before|after|alongside."); return 2; }
                    opt.TwoPrimeMode = args[i].ToLowerInvariant();
                    if (opt.TwoPrimeMode is not ("before" or "after" or "alongside"))
                    { Console.Error.WriteLine("error: --two-prime-mode must be before, after, or alongside."); return 2; }
                    break;
                case "--two-prime-cores":
                    if (++i >= args.Length || !int.TryParse(args[i], out opt.TwoPrimeCores) || opt.TwoPrimeCores < 1)
                    { Console.Error.WriteLine("error: --two-prime-cores needs a positive integer."); return 2; }
                    break;
                case "--two-prime-effort":
                    if (++i >= args.Length || !int.TryParse(args[i], out int tpe) || tpe < 0)
                    { Console.Error.WriteLine("error: --two-prime-effort needs a non-negative integer (seconds)."); return 2; }
                    opt.TwoPrimeEffortMs = tpe * 1000; break;
                case "--max-prime":
                    if (++i >= args.Length || !int.TryParse(args[i], out opt.MaxPrime) || opt.MaxPrime < 3)
                    { Console.Error.WriteLine("error: --max-prime needs an integer >= 3."); return 2; }
                    opt.TwoPrimeLo = 3; opt.TwoPrimeHi = opt.MaxPrime; opt.TwoPrimeList = null;
                    break;
                case "--wheel-max":
                    if (++i >= args.Length || !int.TryParse(args[i], out opt.WheelMax) || opt.WheelMax < 3)
                    { Console.Error.WriteLine("error: --wheel-max needs an integer >= 3."); return 2; }
                    break;
                case "--auto-wheel": opt.AutoWheel = true; break;
                case "--wheel-mem-mb":
                    if (++i >= args.Length || !long.TryParse(args[i], out opt.WheelMemMb) || opt.WheelMemMb < 1)
                    { Console.Error.WriteLine("error: --wheel-mem-mb needs a positive integer (MB)."); return 2; }
                    break;
                case "--status-file":
                    if (++i >= args.Length) { Console.Error.WriteLine("error: --status-file needs a path."); return 2; }
                    opt.StatusFile = args[i]; break;
                case "--status-interval":
                    if (++i >= args.Length || !int.TryParse(args[i], out opt.StatusIntervalSec) || opt.StatusIntervalSec < 1)
                    { Console.Error.WriteLine("error: --status-interval needs a positive integer (seconds)."); return 2; }
                    break;
                case "--max-results":
                    if (++i >= args.Length || !long.TryParse(args[i], out opt.MaxResults) || opt.MaxResults < 1)
                    { Console.Error.WriteLine("error: --max-results needs a positive integer."); return 2; }
                    break;
                case "--force-search": opt.ForceSearch = true; break;
                case "--no-factordb": opt.UseFactorDb = false; break;
                case "--factordb-timeout":
                    if (++i >= args.Length || !int.TryParse(args[i], out int fto) || fto < 1)
                    { Console.Error.WriteLine("error: --factordb-timeout needs a positive integer (seconds)."); return 2; }
                    opt.FactorDbTimeoutMs = fto * 1000; break;
                case "--ecm-seconds":
                    if (++i >= args.Length || !int.TryParse(args[i], out int es) || es < 0)
                    { Console.Error.WriteLine("error: --ecm-seconds needs a non-negative integer."); return 2; }
                    opt.EcmBudgetMs = es * 1000; break;
                case "--factor-verbose": opt.FactorVerbose = true; break;
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

        // Configure the factorisation engine used by the two-prime search.
        Factorizer.UseFactorDb = opt.UseFactorDb;
        Factorizer.FactorDbTimeoutMs = opt.FactorDbTimeoutMs;
        Factorizer.EcmBudgetMs = opt.EcmBudgetMs;
        Factorizer.Verbose = opt.FactorVerbose;

        return new Engine(opt, string.Join(' ', args)).Run();
    }

    // A token like "11,13,47,67": a comma-separated list of integers >= 2 (candidate
    // smaller primes for the two-prime search). Returns them sorted and de-duplicated.
    static bool TryParsePrimeList(string tok, out long[] primes)
    {
        primes = null;
        if (tok.IndexOf(',') < 0) return false;
        var vals = new List<long>();
        foreach (var part in tok.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, out long v) || v < 2) return false;
            vals.Add(v);
        }
        if (vals.Count == 0) return false;
        vals.Sort();
        var distinct = new List<long>();
        foreach (var v in vals) if (distinct.Count == 0 || distinct[^1] != v) distinct.Add(v);
        primes = distinct.ToArray();
        return true;
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
        Console.Error.WriteLine("  -2, --two-prime [A [B]] | [p1,p2,...]");
        Console.Error.WriteLine("                     run the two-prime factoring search (paper Cor 5.2).");
        Console.Error.WriteLine("                     with no args: smaller primes 3..70. With one number N:");
        Console.Error.WriteLine("                     primes 3..N. With two numbers A B: primes A..B. With a");
        Console.Error.WriteLine("                     comma list (e.g. 11,47,67): exactly those smaller primes.");
        Console.Error.WriteLine("      --max-prime P  shorthand for two-prime range 3..P (default 70)");
        Console.Error.WriteLine("      --two-prime-mode M   when to run it: before | after | alongside the sweep");
        Console.Error.WriteLine("                           (default after)");
        Console.Error.WriteLine("      --two-prime-only     run ONLY the two-prime search; skip the sweep entirely");
        Console.Error.WriteLine("      --two-prime-cores N  cores given to two-prime when mode=alongside (default 1)");
        Console.Error.WriteLine("      --two-prime-effort S per-N_p ECM budget for two-prime, seconds (overrides");
        Console.Error.WriteLine("                           --ecm-seconds for this phase; raise it for hard primes)");
        Console.Error.WriteLine("      --no-factordb        do not query factordb.com; factor locally only");
        Console.Error.WriteLine("      --factordb-timeout S HTTP timeout for FactorDB, seconds (default 8)");
        Console.Error.WriteLine("      --ecm-seconds S      per-number ECM time budget, seconds (default 20; 0 disables ECM)");
        Console.Error.WriteLine("      --factor-verbose     log FactorDB / ECM activity during factoring");
        Console.Error.WriteLine("      --wheel-max P    bake all compatible odd primes <= P into the wheel modulus");
        Console.Error.WriteLine("                       (default 11; larger = more pre-filtering, bigger build)");
        Console.Error.WriteLine("      --auto-wheel     pick the wheel modulus automatically by a cost/benefit");
        Console.Error.WriteLine("                       model from the n-range (single-shift sweeps only)");
        Console.Error.WriteLine("      --wheel-mem-mb N memory budget for the wheel residue table (default 256)");
        Console.Error.WriteLine("      --status-file P  periodic run-status file (default status.txt)");
        Console.Error.WriteLine("      --status-interval S  status refresh period, seconds (default 300); also");
        Console.Error.WriteLine("                           written on pause/resume/finish/Ctrl+C");
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
    long _solutionsFound;     // sweep solutions only (two-prime counted separately)
    int _sweepCores;          // cores used by the current/most-recent sweep phase

    // ---- run-wide control & reporting ----
    StatusWriter _status;
    Stopwatch _runClock;
    DateTime _startUtc;
    readonly string _cmdline;
    volatile bool _sweepActive;
    volatile bool _tpActive;
    volatile bool _paused;
    double _lastRate;                                       // last sweep rate, for the status file
    Action<int, long, long, CancellationToken> _sweepChunk; // chosen chunk function
    bool _doSweep;                                          // whether a sweep phase runs
    bool _singleShift;
    long _theShift;
    string _regimeDesc = "";                               // single-shift regime description

    // recent solutions (bounded; for the status file)
    readonly List<string> _recentSolutions = new();
    const int RecentCap = 300;

    // ---- two-prime parallel work ----
    (long a, int p)[] _tpWork;
    int _tpNext;
    long _tpDone;
    long _tpSolutions;
    int _tpTotal;
    long[] _tpCurrentP;                                     // per-worker current prime
    readonly object _tpHardLock = new();
    readonly List<(long a, int p)> _tpHard = new();        // (shift, prime) not fully factored
    int _tpCoresUsed;

    // ---- per-shift wheel + small-prime table (single-shift sweep only) ----
    long _a;
    int _wheelMod;
    int[] _wheelResidues;
    int[] _wheelDeltas;
    int _wheelLen;
    int[] _wheelPrimes = { 3, 5, 7, 11 };                  // odd primes baked into the wheel
    double _wheelDensity;                                   // survivor fraction (residues/mod)
    SmallPrime[] _smallPrimes;
    const int TCAP = 8;

    readonly struct SmallPrime
    {
        public readonly uint P, D, K;
        public readonly bool Admissible;
        public SmallPrime(uint p, uint d, uint k, bool adm) { P = p; D = d; K = k; Admissible = adm; }
    }

    public Engine(Options o, string commandLine)
    {
        _o = o;
        _cmdline = commandLine;
        _resultsPath = Path.Combine(Environment.CurrentDirectory, "results.txt");
    }

    public int Run()
    {
        _sink = new ResultSink(_resultsPath, _o.Spill, _o.SpillBytes);
        _shifts = _o.ShiftList; // null => interval mode
        _aLo = _o.StartA; _aHi = _o.EndA;
        if (_shifts != null) _shiftCount = _shifts.Length;
        else { long w = _aHi - _aLo + 1; _shiftCount = w > int.MaxValue ? int.MaxValue : (int)w; }
        _startUtc = DateTime.UtcNow;
        _runClock = Stopwatch.StartNew();

        _singleShift = _shifts != null ? _shifts.Length == 1 : _aLo == _aHi;
        _theShift = _shifts != null ? _shifts[0] : _aLo;

        Console.WriteLine($"n range : [{_o.StartN}, {_o.EndN}]   ({_o.EndN - _o.StartN + 1} values of n)");
        string noSpillNote = _o.Spill ? "" : "   (--no-spill: buffered in memory until the end)";
        if (_singleShift)
        {
            Console.WriteLine($"shift   : a = {_theShift}");
            Console.WriteLine($"output  : {_resultsPath}{noSpillNote}");
            Console.WriteLine();
            PrepareSingle(_theShift);
        }
        else
        {
            if (_shifts != null)
                Console.WriteLine($"shifts  : explicit list of {_shifts.Length} values: {FormatList(_shifts)}");
            else
                Console.WriteLine($"shifts  : a in [{_aLo}, {_aHi}]   ({_aHi - _aLo + 1} shifts)");
            Console.WriteLine($"output  : {_resultsPath}{noSpillNote}");
            Console.WriteLine();
            PrepareRange();
        }

        if (_o.TwoPrime) BuildTwoPrimeWork();

        bool tp = _o.TwoPrime && _tpTotal > 0;
        if (!_doSweep && !tp)
        {
            // Nothing further to run (e.g. a decided single shift already listed analytically,
            // or two-prime requested but no admissible primes). Persist and exit.
            if (_o.TwoPrime && _tpTotal == 0)
                Console.WriteLine("\n[two-prime] no admissible smaller primes in the requested set; nothing to factor.");
            _sink.Flush();
            return 0;
        }

        SetupController();
        try
        {
            DispatchPhases();
        }
        finally
        {
            _status?.Touch(_cts.IsCancellationRequested ? "interrupted" : "finished");
            _status?.Stop();
            if (!_cts.IsCancellationRequested) _cts.Cancel(); // release the reporter/input threads
            _sink.Flush();
        }
        return 0;
    }

    // Decide, classify, and prepare state for a single shift WITHOUT running any phase.
    void PrepareSingle(long a)
    {
        var cls = Nt.Classify(a);
        ReportClassification(a, cls);
        _regimeDesc = cls.Kind switch
        {
            Regime.Empty => "empty (R5)",
            Regime.Zero => "zero, infinite (R3)",
            Regime.PowerOfTwo => $"power of two 2^{cls.J}, infinite (R4)",
            _ => "open"
        };

        bool decided = cls.Kind != Regime.Open;
        if (_o.TwoPrimeOnly)
        {
            Console.WriteLine("[--two-prime-only] skipping the sweep; running the two-prime search only.\n");
            _doSweep = false;
            return;
        }
        if (decided && !_o.ForceSearch)
        {
            switch (cls.Kind)
            {
                case Regime.Empty: Console.WriteLine("No solutions exist for any n (R5)."); break;
                case Regime.Zero: ListPowersOfTwo(); break;
                case Regime.PowerOfTwo: ListPowerOfTwoFamily(cls.J); break;
            }
            _doSweep = false;
            return;
        }

        if (decided)
            Console.WriteLine("[--force-search] sweeping the full n-range despite the shift being decided.\n");
        _a = a;
        _wheelPrimes = ChooseWheelPrimes(a);
        BuildWheel(a, _wheelPrimes);
        BuildSmallPrimeTable(a);
        _chunkN = (long)_wheelMod * Math.Max(1, 500_000 / _wheelMod);
        string pr = string.Join(",", _wheelPrimes);
        Console.WriteLine($"sweep   : wheel mod {_wheelMod} (2-adic x primes {{{pr}}}), {_wheelLen} residues " +
                          $"({100.0 * _wheelDensity:F2}% of integers examined), {_o.Cores} cores");
        _sweepChunk = PerAChunk;
        _doSweep = true;
    }

    static string FormatList(long[] xs)
    {
        if (xs.Length <= 30) return string.Join(", ", xs);
        var head = string.Join(", ", xs[..15]);
        var tail = string.Join(", ", xs[^5..]);
        return $"{head}, ... , {tail}";
    }

    // =====================================================================
    //  Shared run controller + phase scheduling
    // =====================================================================

    void SetupController()
    {
        _cts = new CancellationTokenSource();
        _runEvent = new ManualResetEventSlim(true);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (!_cts.IsCancellationRequested)
            {
                Console.WriteLine("\n[ctrl+c] stopping; finishing current work (a factor in progress may take a moment)...");
                _cts.Cancel(); _runEvent.Set();
                _status?.Touch("interrupted");
            }
        };

        var input = new Thread(InputLoop) { IsBackground = true, Name = "input" };
        input.Start();
        Console.WriteLine("press <Enter> to pause/resume, Ctrl+C to stop.");

        var reporter = new Thread(ConsoleReporterLoop) { IsBackground = true, Name = "reporter" };
        reporter.Start();

        if (!string.IsNullOrEmpty(_o.StatusFile))
        {
            _status = new StatusWriter(_o.StatusFile, _o.StatusIntervalSec, BuildStatus);
            _status.Start();
            _status.Touch("running");
            Console.WriteLine($"status  : {_o.StatusFile} (refreshed every {_o.StatusIntervalSec}s and on pause/finish/Ctrl+C)");
        }
        Console.WriteLine();
    }

    bool Cancelled() => _cts.IsCancellationRequested;

    void DispatchPhases()
    {
        int C = _o.Cores;
        bool tp = _o.TwoPrime && _tpTotal > 0;
        var swSweep = new Stopwatch();

        if (_o.TwoPrimeMode == "alongside" && tp && _doSweep)
        {
            int tpCores = Math.Min(_o.TwoPrimeCores, Math.Max(1, C - 1));
            int sweepCores = Math.Max(1, C - tpCores);
            Console.WriteLine($"[schedule] sweep on {sweepCores} core(s) running alongside two-prime on {tpCores} core(s)\n");
            swSweep.Start();
            var s = StartSweepTasks(sweepCores);
            var t = StartTwoPrimeTasks(tpCores);
            var all = new List<Task>(s); all.AddRange(t);
            try { Task.WaitAll(all.ToArray()); }
            catch (AggregateException ae) { LogAgg(ae); }
            swSweep.Stop();
            _sweepActive = false; _tpActive = false; _sink.Flush();
            PrintSweepSummary(swSweep); SweepNoResults();
            PrintTwoPrimeSummary();
            return;
        }

        // Sequential phases. "before" => two-prime first, else sweep first.
        if (_o.TwoPrimeMode == "before")
        {
            if (tp) { var t = StartTwoPrimeTasks(C); FinishTwoPrime(t); }
            if (_doSweep && !Cancelled()) { swSweep.Start(); var s = StartSweepTasks(C); FinishSweep(s, swSweep); }
        }
        else
        {
            if (_doSweep) { swSweep.Start(); var s = StartSweepTasks(C); FinishSweep(s, swSweep); }
            if (tp && !Cancelled()) { var t = StartTwoPrimeTasks(C); FinishTwoPrime(t); }
        }
    }

    void LogAgg(AggregateException ae)
    {
        foreach (var inner in ae.Flatten().InnerExceptions)
            if (inner is not OperationCanceledException) Console.Error.WriteLine(inner);
    }

    // ---- sweep phase ----
    Task[] StartSweepTasks(int cores)
    {
        _sweepCores = cores;
        _nextChunkStart = _o.StartN;
        _threadLastN = new long[cores];
        _threadCount = new long[cores];
        Array.Fill(_threadLastN, -1L);
        _sweepActive = true;
        var tasks = new Task[cores];
        for (int i = 0; i < cores; i++) { int id = i; tasks[i] = Task.Run(() => Worker(id, _sweepChunk)); }
        return tasks;
    }

    void FinishSweep(Task[] tasks, Stopwatch sw)
    {
        try { Task.WaitAll(tasks); }
        catch (AggregateException ae) { LogAgg(ae); }
        _sweepActive = false;
        sw.Stop();
        _sink.Flush();
        PrintSweepSummary(sw);
        SweepNoResults();
    }

    long SweepExamined(out long frontier)
    {
        long total = 0; frontier = -1;
        var ln = _threadLastN; var cn = _threadCount;
        if (ln == null) return 0;
        for (int i = 0; i < ln.Length; i++)
        {
            total += Interlocked.Read(ref cn[i]);
            long l = Interlocked.Read(ref ln[i]);
            if (l > frontier) frontier = l;
        }
        return total;
    }

    void PrintSweepSummary(Stopwatch sw)
    {
        Console.WriteLine("\n---- sweep summary ----");
        long total = 0, minLast = long.MaxValue;
        for (int i = 0; i < _sweepCores; i++)
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
            Console.WriteLine($"  rate : {total / secs:N0} n/s ({total / secs / Math.Max(1, _sweepCores):N0} per core)");
        Console.WriteLine($"  elapsed : {secs:F2} s");
    }

    void SweepNoResults()
    {
        if (_singleShift)
        {
            if (Interlocked.Read(ref _solutionsFound) == 0)
            {
                long a = _theShift;
                Console.WriteLine("\n---- shifts with no value found in this n-range ----");
                if (Nt.KnownNonEmpty(a))
                    Console.WriteLine($"  non-empty: a = {a} has a solution by reduction (R2/decided), but none in [{_o.StartN}, {_o.EndN}].");
                else if (a == -1)
                    Console.WriteLine($"  none found: a = {a} (provably empty, R5).");
                else
                    Console.WriteLine($"  none found: a = {a} — no solution found here and non-emptiness is open (a-1 = ±2^k).");
            }
        }
        else ReportNoResults();
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

    void PrepareRange()
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

        if (_o.TwoPrimeOnly)
        {
            Console.WriteLine("\n[--two-prime-only] skipping the sweep; running the two-prime search only.");
            _doSweep = false;
            return;
        }

        Console.WriteLine($"\nsweep   : single-pass over m, one residue 2^(m-1) mod m per m, {_o.Cores} cores");
        if (!_o.ForceSearch)
            Console.WriteLine("          (decided shifts -1/0/2^j are not recorded by the sweep; see notes above)");
        Console.WriteLine();

        _chunkN = 500_000;
        _sweepChunk = _shifts != null ? SinglePassListChunk : SinglePassIntervalChunk;
        _doSweep = true;
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
    //  Two-prime search (paper §5, Cor 5.2 / Rmk 5.3) — parallel, schedulable,
    //  pausable/cancellable, with selectable prime set and per-N_p effort.
    // =====================================================================

    // Build the flat (a, p) work list: for every requested shift a != 0 and every
    // selected smaller odd prime p, keep the pairs that pass the cheap admissibility
    // tests (so the progress total counts only primes actually worth factoring).
    void BuildTwoPrimeWork()
    {
        var primes = new List<int>();
        if (_o.TwoPrimeList != null)
        {
            foreach (long v in _o.TwoPrimeList)
                if (v >= 3 && v <= int.MaxValue && Nt.IsPrime((ulong)v)) primes.Add((int)v);
        }
        else
        {
            long lo = Math.Max(3, _o.TwoPrimeLo), hi = _o.TwoPrimeHi;
            for (long p = lo | 1; p <= hi; p += 2)
                if (Nt.IsPrime((ulong)p)) primes.Add((int)p);
        }

        var work = new List<(long, int)>();
        foreach (long a in EnumerateShifts())
        {
            if (a == 0) continue;
            foreach (int p in primes)
            {
                if (a % p == 0) continue;                       // Lemma 2.1
                uint d = Nt.OrderMod(2u, (uint)p);
                int target = (int)(((a % p) + p) % p);
                int k = Nt.DiscreteLog(2u, (uint)target, (uint)p, d);
                if (k < 0) continue;                            // a not in <2> (mod p)
                if ((a & 1L) == 1L && p >= 5 && !Nt.AdmissibleOddA(a, p, (int)d, k)) continue; // §4 sieve
                if (BigInteger.Pow(2, p - 1) - a <= 1) continue;
                work.Add((a, p));
            }
        }
        _tpWork = work.ToArray();
        _tpTotal = _tpWork.Length;
        _tpNext = 0;
    }

    Task[] StartTwoPrimeTasks(int cores)
    {
        _tpCoresUsed = cores;
        _tpCurrentP = new long[cores];
        Factorizer.EcmBudgetMs = _o.TwoPrimeEffortMs >= 0 ? _o.TwoPrimeEffortMs : _o.EcmBudgetMs;
        _tpActive = true;
        PrintTwoPrimeHeader(cores);
        var tasks = new Task[cores];
        for (int i = 0; i < cores; i++) { int id = i; tasks[i] = Task.Run(() => TwoPrimeWorker(id)); }
        return tasks;
    }

    void PrintTwoPrimeHeader(int cores)
    {
        string sel = _o.TwoPrimeList != null
            ? $"primes {{{string.Join(",", _o.TwoPrimeList)}}}"
            : $"odd primes in [{Math.Max(3, _o.TwoPrimeLo)}, {_o.TwoPrimeHi}]";
        string fdb = _o.UseFactorDb ? "FactorDB then " : "";
        double eff = (_o.TwoPrimeEffortMs >= 0 ? _o.TwoPrimeEffortMs : _o.EcmBudgetMs) / 1000.0;
        Console.WriteLine($"\n[two-prime] factoring N_p = 2^(p-1) - a ({fdb}rho/p-1/ECM) for {sel};");
        Console.WriteLine($"[two-prime] {_tpTotal} (shift,prime) task(s) over {cores} core(s), per-N_p ECM budget {eff:0.#}s.");
    }

    void TwoPrimeWorker(int id)
    {
        var token = _cts.Token;
        try
        {
            while (true)
            {
                _runEvent.Wait(token);                          // pause support
                if (token.IsCancellationRequested) break;
                int idx = Interlocked.Increment(ref _tpNext) - 1;
                if (idx >= _tpWork.Length) break;
                var (a, p) = _tpWork[idx];
                Interlocked.Exchange(ref _tpCurrentP[id], p);
                ProcessTwoPrime(a, p, token);
                Interlocked.Exchange(ref _tpCurrentP[id], 0L);
                Interlocked.Increment(ref _tpDone);
            }
        }
        catch (OperationCanceledException) { }
    }

    void ProcessTwoPrime(long a, int p, CancellationToken token)
    {
        uint d = Nt.OrderMod(2u, (uint)p);
        int target = (int)(((a % p) + p) % p);
        int k = Nt.DiscreteLog(2u, (uint)target, (uint)p, d);
        if (k < 0) return;                                       // pre-filtered, but stay safe
        BigInteger N = BigInteger.Pow(2, p - 1) - a;
        if (N <= 1) return;

        var (factors, complete) = Factorizer.Factor(N, token);   // cancellable
        int residue = (int)(((k + 1) % (int)d + (int)d) % (int)d);
        var seen = new HashSet<BigInteger>();
        foreach (var q in factors)
        {
            if (q <= p || !seen.Add(q)) continue;
            if ((int)(q % d) != residue) continue;
            BigInteger m = (BigInteger)p * q;
            BigInteger lhs = BigInteger.ModPow(2, m - 1, m);
            BigInteger rhs = ((new BigInteger(a) % m) + m) % m;
            if (lhs != rhs) continue;                            // exact verification
            BigInteger n = m - 1;
            Interlocked.Increment(ref _tpSolutions);
            lock (_outputLock)
            {
                Console.WriteLine($"  *** SOLUTION  a = {a}, n = {n}, m = {m} = {p} * {q} ***");
                AppendResultBig(n, m, a);
                RecordRecent($"a={a}  n={n}  m={m} = {p} * {q}");
            }
        }
        if (!complete && !token.IsCancellationRequested)
        {
            lock (_tpHardLock) _tpHard.Add((a, p));
            lock (_outputLock)
                Console.WriteLine($"  (N_{p} = 2^{p - 1} - {a} not fully factored within budget; some solutions may be missed)");
        }
    }

    void FinishTwoPrime(Task[] tasks)
    {
        try { Task.WaitAll(tasks); }
        catch (AggregateException ae) { LogAgg(ae); }
        _tpActive = false;
        _sink.Flush();
        PrintTwoPrimeSummary();
    }

    void PrintTwoPrimeSummary()
    {
        long sol = Interlocked.Read(ref _tpSolutions);
        long done = Interlocked.Read(ref _tpDone);
        Console.WriteLine(sol == 0
            ? $"[two-prime] no two-prime solutions found ({done}/{_tpTotal} primes processed)."
            : $"[two-prime] {sol} two-prime solution(s) found ({done}/{_tpTotal} primes processed).");

        List<(long a, int p)> hard;
        lock (_tpHardLock) hard = new List<(long, int)>(_tpHard);
        if (hard.Count > 0)
        {
            int show = Math.Min(hard.Count, 12);
            var items = new List<string>();
            for (int i = 0; i < show; i++) items.Add($"a={hard[i].a},p={hard[i].p}");
            Console.WriteLine($"[two-prime] {hard.Count} N_p not fully factored within budget: "
                              + string.Join("; ", items) + (hard.Count > show ? ", ..." : ""));

            var (newSec, cmds) = BuildRetryCommands();
            Console.WriteLine($"[two-prime] to retry just those N_p with 3x the ECM budget ({newSec}s per N_p), run:");
            foreach (var c in cmds) Console.WriteLine("    " + c);
        }
    }

    // Build the "retry the exceptions" command(s): one per hard shift, listing exactly the
    // primes whose N_p did not fully factor, at 3x the ECM budget that was just used, and
    // with --two-prime-only so only that factoring runs (no re-sweep). Returns the budget
    // (seconds) and the command lines.
    (int newSec, List<string> cmds) BuildRetryCommands()
    {
        var cmds = new List<string>();
        List<(long a, int p)> hard;
        lock (_tpHardLock) hard = new List<(long, int)>(_tpHard);

        int curMs = _o.TwoPrimeEffortMs >= 0 ? _o.TwoPrimeEffortMs : _o.EcmBudgetMs;
        int newSec = curMs > 0 ? Math.Max(1, curMs * 3 / 1000) : 60; // 3x; if ECM was off, suggest 60s
        if (hard.Count == 0) return (newSec, cmds);

        var byShift = new SortedDictionary<long, SortedSet<int>>();
        foreach (var (a, p) in hard)
        {
            if (!byShift.TryGetValue(a, out var s)) { s = new SortedSet<int>(); byShift[a] = s; }
            s.Add(p);
        }
        string extra = _o.UseFactorDb ? "" : " --no-factordb";
        foreach (var kv in byShift)
        {
            string primes = string.Join(",", kv.Value);
            cmds.Add($"TwoNMod3Search {_o.StartN} {_o.EndN} {kv.Key} --two-prime {primes} --two-prime-only --two-prime-effort {newSec}{extra}");
        }
        return (newSec, cmds);
    }

    void RecordRecent(string line)
    {
        // caller holds _outputLock
        if (_recentSolutions.Count < RecentCap) _recentSolutions.Add(line);
        else { _recentSolutions.RemoveAt(0); _recentSolutions.Add(line); }
    }

    // =====================================================================
    //  Per-shift filters (single-shift sweep)
    // =====================================================================

    // ---- wheel sieve --------------------------------------------------------
    //
    // The wheel works as follows. Write m = n+1; the condition is m | 2^(m-1) - a.
    // Pick a modulus  W = 2^t * (product of some odd primes).  Because membership at
    // a prime p | m depends only on m mod p (and on (m-1) mod ord_p(2)), and because
    // we choose the odd primes so that ord_p(2) divides W, the question "could m be a
    // solution as far as the primes dividing W are concerned?" depends ONLY on m mod W.
    // So we enumerate the residues r in [0, W) that survive those local tests once, and
    // the sweep then visits only m ≡ (surviving r) (mod W), stepping by precomputed gaps.
    // For a = -3 the default modulus is 4620 = 2^2·3·5·7·11 and ~20.8% of residues
    // survive, so the expensive test 2^(m-1) mod m runs for only ~1 in 5 integers.
    //
    // Baking MORE odd primes into W lowers the surviving fraction further (a "heftier"
    // one-off computation for a faster sweep), but W — and the build cost — grow as the
    // product of those primes. ChooseWheelPrimes picks the set: fixed (<= --wheel-max),
    // or, with --auto-wheel, greedily until the predicted sweep saving over the actual
    // n-range no longer outweighs the extra build cost (calibrated on this machine).
    //
    // IMPORTANT: an odd prime p may be baked in only if ord_p(2) | W, otherwise the
    // local test at p would not be a function of m mod W and the wheel would be unsound.
    // We add primes in increasing order and only when their order already divides W.
    // (Primes such as 37, whose order 36 = 4·9 needs 3^2, are skipped — W is squarefree
    // in its odd part.)  Note: extending the wheel past the small-prime table primes
    // (13..97) mostly saves iteration overhead rather than 2^(m-1) mod m calls, since the
    // table already rejects multiples of those primes before the modpow; the real per-op
    // win for the sweep is the branchless Montgomery doubling in PowMod2.

    int[] ChooseWheelPrimes(long a)
    {
        int v2a = BitOperations.TrailingZeroCount((ulong)a);
        int t = Math.Max(2, Math.Min(v2a + 1, TCAP));
        long modCap = 1L << 30;                                   // residues stay in int range
        long survivorCap = Math.Max(1_000_000L, _o.WheelMemMb * 1024L * 1024L / 8L); // 8 bytes/survivor

        var included = new List<int>();
        var set = new HashSet<int>();
        long W = 1L << t;
        double density = TwoAdicFraction(t, v2a);

        double tTest = 0, tBuild = 0; long R = _o.EndN - _o.StartN + 1;
        if (_o.AutoWheel) { (tTest, tBuild) = CalibrateWheel(); }

        int explore = _o.AutoWheel ? 100_000 : _o.WheelMax;
        for (int p = 3; p <= explore; p += 2)
        {
            if (!Nt.IsPrime((ulong)p)) continue;
            if (!_o.AutoWheel && p > _o.WheelMax) break;
            int dp = (int)Nt.OrderMod(2u, (uint)p);
            if (!OrderDividesWheel(dp, t, set)) continue;          // soundness: ord_p(2) | W
            long Wp = W * p;
            if (Wp > modCap) break;
            double gp = WheelPrimeFraction(a, p, dp);
            double newDensity = density * gp;
            if ((long)(Wp * newDensity) + 1 > survivorCap) break;

            if (_o.AutoWheel)
            {
                double sweepSaveDelta = R * density * (1.0 - gp) * tTest; // fewer modpows
                double buildCostDelta = (double)(Wp - W) * tBuild;         // bigger residue enumeration
                if (sweepSaveDelta <= buildCostDelta) break;              // diminishing returns
            }
            included.Add(p); set.Add(p); W = Wp; density = newDensity;
        }

        if (included.Count == 0) included.Add(3); // defensive; 3 is always compatible for t>=2
        return included.ToArray();
    }

    // ord_p(2) = dp divides W = 2^t * (product of distinct odd primes in `set`).
    static bool OrderDividesWheel(int dp, int t, HashSet<int> set)
    {
        int v2 = BitOperations.TrailingZeroCount((uint)dp);
        if (v2 > t) return false;
        int odd = dp >> v2;
        for (int f = 3; (long)f * f <= odd; f += 2)
            if (odd % f == 0)
            {
                odd /= f;
                if (odd % f == 0) return false;     // odd part not squarefree -> W can't contain it
                if (!set.Contains(f)) return false;
            }
        if (odd > 1 && !set.Contains(odd)) return false;
        return true;
    }

    // Fraction of residues mod 2^t passing the 2-adic admissibility test.
    static double TwoAdicFraction(int t, int v2a)
    {
        int span = 1 << t, cnt = 0;
        for (int r = 0; r < span; r++)
        {
            bool ok2 = r != 0 ? BitOperations.TrailingZeroCount(r) <= v2a : t <= v2a;
            if (ok2) cnt++;
        }
        return (double)cnt / span;
    }

    // Fraction of residues mod p that survive the wheel test at odd prime p:
    //   (p-1)/p  for the units, plus (1/p)*(1/ord) for the multiples of p when a in <2>.
    static double WheelPrimeFraction(long a, int p, int dp)
    {
        int target = (int)(((a % p) + p) % p);
        int k = Nt.DiscreteLog(2u, (uint)target, (uint)p, (uint)dp);
        double adm = k >= 0 ? 1.0 / dp : 0.0;
        return (p - 1.0) / p + adm / p;
    }

    // Time one ResidueOf and one residue-build check, in ns, to drive --auto-wheel.
    (double tTest, double tBuild) CalibrateWheel()
    {
        const int n = 200_000;
        ulong m0 = (ulong)Math.Max(3, _o.StartN + 1) | 1UL;
        var sw = Stopwatch.StartNew();
        ulong acc = 0;
        for (int i = 0; i < n; i++) acc += Nt.ResidueOf(m0 + (ulong)i * 2);
        sw.Stop();
        double tTest = sw.Elapsed.TotalMilliseconds * 1e6 / n;
        if (acc == 42) Console.Write("");

        sw.Restart();
        long s = 0; int[] ps = { 3, 5, 7, 11, 13, 17 };
        for (int r = 0; r < n; r++)
            for (int j = 0; j < ps.Length; j++)
                if (r % ps[j] == 0) s += Nt.PowModSmall(2, r % 12, ps[j]);
        sw.Stop();
        double tBuild = sw.Elapsed.TotalMilliseconds * 1e6 / n;
        if (s == 42) Console.Write("");
        return (tTest, Math.Max(tBuild, 0.2));
    }

    void BuildWheel(long a, int[] oddPrimes)
    {
        int v2a = BitOperations.TrailingZeroCount((ulong)a);
        int t = Math.Max(2, Math.Min(v2a + 1, TCAP));
        long Wl = 1L << t;
        foreach (int p in oddPrimes) Wl *= p;
        int W = (int)Wl;                          // bounded < 2^30 by ChooseWheelPrimes
        _wheelMod = W;

        int[] ord = new int[oddPrimes.Length];
        int[] target = new int[oddPrimes.Length];
        for (int i = 0; i < oddPrimes.Length; i++)
        {
            ord[i] = (int)Nt.OrderMod(2u, (uint)oddPrimes[i]);
            target[i] = (int)(((a % oddPrimes[i]) + oddPrimes[i]) % oddPrimes[i]);
        }

        var swBuild = Stopwatch.StartNew();
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
        swBuild.Stop();

        if (residues.Count == 0) residues.Add(1); // pathological; keep iteration valid
        _wheelResidues = residues.ToArray();
        _wheelLen = _wheelResidues.Length;
        _wheelDensity = (double)_wheelLen / W;
        _wheelDeltas = new int[_wheelLen];
        for (int i = 0; i < _wheelLen; i++)
        {
            int next = (i + 1 < _wheelLen) ? _wheelResidues[i + 1] : _wheelResidues[0] + W;
            _wheelDeltas[i] = next - _wheelResidues[i];
        }
        if (swBuild.ElapsedMilliseconds >= 50)
            Console.WriteLine($"          (wheel built in {swBuild.Elapsed.TotalSeconds:F2}s)");
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
    //  Sweep worker + console reporter + pause loop
    // =====================================================================

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
            if (_runEvent.IsSet)
            {
                _runEvent.Reset(); _paused = true;
                Console.WriteLine("[paused] press <Enter> to resume.");
                _status?.Touch("paused");
            }
            else
            {
                _runEvent.Set(); _paused = false;
                Console.WriteLine("[resumed]");
                _status?.Touch("resumed");
            }
        }
    }

    string TpCurrentDesc()
    {
        var cp = _tpCurrentP;
        if (cp == null) return "";
        var ps = new List<long>();
        for (int i = 0; i < cp.Length; i++) { long v = Interlocked.Read(ref cp[i]); if (v != 0) ps.Add(v); }
        return ps.Count == 0 ? "" : $", factoring N_p for p = {string.Join(",", ps)}";
    }

    void ConsoleReporterLoop()
    {
        long lastTotal = 0;
        var sw = Stopwatch.StartNew();
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { Task.Delay(5000, token).Wait(token); } catch { return; }
            if (!_runEvent.IsSet) continue;
            if (_sweepActive)
            {
                long total = SweepExamined(out long frontier);
                double dt = sw.Elapsed.TotalSeconds; sw.Restart();
                double rate = dt > 0 ? (total - lastTotal) / dt : 0; lastTotal = total; _lastRate = rate;
                Console.WriteLine($"[sweep] frontier n = {frontier}, examined = {total:N0}, rate = {rate:N0} n/s, solutions = {Interlocked.Read(ref _solutionsFound)}");
            }
            if (_tpActive)
                Console.WriteLine($"[two-prime] {Interlocked.Read(ref _tpDone)}/{_tpTotal} primes done, {Interlocked.Read(ref _tpSolutions)} solution(s){TpCurrentDesc()}");
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
            {
                Console.WriteLine($"*** SOLUTION  a = {a}, n = {n}, m = {m} ***");
                RecordRecent($"a={a}  n={n}  m={m}");
            }
            else if (idx == _o.MaxResults + 1)
                Console.WriteLine($"... (more than {_o.MaxResults} solutions; further hits buffered to {Path.GetFileName(_resultsPath)} only)");
        }
        _sink.Add(n, m, a);
    }

    void AppendResult(long n, long m, long a) => _sink.Add(n, m, a);

    void AppendResultBig(BigInteger n, BigInteger m, long a) => _sink.Add(n, m, a);

    // =====================================================================
    //  Status file body (written periodically and on state changes)
    // =====================================================================

    string BuildStatus(string state)
    {
        var sb = new StringBuilder();
        TimeSpan el = _runClock?.Elapsed ?? TimeSpan.Zero;
        sb.AppendLine("TwoNMod3Search — run status");
        sb.AppendLine($"generated   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z   (state: {state.ToUpperInvariant()})");
        sb.AppendLine($"started     : {_startUtc:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"elapsed     : {el:hh\\:mm\\:ss}  ({el.TotalSeconds:F1} s)");
        sb.AppendLine($"command     : {_cmdline}");
        sb.AppendLine($"host cores  : {Environment.ProcessorCount} logical;  using {_o.Cores}");
        if (_singleShift)
            sb.AppendLine($"mode        : single shift a = {_theShift}   (regime: {_regimeDesc})");
        else if (_shifts != null)
            sb.AppendLine($"mode        : explicit list of {_shifts.Length} shifts");
        else
            sb.AppendLine($"mode        : shift interval [{_aLo}, {_aHi}]  ({_shiftCount} shifts)");
        sb.AppendLine($"n range     : [{_o.StartN}, {_o.EndN}]   (width {_o.EndN - _o.StartN + 1})");
        sb.AppendLine($"output file : {_resultsPath}");
        sb.AppendLine();

        sb.AppendLine("[parameters]");
        sb.AppendLine($"  cores={_o.Cores}  max-results={_o.MaxResults}  spill={(_o.Spill ? $"on ({_o.SpillBytes / (1024 * 1024)} MB)" : "off")}");
        sb.AppendLine($"  factordb={(_o.UseFactorDb ? $"on (timeout {_o.FactorDbTimeoutMs / 1000}s)" : "off")}  ecm-budget={_o.EcmBudgetMs / 1000}s");
        if (_o.TwoPrime)
        {
            string sel = _o.TwoPrimeList != null ? $"[{string.Join(",", _o.TwoPrimeList)}]" : $"[{Math.Max(3, _o.TwoPrimeLo)}..{_o.TwoPrimeHi}]";
            double eff = (_o.TwoPrimeEffortMs >= 0 ? _o.TwoPrimeEffortMs : _o.EcmBudgetMs) / 1000.0;
            sb.AppendLine($"  two-prime: mode={_o.TwoPrimeMode}, cores={(_o.TwoPrimeMode == "alongside" ? _o.TwoPrimeCores : _o.Cores)}, primes={sel}, effort={eff:0.#}s");
        }
        if (_doSweep && _singleShift)
            sb.AppendLine($"  wheel: mod={_wheelMod}, residues={_wheelLen}, density={100.0 * _wheelDensity:F2}%, primes={{{string.Join(",", _wheelPrimes)}}}, auto={(_o.AutoWheel ? "yes" : "no")}");
        sb.AppendLine();

        if (_doSweep)
        {
            sb.AppendLine("[sweep]");
            sb.AppendLine($"  state      : {(_sweepActive ? (_paused ? "paused" : "running") : "done")}");
            long examined = SweepExamined(out long frontier);
            double pct = (_o.EndN - _o.StartN + 1) > 0 ? 100.0 * examined / (_o.EndN - _o.StartN + 1) : 0;
            double avg = el.TotalSeconds > 0 ? examined / el.TotalSeconds : 0;
            sb.AppendLine($"  frontier n : {frontier}");
            sb.AppendLine($"  examined   : {examined:N0}  ({pct:F3}% of range)");
            sb.AppendLine($"  rate       : {(_lastRate > 0 ? _lastRate : avg):N0} n/s (avg {avg:N0})");
            sb.AppendLine($"  solutions  : {Interlocked.Read(ref _solutionsFound)}");
            sb.AppendLine();
        }

        if (_o.TwoPrime && _tpTotal > 0)
        {
            sb.AppendLine("[two-prime]");
            sb.AppendLine($"  state      : {(_tpActive ? (_paused ? "paused" : "running") : "done")}");
            sb.AppendLine($"  primes     : {Interlocked.Read(ref _tpDone)} / {_tpTotal} done");
            sb.AppendLine($"  solutions  : {Interlocked.Read(ref _tpSolutions)}");
            string cur = TpCurrentDesc(); if (cur.Length > 0) sb.AppendLine($"  current    :{cur}");
            List<(long a, int p)> hard;
            lock (_tpHardLock) hard = new List<(long, int)>(_tpHard);
            if (hard.Count > 0)
            {
                int show = Math.Min(hard.Count, 12);
                var items = new List<string>();
                for (int i = 0; i < show; i++) items.Add($"a={hard[i].a},p={hard[i].p}");
                sb.AppendLine($"  hard       : {hard.Count} not fully factored: {string.Join("; ", items)}{(hard.Count > show ? ", ..." : "")}");
                var (newSec, cmds) = BuildRetryCommands();
                sb.AppendLine($"  retry (3x ECM budget, {newSec}s per N_p):");
                foreach (var c in cmds) sb.AppendLine("    " + c);
            }
            sb.AppendLine();
        }

        lock (_outputLock)
        {
            sb.AppendLine($"[solutions found] ({_recentSolutions.Count} most recent shown; full list in results.txt)");
            if (_recentSolutions.Count == 0) sb.AppendLine("  (none yet)");
            else foreach (var s in _recentSolutions) sb.AppendLine("  " + s);
        }
        return sb.ToString();
    }
}

// =========================================================================
//  StatusWriter: writes a human-readable run-status file periodically and on
//  state changes (pause/resume/finish/Ctrl+C). The body is supplied by a
//  callback so it always reflects the engine's live counters. Writes are
//  serialized and best-effort: any I/O error is swallowed so reporting can
//  never crash or stall the search.
// =========================================================================
public sealed class StatusWriter
{
    readonly string _path;
    readonly int _intervalSec;
    readonly Func<string, string> _build;
    readonly object _lock = new();
    Thread _thread;
    volatile bool _stop;
    DateTime _lastWrite = DateTime.MinValue;

    public StatusWriter(string path, int intervalSec, Func<string, string> build)
    {
        _path = path; _intervalSec = Math.Max(1, intervalSec); _build = build;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "status" };
        _thread.Start();
    }

    void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(1000);
            if (_stop) break;
            if ((DateTime.UtcNow - _lastWrite).TotalSeconds >= _intervalSec)
                Write("running");
        }
    }

    public void Touch(string state) => Write(state);

    void Write(string state)
    {
        lock (_lock)
        {
            try { File.WriteAllText(_path, _build(state)); _lastWrite = DateTime.UtcNow; }
            catch { /* best-effort: never let status I/O disrupt the search */ }
        }
    }

    public void Stop() { _stop = true; }
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

    // ---- Montgomery 2^e mod m, m odd, 1 < m < 2^62 ----
    // Base-2 exponentiation: each set bit multiplies by 2, which in the Montgomery
    // domain is a modular doubling (2*xR mod m = (2x)R mod m), not a full Montgomery
    // multiply. Since m < 2^62 the doubling fits in ulong. The conditional subtract
    // is done BRANCHLESS (an arithmetic mask): a data-dependent branch here measured
    // ~25% slower than the multiply it saves, whereas the branchless form is ~10%
    // faster than multiplying by a "twoMont" constant. ResidueOf/PowMod2 is the hot
    // path of every sweep, so this matters.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong PowMod2(ulong e, ulong m)
    {
        ulong mInvNeg = NegInvMod64(m);
        ulong Rmod; unchecked { Rmod = (0UL - m) % m; }
        ulong res = Rmod;                       // 1 in Montgomery form
        int top = 63; while (top > 0 && (e >> top) == 0) top--;
        for (int i = top; i >= 0; i--)
        {
            res = MontMul(res, res, m, mInvNeg);            // square
            if (((e >> i) & 1UL) != 0)
            {
                ulong t = res + res;                        // < 2^63 since res < m <= 2^62
                ulong msk = (ulong)(-(long)(((t - m) >> 63) ^ 1)); // all-ones iff t >= m
                res = t - (m & msk);                        // subtract m iff t >= m (branchless)
            }
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

    // ---- factorization entry point ----
    // Delegates to the Factorizer engine: small-prime trial division, an online
    // FactorDB lookup for large cofactors (every returned factor verified locally),
    // then a tiered local fallback (Montgomery rho for <=64-bit factors, Pollard p-1,
    // and ECM for the medium factors that rho cannot reach).
    public static (List<BigInteger> factors, bool complete) Factor(BigInteger n)
        => Factorizer.Factor(n);
}

// =========================================================================
//  Factorizer: small-prime trial division + FactorDB + tiered local methods
// =========================================================================
//
// Factor(n) returns the multiset of prime factors of |n| together with a
// "complete" flag that is false iff a composite cofactor could not be split
// within the configured effort. The strategy, in order:
//
//   1. trial division by primes < 10^5;
//   2. for any remaining composite cofactor large enough to be worth a lookup,
//      query factordb.com. Results are VERIFIED locally: every returned factor
//      is primality-tested and the product is checked against the input, so a
//      wrong or malicious response can never introduce a bogus factor — at worst
//      it is ignored and we fall back to local factoring. This is what makes the
//      numbers N_p = 2^(p-1) - a (a "2^k +/- small" shape, heavily represented in
//      FactorDB) cheap to factor, so --max-prime can be pushed much higher;
//   3. local fallback when offline or when FactorDB has no factorisation:
//        - Brent's rho in Montgomery 64-bit arithmetic for cofactors <= 2^64
//          (cracked essentially instantly),
//        - Pollard p-1 (cheap; catches factors p with smooth p-1),
//        - the elliptic-curve method (ECM; Montgomery curves, two stages), which
//          finds medium (~20-35 digit) factors that rho cannot reach.
//
// Every two-prime solution is still re-verified by a direct modular exponentiation
// in ProcessTwoPrime, so correctness never depends on the factoriser or the network.
public static class Factorizer
{
    // ---- configuration (set once at startup from Options) ----
    public static bool UseFactorDb = true;
    public static int FactorDbTimeoutMs = 8000;
    public static int EcmBudgetMs = 20000;   // per-number wall-clock budget for ECM
    public static bool Verbose;

    // Only consult FactorDB for cofactors with at least this many decimal digits;
    // smaller ones are faster to crack locally than to round-trip over the network.
    const int FactorDbMinDigits = 13;

    static readonly int[] SmallTrial = Nt.PrimesUpTo(100_000).ToArray();

    // Caches so repeated cofactors (within a run) are cheap, and so a single
    // network failure disables further lookups instead of stalling every call.
    static readonly ConcurrentDictionary<BigInteger, byte> _fdbNoProgress = new();
    static volatile bool _networkDead;

    // =====================================================================
    //  Public entry point
    // =====================================================================
    public static (List<BigInteger> factors, bool complete) Factor(BigInteger n, CancellationToken ct = default)
    {
        var factors = new List<BigInteger>();
        if (n.Sign < 0) n = -n;
        if (n <= 1) return (factors, true);

        // (1) trial division by small primes
        foreach (int p in SmallTrial)
        {
            if ((BigInteger)p * p > n) break;
            while (n % p == 0) { factors.Add(p); n /= p; }
        }
        if (n == 1) { factors.Sort(); return (factors, true); }

        bool complete = ResolveComposite(n, factors, ct);
        factors.Sort();
        return (factors, complete);
    }

    // Split every composite cofactor down to primes. Returns false if some
    // composite could not be resolved within the configured effort.
    static bool ResolveComposite(BigInteger start, List<BigInteger> outPrimes, CancellationToken ct)
    {
        bool complete = true;
        var stack = new Stack<BigInteger>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) { outPrimes.Add(stack.Pop()); return false; }
            BigInteger x = stack.Pop();
            if (x <= 1) continue;
            if (Nt.IsPrimeBig(x)) { outPrimes.Add(x); continue; }

            // (2) FactorDB, for cofactors worth a network round-trip
            if (UseFactorDb && !_networkDead && !_fdbNoProgress.ContainsKey(x)
                && DecimalDigits(x) >= FactorDbMinDigits)
            {
                if (TryFactorDb(x, out var fdbPrimes, out var fdbComposites))
                {
                    foreach (var pr in fdbPrimes) outPrimes.Add(pr);
                    foreach (var co in fdbComposites) stack.Push(co);
                    continue;
                }
                _fdbNoProgress.TryAdd(x, 1);
            }

            // (3) local fallback: rho -> p-1 -> ECM
            BigInteger f = FindFactor(x, ct);
            if (f <= 1 || f >= x) { complete = false; outPrimes.Add(x); continue; }
            stack.Push(f);
            stack.Push(x / f);
        }
        return complete;
    }

    static BigInteger FindFactor(BigInteger n, CancellationToken ct)
    {
        if (n <= ulong.MaxValue)
            return BrentRho64((ulong)n);                 // 64-bit composites: instant

        BigInteger g = BrentRhoBig(n, 3_000_000, ct);    // quick: small factors of big n (<~13 digits)
        if (g > 1 && g < n) return g;
        g = PollardPMinus1(n, 100_000);                  // smooth-(p-1) factors
        if (g > 1 && g < n) return g;
        g = EcmFindFactor(n, EcmBudgetMs, ct);           // medium factors (the heavy lifter)
        return (g > 1 && g < n) ? g : 0;
    }

    // =====================================================================
    //  FactorDB
    // =====================================================================
    static HttpClient _http;
    static HttpClient Http()
    {
        if (_http == null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, FactorDbTimeoutMs)) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("TwoNMod3Search/1.0 (+factordb)");
        }
        return _http;
    }

    static bool TryFactorDb(BigInteger n, out List<BigInteger> primeFactors, out List<BigInteger> compositeCofactors)
    {
        primeFactors = new List<BigInteger>();
        compositeCofactors = new List<BigInteger>();
        try
        {
            string url = "https://factordb.com/api?query=" + n.ToString();
            string json = Http().GetStringAsync(url).GetAwaiter().GetResult();
            bool ok = ParseFactorDb(json, n, out primeFactors, out compositeCofactors);
            if (ok && Verbose)
                Console.WriteLine($"  [factordb] {Trunc(n)} -> {primeFactors.Count} prime + {compositeCofactors.Count} composite factor(s)");
            return ok;
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
                _networkDead = true; // unreachable/blocked/timeout: stop trying for the rest of the run
            if (Verbose)
                Console.WriteLine($"  [factordb] lookup failed ({ex.GetType().Name}); using local factoring.");
            return false;
        }
    }

    // Pure parser, separated for testability. Returns true only if it learned a
    // genuine, fully-verified split of n (product of returned factors == n).
    public static bool ParseFactorDb(string json, BigInteger n,
                                     out List<BigInteger> primeFactors,
                                     out List<BigInteger> compositeCofactors)
    {
        primeFactors = new List<BigInteger>();
        compositeCofactors = new List<BigInteger>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("factors", out var facs) || facs.ValueKind != JsonValueKind.Array)
            return false;

        var parsed = new List<(BigInteger f, int e)>();
        foreach (var item in facs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 1) continue;
            JsonElement fe = item[0];
            string fs = fe.ValueKind == JsonValueKind.String ? fe.GetString() : fe.GetRawText();
            if (!BigInteger.TryParse(fs, out var f) || f <= 1) return false;
            int e = 1;
            if (item.GetArrayLength() >= 2)
            {
                JsonElement ee = item[1];
                if (ee.ValueKind == JsonValueKind.Number) e = ee.GetInt32();
                else if (ee.ValueKind == JsonValueKind.String && int.TryParse(ee.GetString(), out int ev)) e = ev;
            }
            if (e < 1) e = 1;
            parsed.Add((f, e));
        }
        if (parsed.Count == 0) return false;

        // Verify the factorisation against the input — never trust it blindly.
        BigInteger prod = 1;
        foreach (var (f, e) in parsed) prod *= BigInteger.Pow(f, e);
        if (prod != n) return false;

        // Progress means we learned a proper factor (not just "n is composite").
        bool progress = parsed.Count >= 2 || parsed[0].e >= 2 || Nt.IsPrimeBig(parsed[0].f);
        if (!progress) return false;

        foreach (var (f, e) in parsed)
        {
            bool prime = Nt.IsPrimeBig(f);
            for (int i = 0; i < e; i++)
                (prime ? primeFactors : compositeCofactors).Add(f);
        }
        return true;
    }

    // =====================================================================
    //  Pollard rho (64-bit Montgomery + big-integer variants)
    // =====================================================================
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MulMod(ulong a, ulong b, ulong m) => (ulong)((UInt128)a * b % m);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong AddMod(ulong a, ulong b, ulong m) => (ulong)(((UInt128)a + b) % m);
    static ulong Gcd(ulong a, ulong b) { while (b != 0) { ulong t = a % b; a = b; b = t; } return a; }

    // Brent's improvement to Pollard rho, all arithmetic in fast 64-bit modmul.
    public static ulong BrentRho64(ulong n)
    {
        if (n == 1) return 1;
        if ((n & 1) == 0) return 2;
        var rng = new Random(0x5DEECE66);
        for (int attempt = 0; attempt < 64; attempt++)
        {
            ulong c = (ulong)(rng.NextInt64() & long.MaxValue) % (n - 1) + 1;
            ulong y = (ulong)(rng.NextInt64() & long.MaxValue) % (n - 1) + 1;
            ulong x = 0, ys = 0, g = 1, q = 1;
            long r = 1, m = 128;
            do
            {
                x = y;
                for (long i = 0; i < r; i++) y = AddMod(MulMod(y, y, n), c, n);
                long k = 0;
                while (k < r && g == 1)
                {
                    ys = y;
                    long lim = Math.Min(m, r - k);
                    for (long i = 0; i < lim; i++)
                    {
                        y = AddMod(MulMod(y, y, n), c, n);
                        ulong d = x > y ? x - y : y - x;
                        q = MulMod(q, d == 0 ? 1 : d, n);
                    }
                    g = Gcd(q, n);
                    k += lim;
                }
                r <<= 1;
            } while (g == 1);
            if (g == n)
            {
                do { ys = AddMod(MulMod(ys, ys, n), c, n); ulong d = x > ys ? x - ys : ys - x; g = Gcd(d, n); }
                while (g == 1);
            }
            if (g > 1 && g < n) return g;
        }
        return 0;
    }

    static BigInteger BrentRhoBig(BigInteger n, long maxIters, CancellationToken ct = default)
    {
        if (n % 2 == 0) return 2;
        if (n % 3 == 0) return 3;
        var rng = new Random(987654321);
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
                        if (++iters > maxIters || ((iters & 8191) == 0 && ct.IsCancellationRequested)) return 0;
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

    // =====================================================================
    //  Pollard p-1 (stage 1)
    // =====================================================================
    static BigInteger PollardPMinus1(BigInteger n, int B1)
    {
        BigInteger a = 2;
        int cnt = 0;
        foreach (int p in SievePrimes(B1))
        {
            long pk = p;
            while ((double)pk * p <= B1) pk *= p;
            a = BigInteger.ModPow(a, pk, n);
            if (++cnt % 64 == 0)
            {
                BigInteger gp = BigInteger.GreatestCommonDivisor(a - 1, n);
                if (gp > 1 && gp < n) return gp;
                if (gp == n) return 0;
            }
        }
        BigInteger g = BigInteger.GreatestCommonDivisor(a - 1, n);
        return (g > 1 && g < n) ? g : 0;
    }

    // =====================================================================
    //  ECM (Lenstra) on Montgomery curves, Suyama parameterisation, two stages
    // =====================================================================
    static BigInteger EcmFindFactor(BigInteger n, int budgetMs, CancellationToken ct = default)
    {
        if (budgetMs <= 0) return 0;
        var sw = Stopwatch.StartNew();
        var rng = new Random();
        // (B1, B2, curves) rounds; the wall-clock budget caps total work.
        (int B1, int B2, int curves)[] schedule =
        {
            (2_000,    60_000,    30),
            (11_000,   330_000,   60),
            (50_000,   600_000,   100),
            (250_000,  600_000,   160),
        };
        if (Verbose) Console.WriteLine($"  [ecm] {Trunc(n)} (budget {budgetMs} ms)");
        foreach (var (B1, B2, curves) in schedule)
        {
            int[] stage1 = SievePrimes(B1);
            bool[] flags = SieveFlags(B2);
            for (int c = 0; c < curves; c++)
            {
                if (sw.ElapsedMilliseconds > budgetMs || ct.IsCancellationRequested) return 0;
                BigInteger sigma = 6 + (BigInteger)(rng.NextInt64() & long.MaxValue);
                BigInteger f = EcmOneCurve(n, sigma, B1, B2, stage1, flags);
                if (f > 1 && f < n)
                {
                    if (Verbose) Console.WriteLine($"  [ecm] factor {Trunc(f)} (B1={B1}, curve {c + 1})");
                    return f;
                }
            }
        }
        return 0;
    }

    static BigInteger EcmOneCurve(BigInteger n, BigInteger sigma, int B1, int B2, int[] stage1Primes, bool[] isPrimeUpToB2)
    {
        // Suyama parameterisation of a Montgomery curve and a point on it.
        BigInteger u = Mod(sigma * sigma - 5, n);
        BigInteger v = Mod(4 * sigma, n);
        if (u == 0 || v == 0) return 0;
        BigInteger u3 = Mod(u * u % n * u, n);
        BigInteger denom = Mod(16 * u3 % n * v, n);                 // 16 u^3 v
        if (!TryModInverse(denom, n, out BigInteger denomInv, out BigInteger gden))
            return (gden > 1 && gden < n) ? gden : 0;               // a non-invertible step IS a factor
        BigInteger vmu = Mod(v - u, n);
        BigInteger a24 = Mod(vmu * vmu % n * vmu, n);               // (v-u)^3
        a24 = Mod(a24 * Mod(3 * u + v, n), n);                      // * (3u+v)
        a24 = Mod(a24 * denomInv, n);                              // / (16 u^3 v) = (A+2)/4
        BigInteger X = u3;
        BigInteger Z = Mod(v * v % n * v, n);                       // v^3

        // ---- stage 1: multiply the point by every prime power <= B1 ----
        foreach (int p in stage1Primes)
        {
            long pk = p;
            while ((double)pk * p <= B1) pk *= p;
            (X, Z) = Ladder(pk, X, Z, a24, n);
        }
        BigInteger g = BigInteger.GreatestCommonDivisor(Z, n);
        if (g > 1 && g < n) return g;
        if (g == n) return 0;
        if (B2 <= B1) return 0;

        // ---- stage 2: walk j = 2..B2, accumulate Z(jQ) for primes j in (B1,B2] ----
        BigInteger qX = X, qZ = Z;                 // Q = stage-1 point
        BigInteger px = qX, pz = qZ;               // S_1 = Q
        var (cx, cz) = XDBL(qX, qZ, a24, n);       // S_2 = 2Q
        BigInteger acc = 1;
        int sinceGcd = 0;
        for (int j = 2; j <= B2; j++)
        {
            if (j > B1 && isPrimeUpToB2[j])
            {
                BigInteger zmod = cz % n;
                if (zmod.Sign != 0) acc = acc * zmod % n;          // p | Z(jQ) when jQ == O (mod p)
                if (++sinceGcd >= 256)
                {
                    sinceGcd = 0;
                    BigInteger gg = BigInteger.GreatestCommonDivisor(acc, n);
                    if (gg > 1 && gg < n) return gg;
                    if (gg == n) acc = 1;
                }
            }
            var (nx, nz) = XADD(cx, cz, qX, qZ, px, pz, n);        // S_{j+1} = S_j + Q (diff S_{j-1})
            px = cx; pz = cz; cx = nx; cz = nz;
        }
        BigInteger gf = BigInteger.GreatestCommonDivisor(acc, n);
        return (gf > 1 && gf < n) ? gf : 0;
    }

    // ---- Montgomery curve X:Z arithmetic ----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static BigInteger Mod(BigInteger x, BigInteger n) { x %= n; return x.Sign < 0 ? x + n : x; }

    static (BigInteger X, BigInteger Z) XDBL(BigInteger X, BigInteger Z, BigInteger a24, BigInteger n)
    {
        BigInteger t1 = Mod((X + Z) * (X + Z), n);
        BigInteger t2 = Mod((X - Z) * (X - Z), n);
        BigInteger t = Mod(t1 - t2, n);
        BigInteger x2 = Mod(t1 * t2, n);
        BigInteger z2 = Mod(t * Mod(t2 + Mod(a24 * t, n), n), n);
        return (x2, z2);
    }

    // X(P+Q) given X(P), X(Q) and X(P-Q) = (Xd:Zd).
    static (BigInteger X, BigInteger Z) XADD(BigInteger XP, BigInteger ZP, BigInteger XQ, BigInteger ZQ,
                                             BigInteger Xd, BigInteger Zd, BigInteger n)
    {
        BigInteger uu = Mod((XP - ZP) * (XQ + ZQ), n);
        BigInteger vv = Mod((XP + ZP) * (XQ - ZQ), n);
        BigInteger up = Mod(uu + vv, n); up = Mod(up * up, n);
        BigInteger um = Mod(uu - vv, n); um = Mod(um * um, n);
        return (Mod(Zd * up, n), Mod(Xd * um, n));
    }

    // k*P via the Montgomery ladder (constant difference invariant).
    static (BigInteger X, BigInteger Z) Ladder(long k, BigInteger X1, BigInteger Z1, BigInteger a24, BigInteger n)
    {
        if (k <= 1) return (X1, Z1);
        BigInteger x0 = X1, z0 = Z1;
        var (x1, z1) = XDBL(X1, Z1, a24, n);
        int hb = 62; while (hb >= 0 && ((k >> hb) & 1L) == 0) hb--;
        for (int i = hb - 1; i >= 0; i--)
        {
            if (((k >> i) & 1L) == 1)
            {
                (x0, z0) = XADD(x1, z1, x0, z0, X1, Z1, n);
                (x1, z1) = XDBL(x1, z1, a24, n);
            }
            else
            {
                (x1, z1) = XADD(x1, z1, x0, z0, X1, Z1, n);
                (x0, z0) = XDBL(x0, z0, a24, n);
            }
        }
        return (x0, z0);
    }

    static bool TryModInverse(BigInteger a, BigInteger n, out BigInteger inv, out BigInteger gcd)
    {
        a = Mod(a, n);
        BigInteger t = 0, newt = 1, r = n, newr = a;
        while (newr != 0)
        {
            BigInteger q = r / newr;
            (t, newt) = (newt, t - q * newt);
            (r, newr) = (newr, r - q * newr);
        }
        gcd = r;
        if (r != 1) { inv = 0; return false; }
        inv = Mod(t, n);
        return true;
    }

    // =====================================================================
    //  Sieves (cached) and small helpers
    // =====================================================================
    static bool[] _flags; static int _flagsN = -1;
    static bool[] SieveFlags(int bound)
    {
        if (_flags != null && _flagsN >= bound) return _flags;
        var f = new bool[bound + 1];
        for (int i = 2; i <= bound; i++) f[i] = true;
        for (int i = 2; (long)i * i <= bound; i++)
            if (f[i]) for (long j = (long)i * i; j <= bound; j += i) f[j] = false;
        _flags = f; _flagsN = bound; return f;
    }

    static readonly Dictionary<int, int[]> _primeListCache = new();
    static int[] SievePrimes(int bound)
    {
        if (_primeListCache.TryGetValue(bound, out var cached)) return cached;
        var f = SieveFlags(bound);
        var list = new List<int>();
        for (int i = 2; i <= bound; i++) if (f[i]) list.Add(i);
        var arr = list.ToArray();
        _primeListCache[bound] = arr;
        return arr;
    }

    static int DecimalDigits(BigInteger n) { if (n.Sign < 0) n = -n; return n.ToString().Length; }

    static string Trunc(BigInteger n)
    {
        string s = n.ToString();
        return s.Length <= 28 ? s : s.Substring(0, 12) + $"...({s.Length} digits)..." + s.Substring(s.Length - 6);
    }

    // =====================================================================
    //  Self-test (hidden --selftest): exercises the engine without network
    // =====================================================================
    public static int SelfTest()
    {
        int fail = 0;
        Console.WriteLine("== Factorizer self-test ==");

        // 1) FactorDB parser, fully factored: 90 = 2 * 3^2 * 5
        {
            string json = "{\"id\":\"90\",\"status\":\"FF\",\"factors\":[[\"2\",1],[\"3\",2],[\"5\",1]]}";
            bool ok = ParseFactorDb(json, 90, out var pr, out var co);
            BigInteger prod = 1; foreach (var f in pr) prod *= f;
            bool pass = ok && co.Count == 0 && pr.Count == 4 && prod == 90;
            Report("factordb FF parse (90 = 2*3^2*5)", pass, ref fail);
        }
        // 2) FactorDB parser, composite cofactor preserved
        {
            BigInteger c = (BigInteger)1_000_003 * 1_000_033;   // composite
            BigInteger n = 6 * c;
            string json = "{\"status\":\"CF\",\"factors\":[[\"2\",1],[\"3\",1],[\"" + c + "\",1]]}";
            bool ok = ParseFactorDb(json, n, out var pr, out var co);
            bool pass = ok && pr.Count == 2 && co.Count == 1 && co[0] == c;
            Report("factordb CF parse (composite cofactor kept)", pass, ref fail);
        }
        // 3) FactorDB parser rejects a mismatched product
        {
            string json = "{\"status\":\"FF\",\"factors\":[[\"2\",1],[\"3\",1]]}"; // product 6 != 10
            bool ok = ParseFactorDb(json, 10, out _, out _);
            Report("factordb rejects bad product", !ok, ref fail);
        }
        // 4) rho64 on a ~19-digit semiprime
        {
            ulong p = NextPrime64(1_000_000_007UL), q = NextPrime64(2_000_000_011UL);
            ulong N = p * q;
            ulong f = BrentRho64(N);
            bool pass = f > 1 && f < N && N % f == 0;
            Report($"rho64 splits {N} = {p}*{q}", pass, ref fail);
        }
        // 5) ECM on a >2^64 semiprime (an ~11-digit factor)
        {
            BigInteger p = NextPrimeBig(BigInteger.Parse("10000000019"));
            BigInteger q = NextPrimeBig(BigInteger.Parse("70000000003"));
            BigInteger N = p * q;
            int[] s1 = SievePrimes(11_000);
            bool[] fl = SieveFlags(100_000);
            var rng = new Random(2024);
            BigInteger f = 0;
            for (int i = 0; i < 200 && (f <= 1 || f >= N); i++)
                f = EcmOneCurve(N, 6 + (BigInteger)(rng.NextInt64() & long.MaxValue), 11_000, 100_000, s1, fl);
            bool pass = f > 1 && f < N && N % f == 0;
            Report($"ECM splits {N} -> {f}", pass, ref fail);
        }
        // 6) end-to-end Factor() on N_67 = 2^66 + 3, FactorDB OFF (= 1669 * 44210291368986343)
        {
            bool save = UseFactorDb; UseFactorDb = false;
            BigInteger N = BigInteger.Pow(2, 66) + 3;
            var (facs, complete) = Factor(N);
            BigInteger prod = 1; foreach (var f in facs) prod *= f;
            bool pass = complete && prod == N && facs.Count == 2 && facs.TrueForAll(Nt.IsPrimeBig);
            Report($"Factor(2^66+3) = {string.Join(" * ", facs)}", pass, ref fail);
            UseFactorDb = save;
        }

        Console.WriteLine(fail == 0 ? "ALL TESTS PASSED" : $"{fail} TEST(S) FAILED");
        return fail == 0 ? 0 : 1;
    }

    static void Report(string name, bool pass, ref int fail)
    {
        Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}");
        if (!pass) fail++;
    }

    static ulong NextPrime64(ulong x) { if ((x & 1) == 0) x++; while (!Nt.IsPrime(x)) x += 2; return x; }
    static BigInteger NextPrimeBig(BigInteger x) { if (x.IsEven) x++; while (!Nt.IsPrimeBig(x)) x += 2; return x; }
}

// Optimal for sweep: TwoNMod3Search 10000000000001 100000000000000 -15 --auto-wheel --status-file sweep-15.status
// Optimal for factoring: TwoNMod3Search 1 100 -15 --two-prime 3 2000 --two-prime-effort 300 --status-file factor-15.status
// Example of running for specific numbers to factor: TwoNMod3Search 1 100 -15 --two-prime 181,199,211 --two-prime-effort 1200
