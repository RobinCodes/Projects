# Divisibility program

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
| `-2, --two-prime [A [B]] \| p1,p2,…` | run the two-prime factoring search (paper Cor 5.2). With no argument it tries smaller primes `3..70`; with one number `N`, primes `3..N`; with two numbers `A B`, primes `A..B`; with a comma list (e.g. `11,47,67`), exactly those smaller primes |
| `--max-prime P` | shorthand for the two-prime range `3..P` (default 70) |
| `--two-prime-mode M` | when to run the two-prime search: `before`, `after` (default), or `alongside` the sweep |
| `--two-prime-cores N` | cores given to the two-prime search when `mode=alongside` (the sweep gets the rest; default 1) |
| `--two-prime-effort S` | per-`N_p` ECM time budget for the two-prime phase, seconds (overrides `--ecm-seconds` for this phase; raise it to crack hard `N_p`) |
| `--no-factordb` | do not query factordb.com; factor `N_p` locally only |
| `--factordb-timeout S` | per-request FactorDB HTTP timeout, seconds (default 8) |
| `--ecm-seconds S` | per-number ECM time budget, seconds (default 20; `0` disables ECM) |
| `--factor-verbose` | log FactorDB / ECM activity during factoring |
| `--wheel-max P` | bake all *compatible* odd primes ≤ `P` into the sweep wheel modulus (default 11; larger ⇒ more pre-filtering but a bigger one-off build) |
| `--auto-wheel` | choose the wheel modulus automatically by a calibrated cost/benefit model from the actual `n`-range (single-shift sweeps) |
| `--wheel-mem-mb N` | memory budget (MB) for the wheel residue table (default 256) |
| `--status-file PATH` | periodic run-status file (default `status.txt`) |
| `--status-interval S` | status refresh period, seconds (default 300); the file is also rewritten on pause/resume/finish/Ctrl+C |
| `--max-results N` | cap on solutions printed/listed per shift (default 1,000,000) |
| `--force-search` | sweep even the decided shifts (`−1`, `0`, powers of two) |
| `--no-spill` | keep all results in memory and write once at the end (faster, but memory grows with the solution count) |
| `--spill-mb N` | result-buffer size (MB) before an automatic spill to disk (default 8) |
| `-h, --help` | usage |

During a sweep **or** a two-prime search, press **Enter** to pause/resume and **Ctrl+C** to
stop early. Both phases honour pause and cancel; Ctrl+C also interrupts a factorization in
progress (between ECM curves / ρ batches). Partial results are always written, and the
status file records the final state.

---

## Two search engines

### Single shift → per-shift admissibility wheel (paper §4)
For one `a`, the program builds a residue **wheel** modulo `W = 2^t · (odd primes)` and only
the surviving residues get the exact test `2^(m-1) ≡ a (mod m)`, computed by Montgomery
multiplication (even `m` are handled by a CRT split on the odd part).

**How the wheel works.** Write `m = n+1`. Whether `m` can be a solution *as far as the primes
dividing `W` are concerned* depends only on `m mod W`: membership at a prime `p | m` is
governed by `m mod p` and by `(m-1) mod ord_p(2)`, and the odd primes are chosen so that
`ord_p(2) | W`. So we enumerate, once, the residues `r ∈ [0, W)` that pass those local tests,
and the sweep then visits only `m ≡ r (mod W)`, stepping by precomputed gaps. The default
modulus is `W = 2^t · 3·5·7·11` with `t = max(2, min(v₂(a)+1, 8))`; for `a = −3` this is
`4620` and ≈ 20.8 % of residues survive, so the expensive modpow runs for only ~1 in 5 `m`.
A small-prime table covers `13 ≤ p ≤ 97` per candidate.

**Making the wheel "heftier" (`--wheel-max`, `--auto-wheel`).** Baking *more* odd primes into
`W` lowers the surviving fraction further — a larger one-off computation for a faster sweep,
exactly as you'd expect. `--wheel-max P` includes every *compatible* odd prime ≤ `P`; an odd
prime `p` is compatible **only if `ord_p(2)` divides `W`** (otherwise the local test at `p`
would not be a function of `m mod W` and the wheel would be unsound — e.g. `37`, whose order
`36 = 4·9` needs `3²`, is skipped, since `W`’s odd part is squarefree). For `a = −3`,
`--wheel-max 31` gives `W = 40 060 020` over `{3,5,7,11,13,23,29}` and drops the survivor
fraction to ≈ 18.1 %. The modulus is capped at `2³⁰` and the residue table at `--wheel-mem-mb`.

