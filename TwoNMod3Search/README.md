# TwoNMod3Search

A multi-threaded search for positive integers `n` satisfying

    2^n  ≡  -3   (mod n + 1)

equivalently `(n + 1) | 2^n + 3`. Below `10^15` only two such `n` are known
(OEIS [A245728](https://oeis.org/A245728)):

    n = 13957196316
    n = 2962089521722084980

This program scans an arbitrary range `[startN, endN]` for further candidates.

---

## Build

Requires the .NET 8 SDK (or newer). From the project directory:

    dotnet build -c Release

The optimised binary will be at `bin/Release/net8.0/TwoNMod3Search.dll`. Run it
either with `dotnet run` (slower JIT path; convenient for development) or via
the published binary (recommended for long searches):

    dotnet publish -c Release -r linux-x64   --self-contained false
    dotnet publish -c Release -r win-x64     --self-contained false
    dotnet publish -c Release -r osx-arm64   --self-contained false

(use the runtime identifier matching your platform).

## Usage

    TwoNMod3Search <startN> <endN> [cores]

| argument | meaning                                             | default                   |
|----------|-----------------------------------------------------|---------------------------|
| startN   | inclusive lower bound on `n`, `>= 1`                | required                  |
| endN     | inclusive upper bound on `n`, strictly `< 2^62`     | required                  |
| cores    | worker thread count                                 | `ProcessorCount - 2` (≥ 1)|

Examples:

    # Resume just past the OEIS-verified bound
    TwoNMod3Search 1000000000000000 2000000000000000

    # Same range, restrict to 4 cores
    TwoNMod3Search 1000000000000000 2000000000000000 4

### Interactive controls

While the program runs:

* **Enter** – toggle pause / resume. Workers finish whatever value they are
  currently testing, then idle until you press Enter again.
* **Ctrl + C** – initiate a clean shutdown. Workers finish their current
  chunks; a final report lists each thread's last-checked `n` together with
  the contiguous lower bound on the processed range.

### Output

Solutions are printed to standard output and appended to `results.txt` in
the working directory, one per line in the form

    n<TAB>m

(where `m = n + 1`). A periodic progress line prints every five seconds:

    [progress] frontier n = 12345678..., examined = 4,200,000, rate = 840,000 n/s

`frontier n` is the largest `n` reached by any thread; `examined` is the
total number of `n` values inspected so far across all threads (including
those eliminated by the wheel).

---

## Design notes

### What we test

The condition `(n+1) | 2^n + 3` with `m = n+1` becomes `m | 2^(m-1) + 3`,
which is decided per-`m` by computing `2^(m-1) mod m` and comparing with
`m - 3`.

### Filtering pipeline

For each candidate `m = n+1` the program applies, in order:

1. **Wheel mod 2310 = 2·3·5·7·11.**
   The primes 2, 3, 5, 7, 11 cannot divide any `m` satisfying the
   congruence (see the companion paper, Lemma 5 / Theorem 7). A
   precomputed list of the 480 allowed residues mod 2310 lets the search
   *step over* about 79 % of integers without examining them at all.

2. **Small-prime filter (primes 13 – 97).**
   For each `p` in this range, the program looks up its order `d_p` of 2
   and the discrete log `k_p` of `-3` (or marks `p` inadmissible). If
   `p | m` then the divisibility `p | 2^(m-1) + 3` is equivalent to
   `(m-1) mod d_p == k_p`; otherwise `p` imposes no constraint. This
   eliminates roughly an additional 20 % of wheel-passing candidates with
   negligible cost (a single 64-bit modulo per `p`).

3. **Full Montgomery exponentiation.**
   For surviving `m`, the program computes `2^(m-1) mod m` and tests it
   against `m - 3`.

### Modular arithmetic

`2^(m-1) mod m` is computed with **Montgomery multiplication** over the
implicit ring `Z / m Z` with Montgomery radix `R = 2^64`. The hot inner
multiplication is

    (a, b)  ↦  a · b · R^(-1)  mod m

implemented in three 64×64→128 multiplications (`Math.BigMul`) and a few
additions; in particular it contains **no division**. The required
quantity `-m^(-1) mod 2^64` is computed once per `m` by five Newton-Hensel
iterations starting from the seed `x = m` (which is correct mod 8 for any
odd `m`, since `m^2 ≡ 1 (mod 8)`).

The squaring chain has 63 squarings and at most 63 multiply-by-2 steps; on
modern x86-64 hardware this is roughly 600–1000 ns per `m`.

The constraint `m < 2^62` keeps `2m < 2^63 < 2^64`, which is what the
two-word Montgomery output bound `[0, 2m)` requires for the implementation
to remain in plain `ulong` arithmetic.

### Concurrency model

The range `[startN, endN]` is divided into chunks of `256 · 2310 = 591360`
consecutive `n` values (roughly 30 ms of work). Each worker repeatedly
fetches the next chunk by atomically adding `ChunkN` to a shared 64-bit
counter (`Interlocked.Add`). Inside a chunk the worker iterates the wheel
strictly forward, so cache locality is excellent.

Every `8192` candidates each worker:

* publishes its last-tested `n` to a per-thread atomic slot,
* checks `CancellationToken`,
* waits on the `ManualResetEventSlim` if paused.

The reporter thread reads the per-thread slots once every 5 seconds.

Pausing is implemented by `_runEvent.Reset()`; resuming by `Set()`. Cancel
is propagated through `CancellationTokenSource.Cancel()`, which both
unblocks paused workers and signals them to exit at the next safe point.
On Ctrl+C the workers complete the chunk they are currently inside and
then return; this typically takes well under one second.

The "last number checked" reported on shutdown is intentionally
*per-thread*: because chunks are claimed dynamically, threads in general
operate on non-contiguous ranges, so a single global watermark would be
misleading. The program also prints `min(thread_last_n)` as a guaranteed
contiguous lower bound on what has been processed.

### Results file

Solutions are appended to `results.txt` under a coarse-grained `lock`. The
write happens only on a (vanishingly rare) hit so contention is a
non-issue.

---

## Verifying a hit by hand

If the program reports `n, m`, you can independently verify with any
arbitrary-precision tool. For example, with Python:

    >>> n = 13957196316
    >>> m = n + 1
    >>> pow(2, n, m) == m - 3
    True

---

## Limits and caveats

* `endN < 2^62` is enforced so that `2 · m < 2^63 < 2^64`, which is what
  the simple Montgomery code assumes. To go higher you would need either
  full 128-bit arithmetic throughout or a Montgomery variant that handles
  the "extra bit" case.
* The program only **finds** candidates; it does not prove there are no
  others below the searched bound. For that, the contiguous lower bound
  printed at exit is the rigorous guarantee, and only when no thread was
  cancelled mid-chunk.
* Floating-point is not used anywhere on the hot path; results are exact.
