using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace BinaryRewrite
{
    // ===================================================================================
    //  Structural reductions of the paper, made computable and cross-checkable.
    //
    //  Per-string analysis (Reductions.Analyze):
    //    * Proposition 8   counter via the odd-cumulative run-length formula,
    //    * Corollary 9 / Proposition 15   the step-0 halting dichotomy,
    //    * Lemma 7    odd parity forces survival,
    //    * Lemma 10   reshaping: predicted 1-run lengths of F(L),
    //    * Lemma 11   omega(F(L)) = #{within-pair gaps that are even},
    //    * Lemma 14 / Observation 24   count-floor >= ceil(omega/2), body decomposition,
    //    * Proposition 25   the nu-recurrence (predicted nu of F(L)),
    //    * Lemma 26 / Proposition 27   conjugate kernel: M = V(L^(1)), V(L^(2)) = 2 S(M).
    //
    //  Backward viewpoint (Proposition 29):
    //    * Preimages       all X with F(X) = T (finite; each verified by re-applying F),
    //    * BackwardChain   walk the finite predecessor tree until a root / branch / cap.
    //
    //  Every formula here was validated in Python against the paper's published numbers
    //  (Table 1, Table 4, the conjugate values N0..N3) before being transcribed.
    // ===================================================================================

    /// <summary>Everything one application of the reduction calculus says about a single string L.</summary>
    public sealed class StructInfo
    {
        public long Nu;                 // nu(L)
        public bool NuEven;             // parity of nu
        public long Omega;              // omega(L) = # odd-length 1-runs
        public long R;                  // number of maximal 1-runs

        public long SEngine;            // counter F would compute on L (gap-sum form)
        public bool Prop8Valid;         // Prop 8 applies only when nu is even
        public long SProp8;             // counter via Proposition 8 (odd-cumulative run-length sum)

        public bool HaltsNow;           // F(L) undefined (s < 2)
        public string HaltReason;       // Corollary 9 / Proposition 15 verdict

        public long CountFloor;         // # contributing (nonzero) within-pair gaps  (Obs 24)
        public long Surplus;            // size surplus = s - count                    (Obs 24)
        public long OmegaFloorBound;    // ceil(omega/2)  (Lemma 14 lower bound on count floor)
        public SortedDictionary<int, long> Decomp = new SortedDictionary<int, long>(); // contributing-gap multiset

        public long PredOmegaOfF;       // Lemma 11: omega(F(L)) = #{within-pair gaps even}
        public long PredNextNu;         // Proposition 25 (valid when !HaltsNow)

        public List<int> RunLengths;    // 1-run lengths (display only, when small)
        public List<int> InternalZeros; // internal 0-run lengths c_i (display only, when small)
        public bool RunListTruncated;   // true if the lists were capped
        public SortedDictionary<int, long> PredFRuns; // Lemma 10 predicted run-length multiset of F(L) (small only)

        public bool HasConj;            // conjugate quantities materialized (string short enough)
        public BigInteger V;            // V(L)
        public BigInteger M;            // V(L^(1)) = 4V (nu even) or 16V+1 (nu odd)
        public BigInteger SM;           // S(M), the alternating bit-sum kernel
        public BigInteger TwoSM;        // 2 S(M) = V(L^(2))   (Lemma 26)
    }

    public static class Reductions
    {
        // ---------------------------------------------------------------------------
        //  Per-string structural analysis from an in-memory gap state.
        //  Scalars are always computed; the RLE lists / conjugate values are filled in
        //  only when the string is small enough to display.
        // ---------------------------------------------------------------------------
        public static StructInfo Analyze(GapEngine ge, int runListCap = 4000)
        {
            var si = new StructInfo();
            int[] g = ge.Gaps;
            int t = ge.T;
            long nu = ge.Nu;
            bool oddPad = (nu % 2 == 1);
            si.Nu = nu;
            si.NuEven = !oddPad;

            // virtual padded gap value at the final even index for an odd pad
            long Pg(long k) => k < g.Length ? g[k] : (t + 3);

            long sinit = oddPad ? -1 : 0;
            long m = (oddPad ? nu + 1 : nu) / 2;

            // engine counter (sum of within-pair gaps) and Lemma 11 predicted omega(F)
            long s = sinit, predEven = 0;
            for (long j = 1; j <= m; j++)
            {
                long v = Pg(2 * (j - 1));
                s += v;
                if ((v & 1) == 0) predEven++;
            }
            si.SEngine = s;
            si.PredOmegaOfF = predEven;
            si.HaltsNow = s < 2;

            si.Omega = ge.Omega();
            si.OmegaFloorBound = (si.Omega + 1) / 2;

            // body decomposition (count floor + size surplus + multiset) — Observation 24
            var d = ge.Decompose(0);
            si.CountFloor = d.Count;
            si.Surplus = d.Surplus;
            si.Decomp = d.Multiset;

            // nu-recurrence (Proposition 25)
            if (!si.HaltsNow)
                si.PredNextNu = si.NuEven ? nu / 2 + 3 * s - 4 : (nu + 1) / 2 + 3 * s - 3;

            // ---- 1-run structure: Proposition 8 counter + Corollary 9 classification ----
            bool small = g.Length <= runListCap;
            si.RunLengths = small ? new List<int>() : null;
            si.InternalZeros = small ? new List<int>() : null;

            long C = 0, prop8 = 0, runIndex = 0, curRun = 1;
            int firstOdd = -1, secondOdd = -1; long sepAfterFirstOdd = -1;

            void RegisterOdd(long idx)
            {
                if (firstOdd < 0) firstOdd = (int)idx;
                else if (secondOdd < 0) secondOdd = (int)idx;
            }

            for (int i = 0; i < g.Length; i++)
            {
                if (g[i] == 0) { curRun++; continue; }
                // close current 1-run (index runIndex); internal 0-run after it = g[i]
                if (si.RunLengths != null) { if (si.RunLengths.Count < runListCap) si.RunLengths.Add((int)curRun); else si.RunListTruncated = true; }
                if (si.InternalZeros != null && si.InternalZeros.Count < runListCap) si.InternalZeros.Add(g[i]);
                C += curRun;
                if ((curRun & 1) == 1) RegisterOdd(runIndex);
                if (runIndex == firstOdd) sepAfterFirstOdd = g[i];
                if ((C & 1) == 1) prop8 += g[i];     // internal boundary with odd cumulative 1-count
                runIndex++; curRun = 1;
            }
            // final 1-run (no internal 0-run after it)
            if (si.RunLengths != null) { if (si.RunLengths.Count < runListCap) si.RunLengths.Add((int)curRun); else si.RunListTruncated = true; }
            C += curRun;
            if ((curRun & 1) == 1) RegisterOdd(runIndex);
            si.R = runIndex + 1;

            si.Prop8Valid = si.NuEven;
            si.SProp8 = prop8;

            // Corollary 9 / Proposition 15 verdict
            if (!si.NuEven)
                si.HaltReason = "ν odd ⇒ s ≥ 2 (Lemma 7: odd parity forces survival)";
            else if (si.Omega == 0)
                si.HaltReason = "halt, s = 0: every 1-run even (ω = 0)";
            else if (si.Omega == 2 && secondOdd == firstOdd + 1 && sepAfterFirstOdd == 1)
                si.HaltReason = "halt, s = 1: two adjacent odd 1-runs, separator c = 1";
            else
                si.HaltReason = $"survives, s ≥ 2 ({si.Omega} odd 1-run" + (si.Omega == 1 ? "" : "s") + ")";

            // ---- Lemma 10 predicted run-length multiset of F(L) (small, non-halting only) ----
            if (!si.HaltsNow && m <= runListCap)
            {
                si.PredFRuns = new SortedDictionary<int, long>();
                for (long j = 1; j <= m; j++)
                {
                    int rl = (int)Pg(2 * (j - 1)) + 1;        // body run length = within-pair gap + 1
                    si.PredFRuns.TryGetValue(rl, out long c); si.PredFRuns[rl] = c + 1;
                }
                if (s > 2) { si.PredFRuns.TryGetValue(2, out long c2); si.PredFRuns[2] = c2 + (s - 2); }
            }

            // ---- conjugate kernel (Lemma 26 / Proposition 27), only when V(L) is feasible ----
            long len = ge.Length;
            if (len <= Value.MaxBitsForExact)
            {
                var sb = new StringBuilder();
                sb.Append('1');
                for (int i = 0; i < g.Length; i++) { sb.Append('0', g[i]); sb.Append('1'); }
                sb.Append('0', t);
                if (Value.TryBits(sb.ToString(), out BigInteger v))
                {
                    si.HasConj = true;
                    si.V = v;
                    si.M = si.NuEven ? v * 4 : v * 16 + 1;   // V(L^(1)); matches Prop 27's M = 4N / 16N+1
                    si.SM = Conjugate.AltBitSum(si.M);
                    si.TwoSM = 2 * si.SM;                    // = V(L^(2)) by Lemma 26
                }
            }

            return si;
        }

        // ---------------------------------------------------------------------------
        //  Proposition 29: every preimage X of T, each verified by re-applying F.
        //  X arises by stripping u>=0 copies of (0110) from T (the tail), reconstructing
        //  L^(1) from the maximal 1-blocks of the remainder Y (each run [a,b] -> 1's at
        //  a and b+1), then deleting a Step-1 pad. Finitely many; we keep only those that
        //  actually map to T under the bit engine.
        // ---------------------------------------------------------------------------
        public static List<string> Preimages(string T)
        {
            var res = new SortedSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(T) || T[T.Length - 1] != '0') return new List<string>();
            int n = T.Length;

            for (int u = 0; u <= n / 4; u++)
            {
                if (u > 0 && !EndsWithTail(T, u)) break;   // if T lacks (0110)^u it lacks (0110)^(u+1)
                int ylen = n - 4 * u;
                if (ylen <= 0) break;
                if (T[ylen - 1] != '0') continue;          // Y must end in 0

                // reconstruct L^(1) of length ylen from the maximal 1-runs of Y = T[0..ylen]
                var L1 = new char[ylen];
                for (int i = 0; i < ylen; i++) L1[i] = '0';
                bool valid = true;
                int p = 0;
                while (p < ylen)
                {
                    if (T[p] == '1')
                    {
                        int a = p;
                        while (p < ylen && T[p] == '1') p++;
                        int b = p - 1;
                        if (b + 1 >= ylen) { valid = false; break; } // run reaches the end: no zeroed endpoint
                        L1[a] = '1'; L1[b + 1] = '1';
                    }
                    else p++;
                }
                if (!valid) continue;
                string l1 = new string(L1);
                TryPad(l1, "00", T, res);
                TryPad(l1, "0001", T, res);
            }
            return new List<string>(res);
        }

        private static bool EndsWithTail(string T, int u)
        {
            int need = 4 * u;
            if (T.Length < need) return false;
            int start = T.Length - need;
            for (int k = 0; k < need; k++)
            {
                char want = "0110"[k & 3];
                if (T[start + k] != want) return false;
            }
            return true;
        }

        private static void TryPad(string l1, string pad, string T, SortedSet<string> res)
        {
            if (!l1.EndsWith(pad, StringComparison.Ordinal)) return;
            string cand = l1.Substring(0, l1.Length - pad.Length);
            if (cand.Length == 0) return;
            long k = 0; for (int i = 0; i < cand.Length; i++) if (cand[i] == '1') k++;
            bool parityOk = pad == "00" ? (k % 2 == 0) : (k % 2 == 1);
            if (!parityOk) return;
            string nxt = BitEngine.Step(cand, out bool halted, out long _, out long _);
            if (!halted && nxt == T) res.Add(cand);
        }

        // ---------------------------------------------------------------------------
        //  Walk the finite predecessor tree back from T. Because preimages are strictly
        //  shorter (Lemma 4), this terminates; we stop early at a branch (>1 preimage)
        //  or a depth cap, and report a root (no preimage = a possible seed).
        // ---------------------------------------------------------------------------
        public sealed class BackResult
        {
            public List<string> Chain = new List<string>();  // T, pre(T), pre(pre(T)), ...
            public List<string> Branch;                       // set when a node has >1 preimage
            public string Status = "";
        }

        public static BackResult BackwardChain(string T, int maxDepth = 64)
        {
            var br = new BackResult();
            string cur = T;
            br.Chain.Add(cur);
            for (int d = 0; d < maxDepth; d++)
            {
                var pre = Preimages(cur);
                if (pre.Count == 0) { br.Status = "root reached — no preimage (a possible seed)"; return br; }
                if (pre.Count > 1) { br.Status = $"branch: {pre.Count} preimages at depth {d + 1}"; br.Branch = pre; return br; }
                cur = pre[0];
                br.Chain.Add(cur);
            }
            br.Status = $"stopped at depth cap ({maxDepth})";
            return br;
        }
    }
}