`--auto-wheel` answers “**what modulus is most worth it?**” directly: it times one modpow and
one residue-build step on this machine, then greedily adds compatible primes **only while the
predicted sweep saving over your actual `n`-range exceeds the extra build cost**. On a tiny
range it keeps the default `4620`; on `[1, 10^14]` it extends to `40 060 020` (built in ~0.4 s)
because the saving across 10^14 values dwarfs the build.

> **Honest note on the size of the win.** Extending the wheel past the small-prime table
> primes (13…97) mostly saves *iteration/branch* overhead rather than modpow calls: the table
> already rejects multiples of those primes before the expensive test, so the surviving-modpow
> count barely moves (≈ 20.8 % → 18.1 % for `−3`). The large per-operation win for the sweep
> came instead from a **branchless Montgomery doubling** in the modpow core (see *Performance*).
> Where a bigger wheel genuinely helps is very long single-shift runs, where shaving the
> per-`m` constant over `10^12`–`10^15` values still adds up.

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
`k_p = log₂ a (mod p)`. So for each admissible smaller prime `p` the program factors the
single integer `N_p` and residue-tests the prime factors `q > p`. Every hit is re-verified by
a direct big-integer modular exponentiation, so **correctness never depends on the factoriser
or the network**. This is how the two huge `a = −3` solutions are found essentially instantly
(`N_67 = 2^66 + 3 = 1669 · 44210291368986343`), with no large sweep.

**Tiered factoriser.** `N_p` is factored by, in order: trial division (to 10⁵) → an optional
**FactorDB** lookup for cofactors worth a network round-trip (every returned factor is verified
locally) → Brent–Pollard ρ (Montgomery 64-bit for `N_p ≤ 2⁶⁴`) → Pollard `p−1` → **ECM**
(Montgomery curves, two stages) for medium factors. FactorDB is on by default; `--no-factordb`
forces purely local factoring, and `--factordb-timeout` / `--ecm-seconds` tune the budgets.

**Selecting which primes to try.** `--two-prime` takes optional inline arguments:

```
--two-prime              # smaller primes 3..70 (default)
--two-prime 110          # smaller primes 3..110
--two-prime 150 200      # smaller primes 150..200   (a "start from" range)
--two-prime 11,47,67     # exactly these smaller primes
--max-prime 90           # shorthand for 3..90
```

Only the `(shift, prime)` pairs that pass the cheap §4 admissibility tests become work items,
so the progress total counts primes actually worth factoring.

**Effort for hard `N_p`.** `--two-prime-effort S` sets the per-`N_p` ECM budget for the
two-prime phase (overriding `--ecm-seconds` there). The intended workflow: run a wide range,
note any `N_p` reported as *not fully factored*, then re-run a short prime **list** with a large
effort, e.g. `--two-prime 167,167 --two-prime-effort 600`, to throw real ECM time at exactly
those primes.

**Scheduling relative to the sweep (`--two-prime-mode`).**
- `after` (default) — sweep first, then two-prime.
- `before` — two-prime first (useful when you mainly want the factoring result and the sweep is
  a long tail).
- `alongside` — run both **concurrently**: `--two-prime-cores N` cores go to the two-prime
  search and the remaining `C − N` to the sweep, so a long sweep and the factoring make
  progress at the same time.

**Parallel, with live progress.** The two-prime search itself is multi-threaded (workers pull
`(shift, prime)` items from a shared queue) and prints periodic progress
(`[two-prime] k/N primes done, … solution(s), factoring N_p for p = …`). It is fully
**pausable (Enter) and cancellable (Ctrl+C)** like the sweep — Ctrl+C even interrupts an ECM
factorization in progress (the token is checked between curves), so a 600-second budget on a
hard `N_p` stops within a second of the keypress.

