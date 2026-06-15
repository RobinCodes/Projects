using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BinaryRewrite
{
    public sealed class MainWindow : Window
    {
        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x5a,0xc8,0xfa), Color.FromRgb(0xff,0x9f,0x43),
            Color.FromRgb(0x7b,0xed,0x9f), Color.FromRgb(0xff,0x6b,0x6b),
            Color.FromRgb(0xc8,0x8f,0xff), Color.FromRgb(0xfe,0xca,0x57),
        };

        public MainWindow()
        {
            Title = "Binary-Rewrite Studio";
            Width = 1080; Height = 760;
            MinWidth = 820; MinHeight = 560;

            var tabs = new TabControl
            {
                Items =
                {
                    new TabItem { Header = "Orbit",       Content = BuildOrbitTab() },
                    new TabItem { Header = "Graphs",      Content = BuildGraphTab() },
                    new TabItem { Header = "Seed survey", Content = BuildSurveyTab() },
                    new TabItem { Header = "Conjugate T", Content = BuildConjugateTab() },
                }
            };
            Content = tabs;
        }

        // ---------- small layout helpers ----------
        private static TextBlock Lbl(string t) => new TextBlock
        { Text = t, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

        private static StackPanel Row(params Control[] kids)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
            foreach (var k in kids) p.Children.Add(k);
            return p;
        }

        private static NumericUpDown Num(decimal val, decimal min, decimal max, decimal inc = 1, double w = 110)
            => new NumericUpDown { Value = val, Minimum = min, Maximum = max, Increment = inc, Width = w, FormatString = "0" };

        private static SelectableTextBlock Mono()
            => new SelectableTextBlock { FontFamily = MonoFont, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap };

        private static int IntOf(NumericUpDown n) => (int)(n.Value ?? 0);
        private static long LongOf(NumericUpDown n) => (long)(n.Value ?? 0);

        // ============================================================ ORBIT
        private TextBox _orbSeed;
        private NumericUpDown _orbSteps;
        private ComboBox _orbEngine;
        private CheckBox _orbOmega, _orbValue;
        private SelectableTextBlock _orbTable, _orbStrings;
        private NumericUpDown _orbDecN;
        private SelectableTextBlock _orbDec;
        private TextBlock _orbStatus;
        private CancellationTokenSource _orbCts;

        private Control BuildOrbitTab()
        {
            _orbSeed = new TextBox { Text = "10", Width = 240, Watermark = "binary seed L0, e.g. 10" };
            _orbSteps = Num(26, 0, 100000, 1);
            _orbEngine = new ComboBox
            {
                Width = 190,
                ItemsSource = new[] { "Auto", "Bit (small, exact)", "Gap (in-memory)", "Gap (disk stream)" },
                SelectedIndex = 0
            };
            _orbOmega = new CheckBox { Content = "omega", IsChecked = true };
            _orbValue = new CheckBox { Content = "value V (base 10)", IsChecked = true };
            var run = new Button { Content = "Run orbit" };
            run.Click += async (_, __) => await RunOrbit();
            _orbStatus = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };

            _orbTable = Mono();
            _orbStrings = Mono();
            _orbDecN = Num(10, 0, 100000, 1);
            var decBtn = new Button { Content = "Decompose s at n" };
            decBtn.Click += async (_, __) => await RunDecompose();
            _orbDec = Mono();

            var panel = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
            panel.Children.Add(Row(Lbl("Seed L0"), _orbSeed, Lbl("max steps"), _orbSteps, Lbl("engine"), _orbEngine));
            panel.Children.Add(Row(_orbOmega, _orbValue, run, _orbStatus));
            panel.Children.Add(new TextBlock { Text = "n  s_n  par(nu)  nu(L_n)  omega  |L_n|  V(L_n)", FontFamily = MonoFont, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new ScrollViewer { Height = 300, Content = _orbTable, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            panel.Children.Add(Row(Lbl("decompose at n ="), _orbDecN, decBtn));
            panel.Children.Add(_orbDec);
            panel.Children.Add(new TextBlock { Text = "strings L_n (only where |L_n| is small):", Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new ScrollViewer { Height = 120, Content = _orbStrings, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

            return new ScrollViewer { Content = panel };
        }

        private EngineKind OrbEngineKind() => _orbEngine.SelectedIndex switch
        {
            1 => EngineKind.Bit,
            2 => EngineKind.GapMemory,
            3 => EngineKind.GapDisk,
            _ => EngineKind.Auto
        };

        private async Task RunOrbit()
        {
            _orbCts?.Cancel();
            _orbCts = new CancellationTokenSource();
            var ct = _orbCts.Token;
            string seed = (_orbSeed.Text ?? "").Trim();
            int steps = IntOf(_orbSteps);
            var eng = OrbEngineKind();
            bool wantOmega = _orbOmega.IsChecked == true;
            bool wantVal = _orbValue.IsChecked == true;
            _orbStatus.Text = "running...";

            try
            {
                var rows = await Task.Run(() =>
                {
                    var runner = new TrajectoryRunner
                    {
                        Seed = seed, MaxSteps = steps, Engine = eng,
                        ComputeOmega = wantOmega, ComputeValue = wantVal
                    };
                    return runner.Run(ct);
                }, ct);

                var sb = new StringBuilder();
                var strs = new StringBuilder();
                foreach (var r in rows)
                {
                    string v = !wantVal ? "-" :
                        r.HasValue ? FormatBig(r.Value) :
                        "~" + Value.DecimalDigits(r.Length) + " digits";
                    string om = r.Omega < 0 ? "-" : r.Omega.ToString();
                    string s = r.Halted && r.S < 2 ? r.S + " (HALT)" : r.S.ToString();
                    sb.Append(r.N.ToString().PadLeft(3)).Append("  ")
                      .Append(s.PadLeft(10)).Append("  ")
                      .Append(r.ParityChar.ToString().PadLeft(3)).Append("  ")
                      .Append(r.Nu.ToString("#,0").PadLeft(14)).Append("  ")
                      .Append(om.PadLeft(8)).Append("  ")
                      .Append(r.Length.ToString("#,0").PadLeft(14)).Append("  ")
                      .Append(v).Append('\n');
                    if (r.Bits != null)
                        strs.Append("L").Append(r.N).Append(" = ").Append(r.Bits).Append('\n');
                }
                _orbTable.Text = sb.ToString();
                _orbStrings.Text = strs.Length == 0 ? "(all strings too large to display)" : strs.ToString();
                _orbStatus.Text = $"done: {rows.Count} steps" + (rows.Count > 0 && rows[rows.Count - 1].Halted ? " (halted)" : "");
            }
            catch (OperationCanceledException) { _orbStatus.Text = "cancelled"; }
            catch (Exception ex) { _orbStatus.Text = "error: " + ex.Message; }
        }

        private async Task RunDecompose()
        {
            string seed = (_orbSeed.Text ?? "").Trim();
            int n = IntOf(_orbDecN);
            try
            {
                var d = await Task.Run(() =>
                    new TrajectoryRunner { Seed = seed }.DecomposeAt(n, CancellationToken.None));
                if (d == null) { _orbDec.Text = "no decomposition (halted before n or nu=0)."; return; }
                var sb = new StringBuilder();
                sb.Append($"s_{d.N} = {d.S}    contributing gaps = {d.Count}    size surplus = {d.Surplus}\n");
                sb.Append("multiset {gap: count}:  ");
                foreach (var kv in d.Multiset) sb.Append($"{kv.Key}:{kv.Value}  ");
                _orbDec.Text = sb.ToString();
            }
            catch (Exception ex) { _orbDec.Text = "error: " + ex.Message; }
        }

        // ============================================================ GRAPHS
        private TextBox _grSeeds;
        private NumericUpDown _grSteps;
        private ComboBox _grMetric;
        private CheckBox _grLog;
        private ChartControl _chart;
        private TextBlock _grStatus;
        private CancellationTokenSource _grCts;

        private Control BuildGraphTab()
        {
            _grSeeds = new TextBox { Text = "10, 1011, 110", Width = 300, Watermark = "seeds, comma-separated" };
            _grSteps = Num(20, 0, 100000, 1);
            _grMetric = new ComboBox
            {
                Width = 200,
                ItemsSource = new[] { "s_n (counter)", "nu(L_n)", "|L_n| length", "log10 V(L_n)", "omega(L_n)" },
                SelectedIndex = 0
            };
            _grLog = new CheckBox { Content = "log Y", IsChecked = false };
            var plot = new Button { Content = "Plot" };
            plot.Click += async (_, __) => await RunPlot();
            _grStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            _chart = new ChartControl { MinHeight = 360 };

            var controls = new StackPanel { Margin = new Thickness(12, 12, 12, 4), Spacing = 4 };
            controls.Children.Add(Row(Lbl("Seeds"), _grSeeds, Lbl("steps"), _grSteps));
            controls.Children.Add(Row(Lbl("metric"), _grMetric, _grLog, plot, _grStatus));

            var chartBorder = new Border
            {
                BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1),
                Margin = new Thickness(12, 0, 12, 12), Child = _chart
            };

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Grid.SetRow(controls, 0);
            Grid.SetRow(chartBorder, 1);
            grid.Children.Add(controls);
            grid.Children.Add(chartBorder);
            return grid;
        }

        private async Task RunPlot()
        {
            _grCts?.Cancel(); _grCts = new CancellationTokenSource();
            var ct = _grCts.Token;
            var seeds = new List<string>();
            foreach (var part in (_grSeeds.Text ?? "").Split(','))
            { var s = part.Trim(); if (s.Length > 0) seeds.Add(s); }
            int steps = IntOf(_grSteps);
            int metric = _grMetric.SelectedIndex;
            _grStatus.Text = "computing...";

            try
            {
                var series = await Task.Run(() =>
                {
                    var list = new List<ChartSeries>();
                    int ci = 0;
                    foreach (var seed in seeds)
                    {
                        var runner = new TrajectoryRunner
                        {
                            Seed = seed, MaxSteps = steps, Engine = EngineKind.GapMemory,
                            ComputeOmega = metric == 4,
                            ComputeValue = metric == 3
                        };
                        var rows = runner.Run(ct);
                        var cs = new ChartSeries { Name = "L0=" + seed, Color = Palette[ci++ % Palette.Length] };
                        foreach (var r in rows) cs.Values.Add(MetricValue(metric, r));
                        list.Add(cs);
                    }
                    return list;
                }, ct);

                _chart.Series.Clear();
                _chart.Series.AddRange(series);
                _chart.LogY = _grLog.IsChecked == true;
                _chart.Title = _grMetric.SelectedItem?.ToString() ?? "";
                _chart.YLabel = _chart.Title;
                _chart.Refresh();
                _grStatus.Text = "done";
            }
            catch (OperationCanceledException) { _grStatus.Text = "cancelled"; }
            catch (Exception ex) { _grStatus.Text = "error: " + ex.Message; }
        }

        private static double MetricValue(int metric, StepInfo r) => metric switch
        {
            0 => r.S,
            1 => r.Nu,
            2 => r.Length,
            3 => r.HasValue ? BigInteger.Log(r.Value, 10) : r.Length * 0.30102999566398114,
            4 => r.Omega,
            _ => r.S
        };

        // ============================================================ SURVEY (multicore)
        private NumericUpDown _svFrom, _svTo, _svSteps, _svNu;
        private ProgressBar _svBar;
        private SelectableTextBlock _svOut;
        private TextBlock _svStatus;
        private CancellationTokenSource _svCts;

        private Control BuildSurveyTab()
        {
            _svFrom = Num(1, 1, 40, 1, 90);
            _svTo = Num(16, 1, 40, 1, 90);
            _svSteps = Num(400, 1, 100000, 1, 110);
            _svNu = Num(3_000_000, 1000, 5_000_000_000, 100000, 150);
            var run = new Button { Content = "Run survey (all cores)" };
            run.Click += async (_, __) => await RunSurvey();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _svCts?.Cancel();
            _svBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 16, Width = 360 };
            _svStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            _svOut = Mono();

            var panel = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
            panel.Children.Add(Row(Lbl("seed length from"), _svFrom, Lbl("to"), _svTo,
                                   Lbl("max steps"), _svSteps, Lbl("max nu"), _svNu));
            panel.Children.Add(Row(run, cancel, _svBar, _svStatus));
            panel.Children.Add(new TextBlock
            {
                Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
                Text = "Enumerates every seed (beginning with 1) in the length range across all CPU cores. " +
                       "Length 'to' grows the work as 2^(len-1); ~20-22 is already billions of seeds."
            });
            panel.Children.Add(new ScrollViewer { Height = 360, Content = _svOut });
            return new ScrollViewer { Content = panel };
        }

        private async Task RunSurvey()
        {
            _svCts?.Cancel(); _svCts = new CancellationTokenSource();
            var ct = _svCts.Token;
            int from = IntOf(_svFrom), to = IntOf(_svTo), steps = IntOf(_svSteps);
            long maxNu = LongOf(_svNu);
            if (to < from) { _svStatus.Text = "range invalid"; return; }
            _svStatus.Text = "running on " + Environment.ProcessorCount + " cores...";
            var prog = new Progress<double>(v => { _svBar.Value = v; });

            try
            {
                var res = await Task.Run(() => SeedSurvey.Run(from, to, steps, maxNu, ct, prog), ct);
                _svBar.Value = 1;
                _svOut.Text = FormatSurvey(res);
                _svStatus.Text = $"done in {res.ElapsedSeconds:0.00}s";
            }
            catch (OperationCanceledException) { _svStatus.Text = "cancelled"; }
            catch (Exception ex) { _svStatus.Text = "error: " + ex.Message; }
        }

        private static string FormatSurvey(SurveyResult r)
        {
            var sb = new StringBuilder();
            sb.Append($"seed lengths {r.LengthFrom}..{r.LengthTo}\n");
            sb.Append($"total seeds        : {r.TotalSeeds:#,0}\n");
            sb.Append($"halting            : {r.Halting:#,0}  ({r.HaltFraction:P2})\n");
            sb.Append($"non-halting (capped): {r.NonHalting:#,0}\n\n");
            sb.Append($"halt at step 0 / 1 : {r.HaltStep0:#,0} / {r.HaltStep1:#,0}\n");
            sb.Append($"max halting step   : {r.MaxHaltStep}\n");
            sb.Append($"counter at halt 0/1: {r.HaltCounter0:#,0} / {r.HaltCounter1:#,0}\n");
            sb.Append($"first halts (n>=2) : {r.FirstHaltN2Plus:#,0}\n");
            sb.Append($"  violating s(N-2)<=5 : {r.FirstHaltViolatingBound:#,0}   (Prop. expects 0)\n\n");
            sb.Append($"grazers g(L0)>=1   : {r.Grazers:#,0}   (max multiplicity {r.MaxGrazeMultiplicity})\n");
            sb.Append($"2-step-monotonicity violators (non-halting): {r.MonotonicityViolators:#,0}\n\n");
            sb.Append("halting fraction by seed length:\n");
            for (int len = r.LengthFrom; len <= r.LengthTo; len++)
            {
                if (!r.TotalByLength.TryGetValue(len, out long tot) || tot == 0) continue;
                r.HaltByLength.TryGetValue(len, out long h);
                double frac = (double)h / tot;
                int bars = (int)Math.Round(frac * 30);
                sb.Append(len.ToString().PadLeft(3)).Append("  ")
                  .Append(frac.ToString("0.000")).Append("  ")
                  .Append(new string('#', bars)).Append('\n');
            }
            return sb.ToString();
        }

        // ============================================================ CONJUGATE T
        private TextBox _cjSeed;
        private NumericUpDown _cjN;
        private SelectableTextBlock _cjOut;
        private TextBlock _cjStatus;
        private CancellationTokenSource _cjCts;

        private Control BuildConjugateTab()
        {
            _cjSeed = new TextBox { Text = "10", Width = 240, Watermark = "seed L0 (binary)" };
            _cjN = Num(12, 1, 200, 1);
            var run = new Button { Content = "Iterate T" };
            run.Click += async (_, __) => await RunConjugate();
            _cjStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            _cjOut = Mono();

            var panel = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
            panel.Children.Add(Row(Lbl("Seed L0"), _cjSeed, Lbl("iterations"), _cjN, run, _cjStatus));
            panel.Children.Add(new TextBlock
            {
                Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
                Text = "Integer conjugate of Collatz type: N0 = V(L0), N_{n+1} = T(N_n). Each N_n is cross-checked " +
                       "against the bit-string value V(L_n) wherever the engine can still materialize it."
            });
            panel.Children.Add(new TextBlock { Text = "n   s_n   digits(N_n)   verified   N_n", FontFamily = MonoFont, Foreground = Brushes.Gray });
            panel.Children.Add(new ScrollViewer { Height = 420, Content = _cjOut, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            return new ScrollViewer { Content = panel };
        }

        private async Task RunConjugate()
        {
            _cjCts?.Cancel(); _cjCts = new CancellationTokenSource();
            var ct = _cjCts.Token;
            string seed = (_cjSeed.Text ?? "").Trim();
            int maxN = IntOf(_cjN);
            _cjStatus.Text = "iterating...";
            try
            {
                string text = await Task.Run(() =>
                {
                    // ground-truth V(L_n) from the gap engine, where small enough
                    var truth = new Dictionary<int, BigInteger>();
                    var runner = new TrajectoryRunner { Seed = seed, MaxSteps = maxN, Engine = EngineKind.GapMemory, ComputeOmega = false, ComputeValue = true };
                    foreach (var r in runner.Run(ct)) if (r.HasValue) truth[r.N] = r.Value;

                    var sb = new StringBuilder();
                    string norm = TrajectoryRunner.Normalize(seed);
                    if (norm.Length == 0) return "nu = 0: V(L0) = 0, F undefined.";
                    BigInteger N = BigInteger.Zero;
                    foreach (char c in norm) { N <<= 1; if (c == '1') N += 1; }

                    for (int n = 0; n <= maxN; n++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string verified = truth.TryGetValue(n, out var tv)
                            ? (tv == N ? "yes" : "MISMATCH") : "-";
                        sb.Append(n.ToString().PadLeft(3)).Append("  ");
                        bool ok = Conjugate.T(N, out var next, out long s, out _);
                        sb.Append((ok ? s.ToString() : s + "*").PadLeft(5)).Append("  ")
                          .Append(CountDigits(N).ToString().PadLeft(11)).Append("  ")
                          .Append(verified.PadLeft(9)).Append("  ")
                          .Append(FormatBig(N)).Append('\n');
                        if (!ok) { sb.Append("    -> s < 2: T undefined (halt).\n"); break; }
                        N = next;
                    }
                    return sb.ToString();
                }, ct);
                _cjOut.Text = text;
                _cjStatus.Text = "done";
            }
            catch (OperationCanceledException) { _cjStatus.Text = "cancelled"; }
            catch (Exception ex) { _cjStatus.Text = "error: " + ex.Message; }
        }

        // ---------- formatting ----------
        private static string FormatBig(BigInteger v)
        {
            string s = BigInteger.Abs(v).ToString(CultureInfo.InvariantCulture);
            if (s.Length <= 60) return (v.Sign < 0 ? "-" : "") + s;
            return (v.Sign < 0 ? "-" : "") + s.Substring(0, 24) + "…" + s.Substring(s.Length - 24)
                   + "  (" + s.Length + " digits)";
        }

        private static int CountDigits(BigInteger v) =>
            BigInteger.Abs(v).ToString(CultureInfo.InvariantCulture).Length;
    }
}
