# Binary-Rewrite Studio

A cross-platform desktop app (Windows / macOS / Linux) for computing and analysing the
iterated binary-string rewriting process **F** from the two papers, a Collatz-type halting
problem. It can push a single orbit as far as your RAM or SSD allows, plot the key
statistics, and sweep every seed of a given length across all CPU cores.

## The map F (Definition 1)

For a binary string `L` with `k = ν(L) =` number of 1's:

1. **Pad** — if `k` even append `00`, set `s := 0`; if `k` odd append `0001`, set `s := −1`.
2. **Pair / fill / count** — take the positions `p₁<…<p₂ₘ` of the 1's in consecutive pairs.
   For each pair set `p₂ⱼ₋₁ … p₂ⱼ−1` to 1, set `p₂ⱼ` to 0, and add `(p₂ⱼ − p₂ⱼ₋₁ − 1)` to `s`.
3. **Tail** — if `s < 2`, **halt** (`F` undefined); else `F(L) = L⁽²⁾ · (0110)^(s−2)`.

The trajectory is `L₀, L₁ = F(L₀), …`; `sₙ` is the counter computed during `F(Lₙ)`.

## Build & run

Requires the **.NET 8 SDK** (`dotnet --version` ≥ 8). From this folder:

```bash
dotnet restore      # pulls the Avalonia UI packages from NuGet
dotnet run -c Release
```

That launches the GUI on any of the three platforms. To produce a self-contained binary:

```bash
# pick your target: win-x64 / osx-arm64 / osx-x64 / linux-x64
dotnet publish -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true
```

> The `.csproj` pins Avalonia to `11.*`. If `restore` complains a version is missing,
> open the `.csproj` and set the four Avalonia packages to the newest 11.x NuGet shows.

## What the tabs do

**Orbit** — enter a seed `L₀`, a step cap, an engine, a core count, and an
auto-spill toggle. The runner uses cores for the per-step gap-vector
construction (a single orbit's chain is still sequential, but each step's
inner loop parallelises across cores) and automatically spills to disk on
in-memory exhaustion. Produces a table of `n, sₙ, parity(ν), ν(Lₙ), ω(Lₙ),
|Lₙ|, V(Lₙ)`. **Decompose at n** uses the same cores + auto-spill settings.
**Open in new window** pops the current orbit out as a fully independent
window (you can have many open at once). **Save to file…** / **Copy** export
the table as TSV.

**Graphs** — overlay several seeds in parallel across the chosen cores; engine
+ auto-spill exposed; export to TSV/clipboard.

**Seed survey** — enumerate every seed in a length range across the chosen
cores. The classifier engine can be `Gap (in-memory)` or `Bit (small,
exact)`. **ν** can be capped or unlimited. **Auto-spill per seed** lets
in-memory runaway seeds continue on disk (otherwise they're tagged "memory
cap"). The seed list always retains the first 100,000 seeds (smallest
lengths first); larger surveys show a partial list rather than nothing.

A single orbit is an inherently sequential chain (`L_{n+1} = F(Lₙ)`), so cores
only accelerate the in-step construction; the **Orbit** and **Conjugate**
runs still iterate one step at a time. Multi-seed plots, the survey, and the
master graph builder fan out across cores.

### Survey-tab sections (the page scrolls)

- **Summary statistics** — total / halting / non-halting with a separate
  breakdown for the three non-halting reasons: ν-cap, memory-cap, step-cap.
- **Seed list** — every retained seed; click for the full trajectory. Two
  filters (status + length), both live. **Save to file…** writes TSV,
  **Copy seed list** drops it on the clipboard (≤ 50,000 seeds).
- **Master graph** — toggleable overlay of every retained seed's trajectory
  (sampled down to 500), colour-coded green-halts / red-non-halting. Own
  metric, length filter ("only length n"), core count, auto-spill, progress
  bar and cancel.
- **Halting-time scatter** — toggleable. X = seed index (sorted by length
  then bit pattern), Y = halting step (0 if non-halting). Green points halt,
  red points don't.
- **Non-halting count by length** — toggleable. X = seed length, Y = # of
  non-halting seeds at that length.

**Conjugate T** — the integer Collatz-type conjugate. Cores accelerate the
cross-check trajectory (T iteration itself is sequential). Engine +
auto-spill + export are exposed.

> **Bug fix:** previously a non-halting seed could be labelled "ν cap hit"
> even when no ν cap was set. The non-halting reason is now properly
> tracked as one of `ν-cap`, `memory-cap`, or `step-cap`, and the
> per-seed label and survey breakdown are consistent.

Every long-running action has its own progress bar and a Cancel button.

## Closing the program

Closing the window **interrupts any running computation**, switches to a
"cleaning up temporary disk data" screen, and deletes everything the disk
engines wrote under `%TEMP%/BinaryRewriteStudio/session-<guid>/` before the
program exits.

> **What is ν (nu)?** `ν(L)` is the number of `1`s in the string. Its parity
> drives the padding step, and along a non-halting orbit it roughly doubles
> each step — so an uncapped survey relies on the memory ceiling alone to
> cut a divergent seed off (unless auto-spill is on, in which case it spills
> to disk first).

## Engines & limits

Three independent engines compute F; they agree on every common output.

| Engine | Representation | Practical ceiling |
|---|---|---|
| **Bit** | literal char array | ground truth, small `n` only |
| **Gap (in-memory)** | `int[]` gap vector + trailing-zero count | ≈ 2×10⁹ gaps (multi-GB RAM), around `n ≈ 28–29` for seed 10 |
| **Gap (disk stream)** | raw `Int32` gap file, two passes per step | bounded only by free disk — the "no limit but physical" path |

The gap-vector state is `(gaps between consecutive 1's, trailing-zero count)`; `ν` and
`|L|` are tracked as `long`. The successor length is computed in closed form so the
in-memory engine allocates each new vector exactly once.

## Validation

Every algorithm was checked against the papers and cross-checked between engines:

- Seed-10 counter sequence `sₙ` and `ν(Lₙ)` reproduced through `n = 26`
  (`s₂₆ = 88,613,778`, `ν(L₂₆) = 222,569,758`).
- Conjugate `N₀…N₃ = 2, 998, 45868646, 213192976`.
- Decomposition multisets at `n = 10, 15, 19` reproduced exactly.
- Gap engine vs. bit engine: exact agreement on seed 10 and thousands of random seeds.
- Survey halting fractions match Table 3 (`1.00, .50, 1.00, .75, .69, .69, .61, …`).

## Files

- `Core.cs` — the math: three engines, conjugate `T`, `V`, `ω`, decomposition, trajectory
  runner, and the parallel seed survey. No UI dependencies.
- `ChartControl.cs` — the dependency-free line chart.
- `MainWindow.cs` — the four-tab UI and async compute wiring.
- `Program.cs` — Avalonia entry point.
