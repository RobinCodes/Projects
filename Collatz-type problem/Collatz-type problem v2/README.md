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

**Orbit** — enter a seed `L₀`, a step cap, and an engine. Produces a table of
`n, sₙ, parity(ν), ν(Lₙ), ω(Lₙ), |Lₙ|, V(Lₙ)` (the base-10 value, shown exactly when it
fits and as a digit-count estimate when it doesn't). Strings are shown for small `n`, and a
**Decompose** button breaks an even-parity counter into its contributing-gap multiset plus
size surplus (Observation 24 / 26).

**Graphs** — overlay several seeds and plot any of `sₙ`, `ν(Lₙ)`, `|Lₙ|`, `log₁₀ V(Lₙ)`,
or `ω(Lₙ)` against `n`, with a linear/log Y toggle. Multiple seeds are computed in parallel
across the cores you pick; the chart is drawn directly (no plotting dependency).

A single orbit is an inherently sequential chain (`L_{n+1} = F(Lₙ)`), so the **Orbit** and
**Conjugate** tabs run on one core by design — only the survey and multi-seed plotting fan
out across cores.

**Seed survey (multicore)** — enumerate every seed (those beginning with 1) over a length
range, in parallel across a chosen number of cores (the picker shows how many you have), and
report the Table 2 / Table 3 statistics: halting fraction overall and per length, step-0/1
halts, counter-at-halt `=0/=1`, max halting step, first-halt count and the `s_{N−2} ≤ 5`
sanity check, grazer count `g(L₀) ≥ 1`, and two-step-monotonicity violators. **ν** (see
below) can be left **unlimited**, or capped to stop a runaway non-halting seed from eating
memory. For surveys of ≤ 100,000 seeds, every seed is listed at the bottom with its outcome;
**click any seed** to open a popup with its full trajectory — a plot of whatever metric you
choose, the written `sₙ` trajectory, and the per-step table.

Every long-running action (orbit, plot, survey, conjugate, and the popup) has its own
progress bar and a Cancel button.

> **What is ν (nu)?** `ν(L)` is the number of `1`s in the string. Its parity drives the
> padding step, and along a non-halting orbit it roughly doubles each step — so an
> uncapped survey relies on the memory ceiling alone to cut a divergent seed off.

**Conjugate T** — the integer Collatz-type conjugate: `N₀ = V(L₀)`, `N_{n+1} = T(Nₙ)`
(Proposition 27 / Theorem 29). Each `Nₙ` is cross-checked against the bit-string value
`V(Lₙ)` wherever the engine can still materialise it.

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
