using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace BinaryRewrite
{
    public sealed class MainWindow : Window
    {
        internal static readonly FontFamily MonoFont =
            new FontFamily("Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");
        internal static readonly Color[] Palette =
        {
            Color.FromRgb(0x5a,0xc8,0xfa), Color.FromRgb(0xff,0x9f,0x43),
            Color.FromRgb(0x7b,0xed,0x9f), Color.FromRgb(0xff,0x6b,0x6b),
            Color.FromRgb(0xc8,0x8f,0xff), Color.FromRgb(0xfe,0xca,0x57),
        };
        internal static readonly int CoreCount = Environment.ProcessorCount;

        public MainWindow()
        {
            Title = "Binary-Rewrite Studio";
            Width = 1100; Height = 800;
            MinWidth = 860; MinHeight = 600;

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

        // ---------- layout helpers ----------
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

        private static ProgressBar Bar() => new ProgressBar { Minimum = 0, Maximum = 1, Height = 14, Width = 320 };

        private static int IntOf(NumericUpDown n) => (int)(n.Value ?? 0);
        private static long LongOf(NumericUpDown n) => (long)(n.Value ?? 0);

        // ============================================================ ORBIT
        private TextBox _orbSeed;
        private NumericUpDown _orbSteps;
        private ComboBox _orbEngine;
        private CheckBox _orbOmega, _orbValue;
        private SelectableTextBlock _orbTable, _orbStrings, _orbDec;
        private NumericUpDown _orbDecN;
        private TextBlock _orbStatus;
        private ProgressBar _orbBar;
        private CancellationTokenSource _orbCts;

        private Control BuildOrbitTab()
        {
            _orbSeed = new TextBox { Text = "10", Width = 240, Watermark = "binary seed L0, e.g. 10" };
            _orbSteps = Num(26, 0, 1000000, 1);
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
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _orbCts?.Cancel();
            _orbStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            _orbBar = Bar();

            _orbTable = Mono();
            _orbStrings = Mono();
            _orbDecN = Num(10, 0, 1000000, 1);
            var decBtn = new Button { Content = "Decompose s at n" };
            decBtn.Click += async (_, __) => await RunDecompose();
            _orbDec = Mono();

            var panel = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
            panel.Children.Add(Row(Lbl("Seed L0"), _orbSeed, Lbl("max steps"), _orbSteps, Lbl("engine"), _orbEngine));
            panel.Children.Add(Row(_orbOmega, _orbValue, run, cancel));
            panel.Children.Add(Row(_orbBar, _orbStatus));
            panel.Children.Add(new TextBlock { Text = "n  s_n  par(nu)  nu(L_n)  omega  |L_n|  V(L_n)", FontFamily = MonoFont, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new ScrollViewer { Height = 290, Content = _orbTable, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(Row(Lbl("decompose at n ="), _orbDecN, decBtn));
            panel.Children.Add(_orbDec);
            panel.Children.Add(new TextBlock { Text = "strings L_n (only where |L_n| is small):", Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new ScrollViewer { Height = 110, Content = _orbStrings, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(new TextBlock
            {
                Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Text = "Note: one orbit is an inherently sequential chain (L_{n+1} = F(L_n)), so it uses a single core by design."
            });

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
            _orbBar.Maximum = Math.Max(1, steps);
            _orbBar.Value = 0;
            var prog = new Progress<int>(n => _orbBar.Value = n);

            try
            {
                var rows = await Task.Run(() =>
                {
                    var runner = new TrajectoryRunner
                    {
                        Seed = seed, MaxSteps = steps, Engine = eng,
                        ComputeOmega = wantOmega, ComputeValue = wantVal
                    };
                    return runner.Run(ct, prog);
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
                _orbBar.Value = _orbBar.Maximum;
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
        private NumericUpDown _grSteps, _grCores;
        private ComboBox _grMetric;
        private CheckBox _grLog;
        private ChartControl _chart;
        private TextBlock _grStatus;
        private ProgressBar _grBar;
        private CancellationTokenSource _grCts;

        private Control BuildGraphTab()
        {
            _grSeeds = new TextBox { Text = "10, 1011, 110", Width = 300, Watermark = "seeds, comma-separated" };
            _grSteps = Num(20, 0, 1000000, 1);
            _grCores = Num(CoreCount, 1, CoreCount, 1, 80);
            _grMetric = new ComboBox
            {
                Width = 200,
                ItemsSource = new[] { "s_n (counter)", "nu(L_n)", "|L_n| length", "log10 V(L_n)", "omega(L_n)" },
                SelectedIndex = 0
            };
            _grLog = new CheckBox { Content = "log Y", IsChecked = false };
            var plot = new Button { Content = "Plot" };
            plot.Click += async (_, __) => await RunPlot();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _grCts?.Cancel();
            _grStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            _grBar = Bar();
            _chart = new ChartControl { MinHeight = 340 };

            var controls = new StackPanel { Margin = new Thickness(12, 12, 12, 4), Spacing = 4 };
            controls.Children.Add(Row(Lbl("Seeds"), _grSeeds, Lbl("steps"), _grSteps, Lbl($"cores (of {CoreCount})"), _grCores));
            controls.Children.Add(Row(Lbl("metric"), _grMetric, _grLog, plot, cancel));
            controls.Children.Add(Row(_grBar, _grStatus));

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
            int cores = IntOf(_grCores);
            _grStatus.Text = "computing...";
            _grBar.Value = 0;
            var prog = new Progress<double>(v => _grBar.Value = v);

            try
            {
                var series = await Task.Run(() =>
                {
                    var arr = new ChartSeries[seeds.Count];
                    int doneCount = 0;
                    Parallel.For(0, seeds.Count,
                        new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = ct },
                        i =>
                        {
                            var runner = new TrajectoryRunner
                            {
                                Seed = seeds[i], MaxSteps = steps, Engine = EngineKind.GapMemory,
                                ComputeOmega = metric == 4, ComputeValue = metric == 3
                            };
                            var rows = runner.Run(ct);
                            var cs = new ChartSeries { Name = "L0=" + seeds[i], Color = Palette[i % Palette.Length] };
                            foreach (var r in rows) cs.Values.Add(MetricValue(metric, r));
                            arr[i] = cs;
                            int d = Interlocked.Increment(ref doneCount);
                            ((IProgress<double>)prog).Report((double)d / Math.Max(1, seeds.Count));
                        });
                    return arr.Where(x => x != null).ToList();
                }, ct);

                _chart.Series.Clear();
                _chart.Series.AddRange(series);
                _chart.LogY = _grLog.IsChecked == true;
                _chart.Title = _grMetric.SelectedItem?.ToString() ?? "";
                _chart.YLabel = _chart.Title;
                _chart.Refresh();
                _grBar.Value = 1;
                _grStatus.Text = "done";
            }
            catch (OperationCanceledException) { _grStatus.Text = "cancelled"; }
            catch (Exception ex) { _grStatus.Text = "error: " + ex.Message; }
        }

        internal static double MetricValue(int metric, StepInfo r) => metric switch
        {
            0 => r.S,
            1 => r.Nu,
            2 => r.Length,
            3 => r.HasValue ? BigInteger.Log(r.Value, 10) : r.Length * 0.30102999566398114,
            4 => r.Omega,
            _ => r.S
        };

        // ============================================================ SURVEY (multicore)
        private NumericUpDown _svFrom, _svTo, _svSteps, _svNu, _svCores;
        private CheckBox _svUnlimited;
        private ProgressBar _svBar;
        private SelectableTextBlock _svOut;
        private ListBox _svList;
        private TextBlock _svStatus, _svListHdr;
        private CancellationTokenSource _svCts;

        private Control BuildSurveyTab()
        {
            _svFrom = Num(1, 1, 40, 1, 90);
            _svTo = Num(16, 1, 40, 1, 90);
            _svSteps = Num(400, 1, 1000000, 1, 110);
            _svNu = Num(3_000_000, 1000, 5_000_000_000, 100000, 150);
            _svCores = Num(CoreCount, 1, CoreCount, 1, 80);
            _svUnlimited = new CheckBox { Content = "no ν limit", IsChecked = false };
            _svUnlimited.IsCheckedChanged += (_, __) => _svNu.IsEnabled = _svUnlimited.IsChecked != true;
            var run = new Button { Content = "Run survey" };
            run.Click += async (_, __) => await RunSurvey();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _svCts?.Cancel();
            _svBar = Bar();
            _svStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            _svOut = Mono();
            _svListHdr = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) };
            _svList = new ListBox { Height = 200, FontFamily = MonoFont, FontSize = 12.5 };
            _svList.SelectionChanged += (_, __) =>
            {
                if (_svList.SelectedItem is SeedOutcome so) { OpenSeedDetail(so.Seed); _svList.SelectedItem = null; }
            };

            var panel = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
            panel.Children.Add(Row(Lbl("seed length from"), _svFrom, Lbl("to"), _svTo,
                                   Lbl("max steps"), _svSteps, Lbl($"cores (of {CoreCount})"), _svCores));
            panel.Children.Add(Row(Lbl("max ν"), _svNu, _svUnlimited, run, cancel));
            panel.Children.Add(Row(_svBar, _svStatus));
            panel.Children.Add(new TextBlock
            {
                Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 4),
                Text = "Enumerates every seed (beginning with 1) in the length range across the chosen cores. Work grows as " +
                       "2^(len-1). With no ν limit, a non-halting seed grows doubly-exponentially and is cut off only when it " +
                       "exhausts memory. Per-seed listing is enabled for surveys of at most 100,000 seeds."
            });
            panel.Children.Add(new ScrollViewer { Height = 220, Content = _svOut });
            panel.Children.Add(_svListHdr);
            panel.Children.Add(_svList);
            return new ScrollViewer { Content = panel };
        }

        private async Task RunSurvey()
        {
            _svCts?.Cancel(); _svCts = new CancellationTokenSource();
            var ct = _svCts.Token;
            int from = IntOf(_svFrom), to = IntOf(_svTo), steps = IntOf(_svSteps), cores = IntOf(_svCores);
            long maxNu = _svUnlimited.IsChecked == true ? long.MaxValue : LongOf(_svNu);
            if (to < from) { _svStatus.Text = "range invalid"; return; }
            _svStatus.Text = $"running on {cores} core(s)...";
            _svBar.Value = 0;
            _svList.ItemsSource = null;
            _svListHdr.Text = "";
            var prog = new Progress<double>(v => _svBar.Value = v);

            try
            {
                var res = await Task.Run(() => SeedSurvey.Run(from, to, steps, maxNu, cores, 100_000, ct, prog), ct);
                _svBar.Value = 1;
                _svOut.Text = FormatSurvey(res);
                if (res.SeedsListed)
                {
                    _svListHdr.Text = $"all {res.Seeds.Count:#,0} seeds (click one for its full trajectory):";
                    _svList.ItemsSource = res.Seeds;
                }
                else
                {
                    _svListHdr.Text = $"per-seed listing disabled: {res.TotalSeeds:#,0} seeds exceeds 100,000. " +
                                      "Reduce the length range to enable the clickable list.";
                }
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
            sb.Append($"  violating s(N-2)<=5 : {r.FirstHaltViolatingBound:#,0}   (expect 0)\n\n");
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

        // ---------- seed-detail popup ----------
        private void OpenSeedDetail(string seed)
        {
            var win = new Window
            {
                Title = "Trajectory of L0 = " + seed,
                Width = 920, Height = 700, MinWidth = 600, MinHeight = 440
            };

            var steps = Num(40, 1, 1000000, 1);
            var metric = new ComboBox
            {
                Width = 200,
                ItemsSource = new[] { "s_n (counter)", "nu(L_n)", "|L_n| length", "log10 V(L_n)", "omega(L_n)" },
                SelectedIndex = 0
            };
            var logY = new CheckBox { Content = "log Y", IsChecked = false };
            var replot = new Button { Content = "Recompute / plot" };
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            var bar = Bar();
            var chart = new ChartControl { MinHeight = 280 };
            var table = Mono();
            var sline = new SelectableTextBlock { FontFamily = MonoFont, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            CancellationTokenSource cts = null;

            async Task Compute()
            {
                cts?.Cancel(); cts = new CancellationTokenSource();
                var ct = cts.Token;
                int st = IntOf(steps);
                int m = metric.SelectedIndex;
                status.Text = "computing...";
                bar.Maximum = Math.Max(1, st); bar.Value = 0;
                var prog = new Progress<int>(n => bar.Value = n);
                try
                {
                    var rows = await Task.Run(() => new TrajectoryRunner
                    {
                        Seed = seed, MaxSteps = st, Engine = EngineKind.GapMemory,
                        ComputeOmega = true, ComputeValue = true
                    }.Run(ct, prog), ct);

                    var cs = new ChartSeries { Name = "L0=" + seed, Color = Palette[0] };
                    foreach (var r in rows) cs.Values.Add(MetricValue(m, r));
                    chart.Series.Clear(); chart.Series.Add(cs);
                    chart.LogY = logY.IsChecked == true;
                    chart.Title = metric.SelectedItem?.ToString() ?? "";
                    chart.YLabel = chart.Title; chart.Refresh();

                    var tb = new StringBuilder();
                    var ss = new StringBuilder("s trajectory:  ");
                    foreach (var r in rows)
                    {
                        ss.Append(r.Halted && r.S < 2 ? r.S + "(halt)" : r.S.ToString()).Append(n_sep(r, rows));
                        string v = r.HasValue ? FormatBig(r.Value) : "~" + Value.DecimalDigits(r.Length) + " digits";
                        tb.Append(r.N.ToString().PadLeft(3)).Append("  s=").Append(r.S.ToString().PadLeft(10))
                          .Append("  ").Append(r.ParityChar).Append("  nu=").Append(r.Nu.ToString("#,0").PadLeft(12))
                          .Append("  w=").Append((r.Omega < 0 ? "-" : r.Omega.ToString()).PadLeft(7))
                          .Append("  |L|=").Append(r.Length.ToString("#,0").PadLeft(12))
                          .Append("  V=").Append(v).Append('\n');
                    }
                    sline.Text = ss.ToString();
                    table.Text = tb.ToString();
                    bar.Value = bar.Maximum;
                    var last = rows[rows.Count - 1];
                    status.Text = last.Halted ? $"halts at step {last.N} (s={last.S})" : $"{rows.Count} steps, no halt yet";
                }
                catch (OperationCanceledException) { status.Text = "cancelled"; }
                catch (Exception ex) { status.Text = "error: " + ex.Message; }
            }

            replot.Click += async (_, __) => await Compute();
            metric.SelectionChanged += async (_, __) => await Compute();
            logY.IsCheckedChanged += (_, __) => { chart.LogY = logY.IsChecked == true; chart.Refresh(); };

            var top = new StackPanel { Margin = new Thickness(12, 12, 12, 4), Spacing = 4 };
            top.Children.Add(Row(Lbl("steps"), steps, Lbl("metric"), metric, logY, replot));
            top.Children.Add(Row(bar, status));
            top.Children.Add(new ScrollViewer { Height = 56, Content = sline });

            var chartBorder = new Border { BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1), Margin = new Thickness(12, 0, 12, 6), Child = chart };
            var tableScroll = new ScrollViewer { Content = table, Margin = new Thickness(12, 0, 12, 12), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,2*,1.2*") };
            Grid.SetRow(top, 0); Grid.SetRow(chartBorder, 1); Grid.SetRow(tableScroll, 2);
            grid.Children.Add(top); grid.Children.Add(chartBorder); grid.Children.Add(tableScroll);
            win.Content = grid;
            win.Show(this);
            _ = Compute();
        }

        private static string n_sep(StepInfo r, List<StepInfo> rows) =>
            r.N == rows.Count - 1 ? "" : ", ";

        // ============================================================ CONJUGATE T
        private TextBox _cjSeed;
        private NumericUpDown _cjN;
        private SelectableTextBlock _cjOut;
        private TextBlock _cjStatus;
        private ProgressBar _cjBar;
        private CancellationTokenSource _cjCts;

        private Control BuildConjugateTab()
        {
            _cjSeed = new TextBox { Text = "10", Width = 240, Watermark = "seed L0 (binary)" };
            _cjN = Num(12, 1, 2000, 1);
            var run = new Button { Content = "Iterate T" };
            run.Click += async (_, __) => await RunConjugate();
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, __) => _cjCts?.Cancel();
            _cjStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Goldenrod };
            _cjBar = Bar();
            _cjOut = Mono();

            var panel = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
            panel.Children.Add(Row(Lbl("Seed L0"), _cjSeed, Lbl("iterations"), _cjN, run, cancel));
            panel.Children.Add(Row(_cjBar, _cjStatus));
            panel.Children.Add(new TextBlock
            {
                Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 6),
                Text = "Integer conjugate of Collatz type: N0 = V(L0), N_{n+1} = T(N_n). Each N_n is cross-checked against the " +
                       "bit-string value V(L_n) wherever the engine can still materialise it. (Sequential by nature: single core.)"
            });
            panel.Children.Add(new TextBlock { Text = "n   s_n   digits(N_n)   verified   N_n", FontFamily = MonoFont, Foreground = Brushes.Gray });
            panel.Children.Add(new ScrollViewer { Height = 430, Content = _cjOut, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            return new ScrollViewer { Content = panel };
        }

        private async Task RunConjugate()
        {
            _cjCts?.Cancel(); _cjCts = new CancellationTokenSource();
            var ct = _cjCts.Token;
            string seed = (_cjSeed.Text ?? "").Trim();
            int maxN = IntOf(_cjN);
            _cjStatus.Text = "iterating...";
            _cjBar.Maximum = Math.Max(1, maxN); _cjBar.Value = 0;
            var prog = new Progress<int>(n => _cjBar.Value = n);
            try
            {
                string text = await Task.Run(() =>
                {
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
                        string verified = truth.TryGetValue(n, out var tv) ? (tv == N ? "yes" : "MISMATCH") : "-";
                        sb.Append(n.ToString().PadLeft(3)).Append("  ");
                        bool ok = Conjugate.T(N, out var next, out long s, out _);
                        sb.Append((ok ? s.ToString() : s + "*").PadLeft(5)).Append("  ")
                          .Append(CountDigits(N).ToString().PadLeft(11)).Append("  ")
                          .Append(verified.PadLeft(9)).Append("  ")
                          .Append(FormatBig(N)).Append('\n');
                        ((IProgress<int>)prog).Report(n);
                        if (!ok) { sb.Append("    -> s < 2: T undefined (halt).\n"); break; }
                        N = next;
                    }
                    return sb.ToString();
                }, ct);
                _cjOut.Text = text;
                _cjBar.Value = _cjBar.Maximum;
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
