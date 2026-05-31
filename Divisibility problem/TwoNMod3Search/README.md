# TwoNMod3Search

A fast, multithreaded C# (.NET 8) search for the congruence

```
2^n ≡ a (mod n+1)      equivalently, with m = n+1:   m | 2^(m-1) − a   (m ≥ 2)
```

for a single integer shift `a`, **for a whole interval of shifts** `a ∈ [starta, enda]`,
or **for an explicit list of shifts**. It implements the reductions of the accompanying
paper *On the congruence 2^n ≡ a (mod n+1)*, so that shifts whose answer is known from
theory are reported instantly instead of being searched, and after each sweep it reports
which shifts produced no solution in the searched range.

The set in question is `S_a = { n ≥ 1 : (n+1) | 2^n − a }`. The case `a = −3` is OEIS
[A245728](https://oeis.org/A245728); its only two known solutions are huge
(`n ≈ 1.4×10^10` and `n ≈ 3×10^18`) and this tool recovers both.

---

## The three regimes (paper §1, §9)

Every integer shift falls into one of three classes, and the program decides which:

| Regime | Shifts | What the program does |
|---|---|---|
| **Empty** (R5) | `a = −1` | Reports `S_a = ∅`; no search. |
| **Soluble, infinite** (R3, R4) | `a = 0` and `a = 2^j` (j ≥ 0) | Reports the explicit infinite family; lists the members in the requested `n`-range; no brute search by default. |
| **Open** | everything else | Runs the actual search. Conjecturally infinite but extraordinarily sparse. |

The reductions used:

- **R1 / R2 — prime members & non-emptiness** (Prop 2.2, Cor 2.3). An odd prime `p`
  solves the congruence iff `p | a−1`; `2` solves it iff `a` is even. So the prime
  solutions are the odd prime factors of `a−1` (plus `2` when `a` is even), and `S_a`
  is non-empty whenever `a` is even or `a−1` has an odd prime factor. Emptiness is only
  possible when `a` is odd with `a−1 = ±2^k` (the family of `a = −3`). The program prints
  these prime members and the non-emptiness verdict for every shift.
- **R3** (Prop 3.1): `S_0 = { 2^t : t ≥ 1 }` — exactly the powers of two.
- **R4** (Thm 3.2): for `a = 2^j`, with `c = j+1 = 2^s·c′` (c′ odd) and `e = ord_{c′}(2)`,
  every prime `p ∤ 2c` with `p ≡ 1 (mod e)` gives a solution `m = cp`. This family is
  infinite (Dirichlet). **It is a guaranteed subset, not necessarily all solutions** —
  sporadic non-family solutions exist for some powers of two (e.g. `a = 2, 4, 16`); use
  `--force-search` to find those too.
- **R5** (Thm 3.5): `S_{−1} = ∅`.
- **§4 local admissibility sieve** and **§5 two-prime reduction** are described below.

---

## Usage

```
TwoNMod3Search <startN> <endN> <starta> [enda] [options]
TwoNMod3Search <startN> <endN> <a1,a2,a3,...> [options]
```

The shifts may be given either as an **interval** `starta..enda` (omit `enda` for a single
shift) **or** as an explicit **comma-separated list** with no spaces, e.g. `-3,5,9,17`.

| Positional | Meaning |
|---|---|
| `startN` | inclusive lower bound on `n` (≥ 1) |
| `endN`   | inclusive upper bound on `n` (< 2^62) |
| `starta` | first shift `a` (any integer, may be negative) |
| `enda`   | last shift `a` (optional; **default = starta**, i.e. a single shift) |
| `a1,a2,…` | *or* an explicit list of shifts in place of `starta [enda]` |

| Option | Meaning |
|---|---|
| `-c, --cores N` | thread count (default: `ProcessorCount − 2`) |
| `-2, --two-prime` | also run the two-prime factoring search (paper Cor 5.2) |
| `--max-prime P` | largest smaller prime `p` tried in the two-prime search (default 70) |
| `--max-results N` | cap on solutions printed/listed per shift (default 1,000,000) |
| `--force-search` | sweep even the decided shifts (`−1`, `0`, powers of two) |
| `--no-spill` | keep all results in memory and write once at the end (faster, but memory grows with the solution count) |
| `--spill-mb N` | result-buffer size (MB) before an automatic spill to disk (default 8) |
| `-h, --help` | usage |

During a sweep, press **Enter** to pause/resume and **Ctrl+C** to stop early (it prints how
far the search got contiguously, and the partial results are still written).

---

## Two search engines

### Single shift → per-shift admissibility wheel (paper §4)
For one `a`, the program builds a residue **wheel** modulo `W = 2^t · 1155`
(`1155 = 3·5·7·11`, `t = max(2, min(v₂(a)+1, 8))`) that pre-rejects `m` violating the
local conditions at the primes `{2,3,5,7,11}`, plus a small-prime table for
`13 ≤ p ≤ 97`. Only the surviving candidates get the exact test `2^(m-1) ≡ a (mod m)`,
computed by Montgomery multiplication (even `m` are handled by a CRT split on the odd
part). For `a = −3` this examines only ≈ 20.8 % of all `m`.

### Multiple shifts (interval or list) → single pass (paper §8)
When more than one shift is requested — whether an interval `[starta, enda]` or an explicit
list — the program does **one** residue computation per `m`: `r_m = 2^(m-1) mod m`. Then
`m ∈ S_a` for any shift with `a ≡ r_m (mod m)`, so a single sweep over `m` finds the
solutions of every requested shift at once. For an interval the matching shifts are stepped
out arithmetically; for a list each listed shift is tested against `r_m`. Decided shifts
(`−1`, `0`, `2^j`) are reported from theory and skipped by the recorder (use
`--force-search` to record them too). This is far faster than searching each shift
separately whenever there is more than a handful of shifts.

---

## Two-prime search (paper §5, `--two-prime`)

By Corollary 5.2, for odd primes `p < q` with `gcd(pq, a) = 1`, `pq ∈ S_a` iff
`q | N_p` and `q ≡ k_p + 1 (mod d_p)`, where `N_p = 2^(p-1) − a`, `d_p = ord_p(2)`,
`k_p = log₂ a (mod p)`. So for each admissible small prime `p` the program factors the
single integer `N_p` (trial division to 10^5, then Brent's Pollard-ρ, with Miller–Rabin
primality) and residue-tests the prime factors `q > p`. Every hit is re-verified by a
direct big-integer modular exponentiation. This is how the two huge `a = −3` solutions
are found — essentially instantly, since `N_67 = 2^66 + 3 = 1669 · 44210291368986343`.

**Limitations** (documented honestly):
- Finds only **squarefree, odd, two-prime** solutions `pq`. Non-squarefree solutions
  (e.g. `1715 = 5·7³ ∈ S_9`) and even two-factor solutions are *not* produced by this
  mode — but the `m`-sweep finds them if they lie in the `n`-range.
- `N_p` is only factored within a fixed effort budget; if a hard semiprime `N_p` cannot
  be factored, the program says so and may miss solutions through that `p`. Increasing
  `--max-prime` reaches larger solutions but the `N_p` grow and eventually become
  unfactorable (this wall is intrinsic, paper §5).

---

## Memory

The search itself uses only a few MB of working memory regardless of how large the
`n`-range is (the admissibility wheel is at most ~2.4 MB; the small-prime tables are
fixed). The one quantity that can grow without bound is the **number of solutions**: a
wide shift range fed through the single-pass engine produces on the order of
`(range width) × ln(endN)` hits, and a `--force-search` over a decided shift such as
`a = 1` produces every prime and base-2 pseudoprime in range — easily tens of millions.

To keep this bounded, solutions are written through a buffered sink that **automatically
spills to disk** (default): the in-memory buffer is flushed to `results.txt` whenever it
reaches `--spill-mb` megabytes (8 by default), so resident memory stays flat no matter how
many solutions are found. Batching the writes this way is also markedly faster than writing
each line individually. For example, a run that produces 2.2 million solutions holds at
about 73 MB resident with spilling on; the same run under `--no-spill` (everything kept in
memory, written once at the end) peaks near 150 MB, and larger runs would scale linearly to
exhaustion. `--no-spill` is offered for runs known to produce few solutions, where avoiding
disk I/O until the end is preferable. The buffer is always flushed at the end and on Ctrl+C,
so nothing is lost either way.

The two-prime search generates its candidate primes `p ≤ --max-prime` one at a time
(constant memory), so even an unreasonably large `--max-prime` does not allocate memory
proportional to it; only the per-`p` integer `N_p = 2^(p-1) − a` and its factors are held.

---

## Build & run

```bash
dotnet build -c Release
./bin/Release/net8.0/TwoNMod3Search 1 2000000 -20 20
```

(Offline restore, if needed: `dotnet build -c Release --source <sdk>/FallbackFolder`.)

---

## Output

Solutions print to the console as `*** SOLUTION  a = …, n = …, m = … ***` and are appended
to `results.txt` in the working directory as tab-separated `n <TAB> m <TAB> a` (one line
per solution; big integers are written in full). A periodic progress line shows the
frontier `n` and the current rate.

After every sweep, a **no-result summary** lists the shifts for which *no* value was found
in the searched `n`-range, split into two groups and ordered by the size of `a` (`|a|`,
then signed value):

- **non-empty** — `S_a` is provably non-empty by a reduction (R2: `a` even or `a−1` has an
  odd prime factor; or `a` is a soluble decided shift). A solution exists, just outside the
  searched range.
- **none found** — no solution was found and non-emptiness is **open** (the odd `a−1 = ±2^k`
  shifts such as `5, −7, −15, −3, 17`). `a = −1` is annotated as provably empty (R5).

For example, sweeping `-20..20` over `n ∈ [1, 2000000]` reports `none found: -3, 5, -7, -15`
(every other shift in range has a solution there), while raising the lower bound to
`startN = 1000` additionally reports `non-empty: 7, 11` — those have solutions only at
`n = 2` and `n = 4`, below the new range. For shift sets larger than four million the
per-shift list is summarised by count only (to keep tracking memory bounded).

---

## Examples

```bash
# The classic (n+1) | 2^n + 3 problem; finds nothing below 10^15 (matches A245728).
TwoNMod3Search 1 1000000000000000 -3 --cores 16

# Recover BOTH known a=-3 solutions by factoring (no huge sweep needed):
TwoNMod3Search 1 100 -3 --two-prime --max-prime 70
#   -> 61 * 228806497            (n = 13957196316)
#   -> 67 * 44210291368986343   (n = 2962089521722084980)

# Reproduce the paper's Table 2 region in a single sweep:
TwoNMod3Search 1 2000000 -20 20

# Evaluate only a chosen set of shifts (not a contiguous interval):
TwoNMod3Search 1 2000000 -3,5,9,17

# A decided shift is answered instantly, with no search:
TwoNMod3Search 1 1000000 -1          # S_{-1} = empty (R5)
TwoNMod3Search 1 1000000 0           # powers of two (R3)
TwoNMod3Search 1 1000 16             # family m = 5p, p ≡ 1 (mod 4) (R4)

# Two-prime sweep across a range of shifts at once:
TwoNMod3Search 1 100 3 17 --two-prime
```

---

## Correctness

All search modes were validated against a brute-force reference (`pow(2, m-1, m)`) over
many shifts and ranges, including the non-squarefree solutions `1715 = 5·7³ (a=9)`,
`49 = 7² (a=15)`, `27 = 3³ (a=−5)`; the single-pass range mode and the multithreaded
paths reproduce the reference exactly. The two-prime search reproduces every solution
reported in the paper's §8 (e.g. `47·227 ∈ S_3`, `35,77 ∈ S_9`, `67·2243` and
`19·262127 ∈ S_17`, both `S_{−3}` solutions) and returns nothing for `a ∈ {5, −7, −15}`.