**Limitations** (documented honestly):
- Finds only **squarefree, odd, two-prime** solutions `pq`. Non-squarefree solutions
  (e.g. `1715 = 5·7³ ∈ S_9`) and even two-factor solutions are *not* produced by this mode —
  but the `m`-sweep finds them if they lie in the `n`-range.
- `N_p` is only factored within the effort budget; if a hard semiprime `N_p` cannot be
  factored, the program says so (and lists it in the summary / status file) and may miss
  solutions through that `p`. Larger primes reach larger solutions but the `N_p` grow and
  eventually become unfactorable — this wall is intrinsic (paper §5).
- *Sandbox note:* `factordb.com` is unreachable from the build/test sandbox here, so the live
  FactorDB path could only be exercised through its (verified) parse-and-fallback behaviour;
  on a machine with outbound HTTPS it will be used. If your environment firewalls it, pass
  `--no-factordb`.

---

## Run status file (`--status-file`, `--status-interval`)

Every run writes a human-readable status file (default `status.txt`) that is refreshed every
`--status-interval` seconds (default 300) **and** immediately whenever the run changes state —
paused, resumed, finished, or interrupted by Ctrl+C. It is a single self-contained snapshot of
the whole run:

- timestamp, **state** (RUNNING / PAUSED / FINISHED / INTERRUPTED), start time, elapsed (ms/s);
- the exact command line, host logical cores and cores in use;
- the mode (single shift + regime, shift interval, or explicit list) and the `n`-range;
- every parameter and mode (cores, max-results, spill, FactorDB/ECM budgets, two-prime
  mode/cores/primes/effort, wheel modulus/residues/density/auto);
- a live **`[sweep]`** block (state, frontier `n`, examined count and % of range, rate, solutions);
- a live **`[two-prime]`** block (state, primes done/total, solutions, primes currently being
  factored, and any `N_p` not fully factored);
- the most recent solutions found (the full list is always in `results.txt`).

Writes are serialized and best-effort: a status I/O error can never disrupt or slow the search.

---

## Performance

The hot path of every sweep is the residue `2^(m-1) mod m`, computed with 64-bit Montgomery
arithmetic. Because the exponentiation is base-2, each set exponent bit multiplies the running
value by 2 — which in the Montgomery domain is a **modular doubling**, not a full Montgomery
multiply. Doing that doubling **branchlessly** (an arithmetic mask for the conditional subtract,
valid since `m < 2^62`) measured **~16 % faster** end-to-end than multiplying by a `twoMont`
constant. A microbenchmark settled the design: a *naive* `if (x ≥ m) x −= m;` was actually ~25 %
*slower* than the multiply (a mispredicted branch costs more than a pipelined 64-bit multiply);
only the branchless mask wins. Both sweep modes parallelise over `--cores` worker threads.

For single-shift runs the wheel reduces how many of those modpows happen (see *Two search
engines*); `--auto-wheel` tunes that trade-off to the `n`-range.

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

# Throw heavy ECM at one stubborn prime (e.g. p=167 for a=-20):
TwoNMod3Search 1 10 -20 --two-prime 167,167 --two-prime-effort 600

# Long single-shift sweep with two-prime running ALONGSIDE it (3 sweep + 1 factor core),
# an auto-tuned wheel, and a status file refreshed every 30 s:
TwoNMod3Search 1 1000000000000000 -3 --cores 4 \
    --two-prime 3 90 --two-prime-mode alongside --two-prime-cores 1 \
    --auto-wheel --status-file run.status --status-interval 30

# Bake more primes into the wheel by hand, and start two-prime BEFORE the sweep:
TwoNMod3Search 1 5000000000 -3 --wheel-max 31 --two-prime --two-prime-mode before
```

---

## Correctness

All search modes were validated against a brute-force reference (`pow(2, m-1, m)`) over
many shifts and ranges, including the non-squarefree solutions `1715 = 5·7³ (a=9)`,
`49 = 7² (a=15)`, `27 = 3³ (a=−5)`; the single-pass range mode and the multithreaded
paths reproduce the reference exactly. The two-prime search reproduces every solution
reported in the paper's §8 (e.g. `47·227 ∈ S_3`, `35,77 ∈ S_9`, `67·2243` and
`19·262127 ∈ S_17`, both `S_{−3}` solutions) and returns nothing for `a ∈ {5, −7, −15}`.